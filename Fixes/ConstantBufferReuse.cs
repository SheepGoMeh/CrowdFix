using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace CrowdFix.Fixes;

/// <summary>
/// The game's constant buffer cache (sub_140238BC0) keeps 4 buffers per size and stage and matches on the data pointer. In
/// a 140 character crowd 63% of lookups miss and do Map DISCARD + copy + Unmap, yet 75% of those misses are a pointer already
/// uploaded that render frame, with the same contents. While on, every lookup is answered from a larger per-frame table of
/// our own buffers instead, so a repeated pointer only needs a compare. The game's 4 slots are never written meanwhile, so
/// they can't return old data. A hit always compares the contents and uploads again if they differ, so a reused pointer can't
/// draw with wrong constants even if the measured behaviour doesn't hold in another scene.
///
/// The optional ring goes further: uploads append to one large buffer with Map NO_OVERWRITE, which in DXVK is no allocation
/// and no queued command, and the buffer stays bound with only its offset changing. The game still holds one of our per-entry
/// buffers, which is then just a handle: the SetConstantBuffers hooks swap it for the ring and that entry's offset.
/// </summary>
public unsafe class ConstantBufferReuse: IDisposable
{
	// sub_140238BC0 cache layout, in the uint units it's declared with: buffers at 0, data pointers at 8, recently used slots at 16
	private const int SlotCount = 4;
	private const int SlotPointersIndex = 8;
	private const int RecentSlotsIndex = 16;
	private const int HitCounterIndex = 18;
	private const int MissCounterIndex = 19;

	// A frame normally needs about 6,000. Past this the table starts over, which only costs extra uploads.
	private const int MaxBuffers = 32768;

	// About 35,000 lookups per frame, so this is a flat table. Twice MaxBuffers keeps the probes short.
	private const int TableSize = MaxBuffers * 2;
	private const int TableShift = 64 - 16; // log2(TableSize)

	// COM vtable slots
	private const int QueryInterfaceSlot = 0;
	private const int ReleaseSlot = 2;
	private const int GetDeviceSlot = 3; // ID3D11DeviceChild
	private const int CreateBufferSlot = 3; // ID3D11Device
	private const int GetDescSlot = 10; // ID3D11Buffer
	private const int MapSlot = 14; // ID3D11DeviceContext
	private const int UnmapSlot = 15; // ID3D11DeviceContext

	private const int MapWriteDiscard = 4;
	private const int MapWriteNoOverwrite = 5;

	private const int StageCount = 6;
	private const int ConstantBufferSlots = 14;
	private const int MaxBindCount = ConstantBufferSlots;

	// ID3D11DeviceContext and ID3D11DeviceContext1 slots by stage: VS, PS, GS, HS, DS, CS
	private static readonly int[] SetSlots = [7, 16, 22, 62, 66, 71];
	private static readonly int[] SetRangeSlots = [119, 123, 122, 120, 121, 124];

	private static readonly Guid DeviceContext1Id = new("bb2c6faa-b5fb-4082-8e6b-388b8cfa90e1");

	private const uint RingBytes = 32 * 1024 * 1024;
	private const uint RingAlignment = 256; // offsets and sizes have to be whole multiples of 16 constants
	private const int RingUsageDynamic = 2;
	private const uint RingBindConstantBuffer = 4;
	private const uint RingCpuAccessWrite = 0x10000;

	private delegate void ProcessCommandsDelegate(nint context, nint commands, uint commandCount);
	private delegate nint ConstantBufferCacheDelegate(uint* cache, nint context, byte* source, nint* buffer);
	private delegate void SetConstantBuffersDelegate(nint context, uint start, uint count, nint* buffers);

	internal const string ConstantBufferCacheSignature = "E8 ?? ?? ?? ?? 48 8B 6C 24 ?? 88 44 37";

	[StructLayout(LayoutKind.Sequential)]
	private struct BufferDesc
	{
		public uint ByteWidth;
		public int Usage;
		public uint BindFlags;
		public uint CpuAccessFlags;
		public uint MiscFlags;
		public uint StructureByteStride;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MappedSubresource
	{
		public void* Data;
		public uint RowPitch;
		public uint DepthPitch;
	}

