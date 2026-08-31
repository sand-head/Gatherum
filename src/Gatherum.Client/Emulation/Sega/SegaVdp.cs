namespace Gatherum.Client.Emulation.Sega;

/// <summary>The picture chip both machines share. The Game Gear's is the same silicon
/// behind a smaller window: it draws the identical 256×192 picture and shows the 160×144
/// in the middle of it, which is why a game written for one looks right on the other and
/// why the crop lives here rather than in the player.
///
/// A line is drawn in one go, at the moment the beam finishes it, because that is when
/// the registers that decide it have stopped moving. What a game changes in a line
/// interrupt lands on the line after — which is exactly what a split screen is.</summary>
public sealed class SegaVdp(bool gameGear)
{
    public const int ScreenWidth = 256;
    public const int ScreenHeight = 192;
    public const int GameGearWidth = 160;
    public const int GameGearHeight = 144;

    /// <summary>Where the Game Gear's window sits in the picture the chip draws.</summary>
    private const int GameGearLeft = (ScreenWidth - GameGearWidth) / 2;
    private const int GameGearTop = (ScreenHeight - GameGearHeight) / 2;

    /// <summary>228 processor cycles a line, 262 lines a frame: the console's 3.579545
    /// MHz divided out gives 59.92 frames a second, which is what the player paces to.</summary>
    public const int CyclesPerLine = 228;
    public const int LinesPerFrame = 262;
    private const int ActiveLines = 192;

    private readonly byte[] vram = new byte[16 * 1024];

    /// <summary>Colour memory: thirty-two six-bit entries on a Master System, or
    /// sixty-four bytes making thirty-two twelve-bit ones on a Game Gear.</summary>
    private readonly byte[] cram = new byte[64];

    private readonly byte[] registers = new byte[16];

    /// <summary>One line's worth of where the background is standing in front. Only a
    /// pixel that is both marked so and not colour zero counts: the chip treats the
    /// first colour as see-through whatever the priority bit says, which is how a
    /// sprite walks behind a railing and in front of the sky between its bars.</summary>
    private readonly bool[] backgroundInFront = new bool[ScreenWidth];
    private readonly bool[] spriteHere = new bool[ScreenWidth];

    private ushort address;
    private byte code;
    private byte readBuffer;
    private bool controlLatched;
    private byte controlLow;

    /// <summary>The Game Gear writes colour a nibble-pair at a time; the even byte
    /// waits here for the odd one that completes it.</summary>
    private byte colourLatch;

    private byte status;
    private int lineCounter;
    private bool lineInterruptPending;

    private int scanline;
    private int lineCycles;

    public uint[] Frame { get; } =
        new uint[gameGear ? GameGearWidth * GameGearHeight : ScreenWidth * ScreenHeight];

    public int Width => gameGear ? GameGearWidth : ScreenWidth;
    public int Height => gameGear ? GameGearHeight : ScreenHeight;

    public bool FrameComplete { get; set; }

    private bool DisplayEnabled => (registers[1] & 0x40) != 0;
    private bool FrameInterruptEnabled => (registers[1] & 0x20) != 0;
    private bool LineInterruptEnabled => (registers[0] & 0x10) != 0;

    public bool IrqLine =>
        (FrameInterruptEnabled && (status & 0x80) != 0) ||
        (LineInterruptEnabled && lineInterruptPending);

    public void Reset()
    {
        Array.Clear(vram);
        Array.Clear(cram);
        Array.Clear(registers);
        // The boot state a cartridge finds: mode 4 selected, display off, sprites at
        // the top of memory.
        registers[0] = 0x36;
        registers[1] = 0xA0;
        registers[2] = 0xFF;
        registers[3] = 0xFF;
        registers[4] = 0xFF;
        registers[5] = 0xFF;
        registers[6] = 0xFB;
        registers[10] = 0xFF;
        address = 0;
        code = 0;
        readBuffer = 0;
        controlLatched = false;
        controlLow = 0;
        colourLatch = 0;
        status = 0;
        lineCounter = 0xFF;
        lineInterruptPending = false;
        scanline = 0;
        lineCycles = 0;
        FrameComplete = false;
        Array.Clear(Frame);
    }

    public void Step(int cycles)
    {
        lineCycles += cycles;
        while (lineCycles >= CyclesPerLine)
        {
            lineCycles -= CyclesPerLine;
            FinishLine();
        }
    }

    private void FinishLine()
    {
        if (scanline < ActiveLines)
            RenderLine(scanline);

        // The line counter runs through the active display and one line past it; every
        // line above that reloads it, which is why a split set for line 0 works.
        if (scanline <= ActiveLines)
        {
            if (lineCounter-- == 0)
            {
                lineCounter = registers[10];
                lineInterruptPending = true;
            }
        }
        else
        {
            lineCounter = registers[10];
        }

        if (scanline == ActiveLines)
        {
            status |= 0x80;
            FrameComplete = true;
        }

        scanline++;
        if (scanline >= LinesPerFrame)
            scanline = 0;
    }

    // ---- ports ------------------------------------------------------------------

