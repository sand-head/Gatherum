namespace Gatherum.Client.Emulation.GameBoy;

/// <summary>The Game Boy's processor: Sharp's SM83, which looks like an 8080 that read
/// about the Z80 once. Same shape as the console's other core — every memory access is
/// a machine cycle and ticks the rest of the hardware — so the picture chip's mode
/// changes land between the right instructions rather than in a heap at the end of one.
///
/// The register file is addressed by index in the places the instruction set is regular
/// (the whole $40-$BF block and every CB opcode), because writing those two hundred
/// cases out by hand is two hundred chances to transpose a letter.</summary>
public sealed class Sm83(GameBoyConsole bus)
{
    public byte A, B, C, D, E, H, L;
    public byte Flags;
    public ushort SP, PC;
    public bool InterruptsEnabled;
    public bool Halted;

    /// <summary>EI takes effect after the instruction that follows it, which is what
    /// makes "EI; HALT" safe and "EI; RETI" redundant.</summary>
    private bool enableInterruptsAfterNext;

    private const byte ZeroFlag = 0x80;
    private const byte SubtractFlag = 0x40;
    private const byte HalfCarryFlag = 0x20;
    private const byte CarryFlag = 0x10;

    private bool Zero => (Flags & ZeroFlag) != 0;
    private bool Carry => (Flags & CarryFlag) != 0;

    private void SetFlags(bool zero, bool subtract, bool halfCarry, bool carry) =>
        Flags = (byte)((zero ? ZeroFlag : 0) | (subtract ? SubtractFlag : 0)
            | (halfCarry ? HalfCarryFlag : 0) | (carry ? CarryFlag : 0));

    public ushort BC
    {
        get => (ushort)(B << 8 | C);
        set { B = (byte)(value >> 8); C = (byte)value; }
    }

    public ushort DE
    {
        get => (ushort)(D << 8 | E);
        set { D = (byte)(value >> 8); E = (byte)value; }
    }

    public ushort HL
    {
        get => (ushort)(H << 8 | L);
        set { H = (byte)(value >> 8); L = (byte)value; }
    }

    public ushort AF
    {
        get => (ushort)(A << 8 | Flags);
        set { A = (byte)(value >> 8); Flags = (byte)(value & 0xF0); }
    }

    /// <summary>Where the boot ROM leaves the machine. The player has no boot ROM —
    /// it is Nintendo's code and not ours to ship — so it starts where one would have
    /// finished, which is what every cartridge is written to expect.</summary>
    public void Reset(bool colour)
    {
        AF = colour ? (ushort)0x1180 : (ushort)0x01B0;
        BC = colour ? (ushort)0x0000 : (ushort)0x0013;
        DE = colour ? (ushort)0xFF56 : (ushort)0x00D8;
        HL = colour ? (ushort)0x000D : (ushort)0x014D;
        SP = 0xFFFE;
        PC = 0x0100;
        InterruptsEnabled = false;
        Halted = false;
        enableInterruptsAfterNext = false;
    }

    private byte Read(ushort address)
    {
        bus.Tick();
        return bus.ReadByte(address);
    }

    private void Write(ushort address, byte value)
    {
        bus.Tick();
        bus.WriteByte(address, value);
    }

    private byte Fetch() => Read(PC++);

    private ushort Fetch16()
    {
        var low = Fetch();
        return (ushort)(low | Fetch() << 8);
    }

    private void Push(ushort value)
    {
        Write(--SP, (byte)(value >> 8));
        Write(--SP, (byte)value);
    }

    private ushort Pop()
    {
        var low = Read(SP++);
        return (ushort)(low | Read(SP++) << 8);
    }

    public void Step()
    {
        var enabling = enableInterruptsAfterNext;
        enableInterruptsAfterNext = false;

        if (TakeInterrupt())
            return;

        if (Halted)
        {
            bus.Tick();
            if (enabling)
                InterruptsEnabled = true;
            return;
        }

        Execute(Fetch());
        if (enabling)
            InterruptsEnabled = true;
    }

    private bool TakeInterrupt()
    {
        var pending = (byte)(bus.InterruptEnable & bus.InterruptFlags & 0x1F);
        if (pending == 0)
            return false;

        // A halted processor wakes for a pending interrupt whether or not it is
        // allowed to service one — which is how a game waits for vblank with
        // interrupts off.
        Halted = false;
        if (!InterruptsEnabled)
            return false;

        InterruptsEnabled = false;
        var source = System.Numerics.BitOperations.TrailingZeroCount(pending);
        bus.InterruptFlags &= (byte)~(1 << source);
        bus.Tick();
        bus.Tick();
        Push(PC);
        PC = (ushort)(0x0040 + source * 8);
        bus.Tick();
        return true;
    }

