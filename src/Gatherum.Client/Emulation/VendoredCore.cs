using System.Runtime.InteropServices;
using Microsoft.JSInterop;

namespace Gatherum.Client.Emulation;

/// <summary>A console nobody here wrote: an emulator from elsewhere, compiled to
/// WebAssembly and fetched at build time, wearing the same seam as the cores written in
/// C#. See native/README.md for what it is and how it is built.
///
/// <para>Everything crosses two heaps. The core keeps its picture, its sound and its
/// memory inside its own WebAssembly instance, and the browser copies each of them into
/// arrays pinned here — one memcpy per frame, which measures at a twentieth of a
/// millisecond and is the reason this is worth doing at all rather than marshalling
/// pixels through the runtime.</para>
///
/// <para>Unlike the cores in this project, a vendored one is a claim rather than a
/// promise: its determinism, its accuracy and its bugs are somebody else's. So a
/// descriptor says how many players a core is trusted with, and the answer is one until
/// somebody has <em>measured</em> otherwise — two copies of it, the same cartridge, the
/// same buttons, byte-identical states. bsnes has been measured and mGBA has not, which
/// is the whole of why one of them plays together and the other does not.</para></summary>
public sealed class VendoredCore : IEmulatorCore, IDisposable
{
    /// <summary>What it takes to play one machine on somebody else's core: where the
    /// module is served from — the app's own origin, so no deployment reaches anybody
    /// else's server to play a game — what the cartridge file has to be called for a core
    /// that opens it itself, what the pad is printed with, how many can play, and
    /// anything the core must be told before it powers on.</summary>
    private sealed record Machine(
        string SystemName,
        string ModuleUrl,
        string Extension,
        ButtonLabels Buttons,
        int PlayerCount,
        string[] Settings);

    private static readonly Dictionary<ConsoleKind, Machine> Machines = new()
    {
        [ConsoleKind.GameBoyAdvance] = new(
            "Game Boy Advance", "/cores/mgba.wasm", ".gba",
            new("A", "B", "Start", "Select", "L", "R"),
            // A Game Boy Advance played with somebody else meant a second console and a
            // cable. Nobody has held this core to the seam's promise that two copies of
            // it agree frame for frame, so it is not asked to keep it.
            PlayerCount: 1, Settings: []),

        [ConsoleKind.SuperNintendo] = new(
            "Super Nintendo", "/cores/bsnes.mjs", ".sfc",
            new("A", "B", "Start", "Select", "L", "R", X: "X", Y: "Y"),
            // Two, and measured rather than assumed: two of these run six hundred frames
            // of scripted two-player input and come out byte for byte the same.
            PlayerCount: 2,
            // The one setting that is not taste. bsnes fills memory with noise at
            // power-on, faithfully to the hardware and fatally for two people whose
            // consoles have to start life identical.
            Settings: ["bsnes_entropy", "None"]),
    };

    /// <summary>How often the cartridge's battery memory is checked for changes. Every
    /// frame would be a hash of a hundred kilobytes sixty times a second to answer a
    /// question whose answer is nearly always no.</summary>
    private const int SaveCheckInterval = 60;

    private readonly IJSInProcessObjectReference js;
    private readonly int stateSize;
    private readonly int saveSize;

    private GCHandle pinnedFrame;
    private GCHandle pinnedAudio;
    private GCHandle pinnedScratch;
    private readonly short[] audioScratch = new short[8192];
    private readonly byte[] scratch;

    private readonly GamepadButtons[] pads = new GamepadButtons[2];
    private uint saveFingerprint;
    private int framesSinceSaveCheck;
    private bool disposed;

    private VendoredCore(IJSInProcessObjectReference js, Machine machine, CartridgeFacts facts)
    {
        this.js = js;
        SystemName = machine.SystemName;
        Buttons = machine.Buttons;
        PlayerCount = machine.PlayerCount;
        Width = facts.Width;
        Height = facts.Height;
        FramesPerSecond = facts.Fps;
        SampleRate = (int)Math.Round(facts.SampleRate);
        stateSize = facts.StateSize;
        saveSize = facts.SaveSize;

        Frame = new uint[Width * Height];
        scratch = new byte[Math.Max(1, Math.Max(stateSize, saveSize))];

        pinnedFrame = GCHandle.Alloc(Frame, GCHandleType.Pinned);
        pinnedAudio = GCHandle.Alloc(audioScratch, GCHandleType.Pinned);
        pinnedScratch = GCHandle.Alloc(scratch, GCHandleType.Pinned);

        saveFingerprint = Fingerprint();
    }

