using System;
using System.Diagnostics;
using System.Threading;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace CrowdFix.Fixes;

/// <summary>
/// The system config shows the Fps options as 1/2 and 1/4 of the refresh rate, but the game applies a fixed 60/30
/// cap with a Sleep loop timed from the last Present return, ~1 ms early.
/// VRR: fixed schedule at refresh/2 and refresh/4, Sleep until close, then spin.
/// Fixed refresh: sync interval 2/4 (presets 2 and 4 in SwapChain.Present).
/// </summary>
public unsafe class FramePacer: IDisposable
{
	// Spin the last 2 ms
	private static readonly long SpinTicks = Stopwatch.Frequency / 500;

	private delegate void PresentDelegate(SwapChain* swapChain);

	private readonly Hook<PresentDelegate>? presentHook;

	private FramePacing mode;

	private long deadline;

	public bool Available { get; }

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

	public void SetMode(FramePacing mode)
	{
		if (!this.Available || mode == this.mode)
			return;

		if (mode == FramePacing.Off)
			this.presentHook!.Disable();
		else
			this.presentHook!.Enable();

		this.mode = mode;
		this.deadline = 0;
		this.Status = mode switch
		{
			FramePacing.Vrr => "On (timer)",
			FramePacing.FixedRefresh => "On (sync interval)",
			_ => "Off",
		};
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

		if (this.mode == FramePacing.FixedRefresh)
		{
			device->FrameRateLimitPresetPresent = cap == 60 ? 2u : 4u;
			this.presentHook!.Original(swapChain);
			device->FrameRateLimitPresetPresent = 1;
			return;
		}

		int target = device->FrameRate / (cap == 60 ? 2 : 4);
		this.Wait(Stopwatch.Frequency / (target > 0 ? target : cap));

		device->FrameRateLimitPresent = 0;
		this.presentHook!.Original(swapChain);
		device->FrameRateLimitPresent = cap;
	}

	private void Wait(long period)
	{
		long now = Stopwatch.GetTimestamp();

		// First frame or over a frame behind
		if (this.deadline == 0 || now - this.deadline > period)
			this.deadline = now;

		while (this.deadline - now > SpinTicks)
		{
			Thread.Sleep(1);
			now = Stopwatch.GetTimestamp();
		}

		while (now < this.deadline)
		{
			Thread.SpinWait(16);
			now = Stopwatch.GetTimestamp();
		}

		this.deadline += period;
	}

	public void Dispose()
	{
		this.presentHook?.Dispose();
		GC.SuppressFinalize(this);
	}
}
