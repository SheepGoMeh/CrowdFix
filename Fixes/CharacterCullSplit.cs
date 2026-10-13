using System;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Culling;

using Item = FFXIVClientStructs.FFXIV.Client.Graphics.Culling.CullingManager.RenderCallbackJobItem;

namespace CrowdFix.Fixes;

/// <summary>
/// Camera culling (CullingManager.RenderCallbackJobs) splits BG objects into items of 200, but puts every character
/// into a single item, so one thread culls and registers all characters while the others wait at the join. The job
/// function (ExecuteRenderCallbackJob) only reads its item (type, slot list, start, count), so the thread that gets the character item
/// works through it in small chunks and every thread that finishes its own item of the same group helps.
/// </summary>
public unsafe class CharacterCullSplit: IDisposable
{
	private const byte CharacterItem = 2;
	private const int Chunk = 16;

	private delegate long CullJobDelegate(CullingManager* cullingManager, Item* item);

	/// <summary>
	/// Shared by the owner and its helpers; one character item is in flight at a time (bf00 runs one view at a time
	/// and joins before returning).
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct Shared
	{
		public CullingManager* CullingManager;
		public Item Item;
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
				(nint)CullingManager.Addresses.ExecuteRenderCallbackJob.Value, this.CullJobDetour);
			this.shared = (Shared*)NativeMemory.AllocZeroed((nuint)sizeof(Shared));
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (function not resolved)";
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
	private long CullJobDetour(CullingManager* cullingManager, Item* item)
	{
		Shared* s = this.shared;
		if (item->Type == CharacterItem && item->Count > Chunk && Volatile.Read(ref s->Active) == 0)
		{
			s->Item = *item;
			s->CullingManager = cullingManager;
			s->Total = (int)item->Count;
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
		Item local = s->Item;
		uint start = local.Start;
		int total = s->Total;
		while (true)
		{
			int i = Interlocked.Add(ref s->Next, Chunk) - Chunk;
			if (i >= total)
				break;

			local.Start = start + (uint)i;
			local.Count = (uint)Math.Min(Chunk, total - i);
			this.cullJobHook!.Original(s->CullingManager, &local);
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
