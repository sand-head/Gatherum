namespace Gatherum.Client.Emulation.Nes;

/// <summary>The 2C02 picture processor, one dot at a time.
///
/// Dot by dot rather than line by line because that is the granularity games work at:
/// the background is fetched in eight-dot groups into shift registers, and a game that
/// wants a status bar over a scrolling world rewrites the scroll position partway down
/// the screen and relies on the fetch already in flight finishing with the old one. A
/// renderer that drew whole lines from the registers as they stand at the end of the
/// line would put every such seam in the wrong place.
///
/// The two addresses the hardware calls v and t are the well-known "loopy" registers:
/// fifteen bits holding coarse X, coarse Y, the nametable pair and fine Y all at once,
/// which is why scrolling is a matter of incrementing an address rather than of
/// arithmetic on a pair of coordinates.</summary>
public sealed class NesPpu(NesConsole console)
{
    public const int Width = 256;
    public const int Height = 240;
    private const int PreRenderLine = 261;

    public readonly uint[] Frame = new uint[Width * Height];

    /// <summary>Four kilobytes because a four-screen cartridge brings its own second
    /// pair; the console itself only has two, and the mirroring decides which of them
    /// each nametable address lands in.</summary>
    private readonly byte[] vram = new byte[4 * 1024];
    private readonly byte[] palette = new byte[32];
    private readonly byte[] oam = new byte[256];

    private int scanline = PreRenderLine;
    private int cycle;
    private bool oddFrame;
    public bool FrameComplete;

    // $2000 and $2001, unpacked once rather than masked on every dot.
    private bool nmiEnabled, tallSprites, backgroundLeft, spritesLeft;
    private bool showBackground, showSprites, greyscale;
    private int backgroundTable, spriteTable, emphasis;

    /// <summary>How far $2007 walks after each access. Control resets to zero, which
    /// means one — a game that writes a palette before it writes control is relying on
    /// that, and gets every byte on top of the last one if this starts at zero.</summary>
    private int addressIncrement = 1;

    private bool vblank, spriteZeroHit, spriteOverflow;
    private byte openBus, readBuffer, oamAddress;

    private ushort v, t;
    private byte fineX;
    private bool writeLatch;

    private byte nametableByte, attributeByte, patternLow, patternHigh;
    private ushort patternShiftLow, patternShiftHigh;
    private ushort attributeShiftLow, attributeShiftHigh;

    private readonly int[] spriteX = new int[8];
    private readonly byte[] spriteAttributes = new byte[8];
    private readonly byte[] spritePatternLow = new byte[8];
    private readonly byte[] spritePatternHigh = new byte[8];
    private int spriteCount;
    private bool spriteZeroOnLine;

    public bool NmiLine => nmiEnabled && vblank;

    private bool Rendering => showBackground || showSprites;

    public void Reset()
    {
        scanline = PreRenderLine;
        cycle = 0;
        v = t = 0;
        writeLatch = false;
        vblank = spriteZeroHit = spriteOverflow = false;
        nmiEnabled = tallSprites = showBackground = showSprites = false;
        addressIncrement = 1;
        Array.Clear(Frame);
    }

    internal void Save(ref StateWriter state)
    {
        state.Write(vram);
        state.Write(palette);
        state.Write(oam);
        state.Write(scanline);
        state.Write(cycle);
        state.Write(oddFrame);
        state.Write(FrameComplete);

        state.Write(nmiEnabled);
        state.Write(tallSprites);
        state.Write(backgroundLeft);
        state.Write(spritesLeft);
        state.Write(showBackground);
        state.Write(showSprites);
        state.Write(greyscale);
        state.Write(backgroundTable);
        state.Write(spriteTable);
        state.Write(addressIncrement);
        state.Write(emphasis);

        state.Write(vblank);
        state.Write(spriteZeroHit);
        state.Write(spriteOverflow);
        state.Write(openBus);
        state.Write(readBuffer);
        state.Write(oamAddress);

        state.Write(v);
        state.Write(t);
        state.Write(fineX);
        state.Write(writeLatch);

        state.Write(nametableByte);
        state.Write(attributeByte);
        state.Write(patternLow);
        state.Write(patternHigh);
        state.Write(patternShiftLow);
        state.Write(patternShiftHigh);
        state.Write(attributeShiftLow);
        state.Write(attributeShiftHigh);

        state.Write(spriteX);
        state.Write(spriteAttributes);
        state.Write(spritePatternLow);
        state.Write(spritePatternHigh);
        state.Write(spriteCount);
        state.Write(spriteZeroOnLine);
    }

