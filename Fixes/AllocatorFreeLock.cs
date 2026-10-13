using System;
using System.Runtime.InteropServices;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics;

namespace CrowdFix.Fixes;

/// <summary>
/// The graphics small-object allocator (AllocatorPool, behind AllocatorManager.TempAllocator) takes its lock before checking
/// whether a block came from its slabs, and for every other block calls the backing allocator's free while still
/// holding that lock. Draw building frees ~1,200 such staging blocks per frame from all job threads, so small
/// allocations queue behind backing frees. Here the slab check stays under the lock, but backing frees happen after
/// releasing it; slab blocks still go through the original Free.
/// Mostly relieves the job workers, so it matters on CPUs where the main thread ends up waiting for them.
/// </summary>
public unsafe class AllocatorFreeLock: IDisposable
{
	private static readonly int LockOffset = StructOffset.Of<AllocatorPool>("Lock");

	// AllocatorPool.Chunks is an array of 0x30 byte entries (not typed in ClientStructs): ChunkStride is the distance
	// between entries, ChunkBlockOffset where an entry keeps the address of its 16 KB block.
	private const int ChunkStride = 0x30;
	private const int ChunkBlockOffset = 0x10;
	private const int ChunkSize = 0x4000;
	// Each block is split into 1 KB pages; a page header holds the index of its chunk entry.
	private const nint PageMask = 0x3FF;
	private const int PageChunkIndexOffset = 0x1C;

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
				(nint)AllocatorPool.StaticVirtualTablePointer->Free, this.FreeDetour);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (function not resolved)";
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
		((AllocatorPool*)allocator)->Backing->Free((void*)block);
	}

	/// <summary>
	/// The original's membership test (page header at block &amp; ~PageMask). Only valid under the allocator lock, since
	/// the chunk table is reallocated when it grows.
	/// </summary>
	private static bool IsSlabBlock(nint allocator, nint block)
	{
		AllocatorPool* pool = (AllocatorPool*)allocator;
		nint page = block & ~PageMask;
		uint index = *(uint*)(page + PageChunkIndexOffset);
		if (index >= pool->ChunkCount)
			return false;

		nint chunk = *(nint*)((nint)pool->Chunks + (index * ChunkStride) + ChunkBlockOffset);
		return chunk != 0 && (ulong)(block - chunk) < ChunkSize;
	}

	public void Dispose()
	{
		this.freeHook?.Dispose();
		GC.SuppressFinalize(this);
	}
}
