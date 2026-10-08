using System;
using System.Runtime.InteropServices;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Render;

namespace CrowdFix.Fixes;

/// <summary>
/// Manager.RenderView kicks a single-item job (BG instancing prep, FUN_14028a620) at the start of every view and
/// BGInstancingRenderer.Render waits for it a little later: ~20 kicks per frame, each with an enqueue, a worker wake
/// and an event wait, for ~1 us of work. This runs that one item on the main thread at kick time instead, through the
/// list's own claim and task functions, so the list ends up exactly as a worker would leave it (done and signaled).
/// </summary>
public unsafe class BgPrepInline: IDisposable
{
	// TaskManager kick (EeecuteJobList2)
	private const string KickSignature = "40 53 57 48 83 EC 58 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 40 48 8B 02";
	private const int PrepListOffset = 0x181F8; // single-item job list inside BGInstancingRenderer
	private const int PoolContextOffset = 0x40; // what workers pass to tasks: TaskManager.Pool + 0x38

	private delegate uint KickDelegate(nint taskManager, nint jobList);

	[DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

	/// <summary>
	/// Job descriptor as filled by JobList vf1: claim function, its object, then 16 bytes the claim function reads.
	/// </summary>
	[StructLayout(LayoutKind.Sequential, Size = 0x20)]
	private struct JobDescriptor
	{
		public nint Claim;
		public nint Owner;
	}

	private readonly Hook<KickDelegate>? kickHook;

	private nint prepList;
	private uint mainThreadId;

	public bool Available { get; }

	public bool Enabled => this.kickHook?.IsEnabled ?? false;

	public string Status { get; private set; } = "Off";

	public BgPrepInline()
	{
		try
		{
			this.kickHook = Service.GameInteropProvider.HookFromAddress<KickDelegate>(
				Service.SigScanner.ScanText(KickSignature), this.KickDetour);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (signature not found)";
			Service.PluginLog.Warning(e, "BgPrepInline unavailable");
		}
	}

	/// <summary>
	/// Framework thread. The inline path only runs on the framework thread inside RenderView, so it is never in flight
	/// here; after disabling, the stock kick finds a finished, signaled list.
	/// </summary>
	public void SetEnabled(bool enabled)
	{
		if (!this.Available || enabled == this.Enabled)
			return;

		if (!enabled)
		{
			this.kickHook!.Disable();
			this.Status = "Off";
			return;
		}

		Manager* manager = Manager.Instance();
		if (manager == null)
		{
			this.Status = "Waiting for the render manager";
			return;
		}

		this.prepList = (nint)(&manager->BGInstancingRenderer) + PrepListOffset;
		this.mainThreadId = GetCurrentThreadId();
		this.kickHook!.Enable();
		this.Status = "On";
	}

	/// <summary>
	/// Main thread, job workers, the action timeline thread and bone physics all kick: keep the filter cheap.
	/// </summary>
	private uint KickDetour(nint taskManager, nint jobList)
	{
		if (jobList != this.prepList || GetCurrentThreadId() != this.mainThreadId)
			return this.kickHook!.Original(taskManager, jobList);

		nint* vtable = *(nint**)jobList;
		if (((delegate* unmanaged<nint, uint>)vtable[4])(jobList) == 0) // item count
			return 0;

		((delegate* unmanaged<nint, void>)vtable[2])(jobList); // Prepare: waits for the previous run, resets counters
		JobDescriptor descriptor;
		((delegate* unmanaged<nint, JobDescriptor*, JobDescriptor*>)vtable[1])(jobList, &descriptor);

		// Same steps as InnerThread.Run: claim a task, run it with the pool context.
		while (true)
		{
			nint* argument = null;
			int remaining = 0;
			nint task = ((delegate* unmanaged<nint, nint, nint**, int*, nint>)descriptor.Claim)(
				descriptor.Owner, (nint)(&descriptor) + 0x10, &argument, &remaining);
			if (task == 0)
				break;

			nint* taskVtable = *(nint**)task;
			nint context = taskManager + PoolContextOffset;
			if (argument != null)
				((delegate* unmanaged<nint, nint, nint, void>)taskVtable[2])(task, context, *argument);
			else
				((delegate* unmanaged<nint, nint, void>)taskVtable[1])(task, context);

			if (remaining == 0)
				break;
		}

		return 1;
	}

	public void Dispose()
	{
		this.kickHook?.Dispose();
		GC.SuppressFinalize(this);
	}
}
