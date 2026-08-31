namespace Gatherum.Client.Emulation.Sega;

/// <summary>A Master System, or a Game Gear — the same machine in a different case, with
/// a smaller window onto the same picture, one pad instead of two, and a stereo register
/// the bigger console never had. Which one a cartridge wants is what its file says it
/// is, because the hardware difference is small enough that the code inside is nearly
/// the same.</summary>
public sealed class MasterSystem : IEmulatorCore
{
    /// <summary>3.579545 MHz, 228 cycles a line, 262 lines: 59.92 frames a second.</summary>
    private const int FrameCycles = SegaVdp.CyclesPerLine * SegaVdp.LinesPerFrame;

    private readonly SegaCartridge cartridge;
    private readonly SegaVdp vdp;
    private readonly SegaPsg psg;
    private readonly bool gameGear;

    private readonly byte[] work = new byte[0x2000];

    private readonly GamepadButtons[] pads = new GamepadButtons[2];

    public Z80 Cpu { get; }

    /// <summary>Clocks since reset, which everything else here is counted in.</summary>
    public long Cycles { get; private set; }

    public MasterSystem(ReadOnlySpan<byte> rom, bool gameGear)
    {
        this.gameGear = gameGear;
        cartridge = new SegaCartridge(rom);
        vdp = new SegaVdp(gameGear);
        psg = new SegaPsg(gameGear);
        Cpu = new Z80(this);
        Reset();
    }

    public string SystemName => gameGear ? "Game Gear" : "Master System";
    public int Width => vdp.Width;
    public int Height => vdp.Height;
    public double FramesPerSecond => 3579545.0 / FrameCycles;
    public int SampleRate => SegaPsg.SampleRate;

    /// <summary>Two, and two ports on the front to prove it — which is the whole reason
    /// this console can be played with somebody else. A Game Gear has one.</summary>
    public int PlayerCount => gameGear ? 1 : 2;

    /// <summary>A Game Gear puts a channel in one ear or the other, so its samples come
    /// out in pairs. The Master System's do too, with the same sound in both.</summary>
    public int AudioChannels => 2;

    /// <summary>Sega numbered the two face buttons rather than lettering them, and put
    /// the other two on the console instead of the pad: Pause is a switch beside the
    /// cartridge slot, and Reset is a line the game reads and answers for itself. A
    /// Game Gear has neither — only a Start button by the screen.</summary>
    public ButtonLabels Buttons => gameGear
        ? new("1", "2", "Start", null)
        : new("1", "2", "Pause", "Reset");

    public uint[] Frame => vdp.Frame;
    public bool BatteryBacked => cartridge.Battery;
    public bool SaveDirty => cartridge.SaveDirty;

    public void Reset()
    {
        Array.Clear(work);
        cartridge.Reset();
        vdp.Reset();
        psg.Reset();
        Cpu.Reset();
        pads[0] = pads[1] = GamepadButtons.None;
        Cycles = 0;
    }

    public void SetButtons(int player, GamepadButtons pressed)
    {
        if (player < 0 || player >= PlayerCount)
            return;
        // A pad cannot be pushed two ways at once, and a game handed both is entitled
        // to do something strange with it.
        if ((pressed & (GamepadButtons.Up | GamepadButtons.Down))
            == (GamepadButtons.Up | GamepadButtons.Down))
            pressed &= ~GamepadButtons.Down;
        if ((pressed & (GamepadButtons.Left | GamepadButtons.Right))
            == (GamepadButtons.Left | GamepadButtons.Right))
            pressed &= ~GamepadButtons.Right;
        pads[player] = pressed;

        // Pause is not a button on the pad: on a Master System it is a switch on the
        // console wired to the processor's non-maskable line, which is why no game can
        // ignore it and why holding it does nothing.
        if (!gameGear && player == 0)
            Cpu.SetNmi(pressed.HasFlag(GamepadButtons.Start));
    }

    public void RunFrame()
    {
        vdp.FrameComplete = false;
        var budget = FrameCycles * 2;
        var spent = 0L;
        var started = Cycles;
        while (!vdp.FrameComplete && spent < budget)
        {
            Cpu.Step();
            spent = Cycles - started;
        }
    }

    public int ReadAudio(short[] destination) => psg.ReadAudio(destination);

    public void Tick(int cycles)
    {
        Cycles += cycles;
        vdp.Step(cycles);
        psg.Step(cycles);
        // The picture chip holds the line down until the program reads its status, so
        // this is a level to be sampled and not an event to be delivered.
        Cpu.IrqLine = vdp.IrqLine;
    }

    // ---- memory -----------------------------------------------------------------

    public byte Read(ushort address) => address switch
    {
        < 0xC000 => cartridge.Read(address),
        // Eight kilobytes of work memory, and the top eight are the same eight again.
        _ => work[address & 0x1FFF],
    };

