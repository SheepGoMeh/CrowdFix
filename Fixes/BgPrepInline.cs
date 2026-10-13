using System;
using System.Runtime.InteropServices;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace CrowdFix.Fixes;

/// <summary>
/// Manager.RenderView schedules BGInstancingRenderer.PrepareJobList (a single task) at the start of every view and
/// BGInstancingRenderer.Render waits for it a little later: ~20 kicks per frame, each with an enqueue, a worker wake
/// and an event wait, for ~1 us of work. This runs that one item on the main thread at kick time instead, through the
/// list's own claim and task functions, so the list ends up exactly as a worker would leave it (done and signaled).
/// </summary>
public unsafe class BgPrepInline: IDisposable
{
	private const int PoolContextOffset = 0x38; // what threads pass to tasks: the address of JobPool + 0x38 (ClientStructs TaskManager.cs, JobPool)

	private delegate uint KickDelegate(nint taskManager, nint jobList);

	[DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

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
				(nint)TaskManager.Addresses.ScheduleJobList.Value, this.KickDetour);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (function not resolved)";
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

		this.prepList = (nint)(&manager->BGInstancingRenderer.PrepareJobList);
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

		JobListInterface* list = (JobListInterface*)jobList;
		if (list->GetTaskCount() == 0)
			return 0;

		list->Prepare(); // waits for the previous run, resets counters
		JobListInterface.JobDescriptor descriptor;
		list->GetJob(&descriptor);

		// Same steps as InnerThread.Run: claim a task, run it with the pool context.
		while (true)
		{
			void* argument = null;
			int remaining = 0;
			nint task = (nint)descriptor.Invoker(descriptor.Object, &descriptor.Function, &argument, &remaining);
			if (task == 0)
				break;

			nint* taskVtable = *(nint**)task;
			nint context = (nint)(&((TaskManager*)taskManager)->Pool) + PoolContextOffset;
			if (argument != null)
				((delegate* unmanaged<nint, nint, nint, void>)taskVtable[2])(task, context, *(nint*)argument);
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
