using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace CrowdFix.Fixes;

/// <summary>
/// A parallel-for join (JobListArgArrayAndIndex.vf3, FUN_140062300) waits until every queued task has run: one per
/// worker, each just a help call, even when the main thread already did every item. With few workers awake that is a
/// worker wake-up plus ~14 empty tasks, then the main thread's own wake-up. Here the main thread claims and runs the
/// list's remaining tasks itself from the queue head, exactly as a worker would, then spins briefly before sleeping.
/// Small culling joins (shadow views: ~25 cells, ~3 us of work) skip the queue entirely and run on the main thread at
/// kick time. Spin length and inline limits are picked per frame from measured main-thread time, no manual tuning.
/// </summary>
public unsafe class JoinDrain: IDisposable
{
	// JobListArgArrayAndIndex.vf3: MOV RCX,[RCX+0x10]; MOV EDX,-1; JMP [WaitForSingleObject]
	private const string WaitSignature = "48 8B 49 10 BA FF FF FF FF 48 FF 25 ?? ?? ?? ?? CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 41 B9 01 00 00 00 F0 44 0F C1 49 74";
	// LEA RCX,[queue lock]; CALL [EnterCriticalSection]; MOV EAX,[queue write index]; ...
	private const string QueueSignature = "48 8D 0D ?? ?? ?? ?? FF 15 ?? ?? ?? ?? 8B 05 ?? ?? ?? ?? 4C 8D 0D ?? ?? ?? ?? 48 8B 0B 0F 57 C9";
	// TaskManager kick (EeecuteJobList2), shared with BgPrepInline
	private const string KickSignature = "40 53 57 48 83 EC 58 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 40 48 8B 02";

	// JobListArgArrayAndIndex: +0xC pending tasks, +0x18/+0x20 completion fn/obj, +0x88 group, +0x90 task thunk
	private const int PendingOffset = 0x0C;
	private const int CompletionOffset = 0x18;
	private const int GroupOffset = 0x88;
	private const int ThunkOffset = 0x90;
	private const int GroupListOffset = 0x18; // parallel-for group: its job list
	// queue: write index, read index, lock at +8, ring of 128 x 0x98 entries at +0x30
	private const int LockOffset = 0x08;
	private const int RingOffset = 0x30;
	private const int EntrySize = 0x98;
	// entry: +0x38 claim trampoline, +0x40 list, +0x48 claim state, +0x88 optional event
	private const int PoolContextOffset = 0x38; // what workers pass to tasks: JobPool + 0x38
	private const int KickContextOffset = 0x40; // same, from the TaskManager
	// culling groups: cells (CullingManager+0x2168, 272 B blocks), camera (+0x23A8, 80 B blocks)
	private const int CellGroupOffset = 0x2168;
	private const int CameraGroupOffset = 0x23A8;
	private const int BlockCountOffset = 0xB0;
	private const int ChunksOffset = 0x30;
	private static readonly int[] BlockSizes = [272, 80];

	private delegate void WaitDelegate(nint list);

	private delegate uint KickDelegate(nint taskManager, nint list);

