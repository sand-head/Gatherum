namespace Gatherum.Client.Emulation.GameBoy;

public enum GameBoyInterrupt
{
    VBlank = 0,
    LcdStatus = 1,
    Timer = 2,
    Serial = 3,
    Joypad = 4,
}

/// <summary>A Game Boy, or a Game Boy Color when the cartridge asks to be one: eight
/// kilobytes of memory (thirty-two, in colour), a processor, a picture chip, four sound
/// channels and a cartridge.</summary>
public sealed class GameBoyConsole : IEmulatorCore
{
    private const int FrameCycles = 70224;

    private readonly GameBoyCartridge cartridge;
    private readonly GameBoyPpu ppu;
    private readonly GameBoyApu apu;

    private readonly byte[] work = new byte[8 * 4 * 1024];
    private readonly byte[] high = new byte[127];
    private int workBank = 1;

    private GamepadButtons buttons;

    private byte joypadSelect = 0x30;

    private ushort divider;
    private byte timerCounter, timerModulo, timerControl;
    private bool timerOverflowLast;

    private byte serialData, serialControl;

    private bool doubleSpeed, speedSwitchArmed;

    private ushort blockSource, blockDestination;
    private int blockRemaining;
    private bool blockOnHorizontalBlank, blockWasInHorizontalBlank;

    private int frameCycles;

    public byte InterruptEnable;
    public byte InterruptFlags = 0xE1;

    public bool Colour { get; }

    /// <summary>The processor, so a test can step one instruction and look.</summary>
    public Sm83 Cpu { get; }

    /// <summary>Clocks since reset, at the console's own 4.194304 MHz — the yardstick
    /// every other count here is derived from.</summary>
    public long Cycles { get; private set; }

    public GameBoyConsole(ReadOnlySpan<byte> rom)
    {
        cartridge = new GameBoyCartridge(rom);
        Colour = cartridge.Colour;
        ppu = new GameBoyPpu(this);
        apu = new GameBoyApu();
        Cpu = new Sm83(this);
        Reset();
    }

    public string SystemName => Colour ? "Game Boy Color" : "Game Boy";
    public int Width => GameBoyPpu.Width;
    public int Height => GameBoyPpu.Height;

    /// <summary>Not sixty either: 70224 clocks a frame at 4.194304 MHz.</summary>
    public double FramesPerSecond => 59.7275;

    public int SampleRate => GameBoyApu.SampleRate;

    /// <summary>One. Two people on a Game Boy meant two Game Boys and a cable between
    /// them, which is a machine to emulate rather than a second port to read.</summary>
    public int PlayerCount => 1;
    public uint[] Frame => ppu.Frame;
    public bool BatteryBacked => cartridge.Battery;
    public bool SaveDirty => cartridge.SaveDirty;
    public string Title => cartridge.Title;

    public void Reset()
    {
        Array.Clear(work);
        Array.Clear(high);
        ppu.Reset();
        apu.Reset();
        Cpu.Reset(Colour);
        divider = 0xABCC;
        timerCounter = timerModulo = timerControl = 0;
        InterruptEnable = 0;
        InterruptFlags = 0xE1;
        doubleSpeed = speedSwitchArmed = false;
        blockRemaining = 0;
        frameCycles = 0;
    }

    public void SetButtons(int player, GamepadButtons pressed)
    {
        if (player != 0)
            return;
        if ((pressed & (GamepadButtons.Up | GamepadButtons.Down))
            == (GamepadButtons.Up | GamepadButtons.Down))
            pressed &= ~GamepadButtons.Down;
        if ((pressed & (GamepadButtons.Left | GamepadButtons.Right))
            == (GamepadButtons.Left | GamepadButtons.Right))
            pressed &= ~GamepadButtons.Right;
        // A button going down while its half of the pad is selected is an interrupt,
        // which is the only thing that wakes a console stopped in a low-power state.
        if ((pressed & ~buttons) != GamepadButtons.None)
            RequestInterrupt(GameBoyInterrupt.Joypad);
        buttons = pressed;
    }

    public void RunFrame()
    {
        ppu.FrameComplete = false;
        frameCycles = 0;
        // With the screen off there is no frame to wait for, so the clock decides —
        // otherwise a game that blanks the display to rewrite its tiles would hang
        // the player rather than pause it.
        while (!ppu.FrameComplete && frameCycles < FrameCycles * 2)
            Cpu.Step();
    }

