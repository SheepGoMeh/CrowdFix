using System;

using Dalamud.Hooking;

namespace CrowdFix.Fixes;

/// <summary>
/// FUN_14025ce70 walks every render skeleton and calls hkaPose::syncModelSpace on each partial skeleton. It runs
/// once from Render::Manager.Render and then again from every Manager.RenderView call (~21 per frame), although
/// nothing on the render path dirties poses in between. This lets the first walk of a frame through and skips the rest.
/// </summary>
public class SkeletonSyncDedupe: IDisposable
{
	private const string SyncWalkSignature = "E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 41 8B FF";

	private delegate void SyncWalkDelegate(nint skeletonList);

	private readonly Hook<SyncWalkDelegate>? syncWalkHook;

	private ulong frame;
	private ulong lastSyncedFrame = ulong.MaxValue;

	public bool Available { get; }

	public bool Enabled => this.syncWalkHook?.IsEnabled ?? false;

	public string Status { get; private set; } = "Off";

	public SkeletonSyncDedupe()
	{
		try
		{
			this.syncWalkHook = Service.GameInteropProvider.HookFromAddress<SyncWalkDelegate>(
				Service.SigScanner.ScanText(SyncWalkSignature), this.SyncWalkDetour);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (signature not found)";
			Service.PluginLog.Warning(e, "SkeletonSyncDedupe unavailable");
		}
	}

	public void SetEnabled(bool enabled)
	{
		if (!this.Available || enabled == this.Enabled)
			return;

		if (enabled)
			this.syncWalkHook!.Enable();
		else
			this.syncWalkHook!.Disable();

		this.Status = enabled ? "On" : "Off";
	}

	/// <summary>
	/// Framework thread, once per frame; the walks themselves also run on the framework thread.
	/// </summary>
	public void Update() => this.frame++;

	private void SyncWalkDetour(nint skeletonList)
	{
		if (this.lastSyncedFrame == this.frame)
			return;

		this.lastSyncedFrame = this.frame;
		this.syncWalkHook!.Original(skeletonList);
	}

	public void Dispose()
	{
		this.syncWalkHook?.Dispose();
		GC.SuppressFinalize(this);
	}
}
