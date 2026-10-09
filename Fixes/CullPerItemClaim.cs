using System;

using Dalamud.Hooking;

namespace CrowdFix.Fixes;

/// <summary>
/// The per-view cell culling group (CullingManager+0x2168) and the culling setup tail group (+0x20A8) hand out work
/// in blocks of up to 8 items. With ~20 cells per view only ~3 threads get any, and the main thread then waits for
/// the slowest block. Both groups already have the engine's per-item claim variant (one item at a time from a flat
/// counter); this routes their block help function to it, on workers and the main thread alike.
/// </summary>
public unsafe class CullPerItemClaim: IDisposable
{
	// in FUN_14024b610: ADD RCX,0x2168; LEA R8,[cell job]; MOVZX R9D,R12B; MOV RDX,RSI; CALL fork-join
	private const string CellJoinSignature = "48 81 C1 68 21 00 00 4C 8D 05 ?? ?? ?? ?? 45 0F B6 CC 48 8B D6 E8";
	private const int CellJoinCall = 21;
	// in FUN_14024cf50: LEA RCX,[RSI+0x20A8]; MOV R9B,1; LEA R8,[job]; MOV RDX,RSI; CALL fork-join
	private const string SetupJoinSignature = "48 8D 8E A8 20 00 00 41 B1 01 4C 8D 05 ?? ?? ?? ?? 48 8B D6 E8";
	private const int SetupJoinCall = 20;
	// call to FUN_1402ba8f0, which starts with MOV RAX,[g_CullingManager]
	internal const string CullingManagerSignature = "E8 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 83 60 ?? ?? E8";

	private const int CellGroupOffset = 0x2168;
	private const int SetupGroupOffset = 0x20A8;
	// fork-join layout: JZ +7 at +0x8E, then CALL per-item help (+0x90), CALL block help (+0x97)
	private const int PerItemCall = 0x90;
	private const int BlockCall = 0x97;

	private delegate void HelpDelegate(nint group);

	private readonly nint* cullingManager;
	private readonly Hook<HelpDelegate>? cellHelpHook;
	private readonly Hook<HelpDelegate>? setupHelpHook;
	private readonly delegate* unmanaged<nint, void> cellPerItem;
	private readonly delegate* unmanaged<nint, void> setupPerItem;

	public bool Available { get; }

	public bool Enabled => this.cellHelpHook?.IsEnabled ?? false;

	public string Status { get; private set; } = "Off";

	public CullPerItemClaim()
	{
		try
		{
			nint getter = Service.SigScanner.ScanText(CullingManagerSignature);
			this.cullingManager = (nint*)(getter + 7 + *(int*)(getter + 3));

			nint cellJoin = CallTarget(Service.SigScanner.ScanText(CellJoinSignature) + CellJoinCall);
			nint setupJoin = CallTarget(Service.SigScanner.ScanText(SetupJoinSignature) + SetupJoinCall);
			if (!HasHelpCalls(cellJoin) || !HasHelpCalls(setupJoin))
			{
				this.Status = "Unavailable (unexpected code)";
				return;
			}

			this.cellPerItem = (delegate* unmanaged<nint, void>)CallTarget(cellJoin + PerItemCall);
			this.setupPerItem = (delegate* unmanaged<nint, void>)CallTarget(setupJoin + PerItemCall);
			this.cellHelpHook = Service.GameInteropProvider.HookFromAddress<HelpDelegate>(
				CallTarget(cellJoin + BlockCall), this.CellHelpDetour);
			this.setupHelpHook = Service.GameInteropProvider.HookFromAddress<HelpDelegate>(
				CallTarget(setupJoin + BlockCall), this.SetupHelpDetour);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (signature not found)";
			Service.PluginLog.Warning(e, "CullPerItemClaim unavailable");
		}
	}

	private static nint CallTarget(nint call) => call + 5 + *(int*)(call + 1);

	private static bool HasHelpCalls(nint join) =>
		*(byte*)(join + 0x8E) == 0x74 && *(byte*)(join + 0x8F) == 0x07 &&
		*(byte*)(join + PerItemCall) == 0xE8 && *(byte*)(join + BlockCall) == 0xE8;

	/// <summary>
	/// Framework thread only, outside rendering: every thread in one fork-join must use the same claim mode, and these
	/// groups only run (fully joined) inside Render.
	/// </summary>
	public void SetEnabled(bool enabled)
	{
		if (!this.Available || enabled == this.Enabled)
			return;

		if (enabled)
		{
			this.cellHelpHook!.Enable();
			this.setupHelpHook!.Enable();
		}
		else
		{
			this.cellHelpHook!.Disable();
			this.setupHelpHook!.Disable();
		}

		this.Status = enabled ? "On" : "Off";
	}

	private void CellHelpDetour(nint group)
	{
		if (group == *this.cullingManager + CellGroupOffset)
			this.cellPerItem(group);
		else
			this.cellHelpHook!.Original(group);
	}

	private void SetupHelpDetour(nint group)
	{
		if (group == *this.cullingManager + SetupGroupOffset)
			this.setupPerItem(group);
		else
			this.setupHelpHook!.Original(group);
	}

	public void Dispose()
	{
		this.cellHelpHook?.Dispose();
		this.setupHelpHook?.Dispose();
		GC.SuppressFinalize(this);
	}
}