    internal void Load(ref StateReader state)
    {
        state.Read(vram);
        state.Read(palette);
        state.Read(oam);
        scanline = state.ReadInt32();
        cycle = state.ReadInt32();
        oddFrame = state.ReadBool();
        FrameComplete = state.ReadBool();

        nmiEnabled = state.ReadBool();
        tallSprites = state.ReadBool();
        backgroundLeft = state.ReadBool();
        spritesLeft = state.ReadBool();
        showBackground = state.ReadBool();
        showSprites = state.ReadBool();
        greyscale = state.ReadBool();
        backgroundTable = state.ReadInt32();
        spriteTable = state.ReadInt32();
        addressIncrement = state.ReadInt32();
        emphasis = state.ReadInt32();

        vblank = state.ReadBool();
        spriteZeroHit = state.ReadBool();
        spriteOverflow = state.ReadBool();
        openBus = state.ReadByte();
        readBuffer = state.ReadByte();
        oamAddress = state.ReadByte();

        v = state.ReadUInt16();
        t = state.ReadUInt16();
        fineX = state.ReadByte();
        writeLatch = state.ReadBool();

        nametableByte = state.ReadByte();
        attributeByte = state.ReadByte();
        patternLow = state.ReadByte();
        patternHigh = state.ReadByte();
        patternShiftLow = state.ReadUInt16();
        patternShiftHigh = state.ReadUInt16();
        attributeShiftLow = state.ReadUInt16();
        attributeShiftHigh = state.ReadUInt16();

        state.Read(spriteX);
        state.Read(spriteAttributes);
        state.Read(spritePatternLow);
        state.Read(spritePatternHigh);
        spriteCount = state.ReadInt32();
        spriteZeroOnLine = state.ReadBool();
    }

    // ---- the CPU's window on it ------------------------------------------------

    public byte ReadRegister(ushort address)
    {
        switch (address & 7)
        {
            case 2:
            {
                // The unused bits read back as whatever was last on the bus, which a
                // few games notice.
                var value = (byte)((openBus & 0x1F) | (vblank ? 0x80 : 0)
                    | (spriteZeroHit ? 0x40 : 0) | (spriteOverflow ? 0x20 : 0));
                vblank = false;
                writeLatch = false;
                openBus = value;
                return value;
            }
            case 4:
                openBus = oam[oamAddress];
                return openBus;
            case 7:
            {
                var address14 = (ushort)(v & 0x3FFF);
                // Everything below the palettes arrives one read late: the read
                // returns what the last one buffered. The palettes are on the chip
                // and answer at once.
                if (address14 >= 0x3F00)
                {
                    readBuffer = ReadVram((ushort)(address14 - 0x1000));
                    openBus = (byte)(palette[PaletteIndex(address14)] & (greyscale ? 0x30 : 0x3F));
                }
                else
                {
                    openBus = readBuffer;
                    readBuffer = ReadVram(address14);
                }
                v = (ushort)(v + addressIncrement);
                return openBus;
            }
            default:
                return openBus;
        }
    }

