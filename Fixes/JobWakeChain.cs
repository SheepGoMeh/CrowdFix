using System;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace CrowdFix.Fixes;

/// <summary>
/// Every job submit calls the pool's wake-all (FUN_140060b70), which SetEvents every sleeping worker from the
/// submitting thread: up to 15 syscalls per submit, ~100 submits per frame on the main thread.
/// Here the submitter wakes one sleeping worker, and each worker that wakes while the queue still has work wakes
/// the next one. Same counters as the game (InnerThread +0x35 skip, +0x38 wake count, +0x40 event).
/// </summary>
public unsafe class JobWakeChain: IDisposable
{
	// LEA RCX,[queue lock]; CALL [EnterCriticalSection]; MOV EAX,[queue write index]; ...
	private const string QueueSignature = "48 8D 0D ?? ?? ?? ?? FF 15 ?? ?? ?? ?? 8B 05 ?? ?? ?? ?? 4C 8D 0D ?? ?? ?? ?? 48 8B 0B 0F 57 C9";
	private const string WakeAllSignature = "E8 ?? ?? ?? ?? 48 8B 4C 24 ?? BA ?? ?? ?? ?? FF 15";
	private const uint Infinite = 0xFFFFFFFF;

	private delegate uint WaitForSingleObjectDelegate(nint handle, uint milliseconds);
	private delegate void WakeAllDelegate(nint pool);

	[DllImport("kernel32.dll")] private static extern bool SetEvent(nint handle);

	private readonly uint* queueWrite;
	private readonly uint* queueRead;
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
			nint queue = Service.SigScanner.ScanText(QueueSignature);
			this.queueWrite = (uint*)(queue + 19 + Marshal.ReadInt32(queue, 15));
			this.queueRead = this.queueWrite + 1; // read index sits right after the write index
			this.waitHook = Service.GameInteropProvider.HookFromImport<WaitForSingleObjectDelegate>(
				null, "KERNEL32.DLL", "WaitForSingleObject", 0, this.WaitDetour);
			this.wakeAllHook = Service.GameInteropProvider.HookFromAddress<WakeAllDelegate>(
				Service.SigScanner.ScanText(WakeAllSignature), this.WakeAllDetour);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (signature not found)";
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
		nint* threads = *(nint**)(jobPool + 0x08);
		uint count = *(uint*)(jobPool + 0x10);
		int woken = 0;

		for (int i = 0; i < count; i++)
		{
			nint worker = threads[i];
			if (*(byte*)(worker + 0x35) != 0)
				continue;

			ref int wakeCount = ref *(int*)(worker + 0x38);
			int current = Volatile.Read(ref wakeCount);
			if (current >= 2)
				continue;

			// Further sleepers are left to the chain; awake ones only get bumped by the submitter.
			if (current == 0 ? woken > 0 : !bumpAwake)
				continue;

			if (Interlocked.Increment(ref wakeCount) == 1)
			{
				SetEvent(*(nint*)(worker + 0x40));
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
		if (result == 0 && milliseconds == Infinite && *this.queueRead != *this.queueWrite &&
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