	// Entries live in one array and are referred to by index, 0 meaning none. That keeps the large tables free of references, so
	// the GC has nothing to scan in them and writing a slot needs no write barrier.
	private struct Entry
	{
		public nint Buffer;
		public byte* Contents;
		public uint Bytes;
		public uint Width;
		public uint RingOffset;
		public uint RingGeneration;
		public int Pool;
		public int NextFree;
	}

	private struct Slot
	{
		public nint Pointer;
		public uint Count;
		public uint Width;
		public uint Generation; // a slot from an older generation is empty, so starting over needs no clearing
		public int EntryIndex;
	}

	// Contents are carved from chunks of this size rather than allocated one by one.
	private const int ContentsChunkBytes = 1024 * 1024;

	// Render thread only. 256 entries by address >> 4 can't collide: the 48 caches are 80 bytes apart, so at most 240 apart here.
	private readonly nint[] cacheKeys = new nint[256];
	private readonly BufferDesc[] cacheDescs = new BufferDesc[256];
	private readonly int[] cachePools = new int[256];
	private readonly Slot[] table = new Slot[TableSize];
	private uint generation = 1;

	// One pool per buffer width, each a list through Entry.NextFree. There can't be more widths than caches.
	private readonly Dictionary<uint, int> poolsByWidth = [];
	private readonly int[] freeHeads = new int[256];
	private readonly Entry[] entries = new Entry[MaxBuffers + 1];
	private int entryCount;
	private readonly int[] usedEntries = new int[MaxBuffers];
	private int usedCount;
	private readonly List<nint> contentsChunks = [];
	private byte* contentsCursor;
	private uint contentsLeft;

	// Finds the entry behind a buffer the game passes to SetConstantBuffers. Entries are never removed.
	private readonly nint[] handleKeys = new nint[TableSize];
	private readonly int[] handleEntries = new int[TableSize];

	// What each stage and slot has bound through the ring. The game can keep a binding over several frames without setting it again.
	private readonly int[] boundEntries = new int[StageCount * ConstantBufferSlots];

	// The last ring binding of each slot, kept when something else is bound over it, so restoring the ring gets the right offset.
	private readonly int[] lastRingEntries = new int[StageCount * ConstantBufferSlots];
	private readonly Hook<SetConstantBuffersDelegate>?[] setHooks = new Hook<SetConstantBuffersDelegate>?[StageCount];
	private nint rangeContext;
	private nint ring;
	private uint ringCursor;
	private uint ringGeneration;
	private volatile bool ringActive;
	private volatile bool ringStopRequested;
	private bool ringUnsupported;
	private nint lastContext;

	// Written on the render thread, read on the framework thread after the hooks are off.
	private readonly List<nint> knownCaches = [];

	private readonly Hook<ConstantBufferCacheDelegate>? cacheHook;
	private readonly Hook<ProcessCommandsDelegate>? processCommandsHook;

	private long hits;
	private long uploads;
	private long changedContents;
	private long failures;
	private long ringWraps;
	private long lastHits;
	private long lastUploads;
	private long lastTimestamp;
	private long lastFrames;
	private long frames;

	public bool Available { get; }

	public bool Enabled { get; private set; }

	public string Status { get; private set; } = "Off";

	public ConstantBufferReuse()
	{
		try
		{
			// Hooked at load: RenderCounters hooks the same function later, so it stays outside this one and still sees every lookup.
			this.cacheHook = Service.GameInteropProvider.HookFromAddress<ConstantBufferCacheDelegate>(
				Service.SigScanner.ScanText(ConstantBufferCacheSignature), this.CacheDetour);
			this.processCommandsHook = Service.GameInteropProvider.HookFromAddress<ProcessCommandsDelegate>(
				(nint)ImmediateContext.Addresses.ProcessCommands.Value, this.ProcessCommandsDetour);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (signature not found)";
			Service.PluginLog.Warning(e, "ConstantBufferReuse unavailable");
		}
	}

	/// <summary>
	/// False while a change still has to finish, so the caller keeps calling SetEnabled.
	/// </summary>
	public bool IsSettled(bool enabled, bool useRing)
	{
		if (!this.Available)
			return true;

		bool wantRing = enabled && useRing && !this.ringUnsupported;
		return this.Enabled == enabled && this.ringActive == wantRing && !this.ringStopRequested;
	}