	[DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
	[DllImport("kernel32.dll")] private static extern void EnterCriticalSection(nint section);
	[DllImport("kernel32.dll")] private static extern void LeaveCriticalSection(nint section);
	[DllImport("kernel32.dll")] private static extern bool SetEvent(nint handle);

	/// <summary>
	/// Job descriptor as filled by JobList vf1: claim function, its object, then 16 bytes the claim function reads.
	/// </summary>
	[StructLayout(LayoutKind.Sequential, Size = 0x20)]
	private struct JobDescriptor
	{
		public nint Claim;
		public nint Owner;
	}

	/// <summary>
	/// Holds its best choice. While measuring, cycles through every choice frame by frame (same scene for all), then
	/// keeps the lowest median, switching only when clearly cheaper.
	/// </summary>
	private sealed class Tuner(params int[] choices)
	{
		private const int Rounds = 30;
		private readonly double[][] samples = choices.Select(_ => new double[Rounds]).ToArray();
		private readonly double[] median = choices.Select(_ => double.NaN).ToArray();
		private int best;
		private int sample = -1;

		public bool Measuring => this.sample >= 0;

		public int Current => choices[this.Measuring ? this.sample % choices.Length : this.best];

		public void StartMeasuring() => this.sample = 0;

		public void Record(double frameMs)
		{
			if (!this.Measuring)
				return;

			this.samples[this.sample % choices.Length][this.sample / choices.Length] = frameMs;
			if (++this.sample < choices.Length * Rounds)
				return;

			this.sample = -1;
			for (int i = 0; i < choices.Length; i++)
			{
				double[] sorted = this.samples[i].Order().ToArray();
				this.median[i] = (sorted[(Rounds / 2) - 1] + sorted[Rounds / 2]) / 2;
			}

			int cheapest = Array.IndexOf(this.median, this.median.Min());
			if (this.median[cheapest] < this.median[this.best] * 0.97)
				this.best = cheapest;
		}

	}

	private readonly Hook<WaitDelegate>? waitHook;
	private readonly Hook<KickDelegate>? kickHook;
	private readonly nint waitFunction;
	private readonly nint* cullingManager;
	private readonly uint* queueWrite;
	private readonly uint* queueRead;
	private readonly nint queueLock;
	private readonly byte* ring;

	// spin before sleeping (us); max items run inline, per culling group
	private readonly Tuner spin = new(0, 10, 20, 30, 40, 60);
	private readonly Tuner[] inlineLimit = [new(0, 16, 32, 64), new(0, 1, 8)];
	private const int HoldFrames = 1800; // between measurements; nothing is timed meanwhile
	private Tuner? measuring;
	private bool tuned;
	private int holdFrames;
	private int nextTuner = -1;
	private readonly long[] kickStart = new long[2];
	private readonly long[] groupTicks = new long[2];
	private readonly int[] groupRuns = new int[2];

	private bool drain;
	private uint mainThreadId;
	private nint poolContext;
	private long waitTicks;
	private int waits;

	public bool Available { get; }

	public bool Enabled => this.waitHook?.IsEnabled ?? false;

	public string Status { get; private set; } = "Off";

	public JoinDrain()
	{
		try
		{
			nint queue = Service.SigScanner.ScanText(QueueSignature);
			this.queueWrite = (uint*)(queue + 19 + Marshal.ReadInt32(queue, 15));
			this.queueRead = this.queueWrite + 1;
			this.queueLock = queue + 7 + Marshal.ReadInt32(queue, 3);
			this.ring = (byte*)this.queueWrite + RingOffset;
			if (this.queueLock != (nint)this.queueWrite + LockOffset)
			{
				this.Status = "Unavailable (unexpected queue layout)";
				return;
			}

			nint getter = Service.SigScanner.ScanText(CullPerItemClaim.CullingManagerSignature);
			this.cullingManager = (nint*)(getter + 7 + *(int*)(getter + 3));

			this.waitFunction = Service.SigScanner.ScanText(WaitSignature);
			this.waitHook = Service.GameInteropProvider.HookFromAddress<WaitDelegate>(this.waitFunction, this.WaitDetour);
			this.kickHook = Service.GameInteropProvider.HookFromAddress<KickDelegate>(
				Service.SigScanner.ScanText(KickSignature), this.KickDetour);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (signature not found)";
			Service.PluginLog.Warning(e, "JoinDrain unavailable");
		}
	}

	/// <summary>
	/// Framework thread, outside rendering. Both detours only act on this thread and the inline path never leaves
	/// anything queued, so nothing is in flight here.
	/// </summary>
	public void SetEnabled(bool drainJoins, bool inlineSmallJoins)
	{
		if (!this.Available)
			return;

		if (!drainJoins && !inlineSmallJoins)
		{
			this.waitHook!.Disable();
			this.kickHook!.Disable();
			this.Status = "Off";
			return;
		}

		TaskManager* taskManager = TaskManager.Instance();
		if (taskManager == null)
		{
			this.Status = "Waiting for the job pool";
			return;
		}

		this.poolContext = (nint)(&taskManager->Pool) + PoolContextOffset;
		this.mainThreadId = GetCurrentThreadId();
		this.drain = drainJoins;
		Array.Clear(this.kickStart);
		this.waitHook!.Enable();
		if (inlineSmallJoins)
			this.kickHook!.Enable();
		else
			this.kickHook!.Disable();
	}

	/// <summary>
	/// Framework thread, once per frame, before rendering: score last frame's choices, pick this frame's.
	/// </summary>
	public void Update()
	{
		bool inline = this.kickHook?.IsEnabled ?? false;
		if (this.Enabled)
		{
			double msPerTick = 1000.0 / Stopwatch.Frequency;
			if (this.measuring == this.spin && this.waits > 0)
				this.spin.Record(this.waitTicks * msPerTick);

			for (int i = 0; i < this.inlineLimit.Length; i++)
			{
				if (this.measuring == this.inlineLimit[i] && this.groupRuns[i] > 0)
					this.inlineLimit[i].Record(this.groupTicks[i] * msPerTick);
			}

			this.ScheduleMeasuring(inline);
			this.tuned |= this.measuring == null;
			this.Status = this.tuned ? "On, tuned for this scene" : "On, tuning for this scene";
		}

		this.waitTicks = 0;
		this.waits = 0;
		Array.Clear(this.groupTicks);
		Array.Clear(this.groupRuns);
	}

	/// <summary>
	/// One tuner measures at a time while the others hold their best, so they don't skew each other.
	/// </summary>
	private void ScheduleMeasuring(bool inline)
	{
		if (this.measuring is { Measuring: true })
			return;

		this.measuring = null;
		Tuner[] active = [.. this.drain ? [this.spin] : Array.Empty<Tuner>(), .. inline ? this.inlineLimit : []];
		if (active.Length == 0 || --this.holdFrames > 0)
			return;

		// all tuners back to back, then hold
		this.nextTuner = (this.nextTuner + 1) % active.Length;
		this.holdFrames = this.nextTuner == active.Length - 1 ? HoldFrames : 0;
		this.measuring = active[this.nextTuner];
		this.measuring.StartMeasuring();
		Array.Clear(this.kickStart);
	}

	private void WaitDetour(nint list)
	{
		if (GetCurrentThreadId() != this.mainThreadId || !this.IsGroupList(list))
		{
			this.waitHook!.Original(list);
			return;
		}

		Tuner? measuring = this.measuring;
		long start = measuring == this.spin ? Stopwatch.GetTimestamp() : 0;
		if (this.drain)
		{
			int* pending = (int*)(list + PendingOffset);
			if (Volatile.Read(ref *pending) > 0)
				this.RunQueuedTasks(list);

			// clock only read while still waiting
			if (Volatile.Read(ref *pending) > 0 && this.spin.Current > 0)
			{
				long until = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * this.spin.Current / 1_000_000);
				while (Volatile.Read(ref *pending) > 0 && Stopwatch.GetTimestamp() < until)
					Thread.SpinWait(16);
			}
		}

		this.waitHook!.Original(list);
		if (measuring == null)
			return;

		long end = Stopwatch.GetTimestamp();
		if (measuring == this.spin)
		{
			this.waitTicks += end - start;
			this.waits++;
			return;
		}

		int group = this.GroupOf(list);
		if (group >= 0 && this.kickStart[group] != 0)
		{
			this.groupTicks[group] += end - this.kickStart[group];
			this.groupRuns[group]++;
			this.kickStart[group] = 0;
		}
	}

