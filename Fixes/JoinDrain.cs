using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Culling;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace CrowdFix.Fixes;

/// <summary>
/// A JobSystem join (JobListArgArrayAndIndex.Wait) waits until every queued task has run: one per
/// worker, each just a help call, even when the main thread already did every item. With few workers awake that is a
/// worker wake-up plus ~14 empty tasks, then the main thread's own wake-up. Here the main thread claims and runs the
/// list's remaining tasks itself from the queue head, exactly as a worker would, then spins briefly before sleeping.
/// Small culling joins (shadow views: ~25 cells, ~3 us of work) skip the queue entirely and run on the main thread at
/// kick time. Spin length and inline limits are picked per frame from measured main-thread time, no manual tuning.
/// </summary>
public unsafe class JoinDrain: IDisposable
{
	private static readonly int QueueLockOffset = StructOffset.Of<JobQueue>("Lock");
	private const int PoolContextOffset = 0x38; // what threads pass to tasks: the address of JobPool + 0x38 (ClientStructs TaskManager.cs, JobPool)
	// JobSystem blocks: u32 item count, items from +0x10. Cells (CallbackJobs): 8 items of 0x20, camera (RenderCallbackJobs): 1 item
	private static readonly int[] BlockSizes = [0x10 + (8 * 0x20), 0x10 + sizeof(CullingManager.RenderCallbackJobItem)];

	private delegate void WaitDelegate(JobListArgArrayAndIndex* list);

	private delegate void KickDelegate(TaskManager* taskManager, JobListInterface* list);

