using System;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace CrowdFix.Fixes;

/// <summary>
/// Every job submit calls the pool's wake-all (JobPool.WakeAllThreads), which SetEvents every sleeping worker from the
/// submitting thread: up to 15 syscalls per submit, ~100 submits per frame on the main thread.
/// Here the submitter wakes one sleeping worker, and each worker that wakes while the queue still has work wakes
/// the next one. Same counters as the game (InnerThread +0x35 skip, PendingWakes, EventHandle2).
/// </summary>
public unsafe class JobWakeChain: IDisposable
{
	private const uint Infinite = 0xFFFFFFFF;
	private const int SkipWakeOffset = 0x35; // wakes skip the thread while set (ClientStructs TaskManager.cs, JobPool.InnerThread)

	private delegate uint WaitForSingleObjectDelegate(nint handle, uint milliseconds);
	private delegate void WakeAllDelegate(nint pool);

	[DllImport("kernel32.dll")] private static extern bool SetEvent(nint handle);

	private readonly JobQueue* queue;
	private readonly Hook<WaitForSingleObjectDelegate>? waitHook;
	private readonly Hook<WakeAllDelegate>? wakeAllHook;

	private nint[] workerEvents = [];
	private nint pool;

	public bool Available { get; }

	public bool Enabled => this.wakeAllHook?.IsEnabled ?? false;

	public int WorkerCount => this.workerEvents.Length;

	public string Status { get; private set; } = "Off";

	public JobWakeChain()
	{
		try
		{
			this.queue = JobQueue.Instance();
			this.waitHook = Service.GameInteropProvider.HookFromImport<WaitForSingleObjectDelegate>(
				null, "KERNEL32.DLL", "WaitForSingleObject", 0, this.WaitDetour);
			this.wakeAllHook = Service.GameInteropProvider.HookFromAddress<WakeAllDelegate>(
				(nint)TaskManager.JobPool.Addresses.WakeAllThreads.Value, this.WakeAllDetour);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (function not resolved)";
			Service.PluginLog.Warning(e, "JobWakeChain unavailable");
		}
	}

	public void SetEnabled(bool enabled)
	{
		if (!this.Available || enabled == this.Enabled)
			return;

		if (!enabled)
		{
			this.wakeAllHook!.Disable(); // the stock wake-all takes over immediately
			this.waitHook!.Disable();
			// Sleeping workers are still inside WaitDetour; a stock wake-all gets them out before the hook can be freed.
			if (this.pool != 0)
				this.wakeAllHook.Original(this.pool);

			this.Status = "Off";
			return;
		}

		TaskManager* taskManager = TaskManager.Instance();
		if (taskManager == null || !taskManager->Pool.Initialized)
		{
			this.Status = "Waiting for the job pool";
			return;
		}

		this.pool = (nint)(&taskManager->Pool);
		nint[] events = new nint[taskManager->Pool.ThreadCount];
		for (int i = 0; i < events.Length; i++)
			events[i] = taskManager->Pool.Threads[i]->EventHandle2;

		this.workerEvents = events;
		this.waitHook!.Enable();
		this.wakeAllHook!.Enable();
		this.Status = "On";
	}

	/// <summary>
	/// Wakes at most one sleeping worker. With bumpAwake, running workers also get another pass like the stock code.
	/// </summary>
	private static int WakeOne(nint jobPool, bool bumpAwake)
	{
		TaskManager.JobPool* pool = (TaskManager.JobPool*)jobPool;
		int woken = 0;

		for (int i = 0; i < pool->ThreadCount; i++)
		{
			TaskManager.JobPool.InnerThread* worker = pool->Threads[i];
			if (*((byte*)worker + SkipWakeOffset) != 0)
				continue;

			ref int wakeCount = ref worker->PendingWakes;
			int current = Volatile.Read(ref wakeCount);
			if (current >= 2)
				continue;

			// Further sleepers are left to the chain; awake ones only get bumped by the submitter.
			if (current == 0 ? woken > 0 : !bumpAwake)
				continue;

			if (Interlocked.Increment(ref wakeCount) == 1)
			{
				SetEvent(worker->EventHandle2);
				woken++;
			}
		}

		return woken;
	}

	private void WakeAllDetour(nint jobPool)
	{
		WakeOne(jobPool, true);
	}

	private uint WaitDetour(nint handle, uint milliseconds)
	{
		uint result = this.waitHook!.Original(handle, milliseconds);
		if (result == 0 && milliseconds == Infinite && this.queue->ReadIndex != this.queue->WriteIndex &&
		    Array.IndexOf(this.workerEvents, handle) >= 0)
			WakeOne(this.pool, false);

		return result;
	}

	public void Dispose()
	{
		this.wakeAllHook?.Dispose();
		this.waitHook?.Dispose();
		GC.SuppressFinalize(this);
	}
}