	/// <summary>
	/// Framework thread. Stopping the ring finishes on the render thread, which is the only one allowed to use the context, so
	/// this can take a few calls. The device context may not exist yet right after login, which also needs another call.
	/// </summary>
	public void SetEnabled(bool enabled, bool useRing)
	{
		if (!this.Available)
			return;

		bool wantRing = enabled && useRing && !this.ringUnsupported;
		if (!wantRing && this.ringActive)
		{
			this.ringStopRequested = true;
			return;
		}

		if (!wantRing && !this.ringStopRequested)
			this.SetRingHooks(false);

		if (enabled != this.Enabled)
		{
			this.Enabled = enabled;
			if (enabled)
			{
				this.lastTimestamp = Environment.TickCount64;
				this.lastHits = this.hits;
				this.lastUploads = this.uploads;
				this.lastFrames = this.frames;
				this.Status = "On, collecting";
				this.processCommandsHook!.Enable();
				this.cacheHook!.Enable();
			}
			else
			{
				this.cacheHook!.Disable();
				this.processCommandsHook!.Disable();
				this.ForgetGameSlots();
				this.Status = "Off";
			}

			Service.PluginLog.Information("ConstantBufferReuse: {State}", enabled ? "On" : "Off");
		}

		if (wantRing && !this.ringActive)
			this.StartRing();
	}

	/// <summary>
	/// For unloading, off the framework thread, so the render thread gets to run while this waits. The stop puts the data back in
	/// the game's handles, so it's worth waiting for, but the game must still be able to close if frames have stopped.
	/// </summary>
	public void StopRingAndWait()
	{
		if (!this.ringActive)
			return;

		this.ringStopRequested = true;
		long start = Stopwatch.GetTimestamp();
		while (this.ringActive && Stopwatch.GetElapsedTime(start).TotalMilliseconds < 500)
			Thread.Sleep(10);

		if (this.ringActive)
			Service.PluginLog.Warning("ConstantBufferReuse: ring still active after waiting");
	}

	/// <summary>
	/// Framework thread, once per frame.
	/// </summary>
	public void Update()
	{
		if (!this.Enabled)
			return;

		long now = Environment.TickCount64;
		if (now - this.lastTimestamp < 1000)
			return;

		long hitCount = this.hits;
		long uploadCount = this.uploads;
		long frameCount = this.frames;
		long passed = Math.Max(1, frameCount - this.lastFrames);
		this.Status =
			$"On, {(hitCount - this.lastHits) / passed} reused and {(uploadCount - this.lastUploads) / passed} uploaded per frame, {this.entryCount} buffers\n" +
			(this.ringActive ? $"Ring on, wrapped {this.ringWraps} times\n" : string.Empty) +
			$"Contents changed under a reused pointer: {this.changedContents} times, failed calls: {this.failures}";
		this.lastHits = hitCount;
		this.lastUploads = uploadCount;
		this.lastFrames = frameCount;
		this.lastTimestamp = now;
	}

	/// <summary>
	/// The game's slots went unused while this was on, so the data behind their pointers may be long gone. Without pointers they
	/// just miss and upload again. A render thread lookup racing with this at worst misses once.
	/// </summary>
	private void ForgetGameSlots()
	{
		lock (this.knownCaches)
		{
			foreach (nint cache in this.knownCaches)
			{
				nint* pointers = (nint*)((uint*)cache + SlotPointersIndex);
				for (int i = 0; i < SlotCount; i++)
					pointers[i] = 0;
			}
		}
	}

	/// <summary>
	/// Render thread, once per frame.
	/// </summary>
	private void ProcessCommandsDetour(nint context, nint commands, uint commandCount)
	{
		this.frames++;
		if (this.ringStopRequested)
			this.StopRing();

		this.StartOver();
		this.processCommandsHook!.Original(context, commands, commandCount);
	}

	private void StartOver()
	{
		this.generation++;
		for (int i = 0; i < this.usedCount; i++)
		{
			int index = this.usedEntries[i];
			ref Entry entry = ref this.entries[index];
			entry.NextFree = this.freeHeads[entry.Pool];
			this.freeHeads[entry.Pool] = index;
		}

		this.usedCount = 0;
	}

