using Gatherum.Client.Emulation.GameBoy;
using Gatherum.Client.Emulation.Nes;
using Gatherum.Client.Emulation.Sega;

namespace Gatherum.Client.Emulation;

/// <summary>Which machine a cartridge image is for.</summary>
public enum ConsoleKind
{
    Nes,
    GameBoy,
    MasterSystem,
    GameGear,
    GameBoyAdvance,
    SuperNintendo,
    GameCube,
    /// <summary>Recognised so that a Wii disc is told apart from a GameCube one and
    /// said no to by name, rather than fed to a core that would boot it wrong or
    /// fetched whole into a heap it cannot fit in. Nothing plays it.</summary>
    Wii,
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
        if (LooksLikeSuperNintendo(rom))
            return ConsoleKind.SuperNintendo;
        if (DiscKind(rom) is { } disc)
            return disc;

        // Nothing declared itself, so the name gets the last word. Not `.iso`: that is
        // every optical disc ever imaged, and a GameCube one has already said so above.
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".nes" => ConsoleKind.Nes,
            ".gb" or ".gbc" => ConsoleKind.GameBoy,
            ".sms" => ConsoleKind.MasterSystem,
            ".gg" => ConsoleKind.GameGear,
            ".gba" => ConsoleKind.GameBoyAdvance,
            ".sfc" or ".smc" => ConsoleKind.SuperNintendo,
            ".gcm" or ".rvz" => ConsoleKind.GameCube,
            _ => null,
        };
    }

    /// <summary>How much of a file's front is enough to tell a disc. A disc image is
    /// too big to read whole just to look at its header, so the player reads this much
    /// first and only fetches the rest of a cartridge — a disc goes straight to the core
    /// without passing through this side at all.</summary>
    public const int HeadBytes = 1024;

    /// <summary>A machine whose image is a disc rather than a cartridge: gigabytes, and
    /// handled by name and by URL rather than by bytes on this side.</summary>
    public static bool IsDisc(ConsoleKind kind) => kind is ConsoleKind.GameCube or ConsoleKind.Wii;

    /// <summary>Named the way a disc image is named, whatever its bytes turn out to
    /// say. The player refuses to fetch one of these whole: a file that says it is a
    /// disc and is not a GameCube one is not a cartridge either.</summary>
    public static bool NamedLikeADisc(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".iso" or ".gcm" or ".rvz";

    /// <summary>Whether this cartridge needs a core Gatherum did not write. Those are
    /// fetched and instantiated rather than constructed, which cannot happen inside a
    /// synchronous call — so the player asks this first and takes the other road.</summary>
    public static bool NeedsVendoredCore(ReadOnlySpan<byte> rom, string fileName) =>
        Identify(rom, fileName) is { } kind && VendoredCore.Handles(kind);

    /// <summary>Builds one of the consoles written in C#. A cartridge for a machine that
    /// needs a vendored core does not come through here.</summary>
    public static IEmulatorCore Load(ReadOnlySpan<byte> rom, string fileName) =>
        Identify(rom, fileName) switch
        {
            ConsoleKind.Nes => new NesConsole(rom),
            ConsoleKind.GameBoy => new GameBoyConsole(rom),
            ConsoleKind.MasterSystem => new MasterSystem(rom, gameGear: false),
            ConsoleKind.GameGear => new MasterSystem(rom, gameGear: true),
            ConsoleKind.GameBoyAdvance or ConsoleKind.SuperNintendo or ConsoleKind.GameCube =>
                throw new NotSupportedException(
                    "This cartridge plays on a core this build did not fetch. " +
                    "See native/README.md."),
            ConsoleKind.Wii => throw new NotSupportedException(
                "This is a Wii disc. The GameCube core here boots GameCube discs only: " +
                "a Wii needs a system it would have to invent, and does not."),
            _ => throw new NotSupportedException(
                "This does not look like a cartridge image the player knows: it plays " +
                "Nintendo Entertainment System (.nes), Game Boy (.gb, .gbc), " +
                "Game Boy Advance (.gba), Super Nintendo (.sfc, .smc), " +
                "Master System (.sms), Game Gear (.gg) and GameCube (.iso, .gcm, .rvz) " +
                "files."),
        };

    /// <summary>The Super Nintendo is the one machine here that stamped nothing at a
    /// fixed place. Its header sits at the end of a bank — which bank depending on how the
    /// cartridge was wired — and the sixteen bits at its end are a checksum beside its own
    /// complement, which is what makes it findable at all. A copier header, the 512 bytes
    /// some dumps carry in front, is the only reason a cartridge image is not a whole
    /// number of kilobytes.
    ///
    /// <para>Spelled here as well as in Core's RomHeader because this project does not
    /// reference that one, and a cartridge the player cannot name is a download link.</para></summary>
    private static bool LooksLikeSuperNintendo(ReadOnlySpan<byte> rom)
    {
        var image = rom.Length % 1024 == 512 ? rom[512..] : rom;
        foreach (var at in (ReadOnlySpan<int>)[0x7FC0, 0xFFC0])
        {
            if (image.Length < at + 32)
                continue;
            var header = image.Slice(at, 32);
            var complement = header[28] | header[29] << 8;
            var checksum = header[30] | header[31] << 8;
            if (checksum != 0 && (checksum ^ complement) == 0xFFFF)
                return true;
        }
        return false;
    }

    /// <summary>A GameCube disc carries a magic word at $1C and a Wii one at $18, and
    /// an RVZ — the compressed form both are usually kept in — copies the first 128
    /// bytes of that header to $58 and says which console at $48. So the two magic words
    /// are looked for in both places, and either place is within the first kilobyte,
    /// which is what makes a disc tellable from its head alone.</summary>
    private static ConsoleKind? DiscKind(ReadOnlySpan<byte> rom)
    {
        if (rom.Length >= 0x20 && DiscMagic(rom) is { } plain)
            return plain;
        if (rom.Length >= 0xD8 && rom[..4].SequenceEqual("RVZ\x01"u8))
            return DiscMagic(rom[0x58..]);
        return null;
    }

    private static ConsoleKind? DiscMagic(ReadOnlySpan<byte> header)
    {
        if (header.Slice(0x1C, 4).SequenceEqual(GameCubeMagic))
            return ConsoleKind.GameCube;
        if (header.Slice(0x18, 4).SequenceEqual(WiiMagic))
            return ConsoleKind.Wii;
        return null;
    }

    private static ReadOnlySpan<byte> GameCubeMagic => [0xC2, 0x33, 0x9F, 0x3D];
    private static ReadOnlySpan<byte> WiiMagic => [0x5D, 0x1C, 0x9E, 0xA3];

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
