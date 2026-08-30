namespace Gatherum.Client.Emulation.Nes;

/// <summary>The extra hardware on a cartridge board. The console can only see 32 KB of
/// program and 8 KB of pattern data at a time, so anything bigger arrived with a chip
/// that swaps banks underneath — and every game that does it does it differently. A
/// mapper is that chip: it answers for everything above $4020 and for the pattern
/// tables the PPU fetches from.</summary>
public abstract class NesMapper(NesCartridge cartridge)
{
    protected readonly NesCartridge Cartridge = cartridge;

    public virtual Mirroring Mirroring => Cartridge.HeaderMirroring;

    /// <summary>Whether the board is holding the CPU's interrupt line down. Only the
    /// mappers with a counter on them ever do.</summary>
    public virtual bool IrqPending => false;

    /// <summary>Called once a scanline while the picture is being drawn, at the point
    /// in the line where the PPU's pattern fetches would have clocked a counting
    /// mapper. The real trigger is the A12 line rising as the PPU switches from
    /// nametable to sprite fetches; watching that address bus for every fetch would
    /// cost more than it buys, and the games that use the counter use it to split the
    /// screen at a scanline boundary — which is exactly what this reproduces.</summary>
    public virtual void SignalScanline() { }

    public virtual byte CpuRead(ushort address) => address >= 0x6000 && address < 0x8000
        ? Cartridge.ProgramRam[address & 0x1FFF]
        : (byte)0;

    public virtual void CpuWrite(ushort address, byte value)
    {
        if (address >= 0x6000 && address < 0x8000)
            Cartridge.ProgramRam[address & 0x1FFF] = value;
    }

    public virtual byte PpuRead(ushort address) => Cartridge.Characters[address & 0x1FFF];

    public virtual void PpuWrite(ushort address, byte value)
    {
        if (Cartridge.CharactersAreRam)
            Cartridge.Characters[address & 0x1FFF] = value;
    }

    /// <summary>The last 16 KB of program, which most boards leave nailed to $C000 so
    /// that the reset and interrupt vectors are always reachable.</summary>
    protected int LastProgramBank16 => Cartridge.Program.Length - 16 * 1024;

    protected byte ProgramAt(int offset) =>
        Cartridge.Program[offset % Cartridge.Program.Length];

    public static NesMapper Create(NesCartridge cartridge) => cartridge.MapperNumber switch
    {
        0 => new NromMapper(cartridge),
        1 => new Mmc1Mapper(cartridge),
        2 or 71 => new UxRomMapper(cartridge),
        3 => new CnRomMapper(cartridge),
        4 => new Mmc3Mapper(cartridge),
        7 => new AxRomMapper(cartridge),
        11 => new ColorDreamsMapper(cartridge),
        66 => new GxRomMapper(cartridge),
        _ => throw new NotSupportedException(
            $"This cartridge needs mapper {cartridge.MapperNumber}, which the player " +
            "does not know how to be."),
    };
}

/// <summary>Mapper 0 — no mapper at all. The board is wired straight through: 16 or
/// 32 KB of program, 8 KB of pattern data, and nothing that moves.</summary>
public sealed class NromMapper(NesCartridge cartridge) : NesMapper(cartridge)
{
    public override byte CpuRead(ushort address) => address >= 0x8000
        ? ProgramAt(address - 0x8000)
        : base.CpuRead(address);
}

/// <summary>Mapper 1 — Nintendo's MMC1, and the reason so many NES games are 128 KB.
/// It has no address decoding to speak of: every write above $8000 shifts one bit into
/// a five-bit register, and the fifth write commits it to whichever of four registers
/// the address landed in. A write with the high bit set throws the register away, which
/// is how a game resets it without knowing how far along it was.</summary>
public sealed class Mmc1Mapper(NesCartridge cartridge) : NesMapper(cartridge)
{
    private byte shift = 0x10;
    private byte control = 0x0C;
    private byte characterBank0, characterBank1, programBank;

    public override Mirroring Mirroring => (control & 0x03) switch
    {
        0 => Mirroring.SingleScreenLow,
        1 => Mirroring.SingleScreenHigh,
        2 => Mirroring.Vertical,
        _ => Mirroring.Horizontal,
    };

    public override byte CpuRead(ushort address)
    {
        if (address < 0x8000)
            return base.CpuRead(address);
        var bank16 = 16 * 1024;
        return (control >> 2 & 0x03) switch
        {
            // A 32 KB switch ignores the bank register's low bit.
            0 or 1 => ProgramAt((programBank & 0x0E) * bank16 + (address - 0x8000)),
            2 => address < 0xC000
                ? ProgramAt(address - 0x8000)
                : ProgramAt(programBank * bank16 + (address - 0xC000)),
            _ => address < 0xC000
                ? ProgramAt(programBank * bank16 + (address - 0x8000))
                : ProgramAt(LastProgramBank16 + (address - 0xC000)),
        };
    }