    public int ReadAudio(short[] destination) => apu.ReadAudio(destination);

    /// <summary>A four-byte tag and a version, so a state from another console — or
    /// from a build whose fields have moved — is refused rather than misread.</summary>
    private static ReadOnlySpan<byte> StateTag => "GMB1"u8;

    public int SaveStateSize
    {
        get
        {
            var measure = StateWriter.Measure();
            Write(ref measure);
            return measure.Length;
        }
    }

    public bool SaveState(Span<byte> destination)
    {
        var state = new StateWriter(destination);
        Write(ref state);
        return !state.Failed;
    }

    public bool LoadState(ReadOnlySpan<byte> source)
    {
        if (source.Length < 4 || !source[..4].SequenceEqual(StateTag))
            return false;
        var state = new StateReader(source);
        state.Skip(StateTag.Length);

        Cpu.Load(ref state);
        ppu.Load(ref state);
        apu.Load(ref state);
        cartridge.Load(ref state);
        state.Read(work);
        state.Read(high);
        workBank = state.ReadInt32();
        buttons = (GamepadButtons)state.ReadByte();
        joypadSelect = state.ReadByte();
        divider = state.ReadUInt16();
        timerCounter = state.ReadByte();
        timerModulo = state.ReadByte();
        timerControl = state.ReadByte();
        timerOverflowLast = state.ReadBool();
        serialData = state.ReadByte();
        serialControl = state.ReadByte();
        doubleSpeed = state.ReadBool();
        speedSwitchArmed = state.ReadBool();
        blockSource = state.ReadUInt16();
        blockDestination = state.ReadUInt16();
        blockRemaining = state.ReadInt32();
        blockOnHorizontalBlank = state.ReadBool();
        blockWasInHorizontalBlank = state.ReadBool();
        InterruptEnable = state.ReadByte();
        InterruptFlags = state.ReadByte();
        Cycles = state.ReadInt64();
        frameCycles = state.ReadInt32();

        if (state.Failed)
        {
            Reset();
            return false;
        }
        return true;
    }

    private void Write(ref StateWriter state)
    {
        state.Write(StateTag);
        Cpu.Save(ref state);
        ppu.Save(ref state);
        apu.Save(ref state);
        cartridge.Save(ref state);
        state.Write(work);
        state.Write(high);
        state.Write(workBank);
        state.Write((byte)buttons);
        state.Write(joypadSelect);
        state.Write(divider);
        state.Write(timerCounter);
        state.Write(timerModulo);
        state.Write(timerControl);
        state.Write(timerOverflowLast);
        state.Write(serialData);
        state.Write(serialControl);
        state.Write(doubleSpeed);
        state.Write(speedSwitchArmed);
        state.Write(blockSource);
        state.Write(blockDestination);
        state.Write(blockRemaining);
        state.Write(blockOnHorizontalBlank);
        state.Write(blockWasInHorizontalBlank);
        state.Write(InterruptEnable);
        state.Write(InterruptFlags);
        state.Write(Cycles);
        state.Write(frameCycles);
    }

    public byte[] SaveRam() => cartridge.SaveRam();

    public void LoadSaveRam(ReadOnlySpan<byte> data) => cartridge.LoadSaveRam(data);

    public void MarkSaved() => cartridge.MarkSaved();

    public void RequestInterrupt(GameBoyInterrupt interrupt) =>
        InterruptFlags |= (byte)(1 << (int)interrupt);

    /// <summary>A colour console runs its processor at twice the speed on request, with
    /// everything else — picture, sound, the cartridge's clock — still on the original
    /// one. The switch happens on a STOP instruction, which is what that instruction is
    /// actually for.</summary>
    public void SwitchSpeed()
    {
        if (!Colour || !speedSwitchArmed)
            return;
        doubleSpeed = !doubleSpeed;
        speedSwitchArmed = false;
    }

    /// <summary>One machine cycle: four clocks, or two of everyone else's when the
    /// processor is running double.</summary>
    public void Tick()
    {
        StepTimer(4);
        Cycles += 4;
        var cycles = doubleSpeed ? 2 : 4;
        frameCycles += cycles;
        ppu.Step(cycles);
        apu.Step(cycles);
        cartridge.Tick(cycles);
        StepBlockTransfer();
    }

