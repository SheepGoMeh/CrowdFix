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
	public bool InlineBgPrep;
	public bool SkipHiddenHotbars = true;

	public void Save() => Service.PluginInterface.SavePluginConfig(this);
}
