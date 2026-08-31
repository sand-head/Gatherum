namespace Gatherum.Client.Emulation.Nes;

/// <summary>A Nintendo Entertainment System: two kilobytes of memory, a processor, a
/// picture chip running at three times its speed, a sound chip sharing its die, and a
/// cartridge deciding what the top of the address space contains.</summary>
public sealed class NesConsole : IEmulatorCore
{
    public NesCartridge Cartridge { get; }
    public Cpu6502 Cpu { get; }

    private readonly NesPpu ppu;
    private readonly NesApu apu;
    private readonly byte[] ram = new byte[2 * 1024];

    /// <summary>Two ports, because the console has two and a game that offers a second
    /// player polls both. Nothing plugged into port two reads as no buttons at all,
    /// which is exactly what an empty port did.</summary>
    private readonly GamepadButtons[] buttons = new GamepadButtons[2];
    private readonly byte[] controllerShift = new byte[2];
    private bool controllerStrobe;
    private long cycles;
    private bool saveDirty;

    public NesConsole(ReadOnlySpan<byte> rom)
    {
        Cartridge = NesCartridge.Load(rom);
        ppu = new NesPpu(this);
        apu = new NesApu(this);
        Cpu = new Cpu6502(this);
        Reset();
    }

    /// <summary>Processor cycles since reset. Everything on the board is clocked from
    /// this one count, which is also what makes it worth asserting against.</summary>
    public long Cycles => cycles;

    public string SystemName => "Nintendo Entertainment System";
    public int Width => NesPpu.Width;
    public int Height => NesPpu.Height;

    /// <summary>Sixty frames a second is the television's number, not the console's:
    /// the picture chip takes 89341.5 of its own cycles per frame at 5.37 MHz.</summary>
    public double FramesPerSecond => 60.0988;

    public int SampleRate => NesApu.SampleRate;

    /// <summary>One. The console mixes everything down to a single pin.</summary>
    public int AudioChannels => 1;

    public ButtonLabels Buttons => new("A", "B", "Start", "Select");
    public int PlayerCount => 2;
    public uint[] Frame => ppu.Frame;
    public bool BatteryBacked => Cartridge.Battery;
    public bool SaveDirty => saveDirty;

    public void Reset()
    {
        Array.Clear(ram);
        ppu.Reset();
        apu.Reset();
        Cpu.Reset();
        cycles = 0;
    }

    public void SetButtons(int player, GamepadButtons pressed)
    {
        if (player is < 0 or > 1)
            return;
        // Up and down at once, or left and right, is something the plastic made
        // impossible and a few games crash on. The last one pressed wins.
        if ((pressed & (GamepadButtons.Up | GamepadButtons.Down))
            == (GamepadButtons.Up | GamepadButtons.Down))
            pressed &= ~GamepadButtons.Down;
        if ((pressed & (GamepadButtons.Left | GamepadButtons.Right))
            == (GamepadButtons.Left | GamepadButtons.Right))
            pressed &= ~GamepadButtons.Right;
        buttons[player] = pressed;
    }

    public void RunFrame()
    {
        // A frame is over when the picture chip says so. The guard is for a cartridge
        // that has wedged the processor — a jammed opcode, or a mapper this player
        // guessed wrong about — so that a bad ROM slows the page down rather than
        // hanging the browser's whole thread.
        var guard = 0;
        while (!ppu.FrameComplete && guard++ < 1_000_000)
            Cpu.Step();
        ppu.FrameComplete = false;
    }

    public int ReadAudio(short[] destination) => apu.ReadAudio(destination);

