namespace Gatherum.Client.Emulation.Sega;

/// <summary>The Z80 the Master System is built around.
///
/// Like the 6502 next door, every memory access ticks the rest of the console forward
/// rather than the instruction running atomically and the machine catching up
/// afterwards. On this console that matters most for the picture chip's line counter: a
/// game that splits the screen writes its new scroll value inside a line interrupt, and
/// where that write lands decides which line the seam appears on.
///
/// The flag byte is kept whole, undocumented bits and all, because the block and bit
/// instructions set the two unnamed ones in ways real games have been found to lean
/// on.</summary>
public sealed class Z80(MasterSystem bus)
{
    private const byte SignFlag = 0x80;
    private const byte ZeroFlag = 0x40;
    private const byte YFlag = 0x20;
    private const byte HalfFlag = 0x10;
    private const byte XFlag = 0x08;
    private const byte ParityFlag = 0x04;
    private const byte SubtractFlag = 0x02;
    private const byte CarryFlag = 0x01;

    public byte A, F, B, C, D, E, H, L;
    public byte Aa, Fa, Ba, Ca, Da, Ea, Ha, La;
    public ushort IX, IY, SP, PC;
    public byte I, R;
    public bool Iff1, Iff2;
    public int InterruptMode;
    public bool Halted;

    /// <summary>The picture chip holds this down until the program reads the status
    /// register, so it is a level and not an edge.</summary>
    public bool IrqLine;

    /// <summary>The pause button is wired to the non-maskable line, which is why no
    /// game can refuse it. Only the press arms it, not the holding down.</summary>
    private bool nmiArmed, nmiLast;

