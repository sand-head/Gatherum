using Gatherum.Client.Emulation.GameBoy;
using Gatherum.Client.Emulation.Nes;

namespace Gatherum.Client.Emulation;

/// <summary>Which console a file is for. The bytes are asked before the name is: a ROM
/// that has been renamed, or downloaded with whatever extension a server felt like, is
/// still perfectly playable — and a `.gb` that is really something else should fail
/// with a sentence rather than a stack trace.</summary>
public static class Emulator
{
    public static IEmulatorCore Load(ReadOnlySpan<byte> rom, string fileName)
    {
        if (LooksLikeNes(rom))
            return new NesConsole(rom);
        if (LooksLikeGameBoy(rom))
            return new GameBoyConsole(rom);

        // Nothing declared itself, so the name gets the last word — and whichever core
        // it names says what is wrong with the file.
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".nes" => new NesConsole(rom),
            ".gb" or ".gbc" => new GameBoyConsole(rom),
            _ => throw new NotSupportedException(
                "This does not look like a cartridge image the player knows: it plays " +
                "Nintendo Entertainment System (.nes) and Game Boy (.gb, .gbc) files."),
        };
    }

    private static bool LooksLikeNes(ReadOnlySpan<byte> rom) =>
        rom.Length >= 16 && rom[0] == 'N' && rom[1] == 'E' && rom[2] == 'S' && rom[3] == 0x1A;

    /// <summary>A Game Boy cartridge has no magic number, but it does have Nintendo's
    /// logo at $104 — the boot ROM compares it byte for byte and refuses to start
    /// otherwise, so every cartridge that ever ran carries it.</summary>
    private static bool LooksLikeGameBoy(ReadOnlySpan<byte> rom) =>
        rom.Length >= 0x150 && rom.Slice(0x104, 48).SequenceEqual(NintendoLogo);

    private static ReadOnlySpan<byte> NintendoLogo =>
    [
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83,
        0x00, 0x0C, 0x00, 0x0D, 0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E,
        0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99, 0xBB, 0xBB, 0x67, 0x63,
        0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E,
    ];
}
