using System;

using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace CrowdFix.Fixes;

/// <summary>
/// Every job kick prepares its job list (JobListInterface.Prepare), which waits for the previous run through Wait and then calls
/// WaitForSingleObject on the same manual-reset event a second time before resetting it. The event can only be reset
/// by this function, so the second wait always returns at once: ~75 wasted syscalls per frame on the main thread.
/// This turns that call into a jump over it, in both Prepare variants (JobListArgArrayAndIndex and JobListNoArg).
/// </summary>
public unsafe class PrepareWaitSkip: IDisposable
{
	// Prepare: CALL [RAX+0x18] (Wait); reset counters; MOV RCX,[RBX+0x10]; MOV EDX,-1; CALL [WaitForSingleObject]
	private const int ArrayCallOffset = 0x25;
	private const int NoArgCallOffset = 0x1D;

	private const ushort CallIndirect = 0x15FF; // FF 15
	private const ushort JumpOver = 0x04EB; // EB 04: skips the rest of the 6 byte call

	private readonly ushort*[] sites = [];
	private bool enabled;

	public bool Available { get; }

	public bool Enabled => this.enabled;

	public string Status { get; private set; } = "Off";

	public PrepareWaitSkip()
	{
		try
		{
			ushort* array = (ushort*)((nint)JobListArgArrayAndIndex.StaticVirtualTablePointer->Prepare + ArrayCallOffset);
			ushort* single = (ushort*)((nint)JobListNoArg.StaticVirtualTablePointer->Prepare + NoArgCallOffset);
			this.sites = [array, single];
			this.Available = *array == CallIndirect && *single == CallIndirect;
			if (!this.Available)
				this.Status = "Unavailable (unexpected code)";
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (function not resolved)";
			Service.PluginLog.Warning(e, "PrepareWaitSkip unavailable");
		}
	}

	/// <summary>
	/// Any thread: see CodePatch, a thread in Prepare runs either the call or the jump.
	/// </summary>
	public void SetEnabled(bool enabled)
	{
		if (!this.Available || enabled == this.enabled)
			return;

		this.enabled = enabled;
		foreach (ushort* site in this.sites)
			CodePatch.Write(site, enabled ? JumpOver : CallIndirect);

		this.Status = enabled ? "On" : "Off";
	}

	public void Dispose()
	{
		this.SetEnabled(false);
		GC.SuppressFinalize(this);
	}
}