    // ---- the register file, by index --------------------------------------------

    private byte GetRegister(int index) => index switch
    {
        0 => B, 1 => C, 2 => D, 3 => E, 4 => H, 5 => L,
        6 => Read(HL),
        _ => A,
    };

    private void SetRegister(int index, byte value)
    {
        switch (index)
        {
            case 0: B = value; break;
            case 1: C = value; break;
            case 2: D = value; break;
            case 3: E = value; break;
            case 4: H = value; break;
            case 5: L = value; break;
            case 6: Write(HL, value); break;
            default: A = value; break;
        }
    }

    // ---- arithmetic ------------------------------------------------------------

    private void Add(byte value, bool withCarry)
    {
        var carried = withCarry && Carry ? 1 : 0;
        var sum = A + value + carried;
        SetFlags((byte)sum == 0, false, (A & 0x0F) + (value & 0x0F) + carried > 0x0F,
            sum > 0xFF);
        A = (byte)sum;
    }

    private void Subtract(byte value, bool withCarry)
    {
        var carried = withCarry && Carry ? 1 : 0;
        var difference = A - value - carried;
        SetFlags((byte)difference == 0, true, (A & 0x0F) - (value & 0x0F) - carried < 0,
            difference < 0);
        A = (byte)difference;
    }

    private void Compare(byte value)
    {
        var difference = A - value;
        SetFlags((byte)difference == 0, true, (A & 0x0F) - (value & 0x0F) < 0,
            difference < 0);
    }

    private void AddToHl(ushort value)
    {
        bus.Tick();
        var sum = HL + value;
        SetFlags(Zero, false, (HL & 0x0FFF) + (value & 0x0FFF) > 0x0FFF, sum > 0xFFFF);
        HL = (ushort)sum;
    }

    /// <summary>Adding a signed byte to a sixteen-bit register still carries out of the
    /// low byte, because the hardware does it eight bits at a time.</summary>
    private ushort AddSigned(ushort value, sbyte offset)
    {
        SetFlags(false, false, (value & 0x0F) + (offset & 0x0F) > 0x0F,
            (value & 0xFF) + (offset & 0xFF) > 0xFF);
        return (ushort)(value + offset);
    }

    private byte Increment(byte value)
    {
        var result = (byte)(value + 1);
        SetFlags(result == 0, false, (value & 0x0F) == 0x0F, Carry);
        return result;
    }

    private byte Decrement(byte value)
    {
        var result = (byte)(value - 1);
        SetFlags(result == 0, true, (value & 0x0F) == 0, Carry);
        return result;
    }

    /// <summary>The decimal adjust: the only instruction that reads the half-carry, and
    /// the reason the flag exists at all.</summary>
    private void DecimalAdjust()
    {
        var adjust = 0;
        var carry = Carry;
        if ((Flags & HalfCarryFlag) != 0 || ((Flags & SubtractFlag) == 0 && (A & 0x0F) > 9))
            adjust |= 0x06;
        if (carry || ((Flags & SubtractFlag) == 0 && A > 0x99))
        {
            adjust |= 0x60;
            carry = true;
        }
        A = (byte)((Flags & SubtractFlag) != 0 ? A - adjust : A + adjust);
        Flags = (byte)((A == 0 ? ZeroFlag : 0) | (Flags & SubtractFlag)
            | (carry ? CarryFlag : 0));
    }

    private void Jump(ushort target)
    {
        bus.Tick();
        PC = target;
    }

    private bool Condition(int index) => index switch
    {
        0 => !Zero,
        1 => Zero,
        2 => !Carry,
        _ => Carry,
    };

