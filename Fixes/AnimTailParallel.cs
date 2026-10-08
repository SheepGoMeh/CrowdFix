using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.System.Framework;

using Skeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace CrowdFix.Fixes;

/// <summary>
/// After sampling, the animation update (FUN_14025c720) finishes every skeleton serially on the main thread
/// (FUN_140258ed0: blend timers, pose copies), sorted by attach depth so parents go before children. Skeletons of the
/// same depth do not touch each other, so this runs each depth level on the job pool, through the same parallel-for
/// group the animation submit (FUN_14025c550) uses earlier in the frame, with a full join between levels.
/// Skeletons that cast a ground ray or have pending animation control removals stay on the main thread.
/// </summary>
public unsafe class AnimTailParallel: IDisposable
{
	private const string AnimCoreSignature = "E8 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? 48 8B 6C 24 58";
	private const string TailSignature = "48 89 5C 24 18 55 48 83 EC 30 48 8B E9";
	// inside FUN_14025c720: MOV [entry count],ESI; LEA RDI,[entries]; CMP ESI,1
	private const string EntriesSignature = "89 35 ?? ?? ?? ?? 48 8D 3D ?? ?? ?? ?? 83 FE 01";
	private const string SubmitSignature = "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 48 8B 05 ?? ?? ?? ?? 48 8B D9 F3 0F 11 88 F0 00 00 00";
	private const string AppendSignature = "40 53 55 41 54 48 83 EC 20 8B 0D ?? ?? ?? ?? 4C 8B E2 65 48 8B 04 25 ?? ?? ?? ?? BD 98 02 00 00";

	// FUN_14025c550 layout: base global at +0x0F, kick call +0xC8, help calls +0xD9 / +0xE0
	private const int SubmitBaseOffset = 0x0F;
	private const int SubmitKickCall = 0xC8;
	private const int SubmitHelpBCall = 0xD9;
	private const int SubmitHelpACall = 0xE0;
	private const int GroupOffset = 0x30; // parallel-for group inside the submit base

	private const int MinParallel = 16; // smaller depth levels run serially
	private const int MaxSkeletons = 4096; // group capacity: 16 chunks x 32 blocks x 8 items

	private delegate void AnimCoreDelegate(nint skeletons, float deltaTime);
	private delegate void TailDelegate(nint skeleton, float deltaTime);

	[StructLayout(LayoutKind.Sequential, Size = 0x10)]
	private struct Entry
	{
		public nint Skeleton;
		public int Depth;
	}

	private static delegate* unmanaged<nint, float, void> tailOriginal;
	private static float jobDeltaTime;

	[ThreadStatic] private static bool inAnimCore;
	[ThreadStatic] private static int tailCalls;

	private readonly Hook<AnimCoreDelegate>? animCoreHook;
	private readonly Hook<TailDelegate>? tailHook;
	private readonly int* entryCount;
	private readonly Entry* entries;
	private readonly nint* submitBase;
	private readonly delegate* unmanaged<nint, nint, uint> kick;
	private readonly delegate* unmanaged<nint, void> helpA;
	private readonly delegate* unmanaged<nint, void> helpB;
	private readonly delegate* unmanaged<nint, nint, void> append;
	private readonly nint[] mainOnly = new nint[MaxSkeletons];

	public bool Available { get; }

	public bool Enabled => this.tailHook?.IsEnabled ?? false;

	public string Status { get; private set; } = "Off";

