using System;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace CrowdFix.Fixes;

/// <summary>
/// After sampling, Skeleton.UpdateAnimations finishes every skeleton serially on the main thread
/// (Skeleton.FinishAnimation: blend timers, pose copies), in Skeleton.SortedSkeletons order so parents go before
/// children. Skeletons of the same depth do not touch each other, so this runs each depth level on the job pool,
/// through SkeletonUpdater.UpdaterJobs, which SkeletonUpdater.SampleAnimations uses earlier in the frame, with a full
/// join between levels. Skeletons that cast a ground ray or have pending animation control removals stay on the main
/// thread.
/// </summary>
public unsafe class AnimTailParallel: IDisposable
{
	// SampleAnimations: CALL JobSystem help per item (+0xD9) / per block (+0xE0), after scheduling UpdaterJobs
	private const int HelpPerItemCall = 0xD9;
	private const int HelpBlocksCall = 0xE0;

	private const int MinParallel = 16; // smaller depth levels run serially

	private delegate void AnimCoreDelegate(Skeleton* first, float deltaTime);
	private delegate void TailDelegate(Skeleton* skeleton, float deltaTime);

	private static delegate* unmanaged<Skeleton*, float, void> tailOriginal;
	private static float jobDeltaTime;

	[ThreadStatic] private static bool inAnimCore;
	[ThreadStatic] private static int tailCalls;

	private readonly Hook<AnimCoreDelegate>? animCoreHook;
	private readonly Hook<TailDelegate>? tailHook;
	private readonly delegate* unmanaged<JobSystem*, void> helpPerItem;
	private readonly delegate* unmanaged<JobSystem*, void> helpBlocks;
	private readonly Skeleton*[] mainOnly = [];

	public bool Available { get; }

	public bool Enabled => this.tailHook?.IsEnabled ?? false;

	public string Status { get; private set; } = "Off";