    public override void CpuWrite(ushort address, byte value)
    {
        if (address < 0x8000)
        {
            base.CpuWrite(address, value);
            return;
        }
        if ((value & 0x80) != 0)
        {
            shift = 0x10;
            control |= 0x0C;
            return;
        }

        var complete = (shift & 1) != 0;
        shift = (byte)(shift >> 1 | (value & 1) << 4);
        if (!complete)
            return;

        var loaded = (byte)(shift & 0x1F);
        shift = 0x10;
        switch (address & 0xE000)
        {
            case 0x8000: control = loaded; break;
            case 0xA000: characterBank0 = loaded; break;
            case 0xC000: characterBank1 = loaded; break;
            default: programBank = loaded; break;
        }
    }

    public override byte PpuRead(ushort address) =>
        Cartridge.Characters[CharacterOffset(address)];

    public override void PpuWrite(ushort address, byte value)
    {
        if (Cartridge.CharactersAreRam)
            Cartridge.Characters[CharacterOffset(address)] = value;
    }

    private int CharacterOffset(ushort address)
    {
        var offset = (control & 0x10) != 0
            ? (address < 0x1000 ? characterBank0 : characterBank1) * 4 * 1024
                + (address & 0x0FFF)
            : (characterBank0 & 0x1E) * 4 * 1024 + (address & 0x1FFF);
        return offset % Cartridge.Characters.Length;
    }
}

/// <summary>Mappers 2 and 71 — a switchable 16 KB window at $8000 with the last bank
/// nailed down at $C000, and pattern data in RAM the game draws into itself.</summary>
public sealed class UxRomMapper(NesCartridge cartridge) : NesMapper(cartridge)
{
    private int bank;

    public override byte CpuRead(ushort address) => address switch
    {
        < 0x8000 => base.CpuRead(address),
        < 0xC000 => ProgramAt(bank * 16 * 1024 + (address - 0x8000)),
        _ => ProgramAt(LastProgramBank16 + (address - 0xC000)),
    };

    public override void CpuWrite(ushort address, byte value)
    {
        if (address >= 0x8000)
            bank = value & 0x0F;
        else
            base.CpuWrite(address, value);
    }
}

/// <summary>Mapper 3 — the program is fixed and only the pattern tables move, which is
/// how a small game affords more art than fits in 8 KB.</summary>
public sealed class CnRomMapper(NesCartridge cartridge) : NesMapper(cartridge)
{
    private int bank;

    public override byte CpuRead(ushort address) => address >= 0x8000
        ? ProgramAt(address - 0x8000)
        : base.CpuRead(address);

    public override void CpuWrite(ushort address, byte value)
    {
        if (address >= 0x8000)
            bank = value & 0x03;
        else
            base.CpuWrite(address, value);
    }

    public override byte PpuRead(ushort address) =>
        Cartridge.Characters[(bank * 8 * 1024 + (address & 0x1FFF)) % Cartridge.Characters.Length];

    public override void PpuWrite(ushort address, byte value)
    {
        if (Cartridge.CharactersAreRam)
            Cartridge.Characters[(bank * 8 * 1024 + (address & 0x1FFF))
                % Cartridge.Characters.Length] = value;
    }
}

/// <summary>Mapper 7 — 32 KB of program at a time and a single nametable, which is what
/// Rare's games used instead of scrolling in two directions.</summary>
public sealed class AxRomMapper(NesCartridge cartridge) : NesMapper(cartridge)
{
    private int bank;
    private bool high;

    public override Mirroring Mirroring =>
        high ? Mirroring.SingleScreenHigh : Mirroring.SingleScreenLow;

    public override byte CpuRead(ushort address) => address >= 0x8000
        ? ProgramAt(bank * 32 * 1024 + (address - 0x8000))
        : base.CpuRead(address);

    public override void CpuWrite(ushort address, byte value)
    {
        if (address < 0x8000)
        {
            base.CpuWrite(address, value);
            return;
        }
        bank = value & 0x07;
        high = (value & 0x10) != 0;
    }
}

/// <summary>Mapper 11 — Color Dreams' unlicensed board: program and pattern banks in
/// one byte, no protection, no counter.</summary>
public sealed class ColorDreamsMapper(NesCartridge cartridge) : NesMapper(cartridge)
{
    private int programBank, characterBank;

    public override byte CpuRead(ushort address) => address >= 0x8000
        ? ProgramAt(programBank * 32 * 1024 + (address - 0x8000))
        : base.CpuRead(address);