	private nint CacheDetour(uint* cache, nint context, byte* source, nint* buffer)
	{
		byte* data = *(byte**)source;
		uint count = *(uint*)(source + 8);
		uint bytes = count * 16;
		this.lastContext = context;

		int index = (int)(((nint)cache >> 4) & 255);
		if (this.cacheKeys[index] != (nint)cache && !this.LearnCache(cache, index))
			return this.Fail(cache, context, source, buffer);

		uint width = this.cacheDescs[index].ByteWidth;
		if (data == null || bytes > width)
			return this.Fail(cache, context, source, buffer);

		// The way the game's own hit and miss leave these: no change to which slot was used last.
		nint recentSlot = (nint)(cache[RecentSlotsIndex] & 7);
		int position = this.FindSlot((nint)data, count, width);
		int entryIndex = this.table[position].EntryIndex;
		if (this.table[position].Generation == this.generation)
		{
			ref Entry found = ref this.entries[entryIndex];
			if (new ReadOnlySpan<byte>(found.Contents, (int)bytes).SequenceEqual(new ReadOnlySpan<byte>(data, (int)bytes)))
			{
				this.hits++;
				cache[HitCounterIndex]++;
				*buffer = found.Buffer;
				return recentSlot;
			}

			this.changedContents++;
		}
		else
		{
			entryIndex = this.Acquire(context, index);
			if (entryIndex == 0)
				return this.Fail(cache, context, source, buffer);

			// Acquire can start the table over, which empties the slot found above and maybe earlier ones.
			position = this.FindSlot((nint)data, count, width);
			this.table[position] = new Slot { Pointer = (nint)data, Count = count, Width = width, Generation = this.generation, EntryIndex = entryIndex };
		}

		ref Entry entry = ref this.entries[entryIndex];
		Buffer.MemoryCopy(data, entry.Contents, bytes, bytes);
		entry.Bytes = bytes;
		if (!(this.ringActive ? this.WriteToRing(context, ref entry) : UploadToOwnBuffer(context, ref entry)))
		{
			// This can cut a probe chain short, which at worst uploads a pointer twice.
			this.table[position].Generation = 0;
			return this.Fail(cache, context, source, buffer);
		}

		this.uploads++;
		cache[MissCounterIndex]++;
		*buffer = entry.Buffer;
		return recentSlot;
	}

	/// <summary>
	/// The slot holding this key, or the empty slot it belongs in. The table is at most half full, so this always ends.
	/// </summary>
	private int FindSlot(nint pointer, uint count, uint width)
	{
		ulong mixed = (ulong)pointer ^ ((ulong)count << 40) ^ ((ulong)width << 52);
		int position = (int)((mixed * 0x9E3779B97F4A7C15) >> TableShift);
		while (true)
		{
			ref Slot slot = ref this.table[position];
			if (slot.Generation != this.generation || (slot.Pointer == pointer && slot.Count == count && slot.Width == width))
				return position;

			position = (position + 1) & (TableSize - 1);
		}
	}

	/// <summary>
	/// Only for when our buffers can't be used, which shouldn't happen. The game's own path is still correct on its own.
	/// </summary>
	private nint Fail(uint* cache, nint context, byte* source, nint* buffer)
	{
		this.failures++;
		return this.cacheHook!.Original(cache, context, source, buffer);
	}

	private bool LearnCache(uint* cache, int index)
	{
		nint gameBuffer = *(nint*)cache;
		if (gameBuffer == 0)
			return false;

		BufferDesc desc;
		((delegate* unmanaged<nint, BufferDesc*, void>)(*(nint**)gameBuffer)[GetDescSlot])(gameBuffer, &desc);
		this.cacheDescs[index] = desc;
		if (!this.poolsByWidth.TryGetValue(desc.ByteWidth, out int pool))
			this.poolsByWidth[desc.ByteWidth] = pool = this.poolsByWidth.Count;

		this.cachePools[index] = pool;
		this.cacheKeys[index] = (nint)cache;
		lock (this.knownCaches)
			this.knownCaches.Add((nint)cache);

		return true;
	}