    /// <summary>Enabling interrupts does not take effect until after the instruction
    /// that follows, so that `EI RET` cannot be interrupted between the two — every
    /// interrupt handler on the machine ends that way.</summary>
    private bool interruptsPending;

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
        get => (ushort)(A << 8 | F);
        set { A = (byte)(value >> 8); F = (byte)value; }
    }

    public void Reset()
    {
        A = F = B = C = D = E = H = L = 0xFF;
        Aa = Fa = Ba = Ca = Da = Ea = Ha = La = 0xFF;
        IX = IY = 0xFFFF;
        SP = 0xDFF0;
        PC = 0;
        I = R = 0;
        Iff1 = Iff2 = false;
        InterruptMode = 0;
        Halted = false;
        IrqLine = false;
        nmiArmed = nmiLast = false;
        interruptsPending = false;
    }

    public void SetNmi(bool level)
    {
        if (level && !nmiLast)
            nmiArmed = true;
        nmiLast = level;
    }

    // ---- the bus, and the clock that comes with it ------------------------------

    /// <summary>An opcode fetch is four clocks and bumps the refresh counter; only the
    /// low seven bits count, because the top one is whatever was last loaded.</summary>
    private byte FetchOpcode()
    {
        bus.Tick(4);
        R = (byte)(R & 0x80 | (R + 1) & 0x7F);
        return bus.Read(PC++);
    }

    private byte Fetch()
    {
        bus.Tick(3);
        return bus.Read(PC++);
    }

    private ushort FetchWord()
    {
        var low = Fetch();
        return (ushort)(low | Fetch() << 8);
    }

    private byte ReadByte(ushort address)
    {
        bus.Tick(3);
        return bus.Read(address);
    }

    private void WriteByte(ushort address, byte value)
    {
        bus.Tick(3);
        bus.Write(address, value);
    }

    private ushort ReadWord(ushort address) =>
        (ushort)(ReadByte(address) | ReadByte((ushort)(address + 1)) << 8);

    private void WriteWord(ushort address, ushort value)
    {
        WriteByte(address, (byte)value);
        WriteByte((ushort)(address + 1), (byte)(value >> 8));
    }

    private byte ReadPort(ushort port)
    {
        bus.Tick(4);
        return bus.ReadPort(port);
    }

    private void WritePort(ushort port, byte value)
    {
        bus.Tick(4);
        bus.WritePort(port, value);
    }

    private void Push(ushort value)
    {
        WriteByte(--SP, (byte)(value >> 8));
        WriteByte(--SP, (byte)value);
    }

    private ushort Pop()
    {
        var low = ReadByte(SP++);
        return (ushort)(low | ReadByte(SP++) << 8);
    }

    // ---- flags ------------------------------------------------------------------

    private static readonly byte[] Parity = BuildParity();

    private static byte[] BuildParity()
    {
        var table = new byte[256];
        for (var value = 0; value < 256; value++)
        {
            var bits = 0;
            for (var bit = 0; bit < 8; bit++)
                bits += value >> bit & 1;
            table[value] = (bits & 1) == 0 ? ParityFlag : (byte)0;
        }
        return table;
    }

    private bool Flag(byte flag) => (F & flag) != 0;

    private void SetFlag(byte flag, bool on) => F = (byte)(on ? F | flag : F & ~flag);

    /// <summary>The two unnamed flags are simply bits 5 and 3 of whatever the result
    /// was, on nearly every instruction that touches them.</summary>
    private static byte Undocumented(byte value) => (byte)(value & (YFlag | XFlag));

    private static byte SignZero(byte value) =>
        (byte)((value == 0 ? ZeroFlag : 0) | (value & SignFlag) | Undocumented(value));

    // ---- arithmetic -------------------------------------------------------------

    private void Add(byte value, bool withCarry)
    {
        var carry = withCarry && Flag(CarryFlag) ? 1 : 0;
        var total = A + value + carry;
        var result = (byte)total;
        F = (byte)(SignZero(result)
            | (total > 0xFF ? CarryFlag : 0)
            | ((A ^ value ^ result) & HalfFlag)
            | (((A ^ result) & (value ^ result) & 0x80) != 0 ? ParityFlag : 0));
        A = result;
    }

    private void Subtract(byte value, bool withCarry)
    {
        var carry = withCarry && Flag(CarryFlag) ? 1 : 0;
        var total = A - value - carry;
        var result = (byte)total;
        F = (byte)(SignZero(result) | SubtractFlag
            | (total < 0 ? CarryFlag : 0)
            | ((A ^ value ^ result) & HalfFlag)
            | (((A ^ value) & (A ^ result) & 0x80) != 0 ? ParityFlag : 0));
        A = result;
    }

    /// <summary>Comparing is subtracting and throwing the answer away — except that the
    /// two unnamed flags come from the *operand* rather than the result, which is the
    /// one place they are not simply bits of what was computed.</summary>
    private void Compare(byte value)
    {
        var total = A - value;
        var result = (byte)total;
        F = (byte)((result == 0 ? ZeroFlag : 0) | (result & SignFlag) | SubtractFlag
            | Undocumented(value)
            | (total < 0 ? CarryFlag : 0)
            | ((A ^ value ^ result) & HalfFlag)
            | (((A ^ value) & (A ^ result) & 0x80) != 0 ? ParityFlag : 0));
    }

    private void And(byte value)
    {
        A &= value;
        F = (byte)(SignZero(A) | HalfFlag | Parity[A]);
    }

    private void Or(byte value)
    {
        A |= value;
        F = (byte)(SignZero(A) | Parity[A]);
    }

    private void Xor(byte value)
    {
        A ^= value;
        F = (byte)(SignZero(A) | Parity[A]);
    }

    private byte Increment(byte value)
    {
        var result = (byte)(value + 1);
        F = (byte)(SignZero(result) | (F & CarryFlag)
            | ((value & 0x0F) == 0x0F ? HalfFlag : 0)
            | (result == 0x80 ? ParityFlag : 0));
        return result;
    }

    private byte Decrement(byte value)
    {
        var result = (byte)(value - 1);
        F = (byte)(SignZero(result) | (F & CarryFlag) | SubtractFlag
            | ((value & 0x0F) == 0 ? HalfFlag : 0)
            | (result == 0x7F ? ParityFlag : 0));
        return result;
    }

    private ushort AddWord(ushort left, ushort right)
    {
        var total = left + right;
        var result = (ushort)total;
        F = (byte)((F & (SignFlag | ZeroFlag | ParityFlag))
            | Undocumented((byte)(result >> 8))
            | (total > 0xFFFF ? CarryFlag : 0)
            | ((left ^ right ^ result) >> 8 & HalfFlag));
        return result;
    }

    private void AdcWord(ushort value)
    {
        var carry = Flag(CarryFlag) ? 1 : 0;
        var left = HL;
        var total = left + value + carry;
        var result = (ushort)total;
        F = (byte)((result == 0 ? ZeroFlag : 0) | (result >> 8 & SignFlag)
            | Undocumented((byte)(result >> 8))
            | (total > 0xFFFF ? CarryFlag : 0)
            | ((left ^ value ^ result) >> 8 & HalfFlag)
            | (((left ^ result) & (value ^ result) & 0x8000) != 0 ? ParityFlag : 0));
        HL = result;
    }

    private void SbcWord(ushort value)
    {
        var carry = Flag(CarryFlag) ? 1 : 0;
        var left = HL;
        var total = left - value - carry;
        var result = (ushort)total;
        F = (byte)((result == 0 ? ZeroFlag : 0) | (result >> 8 & SignFlag) | SubtractFlag
            | Undocumented((byte)(result >> 8))
            | (total < 0 ? CarryFlag : 0)
            | ((left ^ value ^ result) >> 8 & HalfFlag)
            | (((left ^ value) & (left ^ result) & 0x8000) != 0 ? ParityFlag : 0));
        HL = result;
    }

    /// <summary>Decimal adjust: the one instruction that has to know whether the last
    /// operation was an addition or a subtraction, which is what the N flag is for.</summary>
    private void DecimalAdjust()
    {
        var correction = 0;
        var carry = Flag(CarryFlag);
        if (Flag(HalfFlag) || (A & 0x0F) > 9)
            correction |= 0x06;
        if (carry || A > 0x99)
        {
            correction |= 0x60;
            carry = true;
        }
        var before = A;
        A = (byte)(Flag(SubtractFlag) ? A - correction : A + correction);
        F = (byte)(SignZero(A) | Parity[A] | (F & SubtractFlag)
            | (carry ? CarryFlag : 0)
            | ((before ^ A) & HalfFlag));
    }

    private void Negate()
    {
        var value = A;
        A = 0;
        Subtract(value, withCarry: false);
    }

    // ---- rotates and shifts -----------------------------------------------------

    /// <summary>The accumulator's own rotates leave sign, zero and parity alone; the
    /// CB-prefixed ones on any register set them all. Same shifts, different flags.</summary>
    private byte Rotate(int operation, byte value)
    {
        var carry = Flag(CarryFlag);
        var result = operation switch
        {
            0 => (byte)(value << 1 | value >> 7),                     // RLC
            1 => (byte)(value >> 1 | value << 7),                     // RRC
            2 => (byte)(value << 1 | (carry ? 1 : 0)),                // RL
            3 => (byte)(value >> 1 | (carry ? 0x80 : 0)),             // RR
            4 => (byte)(value << 1),                                  // SLA
            5 => (byte)(value >> 1 | value & 0x80),                   // SRA
            6 => (byte)(value << 1 | 1),                              // SLL, undocumented
            _ => (byte)(value >> 1),                                  // SRL
        };
        var out7 = operation is 0 or 2 or 4 or 6;
        F = (byte)(SignZero(result) | Parity[result]
            | ((out7 ? value & 0x80 : value & 0x01) != 0 ? CarryFlag : 0));
        return result;
    }

    private void RotateAccumulator(int operation)
    {
        var value = A;
        var carry = Flag(CarryFlag);
        A = operation switch
        {
            0 => (byte)(value << 1 | value >> 7),
            1 => (byte)(value >> 1 | value << 7),
            2 => (byte)(value << 1 | (carry ? 1 : 0)),
            _ => (byte)(value >> 1 | (carry ? 0x80 : 0)),
        };
        var out7 = operation is 0 or 2;
        F = (byte)((F & (SignFlag | ZeroFlag | ParityFlag)) | Undocumented(A)
            | ((out7 ? value & 0x80 : value & 0x01) != 0 ? CarryFlag : 0));
    }

    private void TestBit(int bit, byte value, byte undocumented)
    {
        var result = (byte)(value & 1 << bit);
        F = (byte)((result == 0 ? ZeroFlag | ParityFlag : 0) | (result & SignFlag)
            | HalfFlag | (F & CarryFlag) | Undocumented(undocumented));
    }

    /// <summary>The decimal rotates move a nibble between the accumulator and memory,
    /// which is how a program keeps a long number without unpacking it.</summary>
    private void RotateDecimal(bool left)
    {
        var value = ReadByte(HL);
        bus.Tick(4);
        byte written;
        if (left)
        {
            written = (byte)(value << 4 | A & 0x0F);
            A = (byte)(A & 0xF0 | value >> 4);
        }
        else
        {
            written = (byte)(A << 4 | value >> 4);
            A = (byte)(A & 0xF0 | value & 0x0F);
        }
        WriteByte(HL, written);
        F = (byte)(SignZero(A) | Parity[A] | (F & CarryFlag));
    }

    // ---- the register file ------------------------------------------------------

    /// <summary>Index registers stand in for HL when a prefix says so, halves and all —
    /// which is where IXH and IXL come from. The one exception is an instruction that
    /// also reaches memory through the index: there the *other* operand is the real H
    /// or L, because only one substitution happens per instruction.</summary>
    private byte GetRegister(int index, int prefix) => index switch
    {
        0 => B,
        1 => C,
        2 => D,
        3 => E,
        4 => prefix == 0 ? H : (byte)((prefix == 1 ? IX : IY) >> 8),
        5 => prefix == 0 ? L : (byte)(prefix == 1 ? IX : IY),
        _ => A,
    };

    private void SetRegister(int index, int prefix, byte value)
    {
        switch (index)
        {
            case 0: B = value; return;
            case 1: C = value; return;
            case 2: D = value; return;
            case 3: E = value; return;
            case 4:
                if (prefix == 0) H = value;
                else if (prefix == 1) IX = (ushort)(IX & 0x00FF | value << 8);
                else IY = (ushort)(IY & 0x00FF | value << 8);
                return;
            case 5:
                if (prefix == 0) L = value;
                else if (prefix == 1) IX = (ushort)(IX & 0xFF00 | value);
                else IY = (ushort)(IY & 0xFF00 | value);
                return;
            default: A = value; return;
        }
    }

    private ushort GetPair(int pair, int prefix) => pair switch
    {
        0 => BC,
        1 => DE,
        2 => prefix == 0 ? HL : prefix == 1 ? IX : IY,
        _ => SP,
    };

    private void SetPair(int pair, int prefix, ushort value)
    {
        switch (pair)
        {
            case 0: BC = value; return;
            case 1: DE = value; return;
            case 2:
                if (prefix == 0) HL = value;
                else if (prefix == 1) IX = value;
                else IY = value;
                return;
            default: SP = value; return;
        }
    }

    private ushort GetStackPair(int pair, int prefix) =>
        pair == 3 ? AF : GetPair(pair, prefix);

    private void SetStackPair(int pair, int prefix, ushort value)
    {
        if (pair == 3) AF = value;
        else SetPair(pair, prefix, value);
    }

    private bool Condition(int index) => index switch
    {
        0 => !Flag(ZeroFlag),
        1 => Flag(ZeroFlag),
        2 => !Flag(CarryFlag),
        3 => Flag(CarryFlag),
        4 => !Flag(ParityFlag),
        5 => Flag(ParityFlag),
        6 => !Flag(SignFlag),
        _ => Flag(SignFlag),
    };

    private void Arithmetic(int operation, byte value)
    {
        switch (operation)
        {
            case 0: Add(value, withCarry: false); return;
            case 1: Add(value, withCarry: true); return;
            case 2: Subtract(value, withCarry: false); return;
            case 3: Subtract(value, withCarry: true); return;
            case 4: And(value); return;
            case 5: Xor(value); return;
            case 6: Or(value); return;
            default: Compare(value); return;
        }
    }

    /// <summary>Where an instruction's memory operand is. Without a prefix that is
    /// simply HL; with one it is an index register plus a signed displacement that
    /// comes out of the instruction stream, and costs five clocks to add.</summary>
    private ushort MemoryOperand(int prefix)
    {
        if (prefix == 0)
            return HL;
        var displacement = (sbyte)Fetch();
        bus.Tick(5);
        return (ushort)((prefix == 1 ? IX : IY) + displacement);
    }

    // ---- interrupts -------------------------------------------------------------

    public void Step()
    {
        // An interrupt enabled by the instruction just executed is not accepted until
        // after the next one, which is what makes `EI RET` safe.
        var deferred = interruptsPending;
        interruptsPending = false;

        if (nmiArmed)
        {
            nmiArmed = false;
            Halted = false;
            Iff2 = Iff1;
            Iff1 = false;
            bus.Tick(5);
            Push(PC);
            PC = 0x66;
            return;
        }

        if (IrqLine && Iff1 && !deferred)
        {
            AcceptInterrupt();
            return;
        }

        if (Halted)
        {
            // A halted processor keeps fetching the same instruction until something
            // interrupts it, so the clock has to keep running.
            bus.Tick(4);
            R = (byte)(R & 0x80 | (R + 1) & 0x7F);
            return;
        }

        Execute(FetchOpcode(), prefix: 0);
    }

    private void AcceptInterrupt()
    {
        Halted = false;
        Iff1 = Iff2 = false;
        bus.Tick(7);
        switch (InterruptMode)
        {
            case 2:
                // Nothing on this console drives the data bus during the acknowledge,
                // so the vector's low byte is all ones.
                Push(PC);
                PC = ReadWord((ushort)(I << 8 | 0xFF));
                return;
            case 1:
                Push(PC);
                PC = 0x38;
                return;
            default:
                // Mode 0 executes whatever is on the bus; an undriven bus is $FF,
                // which is RST 38h — the same place mode 1 goes.
                Push(PC);
                PC = 0x38;
                return;
        }
    }

    // ---- the instruction set ----------------------------------------------------

    /// <summary>Opcodes decode by their bit fields rather than by a 256-way table: two
    /// bits of kind, then three of destination and three of source. Writing it the way
    /// the chip decodes it is what keeps the index-register prefixes from needing a
    /// second copy of everything.</summary>
    private void Execute(byte opcode, int prefix)
    {
        var kind = opcode >> 6;
        var high = opcode >> 3 & 7;
        var low = opcode & 7;
        var pair = high >> 1;

        switch (kind)
        {
            case 0:
                ExecuteGeneral(opcode, prefix, high, low, pair);
                return;

            case 1:
                if (high == 6 && low == 6)
                {
                    Halted = true;
                    return;
                }
                // Only one of the two operands can be an index displacement, so
                // whichever side is memory leaves the other as the real H or L.
                if (low == 6)
                    SetRegister(high, 0, ReadByte(MemoryOperand(prefix)));
                else if (high == 6)
                    WriteByte(MemoryOperand(prefix), GetRegister(low, 0));
                else
                    SetRegister(high, prefix, GetRegister(low, prefix));
                return;

            case 2:
                Arithmetic(high, low == 6
                    ? ReadByte(MemoryOperand(prefix))
                    : GetRegister(low, prefix));
                return;

            default:
                ExecuteStack(opcode, prefix, high, low, pair);
                return;
        }
    }

    private void ExecuteGeneral(byte opcode, int prefix, int high, int low, int pair)
    {
        switch (low)
        {
            case 0:
                switch (high)
                {
                    case 0:
                        return;
                    case 1:
                        (A, Aa) = (Aa, A);
                        (F, Fa) = (Fa, F);
                        return;
                    case 2:
                        // The counter is decremented before the branch is considered,
                        // and the extra clock is part of the opcode fetch.
                        bus.Tick(1);
                        B--;
                        JumpRelative(B != 0);
                        return;
                    case 3:
                        JumpRelative(true);
                        return;
                    default:
                        JumpRelative(Condition(high - 4));
                        return;
                }

            case 1:
                if ((high & 1) == 0)
                    SetPair(pair, prefix, FetchWord());
                else
                {
                    bus.Tick(7);
                    SetPair(2, prefix, AddWord(GetPair(2, prefix), GetPair(pair, prefix)));
                }
                return;

            case 2:
                ExecuteLoadIndirect(high, prefix);
                return;

            case 3:
                bus.Tick(2);
                SetPair(pair, prefix, (ushort)(GetPair(pair, prefix) + ((high & 1) == 0 ? 1 : -1)));
                return;

            case 4:
            case 5:
            {
                var decrement = low == 5;
                if (high == 6)
                {
                    var address = MemoryOperand(prefix);
                    var value = ReadByte(address);
                    bus.Tick(1);
                    WriteByte(address, decrement ? Decrement(value) : Increment(value));
                }
                else
                {
                    var value = GetRegister(high, prefix);
                    SetRegister(high, prefix, decrement ? Decrement(value) : Increment(value));
                }
                return;
            }

            case 6:
                if (high == 6)
                {
                    // The displacement comes before the value it stores, which is the
                    // only instruction where two bytes follow the prefix in that order.
                    var address = MemoryOperand(prefix);
                    WriteByte(address, Fetch());
                }
                else
                {
                    SetRegister(high, prefix, Fetch());
                }
                return;

            default:
                switch (high)
                {
                    case 0:
                    case 1:
                    case 2:
                    case 3:
                        RotateAccumulator(high);
                        return;
                    case 4:
                        DecimalAdjust();
                        return;
                    case 5:
                        A = (byte)~A;
                        F = (byte)(F & (SignFlag | ZeroFlag | ParityFlag | CarryFlag)
                            | HalfFlag | SubtractFlag | Undocumented(A));
                        return;
                    case 6:
                        F = (byte)(F & (SignFlag | ZeroFlag | ParityFlag) | CarryFlag
                            | Undocumented(A));
                        return;
                    default:
                        // Complementing the carry keeps the old one as the half-carry,
                        // which is the only record of what it used to be.
                        F = (byte)(F & (SignFlag | ZeroFlag | ParityFlag)
                            | (Flag(CarryFlag) ? HalfFlag : CarryFlag)
                            | Undocumented(A));
                        return;
                }
        }
    }

    private void ExecuteLoadIndirect(int high, int prefix)
    {
        switch (high)
        {
            case 0: WriteByte(BC, A); return;
            case 1: A = ReadByte(BC); return;
            case 2: WriteByte(DE, A); return;
            case 3: A = ReadByte(DE); return;
            case 4: WriteWord(FetchWord(), GetPair(2, prefix)); return;
            case 5: SetPair(2, prefix, ReadWord(FetchWord())); return;
            case 6: WriteByte(FetchWord(), A); return;
            default: A = ReadByte(FetchWord()); return;
        }
    }

    private void JumpRelative(bool taken)
    {
        var displacement = (sbyte)Fetch();
        if (!taken)
            return;
        bus.Tick(5);
        PC = (ushort)(PC + displacement);
    }

    private void ExecuteStack(byte opcode, int prefix, int high, int low, int pair)
    {
        switch (low)
        {
            case 0:
                bus.Tick(1);
                if (Condition(high))
                    PC = Pop();
                return;

            case 1:
                if ((high & 1) == 0)
                {
                    SetStackPair(pair, prefix, Pop());
                    return;
                }
                switch (pair)
                {
                    case 0: PC = Pop(); return;
                    case 1: Exchange(); return;
                    case 2: PC = GetPair(2, prefix); return;
                    default: bus.Tick(2); SP = GetPair(2, prefix); return;
                }

            case 2:
            {
                var target = FetchWord();
                if (Condition(high))
                    PC = target;
                return;
            }

            case 3:
                switch (high)
                {
                    case 0: PC = FetchWord(); return;
                    case 1: ExecuteBit(prefix); return;
                    case 2: WritePort((ushort)(A << 8 | Fetch()), A); return;
                    case 3: A = ReadPort((ushort)(A << 8 | Fetch())); return;
                    case 4:
                    {
                        var top = Pop();
                        bus.Tick(1);
                        var index = GetPair(2, prefix);
                        SP -= 2;
                        WriteByte((ushort)(SP + 1), (byte)(index >> 8));
                        WriteByte(SP, (byte)index);
                        bus.Tick(2);
                        SetPair(2, prefix, top);
                        return;
                    }
                    case 5:
                        (D, H) = (H, D);
                        (E, L) = (L, E);
                        return;
                    case 6:
                        Iff1 = Iff2 = false;
                        return;
                    default:
                        Iff1 = Iff2 = true;
                        interruptsPending = true;
                        return;
                }

            case 4:
            {
                var target = FetchWord();
                if (!Condition(high))
                    return;
                bus.Tick(1);
                Push(PC);
                PC = target;
                return;
            }

            case 5:
                if ((high & 1) == 0)
                {
                    bus.Tick(1);
                    Push(GetStackPair(pair, prefix));
                    return;
                }
                switch (pair)
                {
                    case 0:
                    {
                        var target = FetchWord();
                        bus.Tick(1);
                        Push(PC);
                        PC = target;
                        return;
                    }
                    case 1: Execute(FetchOpcode(), prefix: 1); return;
                    case 2: ExecuteExtended(); return;
                    default: Execute(FetchOpcode(), prefix: 2); return;
                }

            case 6:
                Arithmetic(high, Fetch());
                return;

            default:
                bus.Tick(1);
                Push(PC);
                PC = (ushort)(high * 8);
                return;
        }
    }

    private void Exchange()
    {
        (B, Ba) = (Ba, B);
        (C, Ca) = (Ca, C);
        (D, Da) = (Da, D);
        (E, Ea) = (Ea, E);
        (H, Ha) = (Ha, H);
        (L, La) = (La, L);
    }

    /// <summary>The bit instructions. With an index prefix the displacement comes
    /// *before* the opcode, which is why this cannot be folded into the main decode —
    /// and the result is written back to a named register as well as to memory, an
    /// undocumented habit a few games rely on.</summary>
    private void ExecuteBit(int prefix)
    {
        ushort address;
        byte opcode;
        if (prefix == 0)
        {
            opcode = FetchOpcode();
            address = HL;
        }
        else
        {
            var displacement = (sbyte)Fetch();
            address = (ushort)((prefix == 1 ? IX : IY) + displacement);
            // The second opcode byte of a displaced bit instruction is not a fetch the
            // refresh counter sees.
            opcode = Fetch();
            bus.Tick(2);
        }

        var kind = opcode >> 6;
        var bit = opcode >> 3 & 7;
        var target = opcode & 7;
        var memory = prefix != 0 || target == 6;

        var value = memory ? ReadByte(address) : GetRegister(target, 0);
        if (memory && prefix == 0)
            bus.Tick(1);

        if (kind == 1)
        {
            TestBit(bit, value, memory ? (byte)(address >> 8) : value);
            return;
        }

        var result = kind switch
        {
            0 => Rotate(bit, value),
            2 => (byte)(value & ~(1 << bit)),
            _ => (byte)(value | 1 << bit),
        };

        if (memory)
        {
            WriteByte(address, result);
            // An index-displaced form also drops the answer in the register the low
            // three bits name, unless they name memory itself.
            if (prefix != 0 && target != 6)
                SetRegister(target, 0, result);
        }
        else
        {
            SetRegister(target, 0, result);
        }
    }

    /// <summary>The extended set: sixteen-bit arithmetic with carry, the port
    /// instructions that address through BC, the interrupt modes, and the block
    /// moves.</summary>
    private void ExecuteExtended()
    {
        var opcode = FetchOpcode();
        var kind = opcode >> 6;
        var high = opcode >> 3 & 7;
        var low = opcode & 7;
        var pair = high >> 1;

        if (kind == 2 && low <= 3 && high >= 4)
        {
            ExecuteBlock(high, low);
            return;
        }
        if (kind != 1)
            // Everything else in the extended page does nothing at all, and the two
            // fetches that got here have already cost their clocks.
            return;

        switch (low)
        {
            case 0:
            {
                var value = ReadPort(BC);
                if (high != 6)
                    SetRegister(high, 0, value);
                F = (byte)(SignZero(value) | Parity[value] | (F & CarryFlag));
                return;
            }

            case 1:
                WritePort(BC, high == 6 ? (byte)0 : GetRegister(high, 0));
                return;

            case 2:
                bus.Tick(7);
                if ((high & 1) == 0)
                    SbcWord(GetPair(pair, 0));
                else
                    AdcWord(GetPair(pair, 0));
                return;

            case 3:
                if ((high & 1) == 0)
                    WriteWord(FetchWord(), GetPair(pair, 0));
                else
                    SetPair(pair, 0, ReadWord(FetchWord()));
                return;

            case 4:
                Negate();
                return;

            case 5:
                // Returning from an interrupt puts back the enable the acknowledge
                // took away; the maskable and non-maskable forms differ in name only
                // on this machine.
                PC = Pop();
                Iff1 = Iff2;
                return;

            case 6:
                InterruptMode = high switch
                {
                    0 or 1 or 4 or 5 => 0,
                    2 or 6 => 1,
                    _ => 2,
                };
                return;

            default:
                switch (high)
                {
                    case 0: bus.Tick(1); I = A; return;
                    case 1: bus.Tick(1); R = A; return;
                    case 2:
                    case 3:
                    {
                        bus.Tick(1);
                        A = high == 2 ? I : R;
                        // Parity carries the shadow interrupt enable here rather than
                        // the parity of anything, which is how a program reads whether
                        // interrupts were on.
                        F = (byte)(SignZero(A) | (F & CarryFlag) | (Iff2 ? ParityFlag : 0));
                        return;
                    }
                    case 4: RotateDecimal(left: false); return;
                    case 5: RotateDecimal(left: true); return;
                    default: return;
                }
        }
    }

    /// <summary>Move, compare, and the two port block instructions — each in four
    /// flavours: up or down, once or until the counter runs out. Repeating is done by
    /// stepping the program counter back over the two-byte opcode, which is exactly
    /// what the hardware does and why an interrupt can land in the middle of one.</summary>
    private void ExecuteBlock(int high, int low)
    {
        var step = (high & 1) == 0 ? 1 : -1;
        var repeats = high >= 6;

        switch (low)
        {
            case 0:
            {
                var value = ReadByte(HL);
                WriteByte(DE, value);
                bus.Tick(2);
                HL = (ushort)(HL + step);
                DE = (ushort)(DE + step);
                BC--;
                var undocumented = (byte)(value + A);
                F = (byte)(F & (SignFlag | ZeroFlag | CarryFlag)
                    | (BC != 0 ? ParityFlag : 0)
                    | (undocumented & 0x02) << 4
                    | (undocumented & XFlag));
                if (repeats && BC != 0)
                {
                    bus.Tick(5);
                    PC -= 2;
                }
                return;
            }

            case 1:
            {
                var value = ReadByte(HL);
                bus.Tick(5);
                var carry = F & CarryFlag;
                var total = A - value;
                var result = (byte)total;
                var half = (A ^ value ^ result) & HalfFlag;
                HL = (ushort)(HL + step);
                BC--;
                var undocumented = (byte)(result - (half != 0 ? 1 : 0));
                F = (byte)((result == 0 ? ZeroFlag : 0) | (result & SignFlag)
                    | SubtractFlag | half | carry
                    | (BC != 0 ? ParityFlag : 0)
                    | (undocumented & 0x02) << 4
                    | (undocumented & XFlag));
                if (repeats && BC != 0 && result != 0)
                {
                    bus.Tick(5);
                    PC -= 2;
                }
                return;
            }

            case 2:
            {
                bus.Tick(1);
                var value = ReadPort(BC);
                WriteByte(HL, value);
                B = Decrement(B);
                HL = (ushort)(HL + step);
                SetFlag(SubtractFlag, (value & 0x80) != 0);
                if (repeats && B != 0)
                {
                    bus.Tick(5);
                    PC -= 2;
                }
                return;
            }

            default:
            {
                bus.Tick(1);
                var value = ReadByte(HL);
                // The counter comes down before the port sees the address, so an
                // OUTI writes to a port whose high byte is already one lower.
                B = Decrement(B);
                WritePort(BC, value);
                HL = (ushort)(HL + step);
                SetFlag(SubtractFlag, (value & 0x80) != 0);
                if (repeats && B != 0)
                {
                    bus.Tick(5);
                    PC -= 2;
                }
                return;
            }
        }
    }

    // ---- state ------------------------------------------------------------------

    internal void Save(ref StateWriter state)
    {
        state.Write(A); state.Write(F); state.Write(B); state.Write(C);
        state.Write(D); state.Write(E); state.Write(H); state.Write(L);
        state.Write(Aa); state.Write(Fa); state.Write(Ba); state.Write(Ca);
        state.Write(Da); state.Write(Ea); state.Write(Ha); state.Write(La);
        state.Write(IX); state.Write(IY); state.Write(SP); state.Write(PC);
        state.Write(I); state.Write(R);
        state.Write(Iff1);
        state.Write(Iff2);
        state.Write(InterruptMode);
        state.Write(Halted);
        state.Write(IrqLine);
        state.Write(nmiArmed);
        state.Write(nmiLast);
        state.Write(interruptsPending);
    }

    internal void Load(ref StateReader state)
    {
        A = state.ReadByte(); F = state.ReadByte(); B = state.ReadByte(); C = state.ReadByte();
        D = state.ReadByte(); E = state.ReadByte(); H = state.ReadByte(); L = state.ReadByte();
        Aa = state.ReadByte(); Fa = state.ReadByte(); Ba = state.ReadByte(); Ca = state.ReadByte();
        Da = state.ReadByte(); Ea = state.ReadByte(); Ha = state.ReadByte(); La = state.ReadByte();
        IX = state.ReadUInt16(); IY = state.ReadUInt16();
        SP = state.ReadUInt16(); PC = state.ReadUInt16();
        I = state.ReadByte(); R = state.ReadByte();
        Iff1 = state.ReadBool();
        Iff2 = state.ReadBool();
        InterruptMode = state.ReadInt32();
        Halted = state.ReadBool();
        IrqLine = state.ReadBool();
        nmiArmed = state.ReadBool();
        nmiLast = state.ReadBool();
        interruptsPending = state.ReadBool();
    }
}
