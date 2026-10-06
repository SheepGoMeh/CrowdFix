using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace CrowdFix.Fixes;

/// <summary>
/// Every dynamic buffer write during draw building (FUN_14021dac0) frees its previous staging block and allocates a
/// new one through the graphics allocator at AllocatorManager+0x10. Its Alloc/Free go to a small-object allocator
/// whose Free takes a global lock and, for larger blocks, calls the backing allocator (second global lock, coalescing
/// and a region re-sort) while still holding it, so the draw-building job threads spend about a third of their time
/// waiting on each other.
/// This keeps freed blocks of that allocator in power-of-two size buckets and hands them back out, so most
/// allocations never reach those locks. The allocator's vtable slots are swapped (they are tiny forwarding wrappers).
/// Every pooled block is a genuine allocation of that allocator: its size class comes from the allocator's own size
/// query, so blocks still held by the game stay valid for the original Free after the pool is turned off or unloaded.
/// </summary>
public unsafe class StagingPool: IDisposable
{
	// MOV RAX,[g_AllocatorManager]; ...; MOV RCX,[RAX+0x10]; MOV RAX,[RCX]; CALL [RAX+0x10] (in the buffer write lock)
	private const string ManagerSignature = "48 8B 05 ?? ?? ?? ?? 41 B8 ?? ?? ?? ?? 8B D7 48 8B 48 ?? 48 8B 01 FF 50 ?? 48 89 83";

	// Expected wrapper code: Alloc = MOV EAX,1; LOCK XADD [RCX+0x210],EAX; ...  Free = ADD RCX,0x90; MOV RAX,[RCX]; JMP [RAX+0x20]
	private static readonly byte[] AllocWrapperCode = [0xB8, 0x01, 0x00, 0x00, 0x00, 0xF0, 0x0F, 0xC1, 0x81, 0x10, 0x02, 0x00, 0x00];
	private static readonly byte[] FreeWrapperCode = [0x48, 0x81, 0xC1, 0x90, 0x00, 0x00, 0x00, 0x48, 0x8B, 0x01, 0x48, 0xFF, 0x60, 0x20];
	private static readonly byte[] SizeWrapperCode = [0x48, 0x81, 0xC1, 0x90, 0x00, 0x00, 0x00, 0x48, 0x8B, 0x01, 0x48, 0xFF, 0x60, 0x48];

	private const int TerminateSlot = 1;
	private const int AllocSlot = 2;
	private const int FreeSlot = 4;
	private const int SizeSlot = 9;
	private const int AllocCounterOffset = 0x210;
	private const int DirectMarkerOffset = 0x10;
	private const ushort DirectMarker = 0xFFFF;

	private const int MinClassShift = 6;   // 64 B
	private const int MaxClassShift = 16;  // 64 KB
	private const int ClassBudgetBytes = 2 * 1024 * 1024;
	private const ulong PoolAlignment = 0x10;

	private delegate void TerminateDelegate(nint allocator);
	private delegate nint AllocDelegate(nint allocator, ulong size, ulong alignment);
	private delegate void FreeDelegate(nint allocator, nint block);