	public AnimTailParallel()
	{
		try
		{
			nint submit = Service.SigScanner.ScanText(SubmitSignature);
			if (*(byte*)(submit + SubmitKickCall) != 0xE8 || *(byte*)(submit + SubmitHelpBCall) != 0xE8 ||
			    *(byte*)(submit + SubmitHelpACall) != 0xE8)
			{
				this.Status = "Unavailable (unexpected code)";
				return;
			}

			this.submitBase = (nint*)(submit + SubmitBaseOffset + 7 + *(int*)(submit + SubmitBaseOffset + 3));
			this.kick = (delegate* unmanaged<nint, nint, uint>)CallTarget(submit + SubmitKickCall);
			this.helpB = (delegate* unmanaged<nint, void>)CallTarget(submit + SubmitHelpBCall);
			this.helpA = (delegate* unmanaged<nint, void>)CallTarget(submit + SubmitHelpACall);
			this.append = (delegate* unmanaged<nint, nint, void>)Service.SigScanner.ScanText(AppendSignature);

			nint list = Service.SigScanner.ScanText(EntriesSignature);
			this.entryCount = (int*)(list + 6 + *(int*)(list + 2));
			this.entries = (Entry*)(list + 13 + *(int*)(list + 9));

			this.animCoreHook = Service.GameInteropProvider.HookFromAddress<AnimCoreDelegate>(
				Service.SigScanner.ScanText(AnimCoreSignature), this.AnimCoreDetour);
			this.tailHook = Service.GameInteropProvider.HookFromAddress<TailDelegate>(
				Service.SigScanner.ScanText(TailSignature), this.TailDetour);
			tailOriginal = (delegate* unmanaged<nint, float, void>)Marshal.GetFunctionPointerForDelegate(this.tailHook.Original);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (signature not found)";
			Service.PluginLog.Warning(e, "AnimTailParallel unavailable");
		}
	}

	private static nint CallTarget(nint call) => call + 5 + *(int*)(call + 1);

	/// <summary>
	/// Framework thread, which also runs the animation update, so a toggle never lands inside the tail loop.
	/// </summary>
	public void SetEnabled(bool enabled)
	{
		if (!this.Available || enabled == this.Enabled)
			return;

		if (enabled)
		{
			this.tailHook!.Enable();
			this.animCoreHook!.Enable();
		}
		else
		{
			this.animCoreHook!.Disable();
			this.tailHook!.Disable();
		}

		this.Status = enabled ? "On" : "Off";
	}

	private void AnimCoreDetour(nint skeletons, float deltaTime)
	{
		inAnimCore = true;
		tailCalls = 0;
		try
		{
			this.animCoreHook!.Original(skeletons, deltaTime);
		}
		finally
		{
			inAnimCore = false;
		}
	}

	/// <summary>
	/// The first tail call of the loop runs the whole sorted batch; the loop's later calls return at once.
	/// </summary>
	private void TailDetour(nint skeleton, float deltaTime)
	{
		if (!inAnimCore)
		{
			tailOriginal(skeleton, deltaTime);
			return;
		}

		if (tailCalls++ != 0)
			return;

		int count = *this.entryCount;
		nint group = *this.submitBase == 0 ? 0 : *this.submitBase + GroupOffset;
		if (count <= 0 || count > MaxSkeletons || this.entries[0].Skeleton != skeleton || group == 0)
		{
			inAnimCore = false; // unexpected state: let the stock loop do everything
			tailOriginal(skeleton, deltaTime);
			return;
		}

		for (int start = 0, end; start < count; start = end)
		{
			int depth = this.entries[start].Depth;
			for (end = start; end < count && this.entries[end].Depth == depth; end++)
			{
			}

			if (end - start < MinParallel)
			{
				for (int i = start; i < end; i++)
					tailOriginal(this.entries[i].Skeleton, deltaTime);
			}
			else
			{
				this.RunLevel(group, start, end, deltaTime);
			}
		}
	}

