using System;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace CrowdFix.Fixes;

/// <summary>
/// The system config shows the Fps options as 1/2 and 1/4 of the refresh rate, but the game applies a fixed 60/30
/// cap (Sleep loop timed from the last Present return, then vsync). This presents those options with sync
/// interval 2/4 (presets 2 and 4 in SwapChain.Present) instead.
/// </summary>
public unsafe class FramePacer: IDisposable
{
	private delegate void PresentDelegate(SwapChain* swapChain);

	private readonly Hook<PresentDelegate>? presentHook;

	public bool Available { get; }

	public bool Enabled => this.presentHook?.IsEnabled ?? false;

	public string Status { get; private set; } = "Off";

	public FramePacer()
	{
		try
		{
			this.presentHook = Service.GameInteropProvider.HookFromAddress<PresentDelegate>(
				(nint)SwapChain.MemberFunctionPointers.Present, this.PresentDetour);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (signature not found)";
			Service.PluginLog.Warning(e, "FramePacer unavailable");
		}
	}

	public void SetEnabled(bool enabled)
	{
		if (!this.Available || enabled == this.Enabled)
			return;

		if (enabled)
			this.presentHook!.Enable();
		else
			this.presentHook!.Disable();

		this.Status = enabled ? "On" : "Off";
	}

	private void PresentDetour(SwapChain* swapChain)
	{
		Device* device = Device.Instance();
		ushort cap = device->FrameRateLimitPresent;

		// Preset 1 with cap 60/30 = Fps options 2/3
		if (swapChain != device->SwapChain || device->FrameRateLimitPresetPresent != 1 || cap == 0)
		{
			this.presentHook!.Original(swapChain);
			return;
		}

		device->FrameRateLimitPresetPresent = cap == 60 ? 2u : 4u;
		this.presentHook!.Original(swapChain);
		device->FrameRateLimitPresetPresent = 1;
	}

	public void Dispose()
	{
		this.presentHook?.Dispose();
		GC.SuppressFinalize(this);
	}
}