	/// <summary>
	/// Main thread, job workers, the action timeline thread and bone physics all kick: keep the filter cheap.
	/// </summary>
	private uint KickDetour(nint taskManager, nint list)
	{
		if (GetCurrentThreadId() != this.mainThreadId)
			return this.kickHook!.Original(taskManager, list);

		int group = this.GroupOf(list);
		if (group < 0)
			return this.kickHook!.Original(taskManager, list);

		if (this.measuring != this.inlineLimit[group])
			return this.Kick(taskManager, list, group);

		// Prepare waits on the same list inside the kick: only the join after it counts
		long start = Stopwatch.GetTimestamp();
		this.kickStart[group] = 0;
		uint result = this.Kick(taskManager, list, group);
		this.kickStart[group] = start;
		return result;
	}

	private uint Kick(nint taskManager, nint list, int group)
	{
		int limit = this.inlineLimit[group].Current;
		if (limit == 0 || !HasAtMostItems(*(nint*)(list + GroupOffset), BlockSizes[group], limit))
			return this.kickHook!.Original(taskManager, list);

		nint* vtable = *(nint**)list;
		if (((delegate* unmanaged<nint, uint>)vtable[4])(list) == 0) // task count
			return 0;

		((delegate* unmanaged<nint, void>)vtable[2])(list); // Prepare: waits for the previous run, resets counters
		JobDescriptor descriptor;
		((delegate* unmanaged<nint, JobDescriptor*, JobDescriptor*>)vtable[1])(list, &descriptor);

		// Same steps as InnerThread.Run: the first help task claims every item, the rest only count down.
		while (true)
		{
			nint* argument = null;
			int remaining = 0;
			nint task = ((delegate* unmanaged<nint, nint, nint**, int*, nint>)descriptor.Claim)(
				descriptor.Owner, (nint)(&descriptor) + 0x10, &argument, &remaining);
			if (task == 0)
				break;

			nint* taskVtable = *(nint**)task;
			nint context = taskManager + KickContextOffset;
			if (argument != null)
				((delegate* unmanaged<nint, nint, nint, void>)taskVtable[2])(task, context, *argument);
			else
				((delegate* unmanaged<nint, nint, void>)taskVtable[1])(task, context);

			if (remaining == 0)
				break;
		}

		return 1;
	}

