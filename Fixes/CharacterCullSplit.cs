using System;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Hooking;

namespace CrowdFix.Fixes;

/// <summary>
/// Camera culling (FUN_14024bf00) splits BG objects into jobs of 200, but puts every character into a single job,
/// so one thread culls and registers all characters while the others wait at the join. The job function
/// (FUN_14024e860) only reads its item (type, index list, start, count), so the thread that gets the character item
/// works through it in small chunks and every thread that finishes its own item of the same group helps.
/// </summary>
public unsafe class CharacterCullSplit: IDisposable
{
	private const string CullJobSignature = "40 53 56 41 54 41 55 41 56 48 81 EC 90 00 00 00";
	private const byte CharacterItem = 2;
	private const int ItemSize = 0x40;
	private const int StartOffset = 0x30;
	private const int CountOffset = 0x34;
	private const int Chunk = 16;

	private delegate long CullJobDelegate(nint cullingManager, byte* item);

	/// <summary>
	/// Shared by the owner and its helpers; one character item is in flight at a time (bf00 runs one view at a time
	/// and joins before returning).
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct Shared
	{
		public nint CullingManager;
		public fixed byte Item[ItemSize];
		public int Total;
		public int Next;
		public int Active;
	}

	private readonly Hook<CullJobDelegate>? cullJobHook;
	private readonly Shared* shared;

	public bool Available { get; }

	public bool Enabled => this.cullJobHook?.IsEnabled ?? false;

	public string Status { get; private set; } = "Off";

	public CharacterCullSplit()
	{
		try
		{
			this.cullJobHook = Service.GameInteropProvider.HookFromAddress<CullJobDelegate>(
				Service.SigScanner.ScanText(CullJobSignature), this.CullJobDetour);
			this.shared = (Shared*)NativeMemory.AllocZeroed((nuint)sizeof(Shared));
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (signature not found)";
			Service.PluginLog.Warning(e, "CharacterCullSplit unavailable");
		}
	}

	/// <summary>
	/// Framework thread. An owner already inside the detour keeps claiming until every chunk is done, so disabling
	/// mid-frame only stops new help.
	/// </summary>
	public void SetEnabled(bool enabled)
	{
		if (!this.Available || enabled == this.Enabled)
			return;

		if (enabled)
			this.cullJobHook!.Enable();
		else
			this.cullJobHook!.Disable();

		this.Status = enabled ? "On" : "Off";
	}

	/// <summary>
	/// Job workers and the main thread's help loop.
	/// </summary>
	private long CullJobDetour(nint cullingManager, byte* item)
	{
		Shared* s = this.shared;
		if (item[0] == CharacterItem && *(uint*)(item + CountOffset) > Chunk && Volatile.Read(ref s->Active) == 0)
		{
			Buffer.MemoryCopy(item, s->Item, ItemSize, ItemSize);
			s->CullingManager = cullingManager;
			s->Total = (int)*(uint*)(item + CountOffset);
			s->Next = 0;
			Volatile.Write(ref s->Active, 1);
			this.Help(s);
			Volatile.Write(ref s->Active, 0);
			return 0;
		}

		long result = this.cullJobHook!.Original(cullingManager, item);
		if (Volatile.Read(ref s->Active) != 0)
			this.Help(s);

		return result;
	}

	private void Help(Shared* s)
	{
		byte* local = stackalloc byte[ItemSize];
		Buffer.MemoryCopy(s->Item, local, ItemSize, ItemSize);
		uint start = *(uint*)(local + StartOffset);
		int total = s->Total;
		while (true)
		{
			int i = Interlocked.Add(ref s->Next, Chunk) - Chunk;
			if (i >= total)
				break;

			*(uint*)(local + StartOffset) = start + (uint)i;
			*(uint*)(local + CountOffset) = (uint)Math.Min(Chunk, total - i);
			this.cullJobHook!.Original(s->CullingManager, local);
		}
	}

	public void Dispose()
	{
		this.cullJobHook?.Dispose();
		if (this.shared != null)
			NativeMemory.Free(this.shared);

		GC.SuppressFinalize(this);
	}
}
