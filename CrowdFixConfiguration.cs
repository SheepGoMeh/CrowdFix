using Dalamud.Configuration;

namespace CrowdFix;

public class CrowdFixConfiguration: IPluginConfiguration
{
	public int Version { get; set; }

	public bool SkipIdleNotifiers = true;
	public bool ChainWorkerWakeups = true;
	public bool DedupeSkeletonSyncs = true;
	public bool TrimCullingClear = true;
	public bool ShortenAllocatorLock;
	public bool PoolStagingBlocks;
	public bool FreezeHiddenMinions = true;
	public bool SkipPrepareWait;
	public bool InlineBgPrep = true;
	public bool SkipHiddenHotbars = true;
	public bool ParallelAnimTail = true;
	public bool SplitCharacterCulling = true;
	public bool PerItemCullingClaims = true;
	public bool GatherUsedCommands = true;
	public bool PaceFrameLimit;
	public bool DrainJoins = true;
	public bool InlineSmallJoins = true;

	public void Save() => Service.PluginInterface.SavePluginConfig(this);
}
