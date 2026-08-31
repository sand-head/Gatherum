namespace Gatherum.Client.Emulation;

/// <summary>Every button any of these machines has. The first eight are the ones they
/// all share — a NES pad and a Game Boy pad differ in their plastic, not their bits —
/// and the two shoulders were added when a console arrived that had them. A core simply
/// ignores the bits its hardware never had.</summary>
[Flags]
public enum GamepadButtons
{
    None = 0,
    A = 1,
    B = 2,
    Select = 4,
    Start = 8,
    Up = 16,
    Down = 32,
    Left = 64,
    Right = 128,
    LeftShoulder = 256,
    RightShoulder = 512,
}

/// <summary>What a machine's plastic calls the eight bits above. The buttons are the
/// same everywhere; the printing on them is not, and a Master System that told you to
/// press Start would be naming a button its pad does not have. A label of null is a
/// button the machine never had at all, and the player leaves it off the pad.</summary>
public readonly record struct ButtonLabels(
    string A,
    string B,
    string? Start,
    string? Select,
    string? LeftShoulder = null,
    string? RightShoulder = null);

/// <summary>A console, running. The player component owns the clock — it decides when a
/// frame is due, because only the browser knows when the display will take one — and a
/// core's whole job is to produce exactly one frame's worth of picture and sound when
/// asked.
///
/// <para><b>A core must be deterministic.</b> Two copies of the same core, given the
/// same cartridge and fed the same buttons on the same frames, must reach byte-identical
/// states. That is not a nicety: it is what lets two people play the same game in two
/// browsers by exchanging nothing but their buttons. In practice it forbids three
/// things — reading a wall clock (a cartridge's own real-time clock counts the console's
/// cycles instead), any randomness at reset or anywhere else, and letting anything the
/// player does *outside* the console leak into the machine's state. Draining sound is
/// the case that matters: how often a browser asks for samples is its own business, so
/// the queue that answers is deliberately not part of a save state.</para>
///
/// <para>The serialization below is shaped after libretro's
/// <c>retro_serialize_size</c>/<c>retro_serialize</c>/<c>retro_unserialize</c>, so that
/// a core vendored from that world can satisfy this seam by forwarding three
/// calls.</para></summary>
public interface IEmulatorCore
{
    string SystemName { get; }

    /// <summary>The picture's real size in pixels. The player scales it; a console's
    /// own resolution is never negotiable.</summary>
    int Width { get; }
    int Height { get; }

    /// <summary>How many frames a second the hardware ran at — not 60, on either
    /// machine, which is why the player paces against this rather than the display.</summary>
    double FramesPerSecond { get; }

    /// <summary>The rate the core's samples are written at. The browser resamples to
    /// whatever its own output is running at.</summary>
    int SampleRate { get; }

    /// <summary>How many channels <see cref="ReadAudio"/> interleaves. One on a console
    /// with a single speaker; two where the hardware could put a sound in one ear, which
    /// on a Game Gear is a register a game writes to.</summary>
    int AudioChannels { get; }

    /// <summary>The last completed frame, one 0xAARRGGBB pixel per element, row by
    /// row. Owned by the core and overwritten by the next <see cref="RunFrame"/>.</summary>
    uint[] Frame { get; }

    /// <summary>What this machine's own pad calls its buttons.</summary>
    ButtonLabels Buttons { get; }

    /// <summary>How many pads the machine has ports for. One is a machine nobody can
    /// play together on without emulating a cable, which is a different feature.</summary>
    int PlayerCount { get; }

    /// <summary>Whether the cartridge has memory a battery would have kept — the only
    /// thing worth writing back out, and the reason a save exists at all.</summary>
    bool BatteryBacked { get; }

    /// <summary>Whether that memory has been written since <see cref="MarkSaved"/>.
    /// The player watches this instead of writing a save every frame.</summary>
    bool SaveDirty { get; }

    /// <summary>What a save state costs, in bytes. Constant for a given cartridge.</summary>
    int SaveStateSize { get; }

    void SetButtons(int player, GamepadButtons buttons);

    /// <summary>Runs the console until it has finished the frame it was in the middle
    /// of, filling <see cref="Frame"/> and queueing the sound that went with it.</summary>
    void RunFrame();

    /// <summary>Drains queued sound into the buffer; the return is how many values were
    /// written, which on a stereo core is <see cref="AudioChannels"/> per frame of
    /// sound. What is not taken is kept for the next call.</summary>
    int ReadAudio(short[] destination);

    void Reset();

    /// <summary>Writes the whole machine — everything a frame's execution can read or
    /// change — into the buffer. False when it would not fit.</summary>
    bool SaveState(Span<byte> destination);

    /// <summary>Puts the machine back where a state says it was. False when the bytes
    /// are not a state this core wrote, or are truncated; the machine is left reset
    /// rather than half-loaded.</summary>
    bool LoadState(ReadOnlySpan<byte> source);

    /// <summary>The cartridge's battery-backed memory, or empty when it has none.</summary>
    byte[] SaveRam();

    void LoadSaveRam(ReadOnlySpan<byte> data);
    void MarkSaved();
}
