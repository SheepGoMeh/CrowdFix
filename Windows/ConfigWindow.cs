using System;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using CrowdFix.Fixes;

namespace CrowdFix.Windows;

public class ConfigWindow(
	CrowdFixConfiguration configuration,
	IdleNotifierFilter idleNotifierFilter,
	JobWakeChain jobWakeChain,
	SkeletonSyncDedupe skeletonSyncDedupe,
	CullingClearTrim cullingClearTrim,
	AllocatorFreeLock allocatorFreeLock,
	StagingPool stagingPool,
	HiddenMinionFreeze hiddenMinionFreeze,
	PrepareWaitSkip prepareWaitSkip,
	BgPrepInline bgPrepInline,
	HiddenHotbarSkip hiddenHotbarSkip,
	AnimTailParallel animTailParallel,
	CharacterCullSplit characterCullSplit,
	CullPerItemClaim cullPerItemClaim,
	Action applySettings)
	: Window("CrowdFix", ImGuiWindowFlags.AlwaysAutoResize)
{
	public override void Draw()
	{
		ImGui.BeginDisabled(!idleNotifierFilter.Available);
		if (ImGui.Checkbox("Skip idle GPU resource notifiers", ref configuration.SkipIdleNotifiers))
			this.Save();
		ImGui.EndDisabled();
		ImGui.TextDisabled($"{idleNotifierFilter.Status}, {idleNotifierFilter.ActiveCount} active notifiers");

		ImGui.BeginDisabled(!jobWakeChain.Available);
		if (ImGui.Checkbox("Chain job worker wake-ups", ref configuration.ChainWorkerWakeups))
			this.Save();
		ImGui.EndDisabled();
		ImGui.TextDisabled($"{jobWakeChain.Status}, {jobWakeChain.WorkerCount} workers");

		ImGui.BeginDisabled(!skeletonSyncDedupe.Available);
		if (ImGui.Checkbox("Skip repeated skeleton pose syncs", ref configuration.DedupeSkeletonSyncs))
			this.Save();
		ImGui.EndDisabled();
		ImGui.TextDisabled(skeletonSyncDedupe.Status);

		ImGui.BeginDisabled(!cullingClearTrim.Available);
		if (ImGui.Checkbox("Trim culling visibility clear", ref configuration.TrimCullingClear))
			this.Save();
		ImGui.EndDisabled();
		ImGui.TextDisabled($"{cullingClearTrim.Status}, clearing {cullingClearTrim.ClearedSlots} of 40960 slots");

		ImGui.BeginDisabled(!allocatorFreeLock.Available);
		if (ImGui.Checkbox("Shorten graphics allocator lock (for CPUs with fewer cores)", ref configuration.ShortenAllocatorLock))
			this.Save();
		ImGui.EndDisabled();
		ImGui.TextDisabled(allocatorFreeLock.Status);

		ImGui.BeginDisabled(!stagingPool.Available);
		if (ImGui.Checkbox("Pool graphics staging allocations", ref configuration.PoolStagingBlocks))
			this.Save();
		ImGui.EndDisabled();
		ImGui.TextDisabled($"{stagingPool.Status}, {stagingPool.PooledBlocks} pooled blocks");

		ImGui.BeginDisabled(!hiddenMinionFreeze.Available);
		if (ImGui.Checkbox("Freeze hidden minions", ref configuration.FreezeHiddenMinions))
			this.Save();
		ImGui.EndDisabled();
		ImGui.TextDisabled(hiddenMinionFreeze.Status);

		ImGui.BeginDisabled(!prepareWaitSkip.Available);
		if (ImGui.Checkbox("Skip redundant job list wait", ref configuration.SkipPrepareWait))
			this.Save();
		ImGui.EndDisabled();
		ImGui.TextDisabled(prepareWaitSkip.Status);

		ImGui.BeginDisabled(!bgPrepInline.Available);
		if (ImGui.Checkbox("Run BG instancing prep inline", ref configuration.InlineBgPrep))
			this.Save();
		ImGui.EndDisabled();
		ImGui.TextDisabled(bgPrepInline.Status);

		ImGui.BeginDisabled(!hiddenHotbarSkip.Available);
		if (ImGui.Checkbox("Skip slot updates on hidden hotbars", ref configuration.SkipHiddenHotbars))
			this.Save();
		ImGui.EndDisabled();
		ImGui.TextDisabled(hiddenHotbarSkip.Status);

		ImGui.BeginDisabled(!animTailParallel.Available);
		if (ImGui.Checkbox("Finish skeleton animation in parallel", ref configuration.ParallelAnimTail))
			this.Save();
		ImGui.EndDisabled();
		ImGui.TextDisabled(animTailParallel.Status);

		ImGui.BeginDisabled(!characterCullSplit.Available);
		if (ImGui.Checkbox("Split character culling across workers", ref configuration.SplitCharacterCulling))
			this.Save();
		ImGui.EndDisabled();
		ImGui.TextDisabled(characterCullSplit.Status);

		ImGui.BeginDisabled(!cullPerItemClaim.Available);
		if (ImGui.Checkbox("Share culling cells one at a time", ref configuration.PerItemCullingClaims))
			this.Save();
		ImGui.EndDisabled();
		ImGui.TextDisabled(cullPerItemClaim.Status);
	}

	private void Save()
	{
		configuration.Save();
		applySettings();
	}
}