	/// <summary>
	/// Only parallel-for group lists: help tasks, no completion callback.
	/// </summary>
	private bool IsGroupList(nint list)
	{
		if ((*(nint**)list)[3] != this.waitFunction)
			return false;

		nint group = *(nint*)(list + GroupOffset);
		nint thunk = *(nint*)(list + ThunkOffset);
		return group != 0 && *(nint*)(group + GroupListOffset) == list &&
		       *(nint*)(list + CompletionOffset) == 0 && *(nint*)(list + CompletionOffset + 8) == 0 &&
		       thunk != 0 && *(byte*)thunk == 0xE9;
	}

	/// <summary>
	/// 0 cells, 1 camera, -1 anything else.
	/// </summary>
	private int GroupOf(nint list)
	{
		nint manager = *this.cullingManager;
		if (manager == 0 || (*(nint**)list)[3] != this.waitFunction)
			return -1;

		nint group = *(nint*)(list + GroupOffset);
		return group == manager + CellGroupOffset ? 0 : group == manager + CameraGroupOffset ? 1 : -1;
	}

	/// <summary>
	/// Item counts as the help functions read them: u32 at the start of each block, 32 blocks per chunk.
	/// </summary>
	private static bool HasAtMostItems(nint group, int blockSize, int limit)
	{
		uint blocks = *(uint*)(group + BlockCountOffset);
		long items = 0;
		for (uint block = 0; block < blocks; block++)
		{
			nint chunk = *(nint*)(group + ChunksOffset + (8 * (block >> 5)));
			items += *(uint*)(chunk + (blockSize * (block & 31)));
			if (items > limit)
				return false;
		}

		return true;
	}

	/// <summary>
	/// Same steps as InnerThread.Run, but only while this list's entry is the queue head.
	/// </summary>
	private void RunQueuedTasks(nint list)
	{
		while (true)
		{
			nint* argument = null;
			int remaining = 0;
			nint task;

			EnterCriticalSection(this.queueLock);
			uint read = *this.queueRead;
			byte* entry = this.ring + (read * EntrySize);
			if (read == *this.queueWrite || *(nint*)(entry + 0x40) != list)
			{
				LeaveCriticalSection(this.queueLock);
				return;
			}

			task = ((delegate* unmanaged<nint, nint, nint**, int*, nint>)*(nint*)(entry + 0x38))(
				list, (nint)(entry + 0x48), &argument, &remaining);
			if (task == 0 || remaining == 0)
			{
				nint* done = *(nint**)(entry + 0x88);
				if (done != null)
					SetEvent(*done);

				*this.queueRead = (read + 1) & 0x7F;
			}

			LeaveCriticalSection(this.queueLock);
			if (task == 0)
				return;

			nint* taskVtable = *(nint**)task;
			if (argument != null)
				((delegate* unmanaged<nint, nint, nint, void>)taskVtable[2])(task, this.poolContext, *argument);
			else
				((delegate* unmanaged<nint, nint, void>)taskVtable[1])(task, this.poolContext);

			if (remaining == 0)
				return;
		}
	}

	public void Dispose()
	{
		this.waitHook?.Dispose();
		this.kickHook?.Dispose();
		GC.SuppressFinalize(this);
	}
}