    public void WriteRegister(ushort address, byte value)
    {
        openBus = value;
        switch (address & 7)
        {
            case 0:
                t = (ushort)((t & 0xF3FF) | (value & 0x03) << 10);
                addressIncrement = (value & 0x04) != 0 ? 32 : 1;
                spriteTable = (value & 0x08) != 0 ? 0x1000 : 0;
                backgroundTable = (value & 0x10) != 0 ? 0x1000 : 0;
                tallSprites = (value & 0x20) != 0;
                nmiEnabled = (value & 0x80) != 0;
                break;
            case 1:
                greyscale = (value & 0x01) != 0;
                backgroundLeft = (value & 0x02) != 0;
                spritesLeft = (value & 0x04) != 0;
                showBackground = (value & 0x08) != 0;
                showSprites = (value & 0x10) != 0;
                emphasis = value >> 5 & 0x07;
                break;
            case 3:
                oamAddress = value;
                break;
            case 4:
                oam[oamAddress++] = value;
                break;
            case 5:
                if (!writeLatch)
                {
                    fineX = (byte)(value & 0x07);
                    t = (ushort)((t & 0xFFE0) | value >> 3);
                }
                else
                {
                    t = (ushort)((t & 0x8FFF) | (value & 0x07) << 12);
                    t = (ushort)((t & 0xFC1F) | (value & 0xF8) << 2);
                }
                writeLatch = !writeLatch;
                break;
            case 6:
                if (!writeLatch)
                    t = (ushort)((t & 0x00FF) | (value & 0x3F) << 8);
                else
                {
                    t = (ushort)((t & 0xFF00) | value);
                    v = t;
                }
                writeLatch = !writeLatch;
                break;
            case 7:
                WriteVram((ushort)(v & 0x3FFF), value);
                v = (ushort)(v + addressIncrement);
                break;
        }
    }

    /// <summary>The 256 bytes $4014 copies into sprite memory, starting wherever
    /// $2003 last left the pointer.</summary>
    public void WriteOam(byte value) => oam[oamAddress++] = value;

    // ---- video memory ----------------------------------------------------------

    private byte ReadVram(ushort address) => address switch
    {
        < 0x2000 => console.Cartridge.Mapper.PpuRead(address),
        < 0x3F00 => vram[NametableIndex(address)],
        _ => palette[PaletteIndex(address)],
    };

    private void WriteVram(ushort address, byte value)
    {
        if (address < 0x2000)
            console.Cartridge.Mapper.PpuWrite(address, value);
        else if (address < 0x3F00)
            vram[NametableIndex(address)] = value;
        else
            palette[PaletteIndex(address)] = value;
    }

    private int NametableIndex(ushort address)
    {
        var offset = address & 0x0FFF;
        return console.Cartridge.Mapper.Mirroring switch
        {
            Mirroring.Horizontal => (offset >> 11 & 1) * 0x400 + (offset & 0x3FF),
            Mirroring.Vertical => (offset >> 10 & 1) * 0x400 + (offset & 0x3FF),
            Mirroring.SingleScreenLow => offset & 0x3FF,
            Mirroring.SingleScreenHigh => 0x400 + (offset & 0x3FF),
            _ => offset,
        };
    }

    /// <summary>Each palette's first entry is the same universal backdrop colour, and
    /// the sprite palettes' copies of it are windows onto the background's.</summary>
    private static int PaletteIndex(ushort address)
    {
        var index = address & 0x1F;
        return (index & 0x13) == 0x10 ? index & 0x0F : index;
    }

    // ---- the pipeline ----------------------------------------------------------

