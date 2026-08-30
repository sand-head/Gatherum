namespace Gatherum.Client.Emulation;

/// <summary>The eight buttons both consoles have, which is why one set serves both:
/// a NES pad and a Game Boy pad differ in their plastic, not their bits.</summary>
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
}

/// <summary>A console, running. The player component owns the clock — it decides when a
/// frame is due, because only the browser knows when the display will take one — and a
/// core's whole job is to produce exactly one frame's worth of picture and sound when
/// asked.</summary>
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

    /// <summary>The last completed frame, one 0xAARRGGBB pixel per element, row by
    /// row. Owned by the core and overwritten by the next <see cref="RunFrame"/>.</summary>
    uint[] Frame { get; }

    /// <summary>Whether the cartridge has memory a battery would have kept — the only
    /// thing worth writing back out, and the reason a save exists at all.</summary>
    bool BatteryBacked { get; }

    /// <summary>Whether that memory has been written since <see cref="MarkSaved"/>.
    /// The player watches this instead of writing a save every frame.</summary>
    bool SaveDirty { get; }

    void SetButtons(GamepadButtons buttons);

    /// <summary>Runs the console until it has finished the frame it was in the middle
    /// of, filling <see cref="Frame"/> and queueing the sound that went with it.</summary>
    void RunFrame();

    /// <summary>Drains queued sound into the buffer, in samples; the return is how many
    /// were written. What is not taken is kept for the next call.</summary>
    int ReadAudio(short[] destination);

    void Reset();

    /// <summary>The cartridge's battery-backed memory, or empty when it has none.</summary>
    byte[] SaveRam();

    void LoadSaveRam(ReadOnlySpan<byte> data);
    void MarkSaved();
}
