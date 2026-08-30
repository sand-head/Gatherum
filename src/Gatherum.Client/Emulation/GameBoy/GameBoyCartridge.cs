namespace Gatherum.Client.Emulation.GameBoy;

/// <summary>Which memory bank controller is on the board. Unlike the NES's mappers,
/// these are variations on one idea — a bank register at the bottom of the address
/// space and some RAM behind an enable — so they are one class with a shape, not a
/// family of classes.</summary>
public enum MemoryBankController
{
    None,
    Mbc1,
    Mbc2,
    Mbc3,
    Mbc5,
}

/// <summary>A Game Boy cartridge: the ROM, whatever RAM the board carries, and the
/// banking hardware that decides which slice of each is visible.</summary>
public sealed class GameBoyCartridge
{
    private readonly byte[] rom;
    private readonly byte[] ram;
    private readonly MemoryBankController controller;
    private readonly int romBanks;
    private readonly int ramBanks;

    private int romBank = 1;
    private int ramBank;
    private bool ramEnabled;

    /// <summary>MBC1's one genuinely confusing register: the upper two bank bits are
    /// either the top of the ROM bank number or the RAM bank number, depending on this.</summary>
    private bool advancedBanking;
    private int upperBank;

    private readonly byte[] clock = new byte[5];
    private readonly byte[] latchedClock = new byte[5];
    private byte lastLatchWrite = 0xFF;
    private long clockCycles;

    public bool Battery { get; }
    public bool Colour { get; }
    public string Title { get; }
    public bool SaveDirty { get; private set; }

    public GameBoyCartridge(ReadOnlySpan<byte> image)
    {
        if (image.Length < 0x150)
            throw new NotSupportedException("This cartridge image is too short to be a Game Boy ROM.");

        rom = image.ToArray();
        var type = rom[0x147];
        controller = type switch
        {
            0x00 or 0x08 or 0x09 => MemoryBankController.None,
            <= 0x03 => MemoryBankController.Mbc1,
            0x05 or 0x06 => MemoryBankController.Mbc2,
            >= 0x0F and <= 0x13 => MemoryBankController.Mbc3,
            >= 0x19 and <= 0x1E => MemoryBankController.Mbc5,
            _ => throw new NotSupportedException(
                $"This cartridge's board (type {type:X2}) is one the player does not know."),
        };

        Battery = type is 0x03 or 0x06 or 0x09 or 0x0D or 0x0F or 0x10 or 0x13 or 0x1B
            or 0x1E or 0x22 or 0xFF;
        Colour = rom[0x143] is 0x80 or 0xC0;

        var titleBytes = rom.AsSpan(0x134, Colour ? 15 : 16);
        var end = titleBytes.IndexOf((byte)0);
        Title = System.Text.Encoding.ASCII
            .GetString(end < 0 ? titleBytes : titleBytes[..end]).Trim();

        romBanks = Math.Max(2, rom.Length / 0x4000);
        var ramSize = controller == MemoryBankController.Mbc2
            ? 512
            : rom[0x149] switch
            {
                2 => 8 * 1024,
                3 => 32 * 1024,
                4 => 128 * 1024,
                5 => 64 * 1024,
                _ => 0,
            };
        ram = new byte[ramSize];
        ramBanks = Math.Max(1, ramSize / 0x2000);
    }

    public byte[] SaveRam() => Battery && ram.Length > 0 ? ram.ToArray() : [];

    public void LoadSaveRam(ReadOnlySpan<byte> data)
    {
        if (ram.Length == 0 || data.Length == 0)
            return;
        data[..Math.Min(data.Length, ram.Length)].CopyTo(ram);
        SaveDirty = false;
    }

    public void MarkSaved() => SaveDirty = false;

    /// <summary>The real-time clock on an MBC3 board keeps counting with the console
    /// off, on its own battery. Here it counts machine cycles, which is the only clock
    /// this player has and is what makes a game's in-world day match a played one.</summary>
    public void Tick(int cycles)
    {
        if (controller != MemoryBankController.Mbc3 || (clock[4] & 0x40) != 0)
            return;
        clockCycles += cycles;
        while (clockCycles >= 4194304)
        {
            clockCycles -= 4194304;
            AdvanceClockBySecond();
        }
    }

    private void AdvanceClockBySecond()
    {
        if (++clock[0] < 60)
            return;
        clock[0] = 0;
        if (++clock[1] < 60)
            return;
        clock[1] = 0;
        if (++clock[2] < 24)
            return;
        clock[2] = 0;
        if (++clock[3] != 0)
            return;
        // Day 512 sets the overflow bit and starts over, which is the whole of the
        // clock's range.
        clock[4] = (byte)((clock[4] & 0x40) | ((clock[4] & 0x01) != 0 ? 0x80 : 0x01));
    }

