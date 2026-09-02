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

    /// <summary>A Master System or Game Gear cartridge: 32 KB — two banks, which is the
    /// smallest a mapper can page — with Sega's header at the end of the first one and
    /// the program at the reset vector, address zero.</summary>
    public static byte[] Sega(byte[] program, bool gameGear = false, int banks = 2,
        int productCode = 12345)
    {
        var image = new byte[banks * 0x4000];
        program.CopyTo(image, 0);

        var header = 0x7FF0;
        if (image.Length >= header + 16)
        {
            "TMR SEGA"u8.ToArray().CopyTo(image, header);
            image[header + 12] = ToDecimal(productCode % 100);
            image[header + 13] = ToDecimal(productCode / 100 % 100);
            image[header + 14] = (byte)(productCode / 10000 << 4);
            // The region nibble is the only thing in the file that says which of the
            // two consoles a cartridge was sold for.
            image[header + 15] = (byte)((gameGear ? 6 : 4) << 4 | 0x0C);
        }
        return image;
    }

    private static byte ToDecimal(int value) => (byte)(value / 10 << 4 | value % 10);

    /// <summary>A cartridge on Codemasters' board, which declares itself only by the
    /// checksum their tools wrote where Sega's header would be.</summary>
    public static byte[] Codemasters(byte[] program, int banks = 2)
    {
        var image = new byte[banks * 0x4000];
        program.CopyTo(image, 0);
        var checksum = 0x1234;
        image[0x7FE6] = (byte)checksum;
        image[0x7FE7] = (byte)(checksum >> 8);
        var complement = 0x10000 - checksum;
        image[0x7FE8] = (byte)complement;
        image[0x7FE9] = (byte)(complement >> 8);
        return image;
    }

    /// <summary>A Game Boy Advance cartridge: the ARM branch every one begins with, the
    /// fixed byte the hardware checks, and a header naming the game. There is no program
    /// in it — nothing here runs one, because the core that would is fetched at build
    /// time rather than compiled into the tests.</summary>
    public static byte[] GameBoyAdvance(string title = "TESTCART", string code = "TSTE")
    {
        var image = new byte[0x200];
        image[3] = 0xEA;                          // b <start>
        image[0xB2] = 0x96;                       // the byte a cartridge must carry
        foreach (var (character, index) in title.Take(12).Select((c, i) => (c, i)))
            image[0xA0 + index] = (byte)character;
        foreach (var (character, index) in code.Take(4).Select((c, i) => (c, i)))
            image[0xAC + index] = (byte)character;
        image[0xB0] = (byte)'0';
        image[0xB1] = (byte)'1';
        return image;
    }

    /// <summary>A GameCube disc's first block, or a Wii one's: the game code and maker,
    /// the disc number, the console's magic word and the title. Nothing follows it —
    /// no apploader, no filesystem — because nothing here boots one, and a real disc is
    /// a gigabyte and a half of somebody's game.</summary>
    public static byte[] Disc(string title = "GATHERUM TEST DISC", string code = "GTSE",
        string maker = "01", byte disc = 0, byte revision = 0, bool wii = false)
    {
        var image = new byte[0x800];
        foreach (var (character, index) in code.Take(4).Select((c, i) => (c, i)))
            image[index] = (byte)character;
        foreach (var (character, index) in maker.Take(2).Select((c, i) => (c, i)))
            image[4 + index] = (byte)character;
        image[6] = disc;
        image[7] = revision;
        if (wii)
            new byte[] { 0x5D, 0x1C, 0x9E, 0xA3 }.CopyTo(image, 0x18);
        else
            new byte[] { 0xC2, 0x33, 0x9F, 0x3D }.CopyTo(image, 0x1C);
        foreach (var (character, index) in title.Take(0x60).Select((c, i) => (c, i)))
            image[0x20 + index] = (byte)character;
        return image;
    }

    /// <summary>The same disc, as the head of an RVZ: the compressed container's own
    /// header, which says which console at $48 and records the uncompressed size at
    /// $24, followed by a copy of the disc's first 128 bytes at $58. The compressed data
    /// that would follow is left out for the same reason the disc's is.</summary>
    public static byte[] Rvz(string title = "GATHERUM TEST DISC", string code = "GTSE",
        bool wii = false, ulong discBytes = 1_459_978_240)
    {
        var image = new byte[0x800];
        "RVZ\u0001"u8.CopyTo(image);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(0x24), discBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x48), wii ? 2u : 1u);
        Disc(title, code, wii: wii).AsSpan(0, 0x80).CopyTo(image.AsSpan(0x58));
        return image;
    }

    /// <summary>A Super Nintendo cartridge. Its header sits at the end of a bank rather
    /// than the start of the file — the low half of memory for a LoROM board, the high
    /// half for a HiROM one — and what makes it findable there at all is the checksum
    /// beside its own complement, so this works that out rather than writing a constant.
    ///
    /// <para><paramref name="copierHeader"/> adds the 512 bytes some dumps carry in
    /// front, which is the one thing that shifts every offset in the file.</para></summary>
    public static byte[] SuperNintendo(string title = "GATHERUM TEST", bool hiRom = false,
        byte cartridgeType = 0x00, byte saveRamSize = 0x00, byte region = 0x01,
        bool copierHeader = false)
    {
        var image = new byte[hiRom ? 0x20000 : 0x10000];
        var at = hiRom ? 0xFFC0 : 0x7FC0;

        for (var index = 0; index < 21; index++)
            image[at + index] = (byte)(index < title.Length ? title[index] : ' ');
        image[at + 21] = (byte)(hiRom ? 0x21 : 0x20);
        image[at + 22] = cartridgeType;
        image[at + 23] = 0x05;                    // 32 KB, as a power of two
        image[at + 24] = saveRamSize;
        image[at + 25] = region;
        image[at + 26] = 0x33;                    // developer
        image[at + 27] = 0x00;                    // version
        image[at + 28] = 0xFF;                    // the complement, before it is worked out
        image[at + 29] = 0xFF;

        var total = 0;
        foreach (var b in image)
            total += b;
        total &= 0xFFFF;
        image[at + 30] = (byte)total;
        image[at + 31] = (byte)(total >> 8);
        image[at + 28] = (byte)(total ^ 0xFFFF);
        image[at + 29] = (byte)((total ^ 0xFFFF) >> 8);

        return copierHeader ? [.. new byte[512], .. image] : image;
    }

    /// <summary>A Mega Drive cartridge, or a 32X one: Sega's header at $100 with the
    /// console's name, the overseas title, the serial, where the program ends, the
    /// battery memory if the board has any, and the markets it was made for. No program
    /// in it, for the reason the Game Boy Advance fixture has none.</summary>
    public static byte[] MegaDrive(string title = "GATHERUM TEST", string serial = "GM 00001009-00",
        string region = "JUE", string console = "SEGA MEGA DRIVE", int saveRamBytes = 0,
        bool battery = false)
    {
        var image = new byte[0x10000];
        Array.Fill(image, (byte)' ', 0x100, 0x100);
        Write(image, 0x100, console);
        Write(image, 0x110, "(C)SEGA 1991.JUL");
        Write(image, 0x150, title);
        Write(image, 0x180, serial);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x1A4),
            (uint)image.Length - 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x1A8), 0xFF0000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x1AC), 0xFFFFFF);
        if (saveRamBytes > 0)
        {
            Write(image, 0x1B0, "RA");
            // Odd bytes only, as most boards were wired, so the range spans twice the
            // bytes; bit 6 is the battery.
            image[0x1B2] = (byte)(0xA0 | (battery ? 0x40 : 0) | 0x18);
            image[0x1B3] = 0x20;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x1B4), 0x200001);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x1B8),
                (uint)(0x200001 + saveRamBytes * 2 - 2));
        }
        Write(image, 0x1F0, region);
        return image;
    }

    /// <summary>A Virtual Boy cartridge: 1 KB — the console wants a power of two — with
    /// the header in the last 544 bytes, where the hardware keeps it.</summary>
    public static byte[] VirtualBoy(string title = "GATHERUM TEST", string maker = "01",
        string code = "VGTE", byte version = 0)
    {
        var image = new byte[0x400];
        var header = image.Length - 0x220;
        Array.Fill(image, (byte)' ', header, 20);
        Write(image, header, title);
        Write(image, header + 0x19, maker);
        Write(image, header + 0x1B, code);
        image[header + 0x1F] = version;
        return image;
    }

    private static void Write(byte[] image, int at, string text)
    {
        foreach (var (character, index) in text.Select((c, i) => (c, i)))
            image[at + index] = (byte)character;
    }

    public static readonly byte[] NintendoLogo =
    [
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83,
        0x00, 0x0C, 0x00, 0x0D, 0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E,
        0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99, 0xBB, 0xBB, 0x67, 0x63,
        0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E,
    ];
}