    /// <summary>The vertical counter stops counting straight partway down the frame and
    /// repeats a stretch of values, because the counter is eight bits and the frame is
    /// 262 lines. Games time things against the repeat, so it has to be here.</summary>
    public byte VerticalCounter => (byte)(scanline <= 0xDA ? scanline : scanline - 6);

    /// <summary>Where the beam is across the line, in the chip's own 342 dots.</summary>
    public byte HorizontalCounter => (byte)(lineCycles * 342 / CyclesPerLine >> 1);

    public byte ReadStatus()
    {
        var value = status;
        status = 0;
        lineInterruptPending = false;
        controlLatched = false;
        return value;
    }

    public byte ReadData()
    {
        controlLatched = false;
        // Reading is always one behind: the port hands back what the last read
        // fetched and starts fetching the next.
        var value = readBuffer;
        readBuffer = vram[address & 0x3FFF];
        address = (ushort)((address + 1) & 0x3FFF);
        return value;
    }

    public void WriteData(byte value)
    {
        controlLatched = false;
        if (code == 3)
            WriteColour(value);
        else
            vram[address & 0x3FFF] = value;
        readBuffer = value;
        address = (ushort)((address + 1) & 0x3FFF);
    }

    private void WriteColour(byte value)
    {
        if (!gameGear)
        {
            cram[address & 0x1F] = value;
            return;
        }
        // Twelve bits of colour need two bytes, and only the second one commits.
        if ((address & 1) == 0)
        {
            colourLatch = value;
            return;
        }
        cram[address & 0x3E] = colourLatch;
        cram[(address & 0x3E) + 1] = value;
    }

    public void WriteControl(byte value)
    {
        if (!controlLatched)
        {
            controlLatched = true;
            controlLow = value;
            // The low byte lands immediately, so a program can walk memory by writing
            // one byte per step.
            address = (ushort)(address & 0x3F00 | value);
            return;
        }

        controlLatched = false;
        code = (byte)(value >> 6);
        address = (ushort)((value & 0x3F) << 8 | controlLow);

        switch (code)
        {
            case 0:
                readBuffer = vram[address & 0x3FFF];
                address = (ushort)((address + 1) & 0x3FFF);
                return;
            case 2:
                WriteRegister(value & 0x0F, controlLow);
                return;
        }
    }

    private void WriteRegister(int index, byte value) => registers[index] = value;

    // ---- drawing ----------------------------------------------------------------

    private void RenderLine(int line)
    {
        Array.Clear(backgroundInFront);
        Array.Clear(spriteHere);

        var backdrop = Colour(16 + (registers[7] & 0x0F));

        if (!DisplayEnabled)
        {
            FillLine(line, backdrop);
            return;
        }

        Span<uint> pixels = stackalloc uint[ScreenWidth];
        DrawBackground(line, pixels);
        DrawSprites(line, pixels);

        // The leftmost column can be blanked so that a screen scrolling in from the
        // left has somewhere to hide the tiles arriving.
        if ((registers[0] & 0x20) != 0)
            for (var x = 0; x < 8; x++)
                pixels[x] = backdrop;

        WriteLine(line, pixels);
    }

    private void DrawBackground(int line, Span<uint> pixels)
    {
        var nameTable = (registers[2] & 0x0E) << 10;
        // The top two rows can be pinned against horizontal scrolling, which is how a
        // game keeps a score bar still while the world moves under it.
        var horizontal = (registers[0] & 0x40) != 0 && line < 16 ? 0 : registers[8];
        var lockRight = (registers[0] & 0x80) != 0;

        for (var x = 0; x < ScreenWidth; x++)
        {
            var vertical = lockRight && x >= 192 ? 0 : registers[9];
            var sourceX = x - horizontal & 0xFF;
            // Twenty-eight rows of tiles, not twenty-four: the name table is taller
            // than the screen and vertical scrolling wraps around all of it.
            var sourceY = (line + vertical) % 224;

            var entry = nameTable + ((sourceY >> 3) * 32 + (sourceX >> 3)) * 2;
            var low = vram[entry & 0x3FFF];
            var high = vram[entry + 1 & 0x3FFF];

            var pattern = low | (high & 0x01) << 8;
            var pixelX = (high & 0x02) != 0 ? 7 - (sourceX & 7) : sourceX & 7;
            var pixelY = (high & 0x04) != 0 ? 7 - (sourceY & 7) : sourceY & 7;
            var palette = (high & 0x08) != 0 ? 16 : 0;

            var index = PatternPixel(pattern * 32 + pixelY * 4, pixelX);
            pixels[x] = Colour(palette + index);
            backgroundInFront[x] = index != 0 && (high & 0x10) != 0;
        }
    }