    public void Step()
    {
        var visible = scanline < Height;
        var fetching = visible || scanline == PreRenderLine;

        if (fetching && Rendering)
        {
            if (cycle is >= 2 and <= 257 or >= 322 and <= 337)
                ShiftRegisters();

            if (cycle is >= 1 and <= 256 or >= 321 and <= 336)
            {
                switch ((cycle - 1) & 7)
                {
                    case 0:
                        LoadShiftRegisters();
                        nametableByte = ReadVram((ushort)(0x2000 | (v & 0x0FFF)));
                        break;
                    case 2:
                    {
                        var attribute = ReadVram((ushort)(0x23C0 | (v & 0x0C00)
                            | (v >> 4 & 0x38) | (v >> 2 & 0x07)));
                        if ((v >> 5 & 0x02) != 0)
                            attribute >>= 4;
                        if ((v & 0x02) != 0)
                            attribute >>= 2;
                        attributeByte = (byte)(attribute & 0x03);
                        break;
                    }
                    case 4:
                        patternLow = ReadVram(PatternAddress(0));
                        break;
                    case 6:
                        patternHigh = ReadVram(PatternAddress(8));
                        break;
                    case 7:
                        IncrementCoarseX();
                        break;
                }
            }

            if (cycle == 256)
                IncrementFineY();
            else if (cycle == 257)
            {
                LoadShiftRegisters();
                v = (ushort)((v & 0xFBE0) | (t & 0x041F));
            }
            else if (scanline == PreRenderLine && cycle is >= 280 and <= 304)
                v = (ushort)((v & 0x841F) | (t & 0x7BE0));
        }

        if (visible && cycle is >= 1 and <= Width)
            RenderPixel(cycle - 1);

        // Sprites for the line about to be drawn, chosen by comparing their Y against
        // the line being drawn now — which is why a sprite at Y appears at Y+1, and
        // why every game's sprite table is written a row high. The hardware spreads
        // this across dots 257-320; nothing on the program's side can tell, and doing
        // it in one place keeps the per-dot path short.
        if (cycle == 257 && Rendering && (visible || scanline == PreRenderLine))
            EvaluateSprites(scanline == PreRenderLine ? -1 : scanline);

        // Where a counting mapper's A12 line would have risen, once a line.
        if (cycle == 260 && Rendering && (visible || scanline == PreRenderLine))
            console.Cartridge.Mapper.SignalScanline();

        if (scanline == 241 && cycle == 1)
        {
            vblank = true;
            FrameComplete = true;
        }
        else if (scanline == PreRenderLine && cycle == 1)
        {
            vblank = false;
            spriteZeroHit = false;
            spriteOverflow = false;
        }

        cycle++;
        // An odd frame with the background on is one dot shorter, which is what keeps
        // the colour burst from settling into a fixed pattern on a real television.
        if (cycle > 340 || (scanline == PreRenderLine && cycle == 340 && oddFrame && showBackground))
        {
            cycle = 0;
            scanline++;
            if (scanline > PreRenderLine)
            {
                scanline = 0;
                oddFrame = !oddFrame;
            }
        }
    }

    private ushort PatternAddress(int plane) =>
        (ushort)(backgroundTable + nametableByte * 16 + (v >> 12 & 0x07) + plane);

    private void ShiftRegisters()
    {
        patternShiftLow <<= 1;
        patternShiftHigh <<= 1;
        attributeShiftLow <<= 1;
        attributeShiftHigh <<= 1;
    }

    private void LoadShiftRegisters()
    {
        patternShiftLow = (ushort)((patternShiftLow & 0xFF00) | patternLow);
        patternShiftHigh = (ushort)((patternShiftHigh & 0xFF00) | patternHigh);
        attributeShiftLow = (ushort)((attributeShiftLow & 0xFF00)
            | ((attributeByte & 1) != 0 ? 0xFF : 0x00));
        attributeShiftHigh = (ushort)((attributeShiftHigh & 0xFF00)
            | ((attributeByte & 2) != 0 ? 0xFF : 0x00));
    }

    private void IncrementCoarseX()
    {
        if ((v & 0x001F) == 31)
            v = (ushort)(v & ~0x001F ^ 0x0400);
        else
            v++;
    }

    private void IncrementFineY()
    {
        if ((v & 0x7000) != 0x7000)
        {
            v += 0x1000;
            return;
        }
        v = (ushort)(v & ~0x7000);
        var coarseY = v >> 5 & 0x1F;
        if (coarseY == 29)
        {
            // Rows 30 and 31 are the attribute bytes, not tiles; wrapping past 29
            // flips to the other nametable.
            coarseY = 0;
            v ^= 0x0800;
        }
        else
        {
            coarseY = (coarseY + 1) & 0x1F;
        }
        v = (ushort)(v & ~0x03E0 | coarseY << 5);
    }

