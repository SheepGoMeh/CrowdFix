using System;

namespace CrowdFix.Fixes;

/// <summary>
/// The hotbar update (FUN_14011b840) walks all 18 bars and both cross hotbar sets every frame. For hidden ones it
/// still runs RaptureHotbarModule.PrepareSlotForRender on every slot into a throwaway intermediate. Nothing reads the
/// result: a bar that becomes visible gets a full prepare that same frame. This jumps over the intermediate setup and
/// the prepare call at both hidden-bar sites; the number array clears and the item reload call before them still run.
/// </summary>
public unsafe class HiddenHotbarSkip: IDisposable
{
	// LEA RCX,[tmp]; CALL HotbarUIIntermediate.ctor; MOV RCX,[module]; LEA R8,[tmp]; MOV RDX,slot; CALL Prepare; INC ESI
	private const string BarSignature = "48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8B 8F ?? ?? ?? ?? 4C 8D 44 24 ?? 48 8B D3 E8 ?? ?? ?? ?? FF C6 83 C5 11";
	private const string CrossBarSignature = "48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8B 8F ?? ?? ?? ?? 4C 8D 44 24 ?? 49 8B D6 E8 ?? ?? ?? ?? FF C6 83 C3 11";

	private const ushort LoadAddress = 0x8D48; // 48 8D
	private const ushort JumpPast = 0x1CEB; // EB 1C: lands on INC ESI
	private const ushort IncEsi = 0xC6FF; // FF C6 at +0x1E

	private readonly ushort*[] sites = [];
	private bool enabled;

	public bool Available { get; }

	public bool Enabled => this.enabled;

	public string Status { get; private set; } = "Off";

	public HiddenHotbarSkip()
	{
		try
		{
			ushort* bar = (ushort*)Service.SigScanner.ScanText(BarSignature);
			ushort* crossBar = (ushort*)Service.SigScanner.ScanText(CrossBarSignature);
			this.sites = [bar, crossBar];
			this.Available = *bar == LoadAddress && *(bar + 0x0F) == IncEsi &&
			                 *crossBar == LoadAddress && *(crossBar + 0x0F) == IncEsi; // +0x1E bytes
			if (!this.Available)
				this.Status = "Unavailable (unexpected code)";
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (signature not found)";
			Service.PluginLog.Warning(e, "HiddenHotbarSkip unavailable");
		}
	}

	/// <summary>
	/// Framework thread: the hotbar update runs there too, so the code is never mid-execution while it changes.
	/// </summary>
	public void SetEnabled(bool enabled)
	{
		if (!this.Available || enabled == this.enabled)
			return;

		this.enabled = enabled;
		foreach (ushort* site in this.sites)
			CodePatch.Write(site, enabled ? JumpPast : LoadAddress);

		this.Status = enabled ? "On" : "Off";
	}

	public void Dispose()
	{
		this.SetEnabled(false);
		GC.SuppressFinalize(this);
	}
}
