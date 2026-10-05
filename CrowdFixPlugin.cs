using System;

using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using CrowdFix.Fixes;
using CrowdFix.Windows;

namespace CrowdFix;

public class CrowdFixPlugin: IDalamudPlugin
{
	private const string CommandName = "/crowdfix";

	private readonly CrowdFixConfiguration configuration;
	private readonly IdleNotifierFilter idleNotifierFilter;
	private readonly JobWakeChain jobWakeChain;
	private readonly SkeletonSyncDedupe skeletonSyncDedupe;
	private readonly CullingClearTrim cullingClearTrim;
	private readonly AllocatorFreeLock allocatorFreeLock;
	private readonly StagingPool stagingPool;
	private readonly WindowSystem windowSystem;
	private readonly ConfigWindow configWindow;

	private bool settingsPending = true;

	public CrowdFixPlugin(IDalamudPluginInterface pluginInterface)
	{
		pluginInterface.Create<Service>();

		this.configuration = Service.PluginInterface.GetPluginConfig() as CrowdFixConfiguration ??
		                     new CrowdFixConfiguration();

		this.idleNotifierFilter = new IdleNotifierFilter();
		this.jobWakeChain = new JobWakeChain();
		this.skeletonSyncDedupe = new SkeletonSyncDedupe();
		this.cullingClearTrim = new CullingClearTrim();
		this.allocatorFreeLock = new AllocatorFreeLock();
		this.stagingPool = new StagingPool();

		this.windowSystem = new WindowSystem("CrowdFix");
		this.configWindow = new ConfigWindow(
			this.configuration, this.idleNotifierFilter, this.jobWakeChain, this.skeletonSyncDedupe, this.cullingClearTrim, this.allocatorFreeLock, this.stagingPool,
			() => this.settingsPending = true);
		this.windowSystem.AddWindow(this.configWindow);

		Service.PluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
		Service.PluginInterface.UiBuilder.OpenConfigUi += this.configWindow.Toggle;
		Service.CommandManager.AddHandler(
			CommandName,
			new CommandInfo((_, _) => this.configWindow.Toggle()) { HelpMessage = "Open the CrowdFix settings." });
		Service.Framework.Update += this.OnFrameworkUpdate;
	}

	/// <summary>
	/// Settings are applied here rather than from the UI: the UI draws inside DeviceDX11.PostTick, which one of the
	/// fixes patches.
	/// </summary>
	private void OnFrameworkUpdate(IFramework framework)
	{
		if (this.settingsPending)
		{
			this.idleNotifierFilter.SetEnabled(this.configuration.SkipIdleNotifiers);
			this.jobWakeChain.SetEnabled(this.configuration.ChainWorkerWakeups);
			this.skeletonSyncDedupe.SetEnabled(this.configuration.DedupeSkeletonSyncs);
			this.cullingClearTrim.SetEnabled(this.configuration.TrimCullingClear);
			this.allocatorFreeLock.SetEnabled(this.configuration.ShortenAllocatorLock);
			this.stagingPool.SetEnabled(this.configuration.PoolStagingBlocks);

			// The job pool and the graphics allocator may not exist yet right after login; keep retrying until they do.
			this.settingsPending =
				(this.configuration.ChainWorkerWakeups && this.jobWakeChain.Available && !this.jobWakeChain.Enabled) ||
				(this.configuration.PoolStagingBlocks && this.stagingPool.Available && !this.stagingPool.Enabled);
		}

		this.idleNotifierFilter.Update();
		this.skeletonSyncDedupe.Update();
		this.cullingClearTrim.Update();
	}

	protected virtual void Dispose(bool disposing)
	{
		if (!disposing)
		{
			return;
		}

		Service.Framework.Update -= this.OnFrameworkUpdate;
		Service.CommandManager.RemoveHandler(CommandName);
		Service.PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
		Service.PluginInterface.UiBuilder.OpenConfigUi -= this.configWindow.Toggle;
		this.windowSystem.RemoveAllWindows();

		// The PostTick patch has to be undone on the framework thread.
		Service.Framework.RunOnFrameworkThread(() =>
		{
			this.idleNotifierFilter.Dispose();
			this.cullingClearTrim.Dispose();
			this.stagingPool.Dispose();
		}).Wait();
		this.jobWakeChain.Dispose();
		this.skeletonSyncDedupe.Dispose();
		this.allocatorFreeLock.Dispose();
	}

	public void Dispose()
	{
		this.Dispose(true);
		GC.SuppressFinalize(this);
	}
}