    /// <summary>The divider is a free-running counter whose top byte is what $FF04
    /// shows; the timer counts the *falling* edges of one of its bits, which is why
    /// writing $FF04 can tick the timer as a side effect.</summary>
    private void StepTimer(int cycles)
    {
        divider = (ushort)(divider + cycles);
        var bit = (timerControl & 0x03) switch
        {
            0 => 9,
            1 => 3,
            2 => 5,
            _ => 7,
        };
        var overflow = (timerControl & 0x04) != 0 && (divider >> bit & 1) != 0;
        if (timerOverflowLast && !overflow && ++timerCounter == 0)
        {
            timerCounter = timerModulo;
            RequestInterrupt(GameBoyInterrupt.Timer);
        }
        timerOverflowLast = overflow;
    }

    /// <summary>A colour cartridge's block copier moves sixteen bytes each time the
    /// picture chip rests between lines, so a game can rewrite a screen's worth of
    /// tiles without a frame of its own to do it in.</summary>
    private void StepBlockTransfer()
    {
        if (blockRemaining == 0 || !blockOnHorizontalBlank)
            return;
        var resting = ppu.InHorizontalBlank;
        if (!resting || blockWasInHorizontalBlank)
        {
            blockWasInHorizontalBlank = resting;
            return;
        }
        blockWasInHorizontalBlank = true;
        CopyBlock(16);
    }

    private void CopyBlock(int bytes)
    {
        for (var index = 0; index < bytes && blockRemaining > 0; index++)
        {
            ppu.WriteVram(blockDestination, ReadByte(blockSource));
            blockSource++;
            blockDestination++;
            blockRemaining--;
        }
    }

    public byte ReadByte(ushort address)
    {
        switch (address)
        {
            case < 0x8000:
            case >= 0xA000 and < 0xC000:
                return cartridge.Read(address);
            case < 0xA000:
                return ppu.ReadVram(address);
            case < 0xD000:
                return work[address - 0xC000];
            case < 0xE000:
                return work[workBank * 0x1000 + (address - 0xD000)];
            case < 0xFE00:
                // The echo of work memory, which nothing should use and a few games do.
                return ReadByte((ushort)(address - 0x2000));
            case < 0xFEA0:
                return ppu.ReadOam(address);
            case < 0xFF00:
                return 0xFF;
            case 0xFFFF:
                return InterruptEnable;
            case >= 0xFF80:
                return high[address - 0xFF80];
            default:
                return ReadIo(address);
        }
    }

    public void WriteByte(ushort address, byte value)
    {
        switch (address)
        {
            case < 0x8000:
            case >= 0xA000 and < 0xC000:
                cartridge.Write(address, value);
                return;
            case < 0xA000:
                ppu.WriteVram(address, value);
                return;
            case < 0xD000:
                work[address - 0xC000] = value;
                return;
            case < 0xE000:
                work[workBank * 0x1000 + (address - 0xD000)] = value;
                return;
            case < 0xFE00:
                WriteByte((ushort)(address - 0x2000), value);
                return;
            case < 0xFEA0:
                ppu.WriteOam(address, value);
                return;
            case < 0xFF00:
                return;
            case 0xFFFF:
                InterruptEnable = value;
                return;
            case >= 0xFF80:
                high[address - 0xFF80] = value;
                return;
            default:
                WriteIo(address, value);
                return;
        }
    }

    private byte ReadIo(ushort address) => address switch
    {
        0xFF00 => ReadJoypad(),
        0xFF01 => serialData,
        0xFF02 => (byte)(serialControl | 0x7E),
        0xFF04 => (byte)(divider >> 8),
        0xFF05 => timerCounter,
        0xFF06 => timerModulo,
        0xFF07 => (byte)(timerControl | 0xF8),
        0xFF0F => (byte)(InterruptFlags | 0xE0),
        >= 0xFF10 and <= 0xFF3F => apu.ReadRegister(address),
        >= 0xFF40 and <= 0xFF4B => ppu.ReadRegister(address),
        0xFF4D => (byte)(0x7E | (doubleSpeed ? 0x80 : 0) | (speedSwitchArmed ? 0x01 : 0)),
        0xFF4F => ppu.ReadRegister(address),
        0xFF55 => (byte)(blockRemaining == 0 ? 0xFF : blockRemaining / 16 - 1),
        >= 0xFF68 and <= 0xFF6B => ppu.ReadRegister(address),
        0xFF70 => (byte)workBank,
        _ => 0xFF,
    };

