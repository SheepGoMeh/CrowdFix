using System;
using System.Threading;

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
	private readonly HiddenMinionFreeze hiddenMinionFreeze;
	private readonly PrepareWaitSkip prepareWaitSkip;
	private readonly BgPrepInline bgPrepInline;
	private readonly HiddenHotbarSkip hiddenHotbarSkip;
	private readonly AnimTailParallel animTailParallel;
	private readonly CharacterCullSplit characterCullSplit;
	private readonly CullPerItemClaim cullPerItemClaim;
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
		this.hiddenMinionFreeze = new HiddenMinionFreeze();
		this.prepareWaitSkip = new PrepareWaitSkip();
		this.bgPrepInline = new BgPrepInline();
		this.hiddenHotbarSkip = new HiddenHotbarSkip();
		this.animTailParallel = new AnimTailParallel();
		this.characterCullSplit = new CharacterCullSplit();
		this.cullPerItemClaim = new CullPerItemClaim();

		this.windowSystem = new WindowSystem("CrowdFix");
		this.configWindow = new ConfigWindow(
			this.configuration, this.idleNotifierFilter, this.jobWakeChain, this.skeletonSyncDedupe, this.cullingClearTrim, this.allocatorFreeLock, this.stagingPool,
			this.hiddenMinionFreeze, this.prepareWaitSkip, this.bgPrepInline, this.hiddenHotbarSkip, this.animTailParallel,
			this.characterCullSplit, this.cullPerItemClaim,
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
			this.hiddenMinionFreeze.SetEnabled(this.configuration.FreezeHiddenMinions);
			this.prepareWaitSkip.SetEnabled(this.configuration.SkipPrepareWait);
			this.bgPrepInline.SetEnabled(this.configuration.InlineBgPrep);
			this.hiddenHotbarSkip.SetEnabled(this.configuration.SkipHiddenHotbars);
			this.animTailParallel.SetEnabled(this.configuration.ParallelAnimTail);
			this.characterCullSplit.SetEnabled(this.configuration.SplitCharacterCulling);
			this.cullPerItemClaim.SetEnabled(this.configuration.PerItemCullingClaims);

			// The job pool and the graphics allocator may not exist yet right after login; keep retrying until they do.
			this.settingsPending =
				(this.configuration.ChainWorkerWakeups && this.jobWakeChain.Available && !this.jobWakeChain.Enabled) ||
				(this.configuration.PoolStagingBlocks && this.stagingPool.Available && !this.stagingPool.Enabled) ||
				(this.configuration.InlineBgPrep && this.bgPrepInline.Available && !this.bgPrepInline.Enabled);
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
			this.jobWakeChain.SetEnabled(false);
			this.skeletonSyncDedupe.SetEnabled(false);
			this.allocatorFreeLock.SetEnabled(false);
			this.hiddenMinionFreeze.SetEnabled(false);
			this.bgPrepInline.SetEnabled(false);
			this.animTailParallel.SetEnabled(false);
			this.characterCullSplit.SetEnabled(false);
			this.cullPerItemClaim.SetEnabled(false);
			this.prepareWaitSkip.Dispose();
			this.hiddenHotbarSkip.Dispose();
			this.idleNotifierFilter.Dispose();
			this.cullingClearTrim.Dispose();
			this.stagingPool.Dispose();
		}).Wait();

		// Job and render threads can still be inside a detour; let them leave before the hooks are freed.
		Thread.Sleep(200);
		this.jobWakeChain.Dispose();
		this.skeletonSyncDedupe.Dispose();
		this.allocatorFreeLock.Dispose();
		this.hiddenMinionFreeze.Dispose();
		this.bgPrepInline.Dispose();
		this.animTailParallel.Dispose();
		this.characterCullSplit.Dispose();
		this.cullPerItemClaim.Dispose();
	}

	public void Dispose()
	{
		this.Dispose(true);
		GC.SuppressFinalize(this);
	}
}
