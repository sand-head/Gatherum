using Gatherum.Client.Emulation.GameBoy;
using Gatherum.Client.Emulation.Nes;
using Gatherum.Client.Emulation.Sega;

namespace Gatherum.Client.Emulation;

/// <summary>Which console a file is for. The bytes are asked before the name is: a ROM
/// that has been renamed, or downloaded with whatever extension a server felt like, is
/// still perfectly playable — and a `.gb` that is really something else should fail
/// with a sentence rather than a stack trace.</summary>
/// <summary>Which machine a cartridge image is for.</summary>
public enum ConsoleKind
{
    Nes,
    GameBoy,
    MasterSystem,
    GameGear,
    GameBoyAdvance,
}

public static class Emulator
{
    /// <summary>Which console a file is for, or nothing when it is not a cartridge at
    /// all. The bytes are asked before the name is: a ROM that has been renamed, or
    /// downloaded with whatever extension a server felt like, is still perfectly
    /// playable.</summary>
    public static ConsoleKind? Identify(ReadOnlySpan<byte> rom, string fileName)
    {
        if (LooksLikeNes(rom))
            return ConsoleKind.Nes;
        if (LooksLikeGameBoy(rom))
            return ConsoleKind.GameBoy;
        if (LooksLikeGameBoyAdvance(rom))
            return ConsoleKind.GameBoyAdvance;
        if (SegaRegion(rom) is { } region)
            return region >= 5 ? ConsoleKind.GameGear : ConsoleKind.MasterSystem;

        // Nothing declared itself, so the name gets the last word.
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".nes" => ConsoleKind.Nes,
            ".gb" or ".gbc" => ConsoleKind.GameBoy,
            ".sms" => ConsoleKind.MasterSystem,
            ".gg" => ConsoleKind.GameGear,
            ".gba" => ConsoleKind.GameBoyAdvance,
            _ => null,
        };
    }

    /// <summary>Whether this cartridge needs a core Gatherum did not write. Those are
    /// fetched and instantiated rather than constructed, which cannot happen inside a
    /// synchronous call — so the player asks this first and takes the other road.</summary>
    public static bool NeedsVendoredCore(ReadOnlySpan<byte> rom, string fileName) =>
        Identify(rom, fileName) == ConsoleKind.GameBoyAdvance;

    /// <summary>Builds one of the consoles written in C#. A cartridge for a machine that
    /// needs a vendored core does not come through here.</summary>
    public static IEmulatorCore Load(ReadOnlySpan<byte> rom, string fileName) =>
        Identify(rom, fileName) switch
        {
            ConsoleKind.Nes => new NesConsole(rom),
            ConsoleKind.GameBoy => new GameBoyConsole(rom),
            ConsoleKind.MasterSystem => new MasterSystem(rom, gameGear: false),
            ConsoleKind.GameGear => new MasterSystem(rom, gameGear: true),
            ConsoleKind.GameBoyAdvance => throw new NotSupportedException(
                "A Game Boy Advance cartridge plays on a core this build did not fetch. " +
                "See native/README.md."),
            _ => throw new NotSupportedException(
                "This does not look like a cartridge image the player knows: it plays " +
                "Nintendo Entertainment System (.nes), Game Boy (.gb, .gbc), " +
                "Game Boy Advance (.gba), Master System (.sms) and Game Gear (.gg) files."),
        };

    private static bool LooksLikeNes(ReadOnlySpan<byte> rom) =>
        rom.Length >= 16 && rom[0] == 'N' && rom[1] == 'E' && rom[2] == 'S' && rom[3] == 0x1A;

    /// <summary>A Game Boy cartridge has no magic number, but it does have Nintendo's
    /// logo at $104 — the boot ROM compares it byte for byte and refuses to start
    /// otherwise, so every cartridge that ever ran carries it.</summary>
    private static bool LooksLikeGameBoy(ReadOnlySpan<byte> rom) =>
        rom.Length >= 0x150 && rom.Slice(0x104, 48).SequenceEqual(NintendoLogo);

    /// <summary>The two bytes mGBA itself settles for: the ARM branch every cartridge
    /// begins with, and the fixed byte at $B2 that the hardware checks.</summary>
    private static bool LooksLikeGameBoyAdvance(ReadOnlySpan<byte> rom) =>
        rom.Length >= 0xC0 && rom[3] == 0xEA && rom[0xB2] == 0x96;

    /// <summary>Sega stamped a header near the end of the first bank — at one of three
    /// places, depending on how big the cartridge was — and the code in it says which of
    /// the two consoles the game was sold for. Its absence is not fatal: plenty of
    /// cartridges shipped without one, and the file's name gets the last word.</summary>
    private static int? SegaRegion(ReadOnlySpan<byte> rom)
    {
        foreach (var at in (ReadOnlySpan<int>)[0x1FF0, 0x3FF0, 0x7FF0])
        {
            if (rom.Length < at + 16 || !rom.Slice(at, 8).SequenceEqual("TMR SEGA"u8))
                continue;
            // Codes 3 and 4 are the bigger console, 5 upwards the handheld.
            var region = rom[at + 15] >> 4;
            return region is >= 3 and <= 7 ? region : 4;
        }
        return null;
    }

    private static ReadOnlySpan<byte> NintendoLogo =>
    [
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83,
        0x00, 0x0C, 0x00, 0x0D, 0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E,
        0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99, 0xBB, 0xBB, 0x67, 0x63,
        0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E,
    ];
}
