using System;
using System.Runtime.InteropServices;

namespace CrowdFix.Fixes;

/// <summary>
/// The culling setup (FUN_14024af70, ~6 calls per frame) zeroes the whole per-object view visibility table
/// (CullingManager+0x20): 40,960 object slots x 16 bytes = 640 KB per call, however few objects exist.
/// This rewrites the loop count so only slots up to the highest object slot ever used (from the object bitmask at
/// CullingManager+0x18) are cleared. Slots above that mark have never been written, so they are still zero.
/// </summary>
public unsafe class CullingClearTrim: IDisposable
{
	// MOV ECX,0xA000; MOV R14,[RSP+..]; MOV R13,[RSP+..]; MOV R12,[RSP+..] (count of 16 byte stores)
	private const string ClearCountSignature = "B9 00 A0 00 00 4C 8B B4 24 ?? ?? ?? ?? 4C 8B AC 24 ?? ?? ?? ?? 4C 8B A4 24";
	// call to FUN_1402ba8f0, which starts with MOV RAX,[g_CullingManager]
	private const string CullingManagerSignature = "E8 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 83 60 ?? ?? E8";

	private const uint FullCount = 0xA000;
	private const int MaskWords = 0x500;
	private const int ObjectsPerWord = 32;

	[DllImport("kernel32.dll")] private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint oldProtect);
	[DllImport("kernel32.dll")] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
	[DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();

	private readonly uint* clearCount;
	private readonly nint* cullingManager;

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
			nint getter = Service.SigScanner.ScanText(CullingManagerSignature);
			this.cullingManager = (nint*)(getter + 7 + *(int*)(getter + 3));
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
		if (!this.enabled || *this.cullingManager == 0)
			return;

		uint* mask = *(uint**)(*this.cullingManager + 0x18);
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
