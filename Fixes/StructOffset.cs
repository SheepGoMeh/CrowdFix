using System.Reflection;
using System.Runtime.InteropServices;

namespace CrowdFix.Fixes;

/// <summary>
/// Field offsets from ClientStructs' layout, for fields it keeps private and for vtable slots.
/// </summary>
internal static class StructOffset
{
	public static int Of<T>(string field) =>
		typeof(T).GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetCustomAttribute<FieldOffsetAttribute>()!.Value;

	public static int Slot<TVirtualTable>(string function) => Of<TVirtualTable>(function) / 8;
}