	[DllImport("kernel32.dll")] private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint oldProtect);

	private readonly nint* allocatorManager;
	private readonly Bucket[] buckets = new Bucket[MaxClassShift - MinClassShift + 1];

	// kept alive for the lifetime of the swapped vtable entries
	private readonly TerminateDelegate terminateDetour;
	private readonly AllocDelegate allocDetour;
	private readonly FreeDelegate freeDetour;

	private nint* vtable;
	private nint originalTerminate;
	private nint originalAlloc;
	private nint originalFree;
	private nint blockSize;
	private nint target;

	public bool Available { get; }

	public bool Enabled => this.vtable != null;

	public int PooledBlocks
	{
		get
		{
			int count = 0;
			foreach (Bucket bucket in this.buckets)
				count += bucket.Count;
			return count;
		}
	}

	public string Status { get; private set; } = "Off";

	public StagingPool()
	{
		for (int i = 0; i < this.buckets.Length; i++)
			this.buckets[i] = new Bucket(Math.Max(16, ClassBudgetBytes >> (i + MinClassShift)));

		this.terminateDetour = this.TerminateDetour;
		this.allocDetour = this.AllocDetour;
		this.freeDetour = this.FreeDetour;

		try
		{
			nint manager = Service.SigScanner.ScanText(ManagerSignature);
			this.allocatorManager = (nint*)(manager + 7 + *(int*)(manager + 3));
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (signature not found)";
			Service.PluginLog.Warning(e, "StagingPool unavailable");
		}
	}

	/// <summary>
	/// Framework thread.
	/// </summary>
	public void SetEnabled(bool enabled)
	{
		if (!this.Available || enabled == this.Enabled)
			return;

		if (!enabled)
		{
			this.Restore();
			this.Drain();
			this.Status = "Off";
			return;
		}

		nint manager = *this.allocatorManager;
		nint allocator = manager == 0 ? 0 : *(nint*)(manager + 0x10);
		if (allocator == 0)
		{
			this.Status = "Waiting for the graphics allocator";
			return;
		}

		nint* table = *(nint**)allocator;
		if (!Matches(table[AllocSlot], AllocWrapperCode) || !Matches(table[FreeSlot], FreeWrapperCode) ||
		    !Matches(table[SizeSlot], SizeWrapperCode))
		{
			this.Status = "Unavailable (unexpected allocator layout)";
			return;
		}

		this.target = allocator;
		this.originalTerminate = table[TerminateSlot];
		this.originalAlloc = table[AllocSlot];
		this.originalFree = table[FreeSlot];
		this.blockSize = table[SizeSlot];
		this.vtable = table;
		WriteSlot(table, TerminateSlot, Marshal.GetFunctionPointerForDelegate(this.terminateDetour));
		WriteSlot(table, FreeSlot, Marshal.GetFunctionPointerForDelegate(this.freeDetour));
		WriteSlot(table, AllocSlot, Marshal.GetFunctionPointerForDelegate(this.allocDetour));
		this.Status = "On";
	}

	private static bool Matches(nint function, byte[] code)
	{
		for (int i = 0; i < code.Length; i++)
		{
			if (*(byte*)(function + i) != code[i])
				return false;
		}

		return true;
	}

	private static void WriteSlot(nint* table, int slot, nint function)
	{
		VirtualProtect((nint)(table + slot), 8, 0x04, out uint oldProtect);
		Volatile.Write(ref table[slot], function);
		VirtualProtect((nint)(table + slot), 8, oldProtect, out _);
	}

	private nint AllocDetour(nint allocator, ulong size, ulong alignment)
	{
		delegate* unmanaged<nint, ulong, ulong, nint> original = (delegate* unmanaged<nint, ulong, ulong, nint>)this.originalAlloc;
		if (allocator != this.target || alignment > PoolAlignment || size > (1UL << MaxClassShift))
			return original(allocator, size, alignment);

		int sizeClass = Math.Max(MinClassShift, BitOperations.Log2(BitOperations.RoundUpToPowerOf2(Math.Max(size, 1)))) - MinClassShift;
		if (this.buckets[sizeClass].TryPop(out nint block))
		{
			Interlocked.Increment(ref *(int*)(allocator + AllocCounterOffset)); // as the original wrapper does
			return block;
		}

		// Round misses up to the class size so the block comes back to the same class when freed.
		return original(allocator, 1UL << (sizeClass + MinClassShift), PoolAlignment);
	}

	private void FreeDetour(nint allocator, nint block)
	{
		// Blocks the backing allocator handed out directly (marker 0xFFFF at -0x10) have no size in their header.
		if (block != 0 && allocator == this.target && (block & ((nint)PoolAlignment - 1)) == 0 &&
		    *(ushort*)(block - DirectMarkerOffset) != DirectMarker)
		{
			// The allocator's own size query: slab element size from the page header, or the requested size from the
			// block header. A block goes to the largest class it can hold; past twice the top class it is left alone.
			ulong size = ((delegate* unmanaged<nint, nint, ulong>)this.blockSize)(allocator, block);
			if (size >= (1UL << MinClassShift) && size < (2UL << MaxClassShift))
			{
				int sizeClass = Math.Min(BitOperations.Log2(size), MaxClassShift) - MinClassShift;
				if (this.buckets[sizeClass].TryPush(block))
					return;
			}
		}

		((delegate* unmanaged<nint, nint, void>)this.originalFree)(allocator, block);
	}

	private void TerminateDetour(nint allocator)
	{
		if (allocator == this.target)
		{
			// Everything is about to be released wholesale; forget pooled blocks instead of freeing them.
			this.target = 0;
			foreach (Bucket bucket in this.buckets)
				bucket.Clear();
			this.Status = "Off (allocator terminated)";
		}

		((delegate* unmanaged<nint, void>)this.originalTerminate)(allocator);
	}

	private void Restore()
	{
		if (this.vtable == null)
			return;

		WriteSlot(this.vtable, AllocSlot, this.originalAlloc);
		WriteSlot(this.vtable, FreeSlot, this.originalFree);
		WriteSlot(this.vtable, TerminateSlot, this.originalTerminate);
		this.vtable = null;
	}

	private void Drain()
	{
		nint allocator = this.target;
		this.target = 0;
		foreach (Bucket bucket in this.buckets)
		{
			while (bucket.TryPop(out nint block))
			{
				if (allocator != 0)
					((delegate* unmanaged<nint, nint, void>)this.originalFree)(allocator, block);
			}
		}

		// Blocks still held by the game are genuine allocations and are freed normally later.
	}

	public void Dispose()
	{
		this.SetEnabled(false);
		GC.SuppressFinalize(this);
	}

	private class Bucket(int capacity)
	{
		private readonly nint[] blocks = new nint[capacity];
		private int count;

		public int Count => this.count;

		public bool TryPush(nint block)
		{
			lock (this.blocks)
			{
				if (this.count == this.blocks.Length)
					return false;

				this.blocks[this.count++] = block;
				return true;
			}
		}

		public bool TryPop(out nint block)
		{
			lock (this.blocks)
			{
				if (this.count == 0)
				{
					block = 0;
					return false;
				}

				block = this.blocks[--this.count];
				return true;
			}
		}

		public void Clear()
		{
			lock (this.blocks)
				this.count = 0;
		}
	}
}
