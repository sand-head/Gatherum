namespace Gatherum.Client.Emulation.Nes;

/// <summary>The 2A03's 6502, without the decimal mode Nintendo left off the die.
///
/// Every memory access ticks the rest of the console forward, which is what keeps the
/// picture in step with the program: a game that changes the scroll position partway
/// down the screen is relying on the PPU having advanced exactly as far as the write
/// that moved it, and an instruction executed atomically would put the seam in the
/// wrong place. So the addressing modes here perform the dummy reads the real chip
/// performs, and the cycle counts fall out of them rather than being looked up.</summary>
public sealed class Cpu6502(NesConsole bus)
{
    public byte A, X, Y, S;
    public ushort PC;
    public bool Carry, Zero, InterruptDisable, DecimalMode, Overflow, Negative;

    /// <summary>NMI is edge-triggered: the PPU's vblank flag going up is what arms it,
    /// and the flag standing there afterwards is not.</summary>
    private bool nmiArmed;
    private bool nmiLast;

    /// <summary>IRQ is level-triggered — the APU's frame counter and the mapper hold
    /// the line down until something acknowledges them.</summary>
    public bool IrqLine;

    /// <summary>Cycles the DMC's sample fetch stole from the program. The unit runs on
    /// the APU's clock, inside a tick, so it cannot stall the bus from there; it leaves
    /// the debt here and the next instruction pays it.</summary>
    public int StallCycles;

    public void Reset()
    {
        A = X = Y = 0;
        S = 0xFD;
        Carry = Zero = DecimalMode = Overflow = Negative = false;
        InterruptDisable = true;
        nmiArmed = nmiLast = false;
        // Nothing is asserting an interrupt at reset: the devices that drive this line
        // have just been reset too, and a stale level here would fire a spurious one.
        IrqLine = false;
        StallCycles = 0;
        PC = (ushort)(bus.CpuRead(0xFFFC) | bus.CpuRead(0xFFFD) << 8);
    }

    /// <summary>The PPU's NMI output, sampled every tick. Only the rising edge arms.</summary>
    public void SetNmi(bool level)
    {
        if (level && !nmiLast)
            nmiArmed = true;
        nmiLast = level;
    }

    public byte StatusByte(bool brk) => (byte)(
        (Carry ? 0x01 : 0) | (Zero ? 0x02 : 0) | (InterruptDisable ? 0x04 : 0) |
        (DecimalMode ? 0x08 : 0) | (brk ? 0x10 : 0) | 0x20 |
        (Overflow ? 0x40 : 0) | (Negative ? 0x80 : 0));

    private void SetStatus(byte value)
    {
        Carry = (value & 0x01) != 0;
        Zero = (value & 0x02) != 0;
        InterruptDisable = (value & 0x04) != 0;
        DecimalMode = (value & 0x08) != 0;
        Overflow = (value & 0x40) != 0;
        Negative = (value & 0x80) != 0;
    }

    internal void Save(ref StateWriter state)
    {
        state.Write(A);
        state.Write(X);
        state.Write(Y);
        state.Write(S);
        state.Write(PC);
        state.Write(StatusByte(brk: false));
        state.Write(nmiArmed);
        state.Write(nmiLast);
        state.Write(IrqLine);
        state.Write(StallCycles);
    }

    internal void Load(ref StateReader state)
    {
        A = state.ReadByte();
        X = state.ReadByte();
        Y = state.ReadByte();
        S = state.ReadByte();
        PC = state.ReadUInt16();
        SetStatus(state.ReadByte());
        nmiArmed = state.ReadBool();
        nmiLast = state.ReadBool();
        IrqLine = state.ReadBool();
        StallCycles = state.ReadInt32();
    }

    private byte Read(ushort address)
    {
        bus.Tick();
        return bus.CpuRead(address);
    }

    private void Write(ushort address, byte value)
    {
        bus.Tick();
        bus.CpuWrite(address, value);
    }

    private byte Fetch() => Read(PC++);

    private ushort Fetch16()
    {
        var low = Fetch();
        return (ushort)(low | Fetch() << 8);
    }

    private void Push(byte value) => Write((ushort)(0x0100 | S--), value);

    private byte Pull()
    {
        S++;
        return Read((ushort)(0x0100 | S));
    }

