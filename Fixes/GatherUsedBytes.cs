using System;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace CrowdFix.Fixes;

/// <summary>
/// DeviceDX11.PostTick gathers every context's command list for each of the 87 lists (Device.GatherCommands ->
/// Context.GatherCommands): it copies every 16 KB block twice (once to a scratch area, once back) and merge sorts the
/// entries, ~1300 calls and a few MB of copying per frame. This copies only the used entries, skips the sort when they
/// are already in key order (the sort is stable, so it would leave them unchanged), and otherwise calls the game's
/// merge sort over the whole range, which splits and merges exactly like the gather's inlined top level. The result is
/// byte for byte what the game produces.
/// </summary>
public unsafe class GatherUsedBytes: IDisposable
{
	private const int ListsOffset = 0x18; // per list: first block, write pointer, u32 free slots, u32 blocks
	private const int ListSize = 24;
	private const int BlockSize = 0x4000;
	private const int EntriesPerBlock = 1024;
	private const int NextBlockOffset = 0x3FF0; // link entry in the last slot of a full block
	private const int EntrySize = 16; // u32 sort key, 4 bytes, command pointer
	private const int SortCall = 0x147; // CALL merge sort inside Context.GatherCommands

	private delegate ulong DriverDelegate(nint device, uint list, byte** cursor, uint* remaining, byte** results, uint* counts, uint* total);

	private readonly Hook<DriverDelegate>? driverHook;
	private readonly delegate* unmanaged<byte*, byte*, int, int, void> sort;

	public bool Available { get; }

	public bool Enabled => this.driverHook?.IsEnabled ?? false;

	public string Status { get; private set; } = "Off";

	public GatherUsedBytes()
	{
		try
		{
			nint call = (nint)Context.Addresses.GatherCommands.Value + SortCall;
			if (*(byte*)call != 0xE8)
			{
				this.Status = "Unavailable (unexpected code)";
				return;
			}

			// sort(out, in, first, last): both buffers must hold the entries
			this.sort = (delegate* unmanaged<byte*, byte*, int, int, void>)(call + 5 + *(int*)(call + 1));
			this.driverHook = Service.GameInteropProvider.HookFromAddress<DriverDelegate>(
				(nint)Device.Addresses.GatherCommands.Value, this.DriverDetour);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (function not resolved)";
			Service.PluginLog.Warning(e, "GatherUsedBytes unavailable");
		}
	}

	/// <summary>
	/// Framework thread. Output is identical either way, so a toggle in the middle of PostTick is harmless.
	/// </summary>
	public void SetEnabled(bool enabled)
	{
		if (!this.Available || enabled == this.Enabled)
			return;

		if (enabled)
			this.driverHook!.Enable();
		else
			this.driverHook!.Disable();

		this.Status = enabled ? "On" : "Off";
	}

	/// <summary>
	/// Main thread, PostTick, after every producer has been joined.
	/// </summary>
	private ulong DriverDetour(nint device, uint list, byte** cursor, uint* remaining, byte** results, uint* counts, uint* total)
	{
		if (*cursor == null)
			return this.driverHook!.Original(device, list, cursor, remaining, results, counts, total);

		uint contexts = ((Device*)device)->ContextCount;
		nint contextArray = (nint)((Device*)device)->ContextArray;
		*total = 0;
		for (uint i = 0; i < contexts; i++)
		{
			nint context = contextArray + (nint)(i * Context.StructSize);
			byte* descriptor = (byte*)(context + ListsOffset + (list * ListSize));
			uint blocks = *(uint*)(descriptor + 0x14);
			counts[i] = 0;
			if (blocks == 0)
			{
				results[i] = null;
				continue;
			}

			uint count = (blocks * EntriesPerBlock) - *(uint*)(descriptor + 0x10);
			byte* destination = *cursor;
			byte* block = *(byte**)descriptor;
			byte* output = destination;
			for (uint b = 1; b < blocks; b++)
			{
				Buffer.MemoryCopy(block, output, BlockSize, BlockSize);
				block = *(byte**)(block + NextBlockOffset);
				output += BlockSize;
			}

			long tail = ((long)count * EntrySize) - (output - destination);
			Buffer.MemoryCopy(block, output, tail, tail);

			if (!IsSorted(destination, count))
			{
				// same scratch area the stock gather uses, right after this context's reserved blocks
				byte* scratch = destination + ((long)blocks * BlockSize);
				Buffer.MemoryCopy(destination, scratch, (long)count * EntrySize, (long)count * EntrySize);
				this.sort(destination, scratch, 0, (int)count - 1);
			}

			counts[i] = count;
			results[i] = destination;
			*total += count;
			*cursor = destination + ((long)count * EntrySize);
			*remaining -= count * EntrySize;
		}

		return contexts;
	}

	private static bool IsSorted(byte* entries, uint count)
	{
		uint previous = *(uint*)entries;
		for (uint i = 1; i < count; i++)
		{
			uint key = *(uint*)(entries + ((long)i * EntrySize));
			if (key < previous)
				return false;

			previous = key;
		}

		return true;
	}

	public void Dispose()
	{
		this.driverHook?.Dispose();
		GC.SuppressFinalize(this);
	}
}