    public void Write(ushort address, byte value)
    {
        if (address < 0xC000)
        {
            cartridge.Write(address, value);
            return;
        }
        work[address & 0x1FFF] = value;
        // The paging registers live inside work memory rather than beside it, so a
        // write to the top four bytes does both jobs.
        if (address >= 0xFFFC)
            cartridge.WriteControl(address, value);
    }

    // ---- ports ------------------------------------------------------------------

    /// <summary>Only the top two address bits pick a chip, which is why the same
    /// register answers at a dozen addresses and why games use whichever they like.</summary>
    public byte ReadPort(ushort port)
    {
        var low = (byte)port;
        if (gameGear && low <= 0x06)
            return ReadGameGearPort(low);

        return (low & 0xC0) switch
        {
            0x00 => 0xFF,
            0x40 => (low & 1) == 0 ? vdp.VerticalCounter : vdp.HorizontalCounter,
            0x80 => (low & 1) == 0 ? vdp.ReadData() : vdp.ReadStatus(),
            _ => (low & 1) == 0 ? ReadPadPortA() : ReadPadPortB(),
        };
    }

    public void WritePort(ushort port, byte value)
    {
        var low = (byte)port;
        if (gameGear && low <= 0x06)
        {
            if (low == 0x06)
                psg.WriteStereo(value);
            return;
        }

        switch (low & 0xC0)
        {
            case 0x00:
                // The two control ports here switch chips off the bus and change what
                // the pad lines mean. Nothing this player emulates behaves differently
                // for either, so the writes are accepted and go nowhere — and storing
                // a byte that is never read back would only be state pretending to
                // matter.
                return;
            case 0x40:
                psg.Write(value);
                return;
            case 0x80:
                if ((low & 1) == 0)
                    vdp.WriteData(value);
                else
                    vdp.WriteControl(value);
                return;
        }
    }

    /// <summary>The Game Gear's own corner of the port map: the start button, which is
    /// beside the screen rather than on the pad, and the region the console was sold
    /// in.</summary>
    private byte ReadGameGearPort(byte low) => low switch
    {
        0x00 => (byte)(0x40 | (pads[0].HasFlag(GamepadButtons.Start) ? 0x00 : 0x80)),
        _ => 0xFF,
    };

    /// <summary>A pressed button pulls its line down, so every bit here is inverted:
    /// ones are buttons nobody is touching.</summary>
    private byte ReadPadPortA()
    {
        var lines = 0xFF;
        if (pads[0].HasFlag(GamepadButtons.Up)) lines &= ~0x01;
        if (pads[0].HasFlag(GamepadButtons.Down)) lines &= ~0x02;
        if (pads[0].HasFlag(GamepadButtons.Left)) lines &= ~0x04;
        if (pads[0].HasFlag(GamepadButtons.Right)) lines &= ~0x08;
        if (pads[0].HasFlag(GamepadButtons.A)) lines &= ~0x10;
        if (pads[0].HasFlag(GamepadButtons.B)) lines &= ~0x20;
        if (pads[1].HasFlag(GamepadButtons.Up)) lines &= ~0x40;
        if (pads[1].HasFlag(GamepadButtons.Down)) lines &= ~0x80;
        return (byte)lines;
    }

    private byte ReadPadPortB()
    {
        var lines = 0xFF;
        if (pads[1].HasFlag(GamepadButtons.Left)) lines &= ~0x01;
        if (pads[1].HasFlag(GamepadButtons.Right)) lines &= ~0x02;
        if (pads[1].HasFlag(GamepadButtons.A)) lines &= ~0x04;
        if (pads[1].HasFlag(GamepadButtons.B)) lines &= ~0x08;
        // The reset button, which is a line on the pad port rather than a wire to the
        // processor: a game reads it and decides for itself what to do.
        if (pads[0].HasFlag(GamepadButtons.Select)) lines &= ~0x10;
        return (byte)lines;
    }

    // ---- state ------------------------------------------------------------------

    private static ReadOnlySpan<byte> StateTag => "SMS1"u8;

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
        vdp.Load(ref state);
        psg.Load(ref state);
        cartridge.Load(ref state);
        state.Read(work);
        pads[0] = (GamepadButtons)state.ReadByte();
        pads[1] = (GamepadButtons)state.ReadByte();
        Cycles = state.ReadInt64();

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
        vdp.Save(ref state);
        psg.Save(ref state);
        cartridge.Save(ref state);
        state.Write(work);
        state.Write((byte)pads[0]);
        state.Write((byte)pads[1]);
        state.Write(Cycles);
    }

    public byte[] SaveRam() => cartridge.SaveRam();

    public void LoadSaveRam(ReadOnlySpan<byte> data) => cartridge.LoadSaveRam(data);

    public void MarkSaved() => cartridge.MarkSaved();
}