    /// <summary>Runs one instruction, or takes an interrupt if one is waiting.</summary>
    public void Step()
    {
        while (StallCycles > 0)
        {
            StallCycles--;
            bus.Tick();
        }

        if (nmiArmed)
        {
            nmiArmed = false;
            Interrupt(0xFFFA);
            return;
        }
        if (IrqLine && !InterruptDisable)
        {
            Interrupt(0xFFFE);
            return;
        }
        Execute(Fetch());
    }

    private void Interrupt(ushort vector)
    {
        Read(PC);
        Read(PC);
        Push((byte)(PC >> 8));
        Push((byte)PC);
        Push(StatusByte(brk: false));
        InterruptDisable = true;
        var low = Read(vector);
        PC = (ushort)(low | Read((ushort)(vector + 1)) << 8);
    }

    // ---- addressing ------------------------------------------------------------

    private ushort ZeroPage() => Fetch();

    private ushort ZeroPageX()
    {
        var page = Fetch();
        Read(page);
        return (byte)(page + X);
    }

    private ushort ZeroPageY()
    {
        var page = Fetch();
        Read(page);
        return (byte)(page + Y);
    }

    private ushort Absolute() => Fetch16();

    private ushort AbsoluteIndexed(byte index, bool alwaysPenalise)
    {
        var baseAddress = Fetch16();
        var address = (ushort)(baseAddress + index);
        if (alwaysPenalise || (baseAddress & 0xFF00) != (address & 0xFF00))
            Read((ushort)((baseAddress & 0xFF00) | (address & 0x00FF)));
        return address;
    }

    private ushort IndexedIndirect()
    {
        var pointer = Fetch();
        Read(pointer);
        var low = Read((byte)(pointer + X));
        return (ushort)(low | Read((byte)(pointer + X + 1)) << 8);
    }

    private ushort IndirectIndexed(bool alwaysPenalise)
    {
        var pointer = Fetch();
        var low = Read(pointer);
        var baseAddress = (ushort)(low | Read((byte)(pointer + 1)) << 8);
        var address = (ushort)(baseAddress + Y);
        if (alwaysPenalise || (baseAddress & 0xFF00) != (address & 0xFF00))
            Read((ushort)((baseAddress & 0xFF00) | (address & 0x00FF)));
        return address;
    }

    // ---- operations ------------------------------------------------------------

    private byte SetZeroNegative(byte value)
    {
        Zero = value == 0;
        Negative = (value & 0x80) != 0;
        return value;
    }

    private void Compare(byte register, byte value)
    {
        Carry = register >= value;
        SetZeroNegative((byte)(register - value));
    }

    private void AddWithCarry(byte value)
    {
        var sum = A + value + (Carry ? 1 : 0);
        Carry = sum > 0xFF;
        Overflow = ((A ^ sum) & (value ^ sum) & 0x80) != 0;
        A = SetZeroNegative((byte)sum);
    }

    private void Branch(bool take)
    {
        var offset = (sbyte)Fetch();
        if (!take)
            return;
        Read(PC);
        var target = (ushort)(PC + offset);
        if ((target & 0xFF00) != (PC & 0xFF00))
            Read((ushort)((PC & 0xFF00) | (target & 0x00FF)));
        PC = target;
    }

    /// <summary>A read-modify-write's middle cycle really does write the old value
    /// back before the new one. Mappers latch on writes, so the extra one matters.</summary>
    private byte Modify(ushort address, Func<byte, byte> operation)
    {
        var value = Read(address);
        Write(address, value);
        var result = operation(value);
        Write(address, result);
        return result;
    }

    private byte Asl(byte value)
    {
        Carry = (value & 0x80) != 0;
        return SetZeroNegative((byte)(value << 1));
    }

    private byte Lsr(byte value)
    {
        Carry = (value & 0x01) != 0;
        return SetZeroNegative((byte)(value >> 1));
    }

    private byte Rol(byte value)
    {
        var carried = Carry;
        Carry = (value & 0x80) != 0;
        return SetZeroNegative((byte)(value << 1 | (carried ? 1 : 0)));
    }

    private byte Ror(byte value)
    {
        var carried = Carry;
        Carry = (value & 0x01) != 0;
        return SetZeroNegative((byte)(value >> 1 | (carried ? 0x80 : 0)));
    }

    private void Bit(byte value)
    {
        Zero = (A & value) == 0;
        Overflow = (value & 0x40) != 0;
        Negative = (value & 0x80) != 0;
    }

