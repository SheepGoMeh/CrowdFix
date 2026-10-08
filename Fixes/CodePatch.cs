using System.Runtime.InteropServices;

namespace CrowdFix.Fixes;

/// <summary>
/// Writes a 2 byte code patch: one store inside a cache line, so other threads run either the old or the new bytes.
/// </summary>
public static unsafe class CodePatch
{
	[DllImport("kernel32.dll")] private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint oldProtect);
	[DllImport("kernel32.dll")] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
	[DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();

	public static void Write(ushort* address, ushort value)
	{
		VirtualProtect((nint)address, 2, 0x40, out uint oldProtect);
		*address = value;
		VirtualProtect((nint)address, 2, oldProtect, out _);
		FlushInstructionCache(GetCurrentProcess(), (nint)address, 2);
	}
}
