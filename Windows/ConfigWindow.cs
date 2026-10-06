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
	}

	private void Save()
	{
		configuration.Save();
		applySettings();
	}
}
