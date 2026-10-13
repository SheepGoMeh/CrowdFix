using System;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Culling;

namespace CrowdFix.Fixes;

/// <summary>
/// The per-view cell culling group (CullingManager.CallbackJobs) and the culling setup tail group (CullingJobs) hand out work
/// in blocks of up to 8 items. With ~20 cells per view only ~3 threads get any, and the main thread then waits for
/// the slowest block. Both groups already have the engine's per-item claim variant (one item at a time from a flat
/// counter); this routes their block help function to it, on workers and the main thread alike.
/// JobSystem.ClaimPerItem cannot be used for this: only the main thread reads it, workers run the help stub picked when
/// the job list was created.
/// </summary>
public unsafe class CullPerItemClaim: IDisposable
{
	// The fork-joins and their help functions are not in ClientStructs.
	// cell culling: ADD RCX,0x2168; LEA R8,[cell job]; MOVZX R9D,R12B; MOV RDX,RSI; CALL fork-join
	private const string CellJoinSignature = "48 81 C1 68 21 00 00 4C 8D 05 ?? ?? ?? ?? 45 0F B6 CC 48 8B D6 E8";
	private const int CellJoinCall = 21;
	// culling setup: LEA RCX,[RSI+0x20A8]; MOV R9B,1; LEA R8,[job]; MOV RDX,RSI; CALL fork-join
	private const string SetupJoinSignature = "48 8D 8E A8 20 00 00 41 B1 01 4C 8D 05 ?? ?? ?? ?? 48 8B D6 E8";
	private const int SetupJoinCall = 20;

	// fork-join layout: JZ +7 at +0x8E, then CALL per-item help (+0x90), CALL block help (+0x97)
	private const int PerItemCall = 0x90;
	private const int BlockCall = 0x97;

	private delegate void HelpDelegate(JobSystem* group);

	private readonly Hook<HelpDelegate>? cellHelpHook;
	private readonly Hook<HelpDelegate>? setupHelpHook;
	private readonly delegate* unmanaged<JobSystem*, void> cellPerItem;
	private readonly delegate* unmanaged<JobSystem*, void> setupPerItem;

	public bool Available { get; }

	public bool Enabled => this.cellHelpHook?.IsEnabled ?? false;

	public string Status { get; private set; } = "Off";

	public CullPerItemClaim()
	{
		try
		{
			nint cellJoin = CallTarget(Service.SigScanner.ScanText(CellJoinSignature) + CellJoinCall);
			nint setupJoin = CallTarget(Service.SigScanner.ScanText(SetupJoinSignature) + SetupJoinCall);
			if (!HasHelpCalls(cellJoin) || !HasHelpCalls(setupJoin))
			{
				this.Status = "Unavailable (unexpected code)";
				return;
			}

			this.cellPerItem = (delegate* unmanaged<JobSystem*, void>)CallTarget(cellJoin + PerItemCall);
			this.setupPerItem = (delegate* unmanaged<JobSystem*, void>)CallTarget(setupJoin + PerItemCall);
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

	private void CellHelpDetour(JobSystem* group)
	{
		if (group == &CullingManager.Instance()->CallbackJobs)
			this.cellPerItem(group);
		else
			this.cellHelpHook!.Original(group);
	}

	private void SetupHelpDetour(JobSystem* group)
	{
		if (group == &CullingManager.Instance()->CullingJobs)
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