    /// <summary>A four-byte tag and a version, so a state from another console — or
    /// from a build whose fields have moved — is refused rather than misread.</summary>
    private static ReadOnlySpan<byte> StateTag => "NES1"u8;

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
        Cartridge.Mapper.Load(ref state);
        state.Read(ram);
        cycles = state.ReadInt64();
        buttons[0] = (GamepadButtons)state.ReadByte();
        buttons[1] = (GamepadButtons)state.ReadByte();
        controllerShift[0] = state.ReadByte();
        controllerShift[1] = state.ReadByte();
        controllerStrobe = state.ReadBool();

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
        Cartridge.Mapper.Save(ref state);
        state.Write(ram);
        state.Write(cycles);
        state.Write((byte)buttons[0]);
        state.Write((byte)buttons[1]);
        state.Write(controllerShift[0]);
        state.Write(controllerShift[1]);
        state.Write(controllerStrobe);
    }

    public byte[] SaveRam() => Cartridge.Battery ? Cartridge.ProgramRam.ToArray() : [];

    public void LoadSaveRam(ReadOnlySpan<byte> data)
    {
        if (!Cartridge.Battery || data.Length == 0)
            return;
        data[..Math.Min(data.Length, Cartridge.ProgramRam.Length)]
            .CopyTo(Cartridge.ProgramRam);
        saveDirty = false;
    }

    public void MarkSaved() => saveDirty = false;

    /// <summary>One processor cycle, and everything that rides on it.</summary>
    public void Tick()
    {
        cycles++;
        apu.Tick((cycles & 1) == 0);
        ppu.Step();
        ppu.Step();
        ppu.Step();
        Cpu.SetNmi(ppu.NmiLine);
        Cpu.IrqLine = apu.IrqPending || Cartridge.Mapper.IrqPending;
    }

    public byte CpuRead(ushort address)
    {
        if (address < 0x2000)
            return ram[address & 0x07FF];
        if (address < 0x4000)
            return ppu.ReadRegister(address);
        return address switch
        {
            0x4015 => apu.ReadStatus(),
            0x4016 => ReadController(0),
            // Reading $4017 is the second pad; writing it is the sound chip's frame
            // counter. The address does two unrelated jobs depending on the direction.
            0x4017 => ReadController(1),
            < 0x4020 => 0,
            _ => Cartridge.Mapper.CpuRead(address),
        };
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address < 0x2000)
        {
            ram[address & 0x07FF] = value;
            return;
        }
        if (address < 0x4000)
        {
            ppu.WriteRegister(address, value);
            return;
        }
        switch (address)
        {
            case 0x4014:
                OamDma(value);
                return;
            case 0x4016:
                controllerStrobe = (value & 1) != 0;
                if (controllerStrobe)
                    LatchControllers();
                return;
            case < 0x4014 or 0x4015 or 0x4017:
                apu.WriteRegister(address, value);
                return;
            case < 0x4020:
                return;
            default:
                if (address is >= 0x6000 and < 0x8000 && Cartridge.Battery)
                    saveDirty = true;
                Cartridge.Mapper.CpuWrite(address, value);
                return;
        }
    }

    /// <summary>Copying a page into sprite memory takes the bus away from the program
    /// for over five hundred cycles, and a game that starts one mid-scanline is
    /// counting on exactly that.</summary>
    private void OamDma(byte page)
    {
        Tick();
        if ((cycles & 1) != 0)
            Tick();
        for (var index = 0; index < 256; index++)
        {
            Tick();
            var value = CpuRead((ushort)(page << 8 | index));
            Tick();
            ppu.WriteOam(value);
        }
    }

    private void LatchControllers()
    {
        controllerShift[0] = (byte)buttons[0];
        controllerShift[1] = (byte)buttons[1];
    }

    /// <summary>The pad is a shift register: strobe it and it latches the buttons, then
    /// each read hands back one and shifts. Past the eighth, an official pad reads
    /// high. Both ports share the strobe, which is why one write latches the pair.</summary>
    private byte ReadController(int port)
    {
        if (controllerStrobe)
            LatchControllers();
        var bit = (byte)(controllerShift[port] & 1);
        controllerShift[port] = (byte)(controllerShift[port] >> 1 | 0x80);
        return (byte)(0x40 | bit);
    }
}
