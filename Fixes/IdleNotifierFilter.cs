using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace CrowdFix.Fixes;

/// <summary>
/// DeviceDX11.PostTick walks every Kernel::Notifier twice per frame (EndFrame before Present, BeginFrame after
/// kicking the render thread). Every GPU resource is linked into that list for device events, but only CPU mapped
/// buffers do per frame work, so a crowd turns into ~50k cache missing calls that return immediately.
/// This keeps a set of the notifiers that can do work and patches both loops to walk only that set.
/// </summary>
public unsafe class IdleNotifierFilter: IDisposable
{
	private const int ListLockOffset = 0x18; // Notifier_ListLock follows Notifier_ListHead (ida/data.yml)
	private static readonly int TextureNotifierOffset = StructOffset.Of<Texture>(nameof(Texture.Notifier));
	private static readonly int BeginFrameOffset = StructOffset.Of<Notifier.NotifierVirtualTable>(nameof(Notifier.BeginFrame));
	private static readonly int EndFrameOffset = StructOffset.Of<Notifier.NotifierVirtualTable>(nameof(Notifier.EndFrame));

	// DeviceDX11.PostTick is not in ClientStructs.
	// PostTick loops: MOV RBX,[head]; TEST; JZ; MOV RAX,[RBX]; MOV RCX,RBX; CALL [RAX+off]; MOV RBX,[RBX+0x10]; TEST; JNZ
	private const string PrePresentLoopSignature = "48 8B 1D ?? ?? ?? ?? 48 85 DB 74 12 48 8B 03 48 8B CB FF 50 10 48 8B 5B 10 48 85 DB 75 EE";
	private const string PostKickLoopSignature = "48 8B 1D ?? ?? ?? ?? 48 85 DB 74 14 66 90 48 8B 03 48 8B CB FF 50 08 48 8B 5B 10 48 85 DB 75 EE";
	private static readonly int PrePresentLoopLength = PatternLength(PrePresentLoopSignature);
	private static readonly int PostKickLoopLength = PatternLength(PostKickLoopSignature);

	private enum WorkTest
	{
		BufferFlags,
		IndexBufferFlags,
		TextureMappedFlags,
		TextureMappedOrUploadFlags,
		ConstantBufferFlags,
	}

	// Notifier callbacks up to the flag test that gates their work
	private static readonly (string Name, string Signature, WorkTest Test)[] CallbackSignatures =
	[
		("buffer BeginFrame", "40 53 48 83 EC ?? 8B 05 ?? ?? ?? ?? 48 8B D9 48 8B 54 C1 20 48 85 D2 74 ?? 8B 49 1C F6 C1 11 74", WorkTest.BufferFlags), // also VertexBuffer
		("buffer EndFrame", "40 53 48 83 EC ?? 8B 05 ?? ?? ?? ?? 48 8B D9 48 8B 54 C1 20 48 85 D2 74 ?? F6 41 1C 11 74", WorkTest.BufferFlags), // also VertexBuffer
		("IndexBuffer BeginFrame", "40 53 48 83 EC ?? 8B 05 ?? ?? ?? ?? 48 8B D9 4C 8B 54 C1 28 4D 85 D2 74 ?? 8B 51 20 F6 C2 11 74 ?? F6 C2 40 75", WorkTest.IndexBufferFlags),
		("IndexBuffer EndFrame", "40 53 48 83 EC ?? 8B 05 ?? ?? ?? ?? 48 8B D9 48 8B 54 C1 28 48 85 D2 74 ?? 8B 41 20 A8 11 74 ?? A8 40 75", WorkTest.IndexBufferFlags),
		("TextureDx11 BeginFrame", "40 53 48 83 EC ?? 8B 41 3C 48 8B D9 25 10 00 10 00 3D 10 00 10 00 0F 85", WorkTest.TextureMappedFlags),
		("TextureDx11 EndFrame", "48 8B C4 53 48 83 EC ?? 48 83 79 48 00 48 8B D9 0F 84 ?? ?? ?? ?? 8B 51 3C", WorkTest.TextureMappedOrUploadFlags),
		("ConstantBuffer EndFrame", "48 89 5C 24 ?? 57 48 83 EC ?? 8B 05 ?? ?? ?? ?? 48 8B D9 48 8B 7C C1 18 48 85 FF 74 ?? 48 83 79 30 00 74 ?? F7 41 EC 00 40 00 00 74", WorkTest.ConstantBufferFlags),
	];

	// TextureDx11 EndFrame: AND 0x100010; CMP 0x100010; JNZ to BT reg,0xD
	private static readonly byte[] TextureMappedMask = [0x25, 0x10, 0x00, 0x10, 0x00];
	private static readonly byte[] TextureMappedCompare = [0x3D, 0x10, 0x00, 0x10, 0x00, 0x0F, 0x85];
	private const int TextureTestWindow = 0x40;

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
	private static nint[] snapshot = new nint[256];
	private static readonly Dictionary<nint, int> activeIndex = new();

	// Delegates rather than [UnmanagedCallersOnly]: plugins live in a collectible load context.
	private static readonly LoopDelegate CallActivePrePresent = () => CallActive(EndFrameOffset);
	private static readonly LoopDelegate CallActivePostKick = () => CallActive(BeginFrameOffset);

