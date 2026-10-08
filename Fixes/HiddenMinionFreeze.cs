using System;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CrowdFix.Fixes;

/// <summary>
/// Minions are client-side objects whose follow AI (FUN_1417517b0, called from Companion.Update) sweeps a sphere
/// against the level collision every frame. Hiding a minion (e.g. Visibility) only sets the model render flag, so a
/// crowd of hidden minions still pays for all of it. This skips the follow AI for minions whose model is hidden;
/// Companion.Update still warps them back to their owner when they fall too far behind.
/// </summary>
public unsafe class HiddenMinionFreeze: IDisposable
{
	// Tail jump to the follow AI at the end of Companion.Update
	private const string FollowSignature = "E9 ?? ?? ?? ?? 48 8B CF E8 ?? ?? ?? ?? F3 0F 58 87 ?? ?? ?? ?? 0F 2F 05";

	private delegate void FollowDelegate(nint companion);

	private readonly Hook<FollowDelegate>? followHook;

	public bool Available { get; }

	public bool Enabled => this.followHook?.IsEnabled ?? false;

	public string Status { get; private set; } = "Off";

	public HiddenMinionFreeze()
	{
		try
		{
			this.followHook = Service.GameInteropProvider.HookFromAddress<FollowDelegate>(
				Service.SigScanner.ScanText(FollowSignature), this.FollowDetour);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (signature not found)";
			Service.PluginLog.Warning(e, "HiddenMinionFreeze unavailable");
		}
	}

	public void SetEnabled(bool enabled)
	{
		if (!this.Available || enabled == this.Enabled)
			return;

		if (enabled)
			this.followHook!.Enable();
		else
			this.followHook!.Disable();

		this.Status = enabled ? "On" : "Off";
	}

	private void FollowDetour(nint companion)
	{
		if (((GameObject*)companion)->RenderFlags.HasFlag(VisibilityFlags.Model))
			return;

		this.followHook!.Original(companion);
	}

	public void Dispose()
	{
		this.followHook?.Dispose();
		GC.SuppressFinalize(this);
	}
}