    public byte Read(ushort address)
    {
        if (address < 0x4000)
        {
            // MBC1's advanced mode moves even the first bank, which is how the
            // one-megabyte cartridges reach their upper half.
            var bank = controller == MemoryBankController.Mbc1 && advancedBanking
                ? (upperBank << 5) % romBanks
                : 0;
            return rom[(bank * 0x4000 + address) % rom.Length];
        }
        if (address < 0x8000)
            return rom[(romBank * 0x4000 + (address - 0x4000)) % rom.Length];

        if (!ramEnabled)
            return 0xFF;
        if (controller == MemoryBankController.Mbc3 && ramBank >= 0x08)
            return latchedClock[Math.Min(ramBank - 0x08, 4)];
        if (ram.Length == 0)
            return 0xFF;
        // MBC2's memory is 512 half-bytes; the top nibble reads back as ones.
        if (controller == MemoryBankController.Mbc2)
            return (byte)(ram[address & 0x01FF] | 0xF0);
        return ram[(ramBank * 0x2000 + (address - 0xA000)) % ram.Length];
    }

    public void Write(ushort address, byte value)
    {
        if (address < 0x8000)
        {
            WriteControl(address, value);
            return;
        }
        if (!ramEnabled)
            return;
        if (controller == MemoryBankController.Mbc3 && ramBank >= 0x08)
        {
            clock[Math.Min(ramBank - 0x08, 4)] = value;
            SaveDirty = true;
            return;
        }
        if (ram.Length == 0)
            return;
        if (controller == MemoryBankController.Mbc2)
            ram[address & 0x01FF] = (byte)(value & 0x0F);
        else
            ram[(ramBank * 0x2000 + (address - 0xA000)) % ram.Length] = value;
        SaveDirty = true;
    }

    private void WriteControl(ushort address, byte value)
    {
        switch (controller)
        {
            case MemoryBankController.None:
                return;

            case MemoryBankController.Mbc2:
                // One address line picks between the two registers this board has.
                if (address < 0x4000)
                {
                    if ((address & 0x0100) != 0)
                        romBank = Math.Max(1, value & 0x0F) % romBanks;
                    else
                        ramEnabled = (value & 0x0F) == 0x0A;
                }
                return;

            case MemoryBankController.Mbc1:
                switch (address)
                {
                    case < 0x2000:
                        ramEnabled = (value & 0x0F) == 0x0A;
                        break;
                    case < 0x4000:
                        // Bank zero is not selectable: asking for it gets bank one,
                        // which is why a 512 KB cartridge has four unreachable banks.
                        romBank = ((value & 0x1F) == 0 ? 1 : value & 0x1F)
                            | (advancedBanking ? 0 : upperBank << 5);
                        romBank %= romBanks;
                        if (romBank == 0)
                            romBank = 1;
                        break;
                    case < 0x6000:
                        upperBank = value & 0x03;
                        if (advancedBanking)
                            ramBank = ramBanks > 1 ? upperBank : 0;
                        else
                            romBank = (romBank & 0x1F | upperBank << 5) % romBanks;
                        break;
                    default:
                        advancedBanking = (value & 1) != 0;
                        ramBank = advancedBanking && ramBanks > 1 ? upperBank : 0;
                        break;
                }
                return;

            case MemoryBankController.Mbc3:
                switch (address)
                {
                    case < 0x2000:
                        ramEnabled = (value & 0x0F) == 0x0A;
                        break;
                    case < 0x4000:
                        romBank = Math.Max(1, value & 0x7F) % romBanks;
                        if (romBank == 0)
                            romBank = 1;
                        break;
                    case < 0x6000:
                        ramBank = value <= 0x03 ? value % ramBanks : value;
                        break;
                    default:
                        // Zero then one freezes the clock registers so a game can read
                        // five bytes without a second ticking under it.
                        if (lastLatchWrite == 0 && value == 1)
                            clock.CopyTo(latchedClock, 0);
                        lastLatchWrite = value;
                        break;
                }
                return;

            default:
                switch (address)
                {
                    case < 0x2000:
                        ramEnabled = (value & 0x0F) == 0x0A;
                        break;
                    case < 0x3000:
                        romBank = (romBank & 0x100 | value) % romBanks;
                        break;
                    case < 0x4000:
                        romBank = (romBank & 0xFF | (value & 1) << 8) % romBanks;
                        break;
                    case < 0x6000:
                        ramBank = (value & 0x0F) % ramBanks;
                        break;
                }
                return;
        }
    }
}