    /// <summary>Whether this machine plays on a core from elsewhere.</summary>
    public static bool Handles(ConsoleKind kind) => Machines.ContainsKey(kind);

    /// <summary>Fetches the core if it is not already here and hands it the cartridge.
    /// Null means this build genuinely has no such core — a deployment that did not
    /// build one is a player that offers a download, not a page that breaks. Everything
    /// else that can go wrong throws with words the player shows, because a core that
    /// is here and will not start is a bug to report, not an edition to accept — the
    /// two were once one answer, and the player blamed the build for a serving
    /// failure.</summary>
    public static async Task<VendoredCore?> CreateAsync(
        IJSObjectReference module, ConsoleKind kind, byte[] rom)
    {
        if (module is not IJSInProcessObjectReference js)
            return null;
        if (!Machines.TryGetValue(kind, out var machine))
            return null;
        var status = await module.InvokeAsync<string>(
            "loadEmulatorCore", machine.ModuleUrl, machine.Settings);
        if (status == "missing")
            return null;
        if (status != "ok")
            throw new NotSupportedException(
                $"This Gatherum has a {machine.SystemName} core, but it would not " +
                "start — the browser console has the details.");

        var facts = js.Invoke<CartridgeFacts?>("loadEmulatorCartridge", rom, machine.Extension);
        if (facts is null || facts.Width <= 0 || facts.Height <= 0)
            throw new InvalidOperationException(
                $"the {machine.SystemName} core did not accept it");

        var core = new VendoredCore(js, machine, facts);
        // Prove the picture can actually cross between the two heaps before handing the
        // core back. If it cannot, the player would otherwise run a game nobody could
        // see — a frozen screen with nothing to say why.
        if (core.CopyFrame() > 0)
            return core;
        core.Dispose();
        throw new InvalidOperationException(
            $"the {machine.SystemName} core's picture could not reach the app");
    }

    public string SystemName { get; }
    public int Width { get; }
    public int Height { get; }
    public double FramesPerSecond { get; }
    public int SampleRate { get; }

    /// <summary>libretro hands sound over interleaved, always in pairs.</summary>
    public int AudioChannels => 2;

    /// <summary>How many can play, which is the descriptor's answer rather than this
    /// class's: see the note on <see cref="Machines"/>.</summary>
    public int PlayerCount { get; }

    public ButtonLabels Buttons { get; }

    public uint[] Frame { get; }

    public bool BatteryBacked => saveSize > 0;
    public bool SaveDirty { get; private set; }

    public void SetButtons(int player, GamepadButtons pressed)
    {
        if (player < 0 || player >= pads.Length)
            return;
        if ((pressed & (GamepadButtons.Up | GamepadButtons.Down))
            == (GamepadButtons.Up | GamepadButtons.Down))
            pressed &= ~GamepadButtons.Down;
        if ((pressed & (GamepadButtons.Left | GamepadButtons.Right))
            == (GamepadButtons.Left | GamepadButtons.Right))
            pressed &= ~GamepadButtons.Right;
        pads[player] = pressed;
    }

    public void RunFrame()
    {
        if (disposed)
            return;
        js.InvokeVoid("runEmulatorCore", LibretroMask(pads[0]), LibretroMask(pads[1]));
        CopyFrame();

        if (!BatteryBacked || ++framesSinceSaveCheck < SaveCheckInterval)
            return;
        framesSinceSaveCheck = 0;
        var current = Fingerprint();
        if (current == saveFingerprint)
            return;
        saveFingerprint = current;
        SaveDirty = true;
    }

    /// <summary>The core's picture into this side's array: one memcpy between two
    /// WebAssembly heaps, and the return is how many bytes made it.</summary>
    private int CopyFrame() =>
        js.Invoke<int>("readEmulatorCoreFrame", Address(pinnedFrame), Frame.Length * 4);