	/// <summary>
	/// Each entry is either free or used once, so the used list can't outgrow MaxBuffers.
	/// </summary>
	private int Acquire(nint context, int index)
	{
		int pool = this.cachePools[index];
		if (this.freeHeads[pool] == 0 && this.entryCount >= MaxBuffers)
			this.StartOver();

		int entry = this.freeHeads[pool];
		if (entry != 0)
			this.freeHeads[pool] = this.entries[entry].NextFree;
		else if (this.entryCount >= MaxBuffers || (entry = this.Create(context, ref this.cacheDescs[index], pool)) == 0)
			return 0;

		this.usedEntries[this.usedCount++] = entry;
		return entry;
	}

	private int Create(nint context, ref BufferDesc desc, int pool)
	{
		BufferDesc copy = desc;
		nint created = CreateBuffer(context, &copy);
		if (created == 0)
			return 0;

		int index = ++this.entryCount;
		this.entries[index] = new Entry { Buffer = created, Width = desc.ByteWidth, Contents = this.AllocateContents(desc.ByteWidth), Pool = pool };

		int position = HandlePosition(created);
		while (this.handleKeys[position] != 0)
			position = (position + 1) & (TableSize - 1);

		this.handleEntries[position] = index;
		this.handleKeys[position] = created;
		return index;
	}

	private byte* AllocateContents(uint width)
	{
		width = (width + 15) & ~15u;
		if (width > this.contentsLeft)
		{
			uint size = Math.Max(width, ContentsChunkBytes);
			this.contentsCursor = (byte*)NativeMemory.AlignedAlloc(size, 16);
			this.contentsLeft = size;
			this.contentsChunks.Add((nint)this.contentsCursor);
		}

		byte* contents = this.contentsCursor;
		this.contentsCursor += width;
		this.contentsLeft -= width;
		return contents;
	}

	private static nint CreateBuffer(nint context, BufferDesc* desc)
	{
		nint device;
		((delegate* unmanaged<nint, nint*, void>)(*(nint**)context)[GetDeviceSlot])(context, &device);

		nint created = 0;
		int result = ((delegate* unmanaged<nint, BufferDesc*, void*, nint*, int>)(*(nint**)device)[CreateBufferSlot])(device, desc, null, &created);
		((delegate* unmanaged<nint, uint>)(*(nint**)device)[ReleaseSlot])(device);
		return result < 0 ? 0 : created;
	}

	private static bool UploadToOwnBuffer(nint context, ref Entry entry) =>
		Write(context, entry.Buffer, MapWriteDiscard, 0, entry.Contents, entry.Bytes);

	private static bool Write(nint context, nint buffer, int mapType, uint offset, byte* data, uint bytes)
	{
		nint* vtable = *(nint**)context;
		MappedSubresource mapped;
		if (((delegate* unmanaged<nint, nint, uint, int, uint, MappedSubresource*, int>)vtable[MapSlot])(context, buffer, 0, mapType, 0, &mapped) < 0)
			return false;

		Buffer.MemoryCopy(data, (byte*)mapped.Data + offset, bytes, bytes);
		((delegate* unmanaged<nint, nint, uint, void>)vtable[UnmapSlot])(context, buffer, 0);
		return true;
	}

	/// <summary>
	/// Framework thread. Hooked here rather than at load, because the context has to exist to find these functions.
	/// </summary>
	private void StartRing()
	{
		Device* device = Device.Instance();
		if (device == null || device->D3D11DeviceContext == null)
			return;

		nint context = (nint)device->D3D11DeviceContext;
		if (this.rangeContext == 0)
		{
			nint rangeInterface = 0;
			Guid id = DeviceContext1Id;
			int result = ((delegate* unmanaged<nint, Guid*, nint*, int>)(*(nint**)context)[QueryInterfaceSlot])(context, &id, &rangeInterface);
			if (result < 0 || rangeInterface == 0)
			{
				this.ringUnsupported = true;
				Service.PluginLog.Warning("ConstantBufferReuse: no ID3D11DeviceContext1, ring unavailable");
				return;
			}

			this.rangeContext = rangeInterface;
		}

		if (this.ring == 0)
		{
			var desc = new BufferDesc { ByteWidth = RingBytes, Usage = RingUsageDynamic, BindFlags = RingBindConstantBuffer, CpuAccessFlags = RingCpuAccessWrite };
			this.ring = CreateBuffer(context, &desc);
			if (this.ring == 0)
			{
				this.ringUnsupported = true;
				Service.PluginLog.Warning("ConstantBufferReuse: could not create the ring");
				return;
			}
		}

		if (this.setHooks[0] == null)
		{
			nint* vtable = *(nint**)context;
			for (int stage = 0; stage < StageCount; stage++)
			{
				int captured = stage;
				this.setHooks[stage] = Service.GameInteropProvider.HookFromAddress<SetConstantBuffersDelegate>(
					vtable[SetSlots[stage]], (c, start, count, buffers) => this.SetConstantBuffersDetour(captured, c, start, count, buffers));
			}
		}

		// Nothing has been written since the last wrap as far as a new start is concerned, so the first write discards.
		this.ringGeneration++;
		this.ringCursor = RingBytes;
		this.SetRingHooks(true);
		this.ringActive = true;
		Service.PluginLog.Information("ConstantBufferReuse: ring on");
	}