    private void WriteIo(ushort address, byte value)
    {
        switch (address)
        {
            case 0xFF00: joypadSelect = (byte)(value & 0x30); return;
            case 0xFF01: serialData = value; return;
            case 0xFF02:
                serialControl = value;
                // Nothing is plugged into the link port, so a transfer completes at
                // once against an open cable: all ones in, and the interrupt a game
                // is waiting for.
                if ((value & 0x81) == 0x81)
                {
                    serialData = 0xFF;
                    serialControl &= 0x7F;
                    RequestInterrupt(GameBoyInterrupt.Serial);
                }
                return;
            case 0xFF04: divider = 0; return;
            case 0xFF05: timerCounter = value; return;
            case 0xFF06: timerModulo = value; return;
            case 0xFF07: timerControl = (byte)(value & 0x07); return;
            case 0xFF0F: InterruptFlags = (byte)(value & 0x1F); return;
            case >= 0xFF10 and <= 0xFF3F: apu.WriteRegister(address, value); return;
            case 0xFF46: CopySpriteMemory(value); return;
            case >= 0xFF40 and <= 0xFF4B: ppu.WriteRegister(address, value); return;
            case 0xFF4D:
                if (Colour)
                    speedSwitchArmed = (value & 1) != 0;
                return;
            case 0xFF4F:
                if (Colour)
                    ppu.WriteRegister(address, value);
                return;
            case 0xFF51: blockSource = (ushort)(blockSource & 0x00FF | value << 8); return;
            case 0xFF52: blockSource = (ushort)(blockSource & 0xFF00 | value & 0xF0); return;
            case 0xFF53:
                blockDestination = (ushort)(blockDestination & 0x00FF | (value & 0x1F) << 8);
                return;
            case 0xFF54:
                blockDestination = (ushort)(blockDestination & 0xFF00 | value & 0xF0);
                return;
            case 0xFF55: StartBlockTransfer(value); return;
            case >= 0xFF68 and <= 0xFF6B:
                if (Colour)
                    ppu.WriteRegister(address, value);
                return;
            case 0xFF70:
                if (Colour)
                    workBank = Math.Max(1, value & 0x07);
                return;
        }
    }

    private void StartBlockTransfer(byte value)
    {
        if (!Colour)
            return;
        // Writing with the high bit clear during a resting transfer stops it; that is
        // the only way to cancel one.
        if (blockRemaining > 0 && blockOnHorizontalBlank && (value & 0x80) == 0)
        {
            blockRemaining = 0;
            return;
        }
        blockRemaining = ((value & 0x7F) + 1) * 16;
        blockOnHorizontalBlank = (value & 0x80) != 0;
        blockWasInHorizontalBlank = false;
        if (!blockOnHorizontalBlank)
            CopyBlock(blockRemaining);
    }

    /// <summary>The one transfer both machines have: a page of memory into the sprite
    /// table. It takes the bus for 160 machine cycles on the hardware, which is why
    /// every game runs the routine that starts it from high memory.</summary>
    private void CopySpriteMemory(byte page)
    {
        var source = (ushort)(page << 8);
        for (var index = 0; index < 160; index++)
            ppu.WriteOam((ushort)(0xFE00 + index), ReadByte((ushort)(source + index)));
    }

    /// <summary>A pressed button reads as a zero, and the two halves of the pad share
    /// four lines — which is why reading it means choosing a half first.</summary>
    private byte ReadJoypad()
    {
        var lines = 0x0F;
        if ((joypadSelect & 0x10) == 0)
        {
            if (buttons.HasFlag(GamepadButtons.Right)) lines &= ~0x01;
            if (buttons.HasFlag(GamepadButtons.Left)) lines &= ~0x02;
            if (buttons.HasFlag(GamepadButtons.Up)) lines &= ~0x04;
            if (buttons.HasFlag(GamepadButtons.Down)) lines &= ~0x08;
        }
        if ((joypadSelect & 0x20) == 0)
        {
            if (buttons.HasFlag(GamepadButtons.A)) lines &= ~0x01;
            if (buttons.HasFlag(GamepadButtons.B)) lines &= ~0x02;
            if (buttons.HasFlag(GamepadButtons.Select)) lines &= ~0x04;
            if (buttons.HasFlag(GamepadButtons.Start)) lines &= ~0x08;
        }
        return (byte)(0xC0 | joypadSelect | lines);
    }
}