    /// <summary>libretro numbers the buttons its own way, and the order is neither
    /// alphabetical nor the order they sit on the pad — so the mapping is written out
    /// rather than computed.</summary>
    private static int LibretroMask(GamepadButtons pressed)
    {
        var mask = 0;
        if (pressed.HasFlag(GamepadButtons.B)) mask |= 1 << 0;
        if (pressed.HasFlag(GamepadButtons.Y)) mask |= 1 << 1;
        if (pressed.HasFlag(GamepadButtons.Select)) mask |= 1 << 2;
        if (pressed.HasFlag(GamepadButtons.Start)) mask |= 1 << 3;
        if (pressed.HasFlag(GamepadButtons.Up)) mask |= 1 << 4;
        if (pressed.HasFlag(GamepadButtons.Down)) mask |= 1 << 5;
        if (pressed.HasFlag(GamepadButtons.Left)) mask |= 1 << 6;
        if (pressed.HasFlag(GamepadButtons.Right)) mask |= 1 << 7;
        if (pressed.HasFlag(GamepadButtons.A)) mask |= 1 << 8;
        if (pressed.HasFlag(GamepadButtons.X)) mask |= 1 << 9;
        if (pressed.HasFlag(GamepadButtons.LeftShoulder)) mask |= 1 << 10;
        if (pressed.HasFlag(GamepadButtons.RightShoulder)) mask |= 1 << 11;
        return mask;
    }

    public int ReadAudio(short[] destination)
    {
        if (disposed)
            return 0;
        var wanted = Math.Min(destination.Length, audioScratch.Length);
        var written = js.Invoke<int>("readEmulatorCoreAudio", Address(pinnedAudio), wanted);
        if (written > 0)
            Array.Copy(audioScratch, destination, written);
        return written;
    }

    public void Reset()
    {
        if (!disposed)
            js.InvokeVoid("resetEmulatorCore");
    }

    public int SaveStateSize => stateSize;

    public bool SaveState(Span<byte> destination)
    {
        if (disposed || destination.Length < stateSize)
            return false;
        if (!js.Invoke<bool>("saveEmulatorCoreState", Address(pinnedScratch), stateSize))
            return false;
        scratch.AsSpan(0, stateSize).CopyTo(destination);
        return true;
    }

    public bool LoadState(ReadOnlySpan<byte> source)
    {
        if (disposed || source.Length < stateSize)
            return false;
        source[..stateSize].CopyTo(scratch);
        return js.Invoke<bool>("loadEmulatorCoreState", Address(pinnedScratch), stateSize);
    }

    public byte[] SaveRam()
    {
        if (disposed || !BatteryBacked)
            return [];
        var written = js.Invoke<int>("readEmulatorCoreSave", Address(pinnedScratch));
        return written <= 0 ? [] : scratch.AsSpan(0, written).ToArray();
    }

    public void LoadSaveRam(ReadOnlySpan<byte> data)
    {
        if (disposed || data.Length == 0 || !BatteryBacked)
            return;
        var taken = Math.Min(data.Length, Math.Min(saveSize, scratch.Length));
        data[..taken].CopyTo(scratch);
        js.Invoke<bool>("writeEmulatorCoreSave", Address(pinnedScratch), taken);
        saveFingerprint = Fingerprint();
        SaveDirty = false;
    }

    public void MarkSaved() => SaveDirty = false;

    private uint Fingerprint() => disposed ? 0 : js.Invoke<uint>("fingerprintEmulatorCoreSave");

    private static long Address(GCHandle handle) => handle.AddrOfPinnedObject().ToInt64();

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        js.InvokeVoid("unloadEmulatorCartridge");
        if (pinnedFrame.IsAllocated) pinnedFrame.Free();
        if (pinnedAudio.IsAllocated) pinnedAudio.Free();
        if (pinnedScratch.IsAllocated) pinnedScratch.Free();
    }

    /// <summary>What the core says about the cartridge once it has read it — none of it
    /// is knowable until then, which is why the machine's own size is not a constant.</summary>
    private sealed record CartridgeFacts(
        int Width, int Height, double Fps, double SampleRate, int StateSize, int SaveSize);
}
