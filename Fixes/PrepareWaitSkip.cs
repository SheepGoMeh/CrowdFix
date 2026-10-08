using System;

namespace CrowdFix.Fixes;

/// <summary>
/// Every job kick prepares its job list (vf2), which waits for the previous run through vf3 and then calls
/// WaitForSingleObject on the same manual-reset event a second time before resetting it. The event can only be reset
/// by this function, so the second wait always returns at once: ~75 wasted syscalls per frame on the main thread.
/// This turns that call into a jump over it, in both Prepare variants (array lists and single-item lists).
/// </summary>
public unsafe class PrepareWaitSkip: IDisposable
{
	// JobListArgArrayAndIndex.vf2: CALL [RAX+0x18]; ...; MOV RCX,[RBX+0x10]; MOV EDX,-1; CALL [WaitForSingleObject]
	private const string ArrayPrepareSignature = "40 53 48 83 EC 20 48 8B 01 48 8B D9 FF 50 18 33 D2 8B C2 87 43 7C 87 93 A0 00 00 00 48 8B 4B 10 BA FF FF FF FF FF 15";
	private const int ArrayCallOffset = 0x25;
	// Single-item job list vf2, same shape
	private const string SinglePrepareSignature = "40 53 48 83 EC 20 48 8B 01 48 8B D9 FF 50 18 33 C0 BA FF FF FF FF 87 43 74 48 8B 4B 10 FF 15";
	private const int SingleCallOffset = 0x1D;

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
			ushort* array = (ushort*)(Service.SigScanner.ScanText(ArrayPrepareSignature) + ArrayCallOffset);
			ushort* single = (ushort*)(Service.SigScanner.ScanText(SinglePrepareSignature) + SingleCallOffset);
			this.sites = [array, single];
			this.Available = *array == CallIndirect && *single == CallIndirect;
			if (!this.Available)
				this.Status = "Unavailable (unexpected code)";
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (signature not found)";
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