	private void SetRingHooks(bool enabled)
	{
		foreach (Hook<SetConstantBuffersDelegate>? hook in this.setHooks)
		{
			if (enabled)
				hook?.Enable();
			else
				hook?.Disable();
		}
	}

	/// <summary>
	/// Render thread. The game can pass one of its handles to D3D again at any later point, so each has to hold real data again
	/// before binds stop being swapped. Slots already bound to the ring stay valid: nothing writes to it from here on.
	/// </summary>
	private void StopRing()
	{
		this.ringActive = false;
		if (this.lastContext != 0)
		{
			for (int i = 1; i <= this.entryCount; i++)
			{
				ref Entry entry = ref this.entries[i];
				if (entry.Bytes != 0)
					UploadToOwnBuffer(this.lastContext, ref entry);
			}
		}

		Array.Clear(this.boundEntries);
		Array.Clear(this.lastRingEntries);
		this.ringStopRequested = false;
		Service.PluginLog.Information("ConstantBufferReuse: ring off");
	}

	/// <summary>
	/// Render thread. Appends the entry's contents. Running out of room discards the ring, which makes everything still bound
	/// point at new memory, so whatever is bound is written again right after.
	/// </summary>
	private bool WriteToRing(nint context, ref Entry entry)
	{
		uint size = (Math.Max(entry.Width, RingAlignment) + RingAlignment - 1) & ~(RingAlignment - 1);
		bool wrapped = this.ringCursor + size > RingBytes;
		if (wrapped)
		{
			this.ringCursor = 0;
			this.ringGeneration++;
			this.ringWraps++;
		}

		if (!Write(context, this.ring, wrapped ? MapWriteDiscard : MapWriteNoOverwrite, this.ringCursor, entry.Contents, entry.Bytes))
			return false;

		entry.RingOffset = this.ringCursor;
		entry.RingGeneration = this.ringGeneration;
		this.ringCursor += size;
		if (wrapped)
			this.RebindAfterWrap(context);

		return true;
	}

	private void RebindAfterWrap(nint context)
	{
		for (int stage = 0; stage < StageCount; stage++)
		{
			for (uint slot = 0; slot < ConstantBufferSlots; slot++)
			{
				int bound = this.boundEntries[(stage * ConstantBufferSlots) + slot];
				if (bound == 0)
					continue;

				ref Entry entry = ref this.entries[bound];
				if (entry.RingGeneration != this.ringGeneration && !this.WriteToRing(context, ref entry))
				{
					this.failures++;
					continue;
				}

				nint buffer = this.ring;
				uint first = entry.RingOffset / 16;
				uint constants = ConstantsOf(ref entry);
				this.SetRange(stage, slot, 1, &buffer, &first, &constants);
			}
		}
	}

	private static uint ConstantsOf(ref Entry entry) =>
		((Math.Max(entry.Width, RingAlignment) + RingAlignment - 1) & ~(RingAlignment - 1)) / 16;

	private void SetRange(int stage, uint start, uint count, nint* buffers, uint* first, uint* constants) =>
		((delegate* unmanaged<nint, uint, uint, nint*, uint*, uint*, void>)(*(nint**)this.rangeContext)[SetRangeSlots[stage]])(
			this.rangeContext, start, count, buffers, first, constants);

	private static int HandlePosition(nint buffer) =>
		(int)(((ulong)buffer * 0x9E3779B97F4A7C15) >> TableShift);