	public AnimTailParallel()
	{
		try
		{
			nint sample = (nint)SkeletonUpdater.Addresses.SampleAnimations.Value;
			if (*(byte*)(sample + HelpPerItemCall) != 0xE8 || *(byte*)(sample + HelpBlocksCall) != 0xE8)
			{
				this.Status = "Unavailable (unexpected code)";
				return;
			}

			this.helpPerItem = (delegate* unmanaged<JobSystem*, void>)CallTarget(sample + HelpPerItemCall);
			this.helpBlocks = (delegate* unmanaged<JobSystem*, void>)CallTarget(sample + HelpBlocksCall);
			this.mainOnly = new Skeleton*[Skeleton.SortedSkeletons()->Entries.Length];

			this.animCoreHook = Service.GameInteropProvider.HookFromAddress<AnimCoreDelegate>(
				(nint)Skeleton.Addresses.UpdateAnimations.Value, this.AnimCoreDetour);
			this.tailHook = Service.GameInteropProvider.HookFromAddress<TailDelegate>(
				(nint)Skeleton.Addresses.FinishAnimation.Value, this.TailDetour);
			tailOriginal = (delegate* unmanaged<Skeleton*, float, void>)Marshal.GetFunctionPointerForDelegate(this.tailHook.Original);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (function not resolved)";
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

	private void AnimCoreDetour(Skeleton* first, float deltaTime)
	{
		inAnimCore = true;
		tailCalls = 0;
		try
		{
			this.animCoreHook!.Original(first, deltaTime);
		}
		finally
		{
			inAnimCore = false;
		}
	}

	/// <summary>
	/// The first tail call of the loop runs the whole sorted batch; the loop's later calls return at once.
	/// </summary>
	private void TailDetour(Skeleton* skeleton, float deltaTime)
	{
		if (!inAnimCore)
		{
			tailOriginal(skeleton, deltaTime);
			return;
		}

		if (tailCalls++ != 0)
			return;

		int count = *Skeleton.SortedSkeletonCount();
		Span<Skeleton.SortedSkeleton> entries = Skeleton.SortedSkeletons()->Entries;
		SkeletonUpdater* updater = SkeletonUpdater.Instance();
		if (count <= 0 || count > entries.Length || entries[0].Skeleton != skeleton || updater == null)
		{
			inAnimCore = false; // unexpected state: let the stock loop do everything
			tailOriginal(skeleton, deltaTime);
			return;
		}

		for (int start = 0, end; start < count; start = end)
		{
			int depth = entries[start].Depth;
			for (end = start; end < count && entries[end].Depth == depth; end++)
			{
			}

			if (end - start < MinParallel)
			{
				for (int i = start; i < end; i++)
					tailOriginal(entries[i].Skeleton, deltaTime);
			}
			else
			{
				this.RunLevel(updater, entries[start..end], deltaTime);
			}
		}
	}

	private void RunLevel(SkeletonUpdater* updater, Span<Skeleton.SortedSkeleton> level, float deltaTime)
	{
		JobSystem* jobs = &updater->UpdaterJobs;
		int mainCount = 0;
		int jobCount = 0;
		foreach (Skeleton.SortedSkeleton entry in level)
		{
			if (NeedsMainThread(entry.Skeleton))
			{
				this.mainOnly[mainCount++] = entry.Skeleton;
			}
			else
			{
				updater->AppendSkeleton(entry.Skeleton);
				jobCount++;
			}
		}

		if (jobCount != 0)
		{
			// Same sequence as SampleAnimations: publish the writers' block counts, arm, schedule, help, wait, reset.
			FlushWriters(jobs);
			jobDeltaTime = deltaTime;
			jobs->CallbackThisArg = null;
			jobs->CallbackFunction = (delegate* unmanaged<void*, void*, void>)(delegate* unmanaged<nint, nint*, void>)&TailJob;
			Interlocked.Exchange(ref *(int*)&jobs->NextBlock, 0);
			Interlocked.Exchange(ref *(int*)&jobs->NextItem, 0);
			if (Volatile.Read(ref *(int*)&jobs->BlockCount) != 0)
			{
				JobListInterface* jobList = (JobListInterface*)jobs->JobList;
				TaskManager.Instance()->ScheduleJobList(jobList);
				for (int i = 0; i < mainCount; i++)
					tailOriginal(this.mainOnly[i], deltaTime);

				mainCount = 0;
				if (jobs->ClaimPerItem)
					this.helpPerItem(jobs);
				else
					this.helpBlocks(jobs);

				jobList->Wait();
			}
			else
			{
				foreach (Skeleton.SortedSkeleton entry in level)
				{
					if (!NeedsMainThread(entry.Skeleton))
						tailOriginal(entry.Skeleton, deltaTime);
				}
			}

			jobs->CallbackThisArg = null;
			jobs->CallbackFunction = null;
			ResetWriters(jobs);
			foreach (ref var chunk in jobs->BlockChunks)
			{
				if (chunk.Value != null)
					*chunk.Value = 0;
			}

			jobs->BlockCount = 0;
		}

		for (int i = 0; i < mainCount; i++)
			tailOriginal(this.mainOnly[i], deltaTime);
	}

	private static void FlushWriters(JobSystem* jobs)
	{
		for (uint i = 0; i < jobs->WriterCount; i++)
		{
			JobSystem.Writer* writer = &jobs->Writers[i];
			if (writer->Block != null)
				*writer->Block = writer->ItemCount;
		}
	}

	private static void ResetWriters(JobSystem* jobs)
	{
		FlushWriters(jobs);
		for (uint i = 0; i < jobs->WriterCount; i++)
		{
			jobs->Writers[i].ItemCount = 8;
			jobs->Writers[i].Block = null;
		}
	}

	/// <summary>
	/// Ground ray casts (BG collision) and animation control removals are only done on the main thread.
	/// </summary>
	private static bool NeedsMainThread(Skeleton* skeleton)
	{
		nint ground = *(nint*)((nint)skeleton + 0x80);
		if (ground != 0 && (*(byte*)(ground + 0x10) & 1) != 0 && *(nint*)(ground + 0x18) != 0 && *(nint*)(ground + 0x20) != 0)
			return true;

		for (int i = 0; i < skeleton->PartialSkeletonCount; i++)
		{
			PartialSkeleton* partial = &skeleton->PartialSkeletons[i];
			if (partial->GetHavokPose(0) != null && partial->AnimationControllersEasedOut.LongCount != 0)
				return true;
		}

		return false;
	}

	/// <summary>
	/// Job worker or main help loop: one skeleton per item.
	/// </summary>
	[UnmanagedCallersOnly]
	private static void TailJob(nint context, nint* item) => tailOriginal(*(Skeleton**)item, jobDeltaTime);

	public void Dispose()
	{
		this.animCoreHook?.Dispose();
		this.tailHook?.Dispose();
		GC.SuppressFinalize(this);
	}
}