    private void Execute(byte opcode)
    {
        switch (opcode)
        {
            // ---- loads and stores
            case 0xA9: A = SetZeroNegative(Fetch()); break;
            case 0xA5: A = SetZeroNegative(Read(ZeroPage())); break;
            case 0xB5: A = SetZeroNegative(Read(ZeroPageX())); break;
            case 0xAD: A = SetZeroNegative(Read(Absolute())); break;
            case 0xBD: A = SetZeroNegative(Read(AbsoluteIndexed(X, false))); break;
            case 0xB9: A = SetZeroNegative(Read(AbsoluteIndexed(Y, false))); break;
            case 0xA1: A = SetZeroNegative(Read(IndexedIndirect())); break;
            case 0xB1: A = SetZeroNegative(Read(IndirectIndexed(false))); break;

            case 0xA2: X = SetZeroNegative(Fetch()); break;
            case 0xA6: X = SetZeroNegative(Read(ZeroPage())); break;
            case 0xB6: X = SetZeroNegative(Read(ZeroPageY())); break;
            case 0xAE: X = SetZeroNegative(Read(Absolute())); break;
            case 0xBE: X = SetZeroNegative(Read(AbsoluteIndexed(Y, false))); break;

            case 0xA0: Y = SetZeroNegative(Fetch()); break;
            case 0xA4: Y = SetZeroNegative(Read(ZeroPage())); break;
            case 0xB4: Y = SetZeroNegative(Read(ZeroPageX())); break;
            case 0xAC: Y = SetZeroNegative(Read(Absolute())); break;
            case 0xBC: Y = SetZeroNegative(Read(AbsoluteIndexed(X, false))); break;

            case 0x85: Write(ZeroPage(), A); break;
            case 0x95: Write(ZeroPageX(), A); break;
            case 0x8D: Write(Absolute(), A); break;
            case 0x9D: Write(AbsoluteIndexed(X, true), A); break;
            case 0x99: Write(AbsoluteIndexed(Y, true), A); break;
            case 0x81: Write(IndexedIndirect(), A); break;
            case 0x91: Write(IndirectIndexed(true), A); break;

            case 0x86: Write(ZeroPage(), X); break;
            case 0x96: Write(ZeroPageY(), X); break;
            case 0x8E: Write(Absolute(), X); break;

            case 0x84: Write(ZeroPage(), Y); break;
            case 0x94: Write(ZeroPageX(), Y); break;
            case 0x8C: Write(Absolute(), Y); break;

            // ---- transfers
            case 0xAA: Read(PC); X = SetZeroNegative(A); break;
            case 0xA8: Read(PC); Y = SetZeroNegative(A); break;
            case 0xBA: Read(PC); X = SetZeroNegative(S); break;
            case 0x8A: Read(PC); A = SetZeroNegative(X); break;
            case 0x9A: Read(PC); S = X; break;
            case 0x98: Read(PC); A = SetZeroNegative(Y); break;

            // ---- stack
            case 0x48: Read(PC); Push(A); break;
            case 0x08: Read(PC); Push(StatusByte(brk: true)); break;
            case 0x68: Read(PC); Read((ushort)(0x0100 | S)); A = SetZeroNegative(Pull()); break;
            case 0x28: Read(PC); Read((ushort)(0x0100 | S)); SetStatus(Pull()); break;

            // ---- logic
            case 0x29: A = SetZeroNegative((byte)(A & Fetch())); break;
            case 0x25: A = SetZeroNegative((byte)(A & Read(ZeroPage()))); break;
            case 0x35: A = SetZeroNegative((byte)(A & Read(ZeroPageX()))); break;
            case 0x2D: A = SetZeroNegative((byte)(A & Read(Absolute()))); break;
            case 0x3D: A = SetZeroNegative((byte)(A & Read(AbsoluteIndexed(X, false)))); break;
            case 0x39: A = SetZeroNegative((byte)(A & Read(AbsoluteIndexed(Y, false)))); break;
            case 0x21: A = SetZeroNegative((byte)(A & Read(IndexedIndirect()))); break;
            case 0x31: A = SetZeroNegative((byte)(A & Read(IndirectIndexed(false)))); break;

            case 0x49: A = SetZeroNegative((byte)(A ^ Fetch())); break;
            case 0x45: A = SetZeroNegative((byte)(A ^ Read(ZeroPage()))); break;
            case 0x55: A = SetZeroNegative((byte)(A ^ Read(ZeroPageX()))); break;
            case 0x4D: A = SetZeroNegative((byte)(A ^ Read(Absolute()))); break;
            case 0x5D: A = SetZeroNegative((byte)(A ^ Read(AbsoluteIndexed(X, false)))); break;
            case 0x59: A = SetZeroNegative((byte)(A ^ Read(AbsoluteIndexed(Y, false)))); break;
            case 0x41: A = SetZeroNegative((byte)(A ^ Read(IndexedIndirect()))); break;
            case 0x51: A = SetZeroNegative((byte)(A ^ Read(IndirectIndexed(false)))); break;

            case 0x09: A = SetZeroNegative((byte)(A | Fetch())); break;
            case 0x05: A = SetZeroNegative((byte)(A | Read(ZeroPage()))); break;
            case 0x15: A = SetZeroNegative((byte)(A | Read(ZeroPageX()))); break;
            case 0x0D: A = SetZeroNegative((byte)(A | Read(Absolute()))); break;
            case 0x1D: A = SetZeroNegative((byte)(A | Read(AbsoluteIndexed(X, false)))); break;
            case 0x19: A = SetZeroNegative((byte)(A | Read(AbsoluteIndexed(Y, false)))); break;
            case 0x01: A = SetZeroNegative((byte)(A | Read(IndexedIndirect()))); break;
            case 0x11: A = SetZeroNegative((byte)(A | Read(IndirectIndexed(false)))); break;

            case 0x24: Bit(Read(ZeroPage())); break;
            case 0x2C: Bit(Read(Absolute())); break;

            // ---- arithmetic
            case 0x69: AddWithCarry(Fetch()); break;
            case 0x65: AddWithCarry(Read(ZeroPage())); break;
            case 0x75: AddWithCarry(Read(ZeroPageX())); break;
            case 0x6D: AddWithCarry(Read(Absolute())); break;
            case 0x7D: AddWithCarry(Read(AbsoluteIndexed(X, false))); break;
            case 0x79: AddWithCarry(Read(AbsoluteIndexed(Y, false))); break;
            case 0x61: AddWithCarry(Read(IndexedIndirect())); break;
            case 0x71: AddWithCarry(Read(IndirectIndexed(false))); break;

            case 0xE9 or 0xEB: AddWithCarry((byte)~Fetch()); break;
            case 0xE5: AddWithCarry((byte)~Read(ZeroPage())); break;
            case 0xF5: AddWithCarry((byte)~Read(ZeroPageX())); break;
            case 0xED: AddWithCarry((byte)~Read(Absolute())); break;
            case 0xFD: AddWithCarry((byte)~Read(AbsoluteIndexed(X, false))); break;
            case 0xF9: AddWithCarry((byte)~Read(AbsoluteIndexed(Y, false))); break;
            case 0xE1: AddWithCarry((byte)~Read(IndexedIndirect())); break;
            case 0xF1: AddWithCarry((byte)~Read(IndirectIndexed(false))); break;

            case 0xC9: Compare(A, Fetch()); break;
            case 0xC5: Compare(A, Read(ZeroPage())); break;
            case 0xD5: Compare(A, Read(ZeroPageX())); break;
            case 0xCD: Compare(A, Read(Absolute())); break;
            case 0xDD: Compare(A, Read(AbsoluteIndexed(X, false))); break;
            case 0xD9: Compare(A, Read(AbsoluteIndexed(Y, false))); break;
            case 0xC1: Compare(A, Read(IndexedIndirect())); break;
            case 0xD1: Compare(A, Read(IndirectIndexed(false))); break;

            case 0xE0: Compare(X, Fetch()); break;
            case 0xE4: Compare(X, Read(ZeroPage())); break;
            case 0xEC: Compare(X, Read(Absolute())); break;

            case 0xC0: Compare(Y, Fetch()); break;
            case 0xC4: Compare(Y, Read(ZeroPage())); break;
            case 0xCC: Compare(Y, Read(Absolute())); break;

            case 0xE6: Modify(ZeroPage(), Increment); break;
            case 0xF6: Modify(ZeroPageX(), Increment); break;
            case 0xEE: Modify(Absolute(), Increment); break;
            case 0xFE: Modify(AbsoluteIndexed(X, true), Increment); break;

            case 0xC6: Modify(ZeroPage(), Decrement); break;
            case 0xD6: Modify(ZeroPageX(), Decrement); break;
            case 0xCE: Modify(Absolute(), Decrement); break;
            case 0xDE: Modify(AbsoluteIndexed(X, true), Decrement); break;

            case 0xE8: Read(PC); X = SetZeroNegative((byte)(X + 1)); break;
            case 0xCA: Read(PC); X = SetZeroNegative((byte)(X - 1)); break;
            case 0xC8: Read(PC); Y = SetZeroNegative((byte)(Y + 1)); break;
            case 0x88: Read(PC); Y = SetZeroNegative((byte)(Y - 1)); break;

            // ---- shifts
            case 0x0A: Read(PC); A = Asl(A); break;
            case 0x06: Modify(ZeroPage(), Asl); break;
            case 0x16: Modify(ZeroPageX(), Asl); break;
            case 0x0E: Modify(Absolute(), Asl); break;
            case 0x1E: Modify(AbsoluteIndexed(X, true), Asl); break;

            case 0x4A: Read(PC); A = Lsr(A); break;
            case 0x46: Modify(ZeroPage(), Lsr); break;
            case 0x56: Modify(ZeroPageX(), Lsr); break;
            case 0x4E: Modify(Absolute(), Lsr); break;
            case 0x5E: Modify(AbsoluteIndexed(X, true), Lsr); break;

            case 0x2A: Read(PC); A = Rol(A); break;
            case 0x26: Modify(ZeroPage(), Rol); break;
            case 0x36: Modify(ZeroPageX(), Rol); break;
            case 0x2E: Modify(Absolute(), Rol); break;
            case 0x3E: Modify(AbsoluteIndexed(X, true), Rol); break;

            case 0x6A: Read(PC); A = Ror(A); break;
            case 0x66: Modify(ZeroPage(), Ror); break;
            case 0x76: Modify(ZeroPageX(), Ror); break;
            case 0x6E: Modify(Absolute(), Ror); break;
            case 0x7E: Modify(AbsoluteIndexed(X, true), Ror); break;

            // ---- flow
            case 0x4C: PC = Absolute(); break;
            case 0x6C:
            {
                // The indirect vector never crosses a page: a pointer at $xxFF reads
                // its high byte from $xx00, and games have shipped depending on it.
                var pointer = Fetch16();
                var low = Read(pointer);
                var high = Read((ushort)((pointer & 0xFF00) | (byte)(pointer + 1)));
                PC = (ushort)(low | high << 8);
                break;
            }
            case 0x20:
            {
                var low = Fetch();
                Read((ushort)(0x0100 | S));
                Push((byte)(PC >> 8));
                Push((byte)PC);
                PC = (ushort)(low | Fetch() << 8);
                break;
            }
            case 0x60:
                Read(PC);
                Read((ushort)(0x0100 | S));
                PC = (ushort)(Pull() | Pull() << 8);
                Read(PC);
                PC++;
                break;
            case 0x40:
                Read(PC);
                Read((ushort)(0x0100 | S));
                SetStatus(Pull());
                PC = (ushort)(Pull() | Pull() << 8);
                break;
            case 0x00:
                Fetch();
                Push((byte)(PC >> 8));
                Push((byte)PC);
                Push(StatusByte(brk: true));
                InterruptDisable = true;
                PC = (ushort)(Read(0xFFFE) | Read(0xFFFF) << 8);
                break;

            case 0x10: Branch(!Negative); break;
            case 0x30: Branch(Negative); break;
            case 0x50: Branch(!Overflow); break;
            case 0x70: Branch(Overflow); break;
            case 0x90: Branch(!Carry); break;
            case 0xB0: Branch(Carry); break;
            case 0xD0: Branch(!Zero); break;
            case 0xF0: Branch(Zero); break;

            case 0x18: Read(PC); Carry = false; break;
            case 0x38: Read(PC); Carry = true; break;
            case 0x58: Read(PC); InterruptDisable = false; break;
            case 0x78: Read(PC); InterruptDisable = true; break;
            case 0xB8: Read(PC); Overflow = false; break;
            case 0xD8: Read(PC); DecimalMode = false; break;
            case 0xF8: Read(PC); DecimalMode = true; break;

            case 0xEA or 0x1A or 0x3A or 0x5A or 0x7A or 0xDA or 0xFA: Read(PC); break;

            // ---- undocumented, but shipped in: games and their copy protection both
            // use these, and a cartridge that hits one on real hardware does not stop.
            case 0x80 or 0x82 or 0x89 or 0xC2 or 0xE2: Fetch(); break;
            case 0x04 or 0x44 or 0x64: Read(ZeroPage()); break;
            case 0x14 or 0x34 or 0x54 or 0x74 or 0xD4 or 0xF4: Read(ZeroPageX()); break;
            case 0x0C: Read(Absolute()); break;
            case 0x1C or 0x3C or 0x5C or 0x7C or 0xDC or 0xFC:
                Read(AbsoluteIndexed(X, false));
                break;

            case 0xA7: A = X = SetZeroNegative(Read(ZeroPage())); break;
            case 0xB7: A = X = SetZeroNegative(Read(ZeroPageY())); break;
            case 0xAF: A = X = SetZeroNegative(Read(Absolute())); break;
            case 0xBF: A = X = SetZeroNegative(Read(AbsoluteIndexed(Y, false))); break;
            case 0xA3: A = X = SetZeroNegative(Read(IndexedIndirect())); break;
            case 0xB3: A = X = SetZeroNegative(Read(IndirectIndexed(false))); break;

            case 0x87: Write(ZeroPage(), (byte)(A & X)); break;
            case 0x97: Write(ZeroPageY(), (byte)(A & X)); break;
            case 0x8F: Write(Absolute(), (byte)(A & X)); break;
            case 0x83: Write(IndexedIndirect(), (byte)(A & X)); break;

            case 0xC7: Compare(A, Modify(ZeroPage(), Decrement)); break;
            case 0xD7: Compare(A, Modify(ZeroPageX(), Decrement)); break;
            case 0xCF: Compare(A, Modify(Absolute(), Decrement)); break;
            case 0xDF: Compare(A, Modify(AbsoluteIndexed(X, true), Decrement)); break;
            case 0xDB: Compare(A, Modify(AbsoluteIndexed(Y, true), Decrement)); break;
            case 0xC3: Compare(A, Modify(IndexedIndirect(), Decrement)); break;
            case 0xD3: Compare(A, Modify(IndirectIndexed(true), Decrement)); break;

            case 0xE7: AddWithCarry((byte)~Modify(ZeroPage(), Increment)); break;
            case 0xF7: AddWithCarry((byte)~Modify(ZeroPageX(), Increment)); break;
            case 0xEF: AddWithCarry((byte)~Modify(Absolute(), Increment)); break;
            case 0xFF: AddWithCarry((byte)~Modify(AbsoluteIndexed(X, true), Increment)); break;
            case 0xFB: AddWithCarry((byte)~Modify(AbsoluteIndexed(Y, true), Increment)); break;
            case 0xE3: AddWithCarry((byte)~Modify(IndexedIndirect(), Increment)); break;
            case 0xF3: AddWithCarry((byte)~Modify(IndirectIndexed(true), Increment)); break;

            case 0x07: A = SetZeroNegative((byte)(A | Modify(ZeroPage(), Asl))); break;
            case 0x17: A = SetZeroNegative((byte)(A | Modify(ZeroPageX(), Asl))); break;
            case 0x0F: A = SetZeroNegative((byte)(A | Modify(Absolute(), Asl))); break;
            case 0x1F: A = SetZeroNegative((byte)(A | Modify(AbsoluteIndexed(X, true), Asl))); break;
            case 0x1B: A = SetZeroNegative((byte)(A | Modify(AbsoluteIndexed(Y, true), Asl))); break;
            case 0x03: A = SetZeroNegative((byte)(A | Modify(IndexedIndirect(), Asl))); break;
            case 0x13: A = SetZeroNegative((byte)(A | Modify(IndirectIndexed(true), Asl))); break;

            case 0x27: A = SetZeroNegative((byte)(A & Modify(ZeroPage(), Rol))); break;
            case 0x37: A = SetZeroNegative((byte)(A & Modify(ZeroPageX(), Rol))); break;
            case 0x2F: A = SetZeroNegative((byte)(A & Modify(Absolute(), Rol))); break;
            case 0x3F: A = SetZeroNegative((byte)(A & Modify(AbsoluteIndexed(X, true), Rol))); break;
            case 0x3B: A = SetZeroNegative((byte)(A & Modify(AbsoluteIndexed(Y, true), Rol))); break;
            case 0x23: A = SetZeroNegative((byte)(A & Modify(IndexedIndirect(), Rol))); break;
            case 0x33: A = SetZeroNegative((byte)(A & Modify(IndirectIndexed(true), Rol))); break;

            case 0x47: A = SetZeroNegative((byte)(A ^ Modify(ZeroPage(), Lsr))); break;
            case 0x57: A = SetZeroNegative((byte)(A ^ Modify(ZeroPageX(), Lsr))); break;
            case 0x4F: A = SetZeroNegative((byte)(A ^ Modify(Absolute(), Lsr))); break;
            case 0x5F: A = SetZeroNegative((byte)(A ^ Modify(AbsoluteIndexed(X, true), Lsr))); break;
            case 0x5B: A = SetZeroNegative((byte)(A ^ Modify(AbsoluteIndexed(Y, true), Lsr))); break;
            case 0x43: A = SetZeroNegative((byte)(A ^ Modify(IndexedIndirect(), Lsr))); break;
            case 0x53: A = SetZeroNegative((byte)(A ^ Modify(IndirectIndexed(true), Lsr))); break;

            case 0x67: AddWithCarry(Modify(ZeroPage(), Ror)); break;
            case 0x77: AddWithCarry(Modify(ZeroPageX(), Ror)); break;
            case 0x6F: AddWithCarry(Modify(Absolute(), Ror)); break;
            case 0x7F: AddWithCarry(Modify(AbsoluteIndexed(X, true), Ror)); break;
            case 0x7B: AddWithCarry(Modify(AbsoluteIndexed(Y, true), Ror)); break;
            case 0x63: AddWithCarry(Modify(IndexedIndirect(), Ror)); break;
            case 0x73: AddWithCarry(Modify(IndirectIndexed(true), Ror)); break;

            case 0x0B or 0x2B:
                A = SetZeroNegative((byte)(A & Fetch()));
                Carry = Negative;
                break;
            case 0x4B:
                A = Lsr((byte)(A & Fetch()));
                break;
            case 0x6B:
            {
                var rotated = (byte)((A & Fetch()) >> 1 | (Carry ? 0x80 : 0));
                Carry = (rotated & 0x40) != 0;
                Overflow = ((rotated >> 6 ^ rotated >> 5) & 1) != 0;
                A = SetZeroNegative(rotated);
                break;
            }
            case 0xCB:
            {
                var operand = Fetch();
                var result = (A & X) - operand;
                Carry = (A & X) >= operand;
                X = SetZeroNegative((byte)result);
                break;
            }
            case 0xBB:
            {
                var value = (byte)(Read(AbsoluteIndexed(Y, false)) & S);
                A = X = S = SetZeroNegative(value);
                break;
            }
            case 0x8B:
                A = SetZeroNegative((byte)(A & X & Fetch()));
                break;
            case 0xAB:
                A = X = SetZeroNegative((byte)(A & Fetch()));
                break;

            // The "unstable" stores: the value written is ANDed with the high byte of
            // the target address plus one. Real hardware sometimes drops that too;
            // this is the behaviour the handful of games that use them expect.
            case 0x9C: StoreHigh(AbsoluteIndexed(X, true), Y); break;
            case 0x9E: StoreHigh(AbsoluteIndexed(Y, true), X); break;
            case 0x9F: StoreHigh(AbsoluteIndexed(Y, true), (byte)(A & X)); break;
            case 0x93: StoreHigh(IndirectIndexed(true), (byte)(A & X)); break;
            case 0x9B:
            {
                var address = AbsoluteIndexed(Y, true);
                S = (byte)(A & X);
                StoreHigh(address, S);
                break;
            }

            // The jams. Real hardware locks the bus until reset; there is nothing to
            // do but stop burning cycles pretending otherwise.
            default:
                Read(PC);
                break;
        }
    }

    /// <summary>Shared by INC/DEC and by the read-modify-write pairs (DCP, ISC) that
    /// bolt a compare or a subtract on afterwards — those overwrite these flags with
    /// their own, so setting them here costs nothing and keeps INC honest.</summary>
    private byte Increment(byte value) => SetZeroNegative((byte)(value + 1));

    private byte Decrement(byte value) => SetZeroNegative((byte)(value - 1));

    private void StoreHigh(ushort address, byte value) =>
        Write(address, (byte)(value & ((address >> 8) + 1)));
}