	private readonly nint lockAddress;
	private readonly nint headAddress;
	private readonly Dictionary<nint, WorkTest> callbackTests = new();
	private readonly string missingCallbacks = string.Empty;
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
			this.headAddress = (nint)Notifier.StaticAddressPointers.ppListHead;
			this.lockAddress = this.headAddress + ListLockOffset;
			this.missingCallbacks = this.ResolveCallbacks();
			this.prePresentLoop = this.ScanLoop(PrePresentLoopSignature);
			this.postKickLoop = this.ScanLoop(PostKickLoopSignature);
			this.linkHook = Service.GameInteropProvider.HookFromAddress<NodeDelegate>(
				(nint)Notifier.Addresses.Link.Value, this.LinkDetour);
			this.unlinkHook = Service.GameInteropProvider.HookFromAddress<NodeDelegate>(
				(nint)Notifier.Addresses.Unlink.Value, this.UnlinkDetour);
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
		int found = 0;

		// No allocations, the walk runs under the game's notifier lock
		EnterCriticalSection(this.lockAddress);
		try
		{
			for (nint node = *(nint*)this.headAddress; node != 0; node = (nint)((Notifier*)node)->Prev)
			{
				if (activeIndex.ContainsKey(node))
					found++;
				else if (this.CanDoWork(node))
					missing++;
			}
		}
		finally
		{
			LeaveCriticalSection(this.lockAddress);
		}

		int stale = activeCount - found;

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
			for (nint node = *(nint*)this.headAddress; node != 0; node = (nint)((Notifier*)node)->Prev)
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
		this.Status = this.missingCallbacks.Length == 0 ? "On" : $"On, unfiltered: {this.missingCallbacks}";
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

	/// <summary>
	/// Returns the callbacks not found, their notifiers stay unfiltered.
	/// </summary>
	private string ResolveCallbacks()
	{
		List<string> missing = new();
		foreach ((string name, string signature, WorkTest test) in CallbackSignatures)
		{
			int before = this.callbackTests.Count;
			foreach (nint callback in Service.SigScanner.ScanAllText(signature))
			{
				if ((callback & 0xF) != 0) // not a function start
					continue;
				if (test == WorkTest.TextureMappedOrUploadFlags && !IsTextureEndFrame(callback + PatternLength(signature)))
					continue;

				this.callbackTests[callback] = test;
			}

			if (this.callbackTests.Count == before)
				missing.Add(name);
		}

		if (this.callbackTests.Count == 0)
			throw new InvalidOperationException("No notifier callbacks found");

		if (missing.Count != 0)
			Service.PluginLog.Warning($"IdleNotifierFilter: callbacks not found: {string.Join(", ", missing)}");
		return string.Join(", ", missing);
	}

	private static bool IsTextureEndFrame(nint code)
	{
		ReadOnlySpan<byte> window = new((byte*)code, TextureTestWindow);
		int mask = window.IndexOf(TextureMappedMask);
		int compare = window.IndexOf(TextureMappedCompare);
		if (mask < 0 || compare < mask)
			return false;

		nint jump = code + compare + TextureMappedCompare.Length;
		byte* target = (byte*)(jump + 4 + *(int*)jump);
		return target[0] == 0x0F && target[1] == 0xBA && (target[2] & 0xF8) == 0xE0 && target[3] == 0x0D; // BT r32,imm8 (/4)
	}

	private nint ScanLoop(string signature)
	{
		nint loop = Service.SigScanner.ScanText(signature);
		if (loop + 7 + *(int*)(loop + 3) != this.headAddress)
			throw new InvalidOperationException("PostTick loop does not walk Notifier_ListHead");
		return loop;
	}

	private static int PatternLength(string signature) => (signature.Length + 1) / 3;

	private bool CanDoWork(nint node)
	{
		nint vtable = *(nint*)node;
		return this.CallbackMayWork(node, *(nint*)(vtable + BeginFrameOffset)) ||
		       this.CallbackMayWork(node, *(nint*)(vtable + EndFrameOffset));
	}

	private bool CallbackMayWork(nint node, nint callback)
	{
		if (IsBareReturn(callback))
			return false;

		if (!this.callbackTests.TryGetValue(callback, out WorkTest test))
			return true;

		const TextureFlags mappedTarget = TextureFlags.TextureRenderTarget | TextureFlags.CpuRead;
		uint flags;
		switch (test)
		{
			case WorkTest.BufferFlags:
				return (*(uint*)(node + 0x1C) & 0x11) != 0;
			case WorkTest.IndexBufferFlags:
				flags = *(uint*)(node + 0x20);
				return (flags & 0x11) != 0 && (flags & 0x40) == 0;
			case WorkTest.TextureMappedFlags:
				return (((Texture*)(node - TextureNotifierOffset))->Flags & mappedTarget) == mappedTarget;
			case WorkTest.TextureMappedOrUploadFlags:
				TextureFlags textureFlags = ((Texture*)(node - TextureNotifierOffset))->Flags;
				return (textureFlags & mappedTarget) == mappedTarget || ((uint)textureFlags & 0x2000) != 0; // 0x2000 not named in TextureFlags
			case WorkTest.ConstantBufferFlags:
				return (((ConstantBuffer*)(node - 0x38))->Flags & 0x4000) != 0; // Notifier at ConstantBuffer+0x38
			default:
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
		if (snapshot.Length < count)
			snapshot = new nint[active.Length];
		active.AsSpan(0, count).CopyTo(snapshot); // a callback may link or unlink notifiers

		for (int i = 0; i < count; i++)
		{
			nint node = snapshot[i];
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
