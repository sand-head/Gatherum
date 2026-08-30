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

    private GamepadButtons buttons;
    private byte controllerShift;
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

    public void SetButtons(GamepadButtons pressed)
    {
        // Up and down at once, or left and right, is something the plastic made
        // impossible and a few games crash on. The last one pressed wins.
        if ((pressed & (GamepadButtons.Up | GamepadButtons.Down))
            == (GamepadButtons.Up | GamepadButtons.Down))
            pressed &= ~GamepadButtons.Down;
        if ((pressed & (GamepadButtons.Left | GamepadButtons.Right))
            == (GamepadButtons.Left | GamepadButtons.Right))
            pressed &= ~GamepadButtons.Right;
        buttons = pressed;
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
            0x4016 => ReadController(),
            // The second port, which nothing here is plugged into.
            0x4017 => 0x40,
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
                    controllerShift = (byte)buttons;
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

    /// <summary>The pad is a shift register: strobe it and it latches the buttons, then
    /// each read hands back one and shifts. Past the eighth, an official pad reads
    /// high.</summary>
    private byte ReadController()
    {
        if (controllerStrobe)
            controllerShift = (byte)buttons;
        var bit = (byte)(controllerShift & 1);
        controllerShift = (byte)(controllerShift >> 1 | 0x80);
        return (byte)(0x40 | bit);
    }
}
