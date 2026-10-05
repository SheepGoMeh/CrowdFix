using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Dalamud.Hooking;

namespace CrowdFix.Fixes;

/// <summary>
/// DeviceDX11.PostTick walks every Kernel::Notifier twice per frame (vtbl+0x10 before Present, vtbl+0x08 after
/// kicking the render thread). Every GPU resource is linked into that list for device events, but only CPU mapped
/// buffers do per frame work, so a crowd turns into ~50k cache missing calls that return immediately.
/// This keeps a set of the notifiers that can do work and patches both loops to walk only that set.
/// </summary>
public unsafe class IdleNotifierFilter: IDisposable
{
	// LEA RCX,[lock]; CALL [EnterCriticalSection]; MOV RBX,[head]; TEST; JZ; MOV RAX,[RBX]; MOV RCX,RBX; CALL [RAX+0x10]
	private const string ListSignature = "48 8D 0D ?? ?? ?? ?? FF 15 ?? ?? ?? ?? 48 8B 1D ?? ?? ?? ?? 48 85 DB 74 ?? 48 8B 03 48 8B CB FF 50 10";
	private const string LinkSignature = "E8 ?? ?? ?? ?? 41 0F B6 C5 E9";
	private const string UnlinkSignature = "E8 ?? ?? ?? ?? 45 33 F6 48 8D 5E";

	// PostTick loops: MOV RBX,[head]; TEST; JZ; MOV RAX,[RBX]; MOV RCX,RBX; CALL [RAX+off]; MOV RBX,[RBX+0x10]; TEST; JNZ
	private const string PrePresentLoopSignature = "48 8B 1D ?? ?? ?? ?? 48 85 DB 74 12 48 8B 03 48 8B CB FF 50 10 48 8B 5B 10 48 85 DB 75 EE";
	private const string PostKickLoopSignature = "48 8B 1D ?? ?? ?? ?? 48 85 DB 74 14 66 90 48 8B 03 48 8B CB FF 50 08 48 8B 5B 10 48 85 DB 75 EE";
	private const int PrePresentLoopLength = 0x1E;
	private const int PostKickLoopLength = 0x20;

	private static readonly TimeSpan VerifyInterval = TimeSpan.FromSeconds(10);

	private delegate void NodeDelegate(nint node);
	private delegate void LoopDelegate();