	private void RunLevel(nint group, int start, int end, float deltaTime)
	{
		int mainCount = 0;
		int jobCount = 0;
		for (int i = start; i < end; i++)
		{
			nint skeleton = this.entries[i].Skeleton;
			if (NeedsMainThread(skeleton))
			{
				this.mainOnly[mainCount++] = skeleton;
			}
			else
			{
				this.append(*this.submitBase, skeleton);
				jobCount++;
			}
		}

		if (jobCount != 0)
		{
			// Same sequence as FUN_14025c550: publish the writers' block counts, arm, kick, help, wait, reset.
			FlushWriters(group);
			jobDeltaTime = deltaTime;
			*(nint*)(group + 0x20) = 0;
			*(nint*)(group + 0x28) = (nint)(delegate* unmanaged<nint, nint*, void>)&TailJob;
			Interlocked.Exchange(ref *(int*)(group + 0xB4), 0);
			Interlocked.Exchange(ref *(int*)(group + 0xB8), 0);
			if (Volatile.Read(ref *(int*)(group + 0xB0)) != 0)
			{
				nint jobList = *(nint*)(group + 0x18);
				this.kick((nint)TaskManager.Instance(), jobList);
				for (int i = 0; i < mainCount; i++)
					tailOriginal(this.mainOnly[i], deltaTime);

				mainCount = 0;
				if (*(byte*)(group + 0xBC) != 0)
					this.helpB(group);
				else
					this.helpA(group);

				((delegate* unmanaged<nint, void>)(*(nint**)jobList)[3])(jobList); // wait
			}
			else
			{
				for (int i = start; i < end; i++)
				{
					nint skeleton = this.entries[i].Skeleton;
					if (!NeedsMainThread(skeleton))
						tailOriginal(skeleton, deltaTime);
				}
			}

			*(nint*)(group + 0x20) = 0;
			*(nint*)(group + 0x28) = 0;
			ResetWriters(group);
			for (int i = 0; i < 16; i++)
			{
				uint* chunk = *(uint**)(group + 0x30 + (i * 8));
				if (chunk != null)
					*chunk = 0;
			}

			*(uint*)(group + 0xB0) = 0;
		}

		for (int i = 0; i < mainCount; i++)
			tailOriginal(this.mainOnly[i], deltaTime);
	}

	private static void FlushWriters(nint group)
	{
		uint writers = *(uint*)(group + 0x10);
		nint writer = *(nint*)(group + 0x08);
		for (uint i = 0; i < writers; i++, writer += 40)
		{
			uint* block = *(uint**)(writer + 0x20);
			if (block != null)
				*block = *(uint*)(writer + 0x10);
		}
	}

	private static void ResetWriters(nint group)
	{
		FlushWriters(group);
		uint writers = *(uint*)(group + 0x10);
		nint writer = *(nint*)(group + 0x08);
		for (uint i = 0; i < writers; i++, writer += 40)
		{
			*(uint*)(writer + 0x10) = 8;
			*(nint*)(writer + 0x20) = 0;
		}
	}

	/// <summary>
	/// Ground ray casts (BG collision) and animation control removals are only done on the main thread.
	/// </summary>
	private static bool NeedsMainThread(nint skeleton)
	{
		nint ground = *(nint*)(skeleton + 0x80);
		if (ground != 0 && (*(byte*)(ground + 0x10) & 1) != 0 && *(nint*)(ground + 0x18) != 0 && *(nint*)(ground + 0x20) != 0)
			return true;

		Skeleton* s = (Skeleton*)skeleton;
		for (int i = 0; i < s->PartialSkeletonCount; i++)
		{
			nint partial = (nint)(&s->PartialSkeletons[i]);
			if (*(nint*)(partial + 0x148) != 0 && *(ulong*)(partial + 0x1C0) != 0) // pose, pending removals
				return true;
		}

		return false;
	}

	/// <summary>
	/// Job worker or main help loop: one skeleton per item.
	/// </summary>
	[UnmanagedCallersOnly]
	private static void TailJob(nint context, nint* item) => tailOriginal(*item, jobDeltaTime);

	public void Dispose()
	{
		this.animCoreHook?.Dispose();
		this.tailHook?.Dispose();
		GC.SuppressFinalize(this);
	}
}