    private void DrawSprites(int line, Span<uint> pixels)
    {
        var table = (registers[5] & 0x7E) << 7;
        var patternBase = (registers[6] & 0x04) << 11;
        var tall = (registers[1] & 0x02) != 0;
        var zoom = (registers[1] & 0x01) != 0 ? 2 : 1;
        var height = (tall ? 16 : 8) * zoom;
        var shift = (registers[0] & 0x08) != 0 ? 8 : 0;

        var drawn = 0;
        for (var sprite = 0; sprite < 64; sprite++)
        {
            var top = vram[table + sprite & 0x3FFF];
            // The list ends early on request, and $D0 is how a game says so.
            if (top == 0xD0)
                break;

            var y = top + 1;
            if (line < y || line >= y + height)
                continue;

            // Nine sprites on a line is one too many: the ninth is dropped and the
            // chip says so, which some games use to detect a crowded line.
            if (++drawn > 8)
            {
                status |= 0x40;
                break;
            }

            var entry = table + 0x80 + sprite * 2;
            var x = vram[entry & 0x3FFF] - shift;
            var pattern = vram[entry + 1 & 0x3FFF];
            if (tall)
                pattern &= 0xFE;

            var row = (line - y) / zoom;
            var source = patternBase + pattern * 32 + row * 4;

            for (var column = 0; column < 8; column++)
            {
                var index = PatternPixel(source, column);
                if (index == 0)
                    continue;
                for (var repeat = 0; repeat < zoom; repeat++)
                {
                    var screenX = x + column * zoom + repeat;
                    if (screenX < 0 || screenX >= ScreenWidth)
                        continue;
                    // Two sprites meeting is a fact the chip records, and a game reads
                    // it to decide whether something was hit. The earlier sprite keeps
                    // the pixel: on this chip the lowest-numbered one is in front, so a
                    // later sprite never paints over one already there — even where the
                    // earlier one was itself hidden behind the background.
                    if (spriteHere[screenX])
                    {
                        status |= 0x20;
                        continue;
                    }
                    spriteHere[screenX] = true;
                    if (backgroundInFront[screenX])
                        continue;
                    pixels[screenX] = Colour(16 + index);
                }
            }
        }
    }

    /// <summary>A tile is four bitplanes interleaved a row at a time, so one pixel is
    /// one bit taken from each of four consecutive bytes.</summary>
    private int PatternPixel(int source, int column)
    {
        var bit = 7 - column;
        var plane0 = vram[source & 0x3FFF] >> bit & 1;
        var plane1 = vram[source + 1 & 0x3FFF] >> bit & 1;
        var plane2 = vram[source + 2 & 0x3FFF] >> bit & 1;
        var plane3 = vram[source + 3 & 0x3FFF] >> bit & 1;
        return plane0 | plane1 << 1 | plane2 << 2 | plane3 << 3;
    }

    /// <summary>Six bits of colour on a Master System, twelve on a Game Gear — two bits
    /// or four per channel, spread back over the full eight the browser wants.</summary>
    private uint Colour(int index)
    {
        if (gameGear)
        {
            var low = cram[index * 2 & 0x3F];
            var high = cram[(index * 2 & 0x3E) + 1];
            var red = (low & 0x0F) * 17;
            var green = (low >> 4) * 17;
            var blue = (high & 0x0F) * 17;
            return 0xFF000000u | (uint)(red << 16) | (uint)(green << 8) | (uint)blue;
        }

        var value = cram[index & 0x1F];
        var r = (value & 0x03) * 85;
        var g = (value >> 2 & 0x03) * 85;
        var b = (value >> 4 & 0x03) * 85;
        return 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
    }

    private void FillLine(int line, uint fill)
    {
        if (gameGear)
        {
            if (line < GameGearTop || line >= GameGearTop + GameGearHeight)
                return;
            var start = (line - GameGearTop) * GameGearWidth;
            Frame.AsSpan(start, GameGearWidth).Fill(fill);
            return;
        }
        Frame.AsSpan(line * ScreenWidth, ScreenWidth).Fill(fill);
    }

    private void WriteLine(int line, ReadOnlySpan<uint> pixels)
    {
        if (gameGear)
        {
            if (line < GameGearTop || line >= GameGearTop + GameGearHeight)
                return;
            pixels.Slice(GameGearLeft, GameGearWidth)
                .CopyTo(Frame.AsSpan((line - GameGearTop) * GameGearWidth, GameGearWidth));
            return;
        }
        pixels.CopyTo(Frame.AsSpan(line * ScreenWidth, ScreenWidth));
    }

    // ---- state ------------------------------------------------------------------

    internal void Save(ref StateWriter state)
    {
        state.Write(vram);
        state.Write(cram);
        state.Write(registers);
        state.Write(address);
        state.Write(code);
        state.Write(readBuffer);
        state.Write(controlLatched);
        state.Write(controlLow);
        state.Write(colourLatch);
        state.Write(status);
        state.Write(lineCounter);
        state.Write(lineInterruptPending);
        state.Write(scanline);
        state.Write(lineCycles);
    }

    internal void Load(ref StateReader state)
    {
        state.Read(vram);
        state.Read(cram);
        state.Read(registers);
        address = state.ReadUInt16();
        code = state.ReadByte();
        readBuffer = state.ReadByte();
        controlLatched = state.ReadBool();
        controlLow = state.ReadByte();
        colourLatch = state.ReadByte();
        status = state.ReadByte();
        lineCounter = state.ReadInt32();
        lineInterruptPending = state.ReadBool();
        scanline = state.ReadInt32();
        lineCycles = state.ReadInt32();
    }
}