    private void Execute(byte opcode)
    {
        // LD r,r' fills the whole $40-$7F block bar one hole where HALT sits.
        if (opcode is >= 0x40 and <= 0x7F)
        {
            if (opcode == 0x76)
            {
                Halted = true;
                return;
            }
            SetRegister(opcode >> 3 & 7, GetRegister(opcode & 7));
            return;
        }
        // $80-$BF is the arithmetic block: eight operations by eight sources.
        if (opcode is >= 0x80 and <= 0xBF)
        {
            var operand = GetRegister(opcode & 7);
            switch (opcode >> 3 & 7)
            {
                case 0: Add(operand, false); break;
                case 1: Add(operand, true); break;
                case 2: Subtract(operand, false); break;
                case 3: Subtract(operand, true); break;
                case 4: SetFlags((A &= operand) == 0, false, true, false); break;
                case 5: SetFlags((A ^= operand) == 0, false, false, false); break;
                case 6: SetFlags((A |= operand) == 0, false, false, false); break;
                default: Compare(operand); break;
            }
            return;
        }

        switch (opcode)
        {
            case 0x00: break;
            case 0x10:
                // STOP. Its only use outside a dead cartridge is asking a Game Boy
                // Color to change gear.
                Fetch();
                bus.SwitchSpeed();
                break;
            case 0xF3: InterruptsEnabled = false; break;
            case 0xFB: enableInterruptsAfterNext = true; break;

            case 0x01: BC = Fetch16(); break;
            case 0x11: DE = Fetch16(); break;
            case 0x21: HL = Fetch16(); break;
            case 0x31: SP = Fetch16(); break;

            case 0x02: Write(BC, A); break;
            case 0x12: Write(DE, A); break;
            case 0x22: Write(HL, A); HL++; break;
            case 0x32: Write(HL, A); HL--; break;
            case 0x0A: A = Read(BC); break;
            case 0x1A: A = Read(DE); break;
            case 0x2A: A = Read(HL); HL++; break;
            case 0x3A: A = Read(HL); HL--; break;

            case 0x06: B = Fetch(); break;
            case 0x0E: C = Fetch(); break;
            case 0x16: D = Fetch(); break;
            case 0x1E: E = Fetch(); break;
            case 0x26: H = Fetch(); break;
            case 0x2E: L = Fetch(); break;
            case 0x36: Write(HL, Fetch()); break;
            case 0x3E: A = Fetch(); break;

            case 0x08:
            {
                var address = Fetch16();
                Write(address, (byte)SP);
                Write((ushort)(address + 1), (byte)(SP >> 8));
                break;
            }
            case 0xEA: Write(Fetch16(), A); break;
            case 0xFA: A = Read(Fetch16()); break;
            case 0xE0: Write((ushort)(0xFF00 + Fetch()), A); break;
            case 0xF0: A = Read((ushort)(0xFF00 + Fetch())); break;
            case 0xE2: Write((ushort)(0xFF00 + C), A); break;
            case 0xF2: A = Read((ushort)(0xFF00 + C)); break;

            case 0x03: bus.Tick(); BC++; break;
            case 0x13: bus.Tick(); DE++; break;
            case 0x23: bus.Tick(); HL++; break;
            case 0x33: bus.Tick(); SP++; break;
            case 0x0B: bus.Tick(); BC--; break;
            case 0x1B: bus.Tick(); DE--; break;
            case 0x2B: bus.Tick(); HL--; break;
            case 0x3B: bus.Tick(); SP--; break;

            case 0x04: B = Increment(B); break;
            case 0x0C: C = Increment(C); break;
            case 0x14: D = Increment(D); break;
            case 0x1C: E = Increment(E); break;
            case 0x24: H = Increment(H); break;
            case 0x2C: L = Increment(L); break;
            case 0x34: Write(HL, Increment(Read(HL))); break;
            case 0x3C: A = Increment(A); break;

            case 0x05: B = Decrement(B); break;
            case 0x0D: C = Decrement(C); break;
            case 0x15: D = Decrement(D); break;
            case 0x1D: E = Decrement(E); break;
            case 0x25: H = Decrement(H); break;
            case 0x2D: L = Decrement(L); break;
            case 0x35: Write(HL, Decrement(Read(HL))); break;
            case 0x3D: A = Decrement(A); break;

            case 0x09: AddToHl(BC); break;
            case 0x19: AddToHl(DE); break;
            case 0x29: AddToHl(HL); break;
            case 0x39: AddToHl(SP); break;

            case 0xC6: Add(Fetch(), false); break;
            case 0xCE: Add(Fetch(), true); break;
            case 0xD6: Subtract(Fetch(), false); break;
            case 0xDE: Subtract(Fetch(), true); break;
            case 0xE6: SetFlags((A &= Fetch()) == 0, false, true, false); break;
            case 0xEE: SetFlags((A ^= Fetch()) == 0, false, false, false); break;
            case 0xF6: SetFlags((A |= Fetch()) == 0, false, false, false); break;
            case 0xFE: Compare(Fetch()); break;

            case 0xE8:
                SP = AddSigned(SP, (sbyte)Fetch());
                bus.Tick();
                bus.Tick();
                break;
            case 0xF8:
                HL = AddSigned(SP, (sbyte)Fetch());
                bus.Tick();
                break;
            case 0xF9: bus.Tick(); SP = HL; break;

            // The accumulator's own rotates clear zero, unlike their CB twins.
            case 0x07: A = (byte)(A << 1 | A >> 7); SetFlags(false, false, false, (A & 1) != 0); break;
            case 0x0F: A = (byte)(A >> 1 | A << 7); SetFlags(false, false, false, (A & 0x80) != 0); break;
            case 0x17:
            {
                var carried = Carry;
                SetFlags(false, false, false, (A & 0x80) != 0);
                A = (byte)(A << 1 | (carried ? 1 : 0));
                break;
            }
            case 0x1F:
            {
                var carried = Carry;
                SetFlags(false, false, false, (A & 1) != 0);
                A = (byte)(A >> 1 | (carried ? 0x80 : 0));
                break;
            }
            case 0x27: DecimalAdjust(); break;
            case 0x2F:
                A = (byte)~A;
                Flags |= SubtractFlag | HalfCarryFlag;
                break;
            case 0x37: SetFlags(Zero, false, false, true); break;
            case 0x3F: SetFlags(Zero, false, false, !Carry); break;

            case 0x18: { var offset = (sbyte)Fetch(); Jump((ushort)(PC + offset)); break; }
            case 0x20 or 0x28 or 0x30 or 0x38:
            {
                var offset = (sbyte)Fetch();
                if (Condition(opcode >> 3 & 3))
                    Jump((ushort)(PC + offset));
                break;
            }
            case 0xC3: Jump(Fetch16()); break;
            case 0xC2 or 0xCA or 0xD2 or 0xDA:
            {
                var target = Fetch16();
                if (Condition(opcode >> 3 & 3))
                    Jump(target);
                break;
            }
            case 0xE9: PC = HL; break;

            case 0xCD: { var target = Fetch16(); bus.Tick(); Push(PC); PC = target; break; }
            case 0xC4 or 0xCC or 0xD4 or 0xDC:
            {
                var target = Fetch16();
                if (!Condition(opcode >> 3 & 3))
                    break;
                bus.Tick();
                Push(PC);
                PC = target;
                break;
            }
            case 0xC9: Jump(Pop()); break;
            case 0xD9: InterruptsEnabled = true; Jump(Pop()); break;
            case 0xC0 or 0xC8 or 0xD0 or 0xD8:
                bus.Tick();
                if (Condition(opcode >> 3 & 3))
                    Jump(Pop());
                break;
            case 0xC7 or 0xCF or 0xD7 or 0xDF or 0xE7 or 0xEF or 0xF7 or 0xFF:
                bus.Tick();
                Push(PC);
                PC = (ushort)(opcode & 0x38);
                break;

            case 0xC1: BC = Pop(); break;
            case 0xD1: DE = Pop(); break;
            case 0xE1: HL = Pop(); break;
            case 0xF1: AF = Pop(); break;
            case 0xC5: bus.Tick(); Push(BC); break;
            case 0xD5: bus.Tick(); Push(DE); break;
            case 0xE5: bus.Tick(); Push(HL); break;
            case 0xF5: bus.Tick(); Push(AF); break;

            case 0xCB: ExecuteBitOperation(Fetch()); break;

            // The holes in the map. Real hardware locks up; there is nothing better
            // to do than let the frame's cycle budget run out.
            default: break;
        }
    }