	[DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
	[DllImport("kernel32.dll")] private static extern void EnterCriticalSection(nint section);
	[DllImport("kernel32.dll")] private static extern void LeaveCriticalSection(nint section);
	[DllImport("kernel32.dll")] private static extern bool SetEvent(nint handle);

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
	private readonly JobQueue* queue;

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
	private void* poolContext;
	private long waitTicks;
	private int waits;

	public bool Available { get; }

	public bool Enabled => this.waitHook?.IsEnabled ?? false;

	public string Status { get; private set; } = "Off";

	public JoinDrain()
	{
		try
		{
			this.queue = JobQueue.Instance();
			this.waitFunction = (nint)JobListArgArrayAndIndex.StaticVirtualTablePointer->Wait;
			this.waitHook = Service.GameInteropProvider.HookFromAddress<WaitDelegate>(this.waitFunction, this.WaitDetour);
			this.kickHook = Service.GameInteropProvider.HookFromAddress<KickDelegate>(
				(nint)TaskManager.Addresses.ScheduleJobList.Value, this.KickDetour);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (function not resolved)";
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

		this.poolContext = (byte*)&taskManager->Pool + PoolContextOffset;
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

	private void WaitDetour(JobListArgArrayAndIndex* list)
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
			ref int pending = ref list->PendingTasks;
			if (Volatile.Read(ref pending) > 0)
				this.RunQueuedTasks(list);

			// clock only read while still waiting
			if (Volatile.Read(ref pending) > 0 && this.spin.Current > 0)
			{
				long until = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * this.spin.Current / 1_000_000);
				while (Volatile.Read(ref pending) > 0 && Stopwatch.GetTimestamp() < until)
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

		int group = this.GroupOf((JobListInterface*)list);
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
	private void KickDetour(TaskManager* taskManager, JobListInterface* list)
	{
		int group = GetCurrentThreadId() == this.mainThreadId ? this.GroupOf(list) : -1;
		if (group < 0)
		{
			this.kickHook!.Original(taskManager, list);
			return;
		}

		if (this.measuring != this.inlineLimit[group])
		{
			this.Kick(taskManager, list, group);
			return;
		}

		// Prepare waits on the same list inside the kick: only the join after it counts
		long start = Stopwatch.GetTimestamp();
		this.kickStart[group] = 0;
		this.Kick(taskManager, list, group);
		this.kickStart[group] = start;
	}

	private void Kick(TaskManager* taskManager, JobListInterface* list, int group)
	{
		int limit = this.inlineLimit[group].Current;
		if (limit == 0 || !HasAtMostItems((JobSystem*)((JobListArgArrayAndIndex*)list)->Object, BlockSizes[group], limit))
		{
			this.kickHook!.Original(taskManager, list);
			return;
		}

		if (list->GetTaskCount() == 0)
			return;

		list->Prepare(); // waits for the previous run, resets counters
		JobListInterface.JobDescriptor descriptor;
		list->GetJob(&descriptor);

		// Same steps as InnerThread.Run: the first help task claims every item, the rest only count down.
		while (true)
		{
			void* argument = null;
			int remaining = 0;
			nint task = (nint)descriptor.Invoker(descriptor.Object, &descriptor.Function, &argument, &remaining);
			if (task == 0)
				break;

			nint* taskVtable = *(nint**)task;
			nint context = (nint)((byte*)&taskManager->Pool + PoolContextOffset);
			if (argument != null)
				((delegate* unmanaged<nint, nint, nint, void>)taskVtable[2])(task, context, *(nint*)argument);
			else
				((delegate* unmanaged<nint, nint, void>)taskVtable[1])(task, context);

			if (remaining == 0)
				break;
		}
	}

	/// <summary>
	/// Only parallel-for group lists: help tasks, no completion callback.
	/// </summary>
	private bool IsGroupList(JobListArgArrayAndIndex* list)
	{
		if ((nint)list->VirtualTable->Wait != this.waitFunction)
			return false;

		JobSystem* group = (JobSystem*)list->Object;
		nint thunk = *(nint*)list->Thunk;
		return group != null && group->JobList == list &&
		       list->CompletionFunction == null && list->CompletionObject == null &&
		       thunk != 0 && *(byte*)thunk == 0xE9;
	}

	/// <summary>
	/// 0 cells, 1 camera, -1 anything else.
	/// </summary>
	private int GroupOf(JobListInterface* list)
	{
		CullingManager* manager = CullingManager.Instance();
		if (manager == null || (nint)((JobListArgArrayAndIndex*)list)->VirtualTable->Wait != this.waitFunction)
			return -1;

		JobSystem* group = (JobSystem*)((JobListArgArrayAndIndex*)list)->Object;
		return group == &manager->CallbackJobs ? 0 : group == &manager->RenderCallbackJobs ? 1 : -1;
	}

	/// <summary>
	/// Item counts as the help functions read them: u32 at the start of each block, 32 blocks per chunk.
	/// </summary>
	private static bool HasAtMostItems(JobSystem* group, int blockSize, int limit)
	{
		uint blocks = group->BlockCount;
		long items = 0;
		for (uint block = 0; block < blocks; block++)
		{
			nint chunk = (nint)group->BlockChunks[(int)(block >> 5)].Value;
			items += *(uint*)(chunk + (blockSize * (block & 31)));
			if (items > limit)
				return false;
		}

		return true;
	}

	/// <summary>
	/// Same steps as InnerThread.Run, but only while this list's entry is the queue head.
	/// </summary>
	private void RunQueuedTasks(JobListArgArrayAndIndex* list)
	{
		nint queueLock = (nint)this.queue + QueueLockOffset;
		while (true)
		{
			void* argument = null;
			int remaining = 0;
			nint task;

			EnterCriticalSection(queueLock);
			uint read = this.queue->ReadIndex;
			JobQueue.JobQueueListData* entry = (JobQueue.JobQueueListData*)Unsafe.AsPointer(ref this.queue->Entries[(int)read]);
			if (read == this.queue->WriteIndex || entry->Job.Object != list)
			{
				LeaveCriticalSection(queueLock);
				return;
			}

			task = (nint)entry->Job.Invoker(list, &entry->Job.Function, &argument, &remaining);
			if (task == 0 || remaining == 0)
			{
				if (entry->CompletionEvent != null)
					SetEvent(*entry->CompletionEvent);

				this.queue->ReadIndex = (read + 1) % (uint)this.queue->Entries.Length;
			}

			LeaveCriticalSection(queueLock);
			if (task == 0)
				return;

			nint* taskVtable = *(nint**)task;
			if (argument != null)
				((delegate* unmanaged<nint, void*, nint, void>)taskVtable[2])(task, this.poolContext, *(nint*)argument);
			else
				((delegate* unmanaged<nint, void*, void>)taskVtable[1])(task, this.poolContext);

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
