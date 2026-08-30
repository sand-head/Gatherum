namespace Gatherum.Tests;

/// <summary>Cartridge images assembled by hand. A real ROM cannot be checked in — they
/// are somebody's copyrighted game — so the emulator tests build the smallest cartridge
/// that exercises what is under test and run that.</summary>
public static class RomFixtures
{
    /// <summary>An NROM cartridge: 16 KB of program mirrored into both halves of the
    /// address space, 8 KB of pattern data, and vectors pointing at the program.</summary>
    public static byte[] Nes(byte[] program, byte[]? characters = null,
        ushort start = 0x8000, byte flags6 = 0x01)
    {
        var image = new byte[16 + 16 * 1024 + 8 * 1024];
        image[0] = (byte)'N';
        image[1] = (byte)'E';
        image[2] = (byte)'S';
        image[3] = 0x1A;
        image[4] = 1;
        image[5] = 1;
        image[6] = flags6;

        program.CopyTo(image, 16 + (start - 0x8000));
        // NMI, reset and IRQ vectors, at the very top of the mirrored bank.
        var vectors = 16 + 16 * 1024 - 6;
        image[vectors + 2] = (byte)start;
        image[vectors + 3] = (byte)(start >> 8);
        characters?.CopyTo(image, 16 + 16 * 1024);
        return image;
    }

    /// <summary>A Game Boy cartridge: the logo the boot ROM checks, a header naming a
    /// plain 32 KB board, and the program at the entry point.</summary>
    public static byte[] GameBoy(byte[] program, ushort start = 0x0150,
        byte cartridgeType = 0x00, string title = "TESTCART")
    {
        var image = new byte[32 * 1024];
        Array.Fill(image, (byte)0x00);
        NintendoLogo.CopyTo(image, 0x104);
        foreach (var (character, index) in title.Select((c, i) => (c, i)))
            image[0x134 + index] = (byte)character;
        image[0x147] = cartridgeType;
        image[0x148] = 0x00;
        image[0x149] = cartridgeType is 0x02 or 0x03 or 0x12 or 0x13 or 0x1A or 0x1B
            ? (byte)0x02
            : (byte)0x00;

        // The entry point at $100 is three bytes of room before the header, which is
        // exactly enough for a jump past it.
        image[0x100] = 0x00;
        image[0x101] = 0xC3;
        image[0x102] = (byte)start;
        image[0x103] = (byte)(start >> 8);
        program.CopyTo(image, start);

        byte checksum = 0;
        for (var address = 0x134; address <= 0x14C; address++)
            checksum = (byte)(checksum - image[address] - 1);
        image[0x14D] = checksum;
        return image;
    }

    public static readonly byte[] NintendoLogo =
    [
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83,
        0x00, 0x0C, 0x00, 0x0D, 0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E,
        0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99, 0xBB, 0xBB, 0x67, 0x63,
        0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E,
    ];
}
