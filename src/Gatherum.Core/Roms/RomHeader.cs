using System.Text;

namespace Gatherum.Core.Roms;

/// <summary>Which machine a cartridge image is for. The player picks a core by this,
/// and it is the one thing a ROM's own bytes always say about themselves.</summary>
public enum RomSystem
{
    Nes,
    GameBoy,
    MasterSystem,
    GameGear,
}

/// <summary>What a cartridge image says about itself before anything runs it: the
/// console, the name printed in the header (Game Boy carts carry one; iNES files do
/// not), the mapper hardware the board needs, and how much of it there is.
///
/// This is identification, not emulation — the player has its own cartridge loader,
/// because a core has to understand banking in a way a search index never will. What
/// lives here is only what makes a ROM findable and legible in a listing.</summary>
public sealed record RomHeader(
    RomSystem System,
    string SystemName,
    string? Title,
    string Cartridge,
    int ProgramBytes,
    int GraphicsBytes,
    int SaveRamBytes,
    bool Battery,
    string? Region)
{
    /// <summary>Reads the header, or nothing when the bytes are not a cartridge this
    /// knows — a truncated download, or a `.gb` that is really something else.</summary>
    public static RomHeader? Read(ReadOnlySpan<byte> bytes) =>
        ReadNes(bytes) ?? ReadGameBoy(bytes) ?? ReadSega(bytes);

    /// <summary>The header as lines a person — or a search index — can read.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine($"System: {SystemName}");
        if (Title is { Length: > 0 })
            text.AppendLine($"Title: {Title}");
        text.AppendLine($"Cartridge: {Cartridge}");
        text.AppendLine($"Program: {Kilobytes(ProgramBytes)}");
        if (GraphicsBytes > 0)
            text.AppendLine($"Graphics: {Kilobytes(GraphicsBytes)}");
        if (SaveRamBytes > 0)
            text.AppendLine($"Save memory: {Kilobytes(SaveRamBytes)}");
        text.AppendLine(Battery ? "Saves: battery-backed" : "Saves: none");
        if (Region is { Length: > 0 })
            text.AppendLine($"Region: {Region}");
        return text.ToString();
    }

    private static string Kilobytes(int bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024)} MB" : $"{bytes / 1024} KB";

    // ---- iNES / NES 2.0 --------------------------------------------------------

    private static RomHeader? ReadNes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16 || bytes[0] != 'N' || bytes[1] != 'E' || bytes[2] != 'S'
            || bytes[3] != 0x1A)
            return null;

        var flags6 = bytes[6];
        var flags7 = bytes[7];
        var nes20 = (flags7 & 0x0C) == 0x08;
        var mapper = (flags6 >> 4) | (flags7 & 0xF0);
        if (nes20)
            mapper |= (bytes[8] & 0x0F) << 8;

        // NES 2.0 splits each size across a byte and a nibble; an exponent form
        // (the nibble at 0xF) exists for oversized boards and is left to say so
        // rather than guessed at.
        var prgBanks = bytes[4] | (nes20 ? (bytes[9] & 0x0F) << 8 : 0);
        var chrBanks = bytes[5] | (nes20 ? (bytes[9] & 0xF0) << 4 : 0);
        var battery = (flags6 & 0x02) != 0;

        var saveRam = 0;
        if (nes20)
        {
            // Byte 10's high nibble is the battery-backed half, as a shift count.
            var shift = (bytes[10] & 0xF0) >> 4;
            saveRam = shift == 0 ? 0 : 64 << shift;
        }
        else if (battery)
        {
            saveRam = 8 * 1024;
        }

        var region = nes20
            ? (bytes[12] & 0x03) switch
            {
                0 => "NTSC",
                1 => "PAL",
                2 => "NTSC and PAL",
                _ => "Dendy",
            }
            : null;

        return new RomHeader(RomSystem.Nes, "Nintendo Entertainment System", null,
            NesBoard(mapper), prgBanks * 16 * 1024,
            chrBanks == 0 ? 8 * 1024 : chrBanks * 8 * 1024, saveRam, battery, region);
    }

    /// <summary>What the board is called, for the mappers with names worth printing;
    /// everything else is honestly just its number.</summary>
    private static string NesBoard(int mapper) => mapper switch
    {
        0 => "NROM (mapper 0)",
        1 => "MMC1 (mapper 1)",
        2 => "UxROM (mapper 2)",
        3 => "CNROM (mapper 3)",
        4 => "MMC3 (mapper 4)",
        5 => "MMC5 (mapper 5)",
        7 => "AxROM (mapper 7)",
        9 => "MMC2 (mapper 9)",
        10 => "MMC4 (mapper 10)",
        11 => "Color Dreams (mapper 11)",
        66 => "GxROM (mapper 66)",
        69 => "Sunsoft FME-7 (mapper 69)",
        71 => "Camerica (mapper 71)",
        _ => $"Mapper {mapper}",
    };

    // ---- Game Boy --------------------------------------------------------------

    /// <summary>The logo the boot ROM checks. A cartridge that does not carry it byte
    /// for byte would not start on the hardware either, which makes it the one honest
    /// test that a file is a Game Boy ROM at all — there is no magic number.</summary>
    private static ReadOnlySpan<byte> NintendoLogo =>
    [
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83,
        0x00, 0x0C, 0x00, 0x0D, 0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E,
        0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99, 0xBB, 0xBB, 0x67, 0x63,
        0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E,
    ];

    private static RomHeader? ReadGameBoy(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 0x150 || !bytes.Slice(0x104, 48).SequenceEqual(NintendoLogo))
            return null;

        var colour = bytes[0x143] is 0x80 or 0xC0;
        var colourOnly = bytes[0x143] == 0xC0;
        var superGameBoy = bytes[0x146] == 0x03;

        // The title field lost bytes to the colour flag and the manufacturer code as
        // the format aged; trailing padding is zeroes either way, so reading to the
        // first zero is what every revision agrees on.
        var titleBytes = bytes.Slice(0x134, colour ? 15 : 16);
        var end = titleBytes.IndexOf((byte)0);
        var title = Encoding.ASCII
            .GetString(end < 0 ? titleBytes : titleBytes[..end])
            .Trim();

        var cartridgeType = bytes[0x147];
        var programBytes = 32 * 1024 << bytes[0x148];
        var saveRam = bytes[0x149] switch
        {
            2 => 8 * 1024,
            3 => 32 * 1024,
            4 => 128 * 1024,
            5 => 64 * 1024,
            _ => 0,
        };
        // MBC2's 512 half-bytes are on the mapper itself, so the size byte is zero
        // and would otherwise read as a cartridge that cannot save at all.
        if (cartridgeType is 0x05 or 0x06)
            saveRam = 512;

        var name = colourOnly ? "Game Boy Color"
            : colour ? "Game Boy Color (also plays on Game Boy)"
            : superGameBoy ? "Game Boy (Super Game Boy enhanced)"
            : "Game Boy";

        return new RomHeader(RomSystem.GameBoy, name, title.Length == 0 ? null : title,
            GameBoyBoard(cartridgeType), programBytes, 0, saveRam,
            GameBoyBattery(cartridgeType),
            bytes[0x14A] == 0 ? "Japan" : "Overseas");
    }

    // ---- Master System and Game Gear -------------------------------------------

    /// <summary>Sega's header sits near the end of the first bank rather than at the
    /// start of the file — at whichever of three places left room on the cartridge —
    /// and it carries no title at all. What it does carry is the product code the game
    /// was catalogued under and the region it was sold in, which together are what
    /// makes one findable.</summary>
    private static RomHeader? ReadSega(ReadOnlySpan<byte> bytes)
    {
        foreach (var at in (ReadOnlySpan<int>)[0x1FF0, 0x3FF0, 0x7FF0])
        {
            if (bytes.Length < at + 16 || !bytes.Slice(at, 8).SequenceEqual("TMR SEGA"u8))
                continue;

            var header = bytes.Slice(at, 16);
            var region = header[15] >> 4;
            var handheld = region >= 5;

            // Five digits packed as binary-coded decimal, with the last one squeezed
            // into the nibble above the version number.
            var code = (header[14] >> 4) * 10000
                + FromDecimal(header[13]) * 100
                + FromDecimal(header[12]);

            return new RomHeader(
                handheld ? RomSystem.GameGear : RomSystem.MasterSystem,
                handheld ? "Game Gear" : "Master System",
                null,
                // Nothing in the header names the board; the paging hardware is worked
                // out from the file itself when a game is loaded, not from here.
                $"Product {code:00000}, version {header[14] & 0x0F}",
                SegaRomBytes(header[15] & 0x0F, bytes.Length),
                0, 0, false,
                region switch
                {
                    3 or 5 => "Japan",
                    4 => "Export",
                    6 => "Export",
                    7 => "International",
                    _ => null,
                });
        }
        return null;
    }

    private static int FromDecimal(byte packed) => (packed >> 4) * 10 + (packed & 0x0F);

    /// <summary>The size nibble stops being meaningful above a megabyte and several
    /// cartridges lie about it outright, so the file's own length wins wherever the two
    /// disagree.</summary>
    private static int SegaRomBytes(int code, int fileLength) => code switch
    {
        0xA => 8 * 1024,
        0xB => 16 * 1024,
        0xC => 32 * 1024,
        0xD => 48 * 1024,
        0xE => 64 * 1024,
        0xF => 128 * 1024,
        0x0 => 256 * 1024,
        0x1 => 512 * 1024,
        0x2 => 1024 * 1024,
        _ => fileLength,
    };

    private static bool GameBoyBattery(byte type) => type is 0x03 or 0x06 or 0x09 or 0x0D
        or 0x0F or 0x10 or 0x13 or 0x1B or 0x1E or 0x22 or 0xFF;

    private static string GameBoyBoard(byte type) => type switch
    {
        0x00 => "ROM only",
        0x01 => "MBC1",
        0x02 => "MBC1 with RAM",
        0x03 => "MBC1 with battery-backed RAM",
        0x05 => "MBC2",
        0x06 => "MBC2 with battery",
        0x08 => "ROM with RAM",
        0x09 => "ROM with battery-backed RAM",
        0x0B or 0x0C or 0x0D => "MMM01",
        0x0F => "MBC3 with timer and battery",
        0x10 => "MBC3 with timer, RAM and battery",
        0x11 => "MBC3",
        0x12 => "MBC3 with RAM",
        0x13 => "MBC3 with battery-backed RAM",
        0x19 => "MBC5",
        0x1A => "MBC5 with RAM",
        0x1B => "MBC5 with battery-backed RAM",
        0x1C => "MBC5 with rumble",
        0x1D => "MBC5 with rumble and RAM",
        0x1E => "MBC5 with rumble and battery-backed RAM",
        0x20 => "MBC6",
        0x22 => "MBC7 with tilt, battery-backed RAM",
        0xFC => "Pocket Camera",
        0xFD => "Bandai TAMA5",
        0xFE => "HuC3",
        0xFF => "HuC1 with battery-backed RAM",
        _ => $"Cartridge type {type:X2}",
    };
}