    private void EvaluateSprites(int comparisonLine)
    {
        spriteCount = 0;
        spriteZeroOnLine = false;
        var height = tallSprites ? 16 : 8;
        for (var index = 0; index < 64; index++)
        {
            var top = oam[index * 4];
            var row = comparisonLine - top;
            if (row < 0 || row >= height)
                continue;
            if (spriteCount == 8)
            {
                spriteOverflow = true;
                break;
            }

            var tile = oam[index * 4 + 1];
            var attributes = oam[index * 4 + 2];
            if ((attributes & 0x80) != 0)
                row = height - 1 - row;

            ushort address;
            if (tallSprites)
            {
                // A tall sprite's tile number carries which pattern table it lives
                // in, and its bottom half is the tile after its top half.
                address = (ushort)((tile & 0x01) * 0x1000 + (tile & 0xFE) * 16
                    + (row >= 8 ? 16 : 0) + (row & 7));
            }
            else
            {
                address = (ushort)(spriteTable + tile * 16 + row);
            }

            var low = ReadVram(address);
            var high = ReadVram((ushort)(address + 8));
            if ((attributes & 0x40) != 0)
            {
                low = Reverse(low);
                high = Reverse(high);
            }

            spriteX[spriteCount] = oam[index * 4 + 3];
            spriteAttributes[spriteCount] = attributes;
            spritePatternLow[spriteCount] = low;
            spritePatternHigh[spriteCount] = high;
            if (index == 0)
                spriteZeroOnLine = true;
            spriteCount++;
        }
    }

    private static byte Reverse(byte value)
    {
        value = (byte)(value >> 4 | value << 4);
        value = (byte)((value & 0xCC) >> 2 | (value & 0x33) << 2);
        return (byte)((value & 0xAA) >> 1 | (value & 0x55) << 1);
    }

    private void RenderPixel(int x)
    {
        var backgroundPixel = 0;
        var backgroundPalette = 0;
        if (showBackground && (x >= 8 || backgroundLeft))
        {
            var bit = (ushort)(0x8000 >> fineX);
            backgroundPixel = ((patternShiftLow & bit) != 0 ? 1 : 0)
                | ((patternShiftHigh & bit) != 0 ? 2 : 0);
            if (backgroundPixel != 0)
                backgroundPalette = ((attributeShiftLow & bit) != 0 ? 1 : 0)
                    | ((attributeShiftHigh & bit) != 0 ? 2 : 0);
        }

        var spritePixel = 0;
        var spritePalette = 0;
        var spriteBehind = false;
        if (showSprites && (x >= 8 || spritesLeft))
        {
            for (var index = 0; index < spriteCount; index++)
            {
                var offset = x - spriteX[index];
                if (offset is < 0 or > 7)
                    continue;
                var bit = (byte)(0x80 >> offset);
                var pixel = ((spritePatternLow[index] & bit) != 0 ? 1 : 0)
                    | ((spritePatternHigh[index] & bit) != 0 ? 2 : 0);
                if (pixel == 0)
                    continue;

                // Sprite zero overlapping the background is how a game finds the
                // scanline it wants without a counter on the cartridge. It never
                // fires on the last dot of the line.
                if (index == 0 && spriteZeroOnLine && backgroundPixel != 0
                    && showBackground && x != 255)
                    spriteZeroHit = true;

                spritePixel = pixel;
                spritePalette = 4 + (spriteAttributes[index] & 0x03);
                spriteBehind = (spriteAttributes[index] & 0x20) != 0;
                break;
            }
        }

        int paletteIndex;
        if (spritePixel != 0 && (backgroundPixel == 0 || !spriteBehind))
            paletteIndex = spritePalette * 4 + spritePixel;
        else if (backgroundPixel != 0)
            paletteIndex = backgroundPalette * 4 + backgroundPixel;
        else
            paletteIndex = 0;

        var colour = palette[PaletteIndex((ushort)(0x3F00 + paletteIndex))]
            & (greyscale ? 0x30 : 0x3F);
        Frame[scanline * Width + x] = Emphasise(NesPalette.Colours[colour]);
    }

    /// <summary>The three emphasis bits dim the other channels rather than brightening
    /// their own, which is how a game fades the screen without touching its palette.</summary>
    private uint Emphasise(uint colour)
    {
        if (emphasis == 0)
            return colour;
        var red = colour >> 16 & 0xFF;
        var green = colour >> 8 & 0xFF;
        var blue = colour & 0xFF;
        if ((emphasis & 1) != 0) { green = green * 3 / 4; blue = blue * 3 / 4; }
        if ((emphasis & 2) != 0) { red = red * 3 / 4; blue = blue * 3 / 4; }
        if ((emphasis & 4) != 0) { red = red * 3 / 4; green = green * 3 / 4; }
        return 0xFF000000u | red << 16 | green << 8 | blue;
    }
}