    public override void CpuWrite(ushort address, byte value)
    {
        if (address < 0x8000)
        {
            base.CpuWrite(address, value);
            return;
        }
        programBank = value & 0x03;
        characterBank = value >> 4 & 0x0F;
    }

    public override byte PpuRead(ushort address) =>
        Cartridge.Characters[(characterBank * 8 * 1024 + (address & 0x1FFF))
            % Cartridge.Characters.Length];
}

/// <summary>Mapper 66 — the same idea as 11 with the two halves of the byte swapped.</summary>
public sealed class GxRomMapper(NesCartridge cartridge) : NesMapper(cartridge)
{
    private int programBank, characterBank;

    public override byte CpuRead(ushort address) => address >= 0x8000
        ? ProgramAt(programBank * 32 * 1024 + (address - 0x8000))
        : base.CpuRead(address);

    public override void CpuWrite(ushort address, byte value)
    {
        if (address < 0x8000)
        {
            base.CpuWrite(address, value);
            return;
        }
        programBank = value >> 4 & 0x03;
        characterBank = value & 0x03;
    }

    public override byte PpuRead(ushort address) =>
        Cartridge.Characters[(characterBank * 8 * 1024 + (address & 0x1FFF))
            % Cartridge.Characters.Length];
}

/// <summary>Mapper 4 — Nintendo's MMC3, the board most of the late library shipped on.
/// Two 8 KB program windows move, two are fixed, six pattern windows move, and there is
/// a scanline counter on it: the game loads a line number and gets an interrupt when the
/// beam reaches it, which is how a status bar stays still while the world scrolls under
/// it.</summary>
public sealed class Mmc3Mapper(NesCartridge cartridge) : NesMapper(cartridge)
{
    private readonly byte[] banks = new byte[8];
    private byte select;
    private bool horizontal = true;
    private byte irqLatch, irqCounter;
    private bool irqEnabled, irqReload, irqAsserted;

    public override Mirroring Mirroring => Cartridge.HeaderMirroring == Mirroring.FourScreen
        ? Mirroring.FourScreen
        : horizontal ? Mirroring.Horizontal : Mirroring.Vertical;

    public override bool IrqPending => irqAsserted;

    public override void SignalScanline()
    {
        if (irqCounter == 0 || irqReload)
        {
            irqCounter = irqLatch;
            irqReload = false;
        }
        else
        {
            irqCounter--;
        }
        if (irqCounter == 0 && irqEnabled)
            irqAsserted = true;
    }

    public override byte CpuRead(ushort address)
    {
        if (address < 0x8000)
            return base.CpuRead(address);
        var bank8 = 8 * 1024;
        var last = Cartridge.Program.Length - bank8;
        var secondLast = Cartridge.Program.Length - 2 * bank8;
        var swapped = (select & 0x40) != 0;
        var offset = (address - 0x8000) & 0x1FFF;
        return (address & 0xE000) switch
        {
            0x8000 => ProgramAt((swapped ? secondLast : banks[6] * bank8) + offset),
            0xA000 => ProgramAt(banks[7] * bank8 + offset),
            0xC000 => ProgramAt((swapped ? banks[6] * bank8 : secondLast) + offset),
            _ => ProgramAt(last + offset),
        };
    }

    public override void CpuWrite(ushort address, byte value)
    {
        if (address < 0x8000)
        {
            base.CpuWrite(address, value);
            return;
        }
        var odd = (address & 1) != 0;
        switch (address & 0xE000)
        {
            case 0x8000:
                if (odd)
                    banks[select & 0x07] = value;
                else
                    select = value;
                break;
            case 0xA000:
                if (!odd)
                    horizontal = (value & 1) != 0;
                break;
            case 0xC000:
                if (odd)
                    irqReload = true;
                else
                    irqLatch = value;
                break;
            default:
                irqEnabled = odd;
                if (!odd)
                    irqAsserted = false;
                break;
        }
    }

    public override byte PpuRead(ushort address) =>
        Cartridge.Characters[CharacterOffset(address)];

    public override void PpuWrite(ushort address, byte value)
    {
        if (Cartridge.CharactersAreRam)
            Cartridge.Characters[CharacterOffset(address)] = value;
    }

    /// <summary>Two 2 KB windows and four 1 KB ones, in whichever of the two orders
    /// bit 7 of the select register asks for.</summary>
    private int CharacterOffset(ushort address)
    {
        var slot = address >> 10 & 0x07;
        if ((select & 0x80) != 0)
            slot ^= 4;
        var offset = slot switch
        {
            0 or 1 => (banks[0] & 0xFE) * 1024 + (slot & 1) * 1024,
            2 or 3 => (banks[1] & 0xFE) * 1024 + (slot & 1) * 1024,
            _ => banks[slot - 2] * 1024,
        };
        return (offset + (address & 0x03FF)) % Cartridge.Characters.Length;
    }
}
