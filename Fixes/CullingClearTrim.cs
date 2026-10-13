using System;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Client.Graphics.Culling;

namespace CrowdFix.Fixes;

/// <summary>
/// The culling setup (~6 calls per frame) zeroes the whole per-object view visibility table
/// (CullingManager.Visibility): 40,960 object slots x 16 bytes = 640 KB per call, however few objects exist.
/// This rewrites the loop count so only slots up to the highest object slot ever used (from
/// CullingManager.ObjectSlotMask) are cleared. Slots above that mark have never been written, so they are still zero.
/// </summary>
public unsafe class CullingClearTrim: IDisposable
{
	// The culling setup is not in ClientStructs.
	// MOV ECX,0xA000; MOV R14,[RSP+..]; MOV R13,[RSP+..]; MOV R12,[RSP+..] (count of 16 byte stores)
	private const string ClearCountSignature = "B9 00 A0 00 00 4C 8B B4 24 ?? ?? ?? ?? 4C 8B AC 24 ?? ?? ?? ?? 4C 8B A4 24";

	private const uint FullCount = 0xA000; // object slots, see CullingManager.Objects
	private const int ObjectsPerWord = 32; // bits per CullingManager.ObjectSlotMask word
	private const int MaskWords = (int)FullCount / ObjectsPerWord;

	[DllImport("kernel32.dll")] private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint oldProtect);
	[DllImport("kernel32.dll")] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
	[DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();

	private readonly uint* clearCount;

	private int highestWord = -1;
	private bool enabled;

	public bool Available { get; }

	public bool Enabled => this.enabled;

	public uint ClearedSlots => this.clearCount == null ? 0 : *this.clearCount;

	public string Status { get; private set; } = "Off";

	public CullingClearTrim()
	{
		try
		{
			this.clearCount = (uint*)(Service.SigScanner.ScanText(ClearCountSignature) + 1);
			this.Available = *this.clearCount == FullCount;
			if (!this.Available)
				this.Status = "Unavailable (unexpected clear count)";
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (signature not found)";
			Service.PluginLog.Warning(e, "CullingClearTrim unavailable");
		}
	}

	/// <summary>
	/// Framework thread, outside rendering: the culling setup only runs on the framework thread too.
	/// </summary>
	public void SetEnabled(bool enabled)
	{
		if (!this.Available || enabled == this.enabled)
			return;

		this.enabled = enabled;
		if (!enabled)
			WriteCount(this.clearCount, FullCount);

		this.Status = enabled ? "On" : "Off";
	}

	/// <summary>
	/// Framework thread, once per frame.
	/// </summary>
	public void Update()
	{
		CullingManager* manager = CullingManager.Instance();
		if (!this.enabled || manager == null)
			return;

		uint* mask = manager->ObjectSlotMask;
		if (mask == null)
			return;

		// High water mark: never shrinks, so a slot that ever held an object keeps getting cleared.
		for (int word = MaskWords - 1; word > this.highestWord; word--)
		{
			if (mask[word] != 0)
			{
				this.highestWord = word;
				break;
			}
		}

		// One spare word of margin for objects added later in the frame.
		uint count = (uint)Math.Min((this.highestWord + 2) * ObjectsPerWord, (int)FullCount);
		if (count != *this.clearCount)
			WriteCount(this.clearCount, count);
	}

	private static void WriteCount(uint* address, uint count)
	{
		VirtualProtect((nint)address, 4, 0x40, out uint oldProtect);
		*address = count;
		VirtualProtect((nint)address, 4, oldProtect, out _);
		FlushInstructionCache(GetCurrentProcess(), (nint)address, 4);
	}

	public void Dispose()
	{
		this.SetEnabled(false);
		GC.SuppressFinalize(this);
	}
}
