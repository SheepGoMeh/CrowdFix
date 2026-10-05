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

	public void Save() => Service.PluginInterface.SavePluginConfig(this);
}