	private int FindEntry(nint buffer)
	{
		int position = HandlePosition(buffer);
		while (true)
		{
			nint key = this.handleKeys[position];
			if (key == buffer)
				return this.handleEntries[position];

			if (key == 0)
				return 0;

			position = (position + 1) & (TableSize - 1);
		}
	}

	/// <summary>
	/// Swaps our handles for the ring and their offsets. Any other buffer passes through untouched. The ring itself only arrives
	/// here from code that saved and restored the binding, like an overlay, so that gets the offset it had.
	/// </summary>
	private void SetConstantBuffersDetour(int stage, nint context, uint start, uint count, nint* buffers)
	{
		Hook<SetConstantBuffersDelegate> hook = this.setHooks[stage]!;
		if (!this.ringActive)
		{
			hook.Original(context, start, count, buffers);
			return;
		}

		int firstSlot = (stage * ConstantBufferSlots) + (int)start;
		if (buffers == null || count == 0 || count > MaxBindCount || start + count > ConstantBufferSlots)
		{
			if (start < ConstantBufferSlots)
				Array.Clear(this.boundEntries, firstSlot, (int)Math.Min(count, ConstantBufferSlots - start));

			hook.Original(context, start, count, buffers);
			return;
		}

		nint* ringBuffers = stackalloc nint[MaxBindCount];
		uint* firsts = stackalloc uint[MaxBindCount];
		uint* constants = stackalloc uint[MaxBindCount];
		int* indices = stackalloc int[MaxBindCount];
		int[] bound = this.boundEntries;

		// One pass: neighbouring slots of the same kind form a run, which is bound in one call as soon as the next slot differs.
		uint runStart = 0;
		bool runIsRing = false;
		uint runGeneration = 0;
		for (uint i = 0; ; i++)
		{
			bool isRing = false;
			if (i < count)
			{
				nint buffer = buffers[i];
				int index = buffer == this.ring ? this.lastRingEntries[firstSlot + i] : buffer == 0 ? 0 : this.FindEntry(buffer);

				// Marked bound before writing, so a wrap the write causes rebinds this entry rather than whatever the slot held before.
				bound[firstSlot + i] = index;
				if (index != 0 && this.entries[index].RingGeneration != this.ringGeneration && !this.WriteToRing(context, ref this.entries[index]))
				{
					this.failures++;
					bound[firstSlot + i] = index = 0;
				}

				isRing = index != 0;
				if (isRing)
				{
					ref Entry entry = ref this.entries[index];
					this.lastRingEntries[firstSlot + i] = index;
					indices[i] = index;
					ringBuffers[i] = this.ring;
					firsts[i] = entry.RingOffset / 16;
					constants[i] = ConstantsOf(ref entry);
				}
			}

			if (i == count || (i != runStart && isRing != runIsRing))
			{
				uint runCount = i - runStart;
				// A wrap since a ring run began moved its entries. The wrap wrote them again because they're marked bound.
				if (runCount != 0 && runIsRing && runGeneration != this.ringGeneration)
				{
					for (uint j = runStart; j < i; j++)
					{
						ref Entry entry = ref this.entries[indices[j]];
						firsts[j] = entry.RingOffset / 16;
						constants[j] = ConstantsOf(ref entry);
					}
				}

				if (runCount != 0 && runIsRing)
					this.SetRange(stage, start + runStart, runCount, ringBuffers + runStart, firsts + runStart, constants + runStart);
				else if (runCount != 0)
					hook.Original(context, start + runStart, runCount, buffers + runStart);

				if (i == count)
					return;

				runStart = i;
			}

			if (i == runStart)
				runGeneration = this.ringGeneration;

			runIsRing = isRing;
		}
	}

	public void Dispose()
	{
		this.cacheHook?.Dispose();
		this.processCommandsHook?.Dispose();
		foreach (Hook<SetConstantBuffersDelegate>? hook in this.setHooks)
			hook?.Dispose();

		// The buffers themselves are left alive on purpose: the game keeps raw pointers to what it bound last and can pass them
		// to D3D again later. That's a few MB until the game closes, and only if this was turned on.
		foreach (nint chunk in this.contentsChunks)
			NativeMemory.AlignedFree((void*)chunk);

		GC.SuppressFinalize(this);
	}
}
