using System;
using System.Runtime.InteropServices;

using Dalamud.Hooking;

namespace CrowdFix.Fixes;

/// <summary>
/// The graphics small-object allocator (AllocatorManager+0x10, Free FUN_140334ba0) takes its lock before checking
/// whether a block came from its slabs, and for every other block calls the backing allocator's free while still
/// holding that lock. Draw building frees ~1,200 such staging blocks per frame from all job threads, so small
/// allocations queue behind backing frees. Here the slab check stays under the lock, but backing frees happen after
/// releasing it; slab blocks still go through the original Free.
/// Mostly relieves the job workers, so it matters on CPUs where the main thread ends up waiting for them.
/// </summary>
public unsafe class AllocatorFreeLock: IDisposable
{
	private const string FreeSignature = "48 85 D2 0F 84 ?? ?? ?? ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B F1 48 89 5C 24 ?? 48 81 C1";

	// Small-object allocator layout (FUN_140334ba0 / FUN_1403348e0)
	private const int BackingOffset = 0x108;
	private const int ChunkTableOffset = 0x110;
	private const int ChunkCountOffset = 0x130;
	private const int LockOffset = 0x158;
	private const int BackingFreeSlot = 0x20;

	private delegate void FreeDelegate(nint allocator, nint block);

	[DllImport("kernel32.dll")] private static extern void EnterCriticalSection(nint criticalSection);
	[DllImport("kernel32.dll")] private static extern void LeaveCriticalSection(nint criticalSection);

	private readonly Hook<FreeDelegate>? freeHook;

	public bool Available { get; }

	public bool Enabled => this.freeHook?.IsEnabled ?? false;

	public string Status { get; private set; } = "Off";

	public AllocatorFreeLock()
	{
		try
		{
			this.freeHook = Service.GameInteropProvider.HookFromAddress<FreeDelegate>(
				Service.SigScanner.ScanText(FreeSignature), this.FreeDetour);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (signature not found)";
			Service.PluginLog.Warning(e, "AllocatorFreeLock unavailable");
		}
	}

	public void SetEnabled(bool enabled)
	{
		if (!this.Available || enabled == this.Enabled)
			return;

		if (enabled)
			this.freeHook!.Enable();
		else
			this.freeHook!.Disable();

		this.Status = enabled ? "On" : "Off";
	}

	private void FreeDetour(nint allocator, nint block)
	{
		if (block == 0)
			return;

		nint criticalSection = allocator + LockOffset;
		EnterCriticalSection(criticalSection);
		if (IsSlabBlock(allocator, block))
		{
			try
			{
				this.freeHook!.Original(allocator, block); // takes the (recursive) lock again
			}
			finally
			{
				LeaveCriticalSection(criticalSection);
			}

			return;
		}

		LeaveCriticalSection(criticalSection);
		nint backing = *(nint*)(allocator + BackingOffset);
		((delegate* unmanaged<nint, nint, void>)(*(nint*)(*(nint*)backing + BackingFreeSlot)))(backing, block);
	}

	/// <summary>
	/// The original's membership test (page header at block &amp; ~0x3FF). Only valid under the allocator lock, since
	/// the chunk table is reallocated when it grows.
	/// </summary>
	private static bool IsSlabBlock(nint allocator, nint block)
	{
		nint page = block & ~(nint)0x3FF;
		uint index = *(uint*)(page + 0x1C);
		if (index >= *(uint*)(allocator + ChunkCountOffset))
			return false;

		nint chunk = *(nint*)(*(nint*)(allocator + ChunkTableOffset) + 0x10 + (index * 0x30));
		return chunk != 0 && (ulong)(block - chunk) < 0x4000;
	}

	public void Dispose()
	{
		this.freeHook?.Dispose();
		GC.SuppressFinalize(this);
	}
}