	[DllImport("kernel32.dll")] private static extern void EnterCriticalSection(nint criticalSection);
	[DllImport("kernel32.dll")] private static extern void LeaveCriticalSection(nint criticalSection);
	[DllImport("kernel32.dll")] private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint oldProtect);
	[DllImport("kernel32.dll")] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
	[DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();

	// Read by the loop replacements while PostTick holds the notifier lock; only changed under that lock.
	private static nint[] active = new nint[256];
	private static int activeCount;
	private static readonly Dictionary<nint, int> activeIndex = new();

	// Delegates rather than [UnmanagedCallersOnly]: plugins live in a collectible load context.
	private static readonly LoopDelegate CallActivePrePresent = () => CallActive(0x10);
	private static readonly LoopDelegate CallActivePostKick = () => CallActive(0x08);

	private readonly nint lockAddress;
	private readonly nint headAddress;
	private readonly nint moduleBase;
	private readonly nint prePresentLoop;
	private readonly nint postKickLoop;
	private readonly Hook<NodeDelegate>? linkHook;
	private readonly Hook<NodeDelegate>? unlinkHook;
	private readonly Stopwatch verifyTimer = new();

	private byte[]? prePresentOriginal;
	private byte[]? postKickOriginal;

	public bool Available { get; }

	public bool Enabled => this.prePresentOriginal != null;

	public int ActiveCount => activeCount;

	public string Status { get; private set; } = "Off";

	public IdleNotifierFilter()
	{
		try
		{
			nint list = Service.SigScanner.ScanText(ListSignature);
			this.lockAddress = list + 7 + Marshal.ReadInt32(list, 3);
			this.headAddress = list + 20 + Marshal.ReadInt32(list, 16);
			this.moduleBase = Service.SigScanner.Module.BaseAddress;
			this.prePresentLoop = Service.SigScanner.ScanText(PrePresentLoopSignature);
			this.postKickLoop = Service.SigScanner.ScanText(PostKickLoopSignature);
			this.linkHook = Service.GameInteropProvider.HookFromAddress<NodeDelegate>(
				Service.SigScanner.ScanText(LinkSignature), this.LinkDetour);
			this.unlinkHook = Service.GameInteropProvider.HookFromAddress<NodeDelegate>(
				Service.SigScanner.ScanText(UnlinkSignature), this.UnlinkDetour);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (signature not found)";
			Service.PluginLog.Warning(e, "IdleNotifierFilter unavailable");
		}
	}

	/// <summary>
	/// Must run on the framework thread: the UI draws inside PostTick's Present, so patching from there would
	/// rewrite the function that is currently running.
	/// </summary>
	public void SetEnabled(bool enabled)
	{
		if (!this.Available || enabled == this.Enabled)
			return;

		if (enabled)
			this.Enable();
		else
			this.Disable("Off");
	}

	/// <summary>
	/// Framework thread. Periodically re-walks the whole list and drops the patch if the set ever drifted.
	/// </summary>
	public void Update()
	{
		if (!this.Enabled || this.verifyTimer.Elapsed < VerifyInterval)
			return;

		this.verifyTimer.Restart();
		int missing = 0;
		int stale = 0;

		EnterCriticalSection(this.lockAddress);
		try
		{
			HashSet<nint> linked = [];
			for (nint node = *(nint*)this.headAddress; node != 0; node = *(nint*)(node + 0x10))
			{
				linked.Add(node);
				if (!activeIndex.ContainsKey(node) && this.CanDoWork(node))
				{
					Add(node);
					missing++;
				}
			}

			for (int i = activeCount - 1; i >= 0; i--)
			{
				if (!linked.Contains(active[i]))
				{
					Remove(active[i]);
					stale++;
				}
			}
		}
		finally
		{
			LeaveCriticalSection(this.lockAddress);
		}

		if (missing > 0 || stale > 0)
		{
			this.Disable($"Disabled: verification found {missing} missing and {stale} stale notifiers");
			Service.PluginLog.Warning(this.Status);
		}
	}

	private void Enable()
	{
		EnterCriticalSection(this.lockAddress);
		try
		{
			activeIndex.Clear();
			activeCount = 0;
			for (nint node = *(nint*)this.headAddress; node != 0; node = *(nint*)(node + 0x10))
			{
				if (this.CanDoWork(node))
					Add(node);
			}

			this.linkHook!.Enable();
			this.unlinkHook!.Enable();
		}
		finally
		{
			LeaveCriticalSection(this.lockAddress);
		}

		this.prePresentOriginal = WriteCall(this.prePresentLoop, PrePresentLoopLength, Marshal.GetFunctionPointerForDelegate(CallActivePrePresent));
		this.postKickOriginal = WriteCall(this.postKickLoop, PostKickLoopLength, Marshal.GetFunctionPointerForDelegate(CallActivePostKick));
		this.verifyTimer.Restart();
		this.Status = "On";
	}

	private void Disable(string status)
	{
		if (this.prePresentOriginal != null)
			Write(this.prePresentLoop, this.prePresentOriginal);
		if (this.postKickOriginal != null)
			Write(this.postKickLoop, this.postKickOriginal);

		this.prePresentOriginal = null;
		this.postKickOriginal = null;
		this.linkHook?.Disable();
		this.unlinkHook?.Disable();
		this.Status = status;
	}

	private bool CanDoWork(nint node)
	{
		nint vtable = *(nint*)node;
		if (IsBareReturn(*(nint*)(vtable + 0x08)) && IsBareReturn(*(nint*)(vtable + 0x10)))
			return false;

		uint flags;
		switch ((long)(vtable - this.moduleBase))
		{
			case 0x21429B0: // buffer (FUN_14021e080)
			case 0x2142A70: // VertexBuffer
				return (*(uint*)(node + 0x1C) & 0x11) != 0;
			case 0x2142B30: // IndexBuffer
				flags = *(uint*)(node + 0x20);
				return (flags & 0x11) != 0 && (flags & 0x40) == 0;
			case 0x2142598: // TextureDx11
				flags = *(uint*)(node + 0x3C);
				return (flags & 0x100010) == 0x100010 || (flags & 0x2000) != 0;
			case 0x2142BD8: // ConstantBuffer
				return (*(uint*)(node - 0x14) & 0x4000) != 0;
			default: // unknown class, always visit
				return true;
		}
	}

	private static bool IsBareReturn(nint function)
	{
		byte* code = (byte*)function;
		return code[0] == 0xC3 || (code[0] == 0xC2 && code[1] == 0 && code[2] == 0);
	}

	private static void Add(nint node)
	{
		if (activeIndex.ContainsKey(node))
			return;

		if (activeCount == active.Length)
			Array.Resize(ref active, active.Length * 2);

		activeIndex[node] = activeCount;
		active[activeCount++] = node;
	}

	private static void Remove(nint node)
	{
		if (!activeIndex.Remove(node, out int index))
			return;

		nint last = active[--activeCount];
		if (index != activeCount)
		{
			active[index] = last;
			activeIndex[last] = index;
		}
	}

	private void LinkDetour(nint node)
	{
		EnterCriticalSection(this.lockAddress);
		try
		{
			this.linkHook!.Original(node);
			if (this.CanDoWork(node))
				Add(node);
		}
		finally
		{
			LeaveCriticalSection(this.lockAddress);
		}
	}

	private void UnlinkDetour(nint node)
	{
		EnterCriticalSection(this.lockAddress);
		try
		{
			// Before the original, so PostTick can never reach a node that is being destroyed.
			Remove(node);
			this.unlinkHook!.Original(node);
		}
		finally
		{
			LeaveCriticalSection(this.lockAddress);
		}
	}

	private static void CallActive(int slot)
	{
		int count = activeCount;
		Span<nint> snapshot = count <= 1024 ? stackalloc nint[count] : new nint[count];
		active.AsSpan(0, count).CopyTo(snapshot); // a callback may link or unlink notifiers

		foreach (nint node in snapshot)
		{
			nint function = *(nint*)(*(nint*)node + slot);
			((delegate* unmanaged<nint, void>)function)(node);
		}
	} 

	// MOV RAX, imm64; CALL RAX; JMP rel8 to the end of the original loop
	private static byte[] WriteCall(nint site, int length, nint target)
	{
		byte[] original = new byte[length];
		Marshal.Copy(site, original, 0, length);

		byte[] code = new byte[14];
		code[0] = 0x48;
		code[1] = 0xB8;
		BitConverter.GetBytes((long)target).CopyTo(code, 2);
		code[10] = 0xFF;
		code[11] = 0xD0;
		code[12] = 0xEB;
		code[13] = (byte)(length - 14);
		Write(site, code);
		return original;
	}

	private static void Write(nint address, byte[] bytes)
	{
		VirtualProtect(address, (nuint)bytes.Length, 0x40, out uint oldProtect);
		Marshal.Copy(bytes, 0, address, bytes.Length);
		VirtualProtect(address, (nuint)bytes.Length, oldProtect, out _);
		FlushInstructionCache(GetCurrentProcess(), address, (nuint)bytes.Length);
	}

	/// <summary>
	/// Must run on the framework thread.
	/// </summary>
	public void Dispose()
	{
		this.Disable("Off");
		this.linkHook?.Dispose();
		this.unlinkHook?.Dispose();
		GC.SuppressFinalize(this);
	}
}