    private void ExecuteBitOperation(byte opcode)
    {
        var index = opcode & 7;
        var operation = opcode >> 3 & 7;

        if (opcode >= 0x40)
        {
            var value = GetRegister(index);
            var bit = operation;
            switch (opcode >> 6)
            {
                case 1:
                    SetFlags((value & 1 << bit) == 0, false, true, Carry);
                    return;
                case 2:
                    SetRegister(index, (byte)(value & ~(1 << bit)));
                    return;
                default:
                    SetRegister(index, (byte)(value | 1 << bit));
                    return;
            }
        }

        var source = GetRegister(index);
        byte result;
        switch (operation)
        {
            case 0:
                result = (byte)(source << 1 | source >> 7);
                SetFlags(result == 0, false, false, (source & 0x80) != 0);
                break;
            case 1:
                result = (byte)(source >> 1 | source << 7);
                SetFlags(result == 0, false, false, (source & 1) != 0);
                break;
            case 2:
                result = (byte)(source << 1 | (Carry ? 1 : 0));
                SetFlags(result == 0, false, false, (source & 0x80) != 0);
                break;
            case 3:
                result = (byte)(source >> 1 | (Carry ? 0x80 : 0));
                SetFlags(result == 0, false, false, (source & 1) != 0);
                break;
            case 4:
                result = (byte)(source << 1);
                SetFlags(result == 0, false, false, (source & 0x80) != 0);
                break;
            case 5:
                // An arithmetic shift keeps the sign bit where it was.
                result = (byte)(source >> 1 | source & 0x80);
                SetFlags(result == 0, false, false, (source & 1) != 0);
                break;
            case 6:
                result = (byte)(source >> 4 | source << 4);
                SetFlags(result == 0, false, false, false);
                break;
            default:
                result = (byte)(source >> 1);
                SetFlags(result == 0, false, false, (source & 1) != 0);
                break;
        }
        SetRegister(index, result);
    }
}
