namespace Gatherum.Client.Emulation.GameBoy;

/// <summary>The picture chip. A line is 456 dots long whatever is on it: the chip
/// spends the first eighty looking through sprite memory, then draws, then rests until
/// the line is up — and a game watches those mode changes to know when it is safe to
/// touch video memory.
///
/// The drawing itself happens in one go at the end of the drawing mode rather than dot
/// by dot. A Game Boy game changes the scroll registers between lines, not within one
/// (there is no sprite-zero trick here to make mid-line changes worth the cost), so a
/// line drawn from the registers as they stand when the line ends is the same picture
/// with a great deal less arithmetic.</summary>
public sealed class GameBoyPpu(GameBoyConsole console)
{
    public const int Width = 160;
    public const int Height = 144;

    private const int OamScanDots = 80;
    private const int DrawDots = 172;
    private const int LineDots = 456;

    public readonly uint[] Frame = new uint[Width * Height];
    public bool FrameComplete;

    /// <summary>Two banks on a Game Boy Color, one used on a Game Boy.</summary>
    private readonly byte[] vram = new byte[2 * 8 * 1024];
    private readonly byte[] oam = new byte[160];

    /// <summary>Sixty-four bytes each: eight palettes of four colours, each colour two
    /// bytes of five-bit red, green and blue.</summary>
    private readonly byte[] backgroundPalettes = new byte[64];
    private readonly byte[] spritePalettes = new byte[64];
    private byte backgroundPaletteIndex, spritePaletteIndex;

    /// <summary>Which colour each pixel of the line being drawn took from the
    /// background, so a sprite can tell whether it is allowed to cover it.</summary>
    private readonly byte[] backgroundIndices = new byte[Width];
    private readonly bool[] backgroundOverSprites = new bool[Width];

    private byte control = 0x91;
    private byte status;
    private byte scrollY, scrollX, compareLine, windowY, windowX;
    private byte monochromeBackground = 0xFC, monochromeSprite0 = 0xFF, monochromeSprite1 = 0xFF;
    private byte line;
    private int dots;
    private int windowLine;
    private bool statLine;

    public int VramBank { get; private set; }
    public byte Line => line;
    public bool Enabled => (control & 0x80) != 0;

    /// <summary>Whether the chip is between lines, which is the only window a colour
    /// cartridge's block copier may move in.</summary>
    public bool InHorizontalBlank => Enabled && Mode == 0;

    private int Mode => status & 0x03;

    /// <summary>The four shades a Game Boy actually showed: a green screen, not a grey
    /// one. A colour cartridge never sees these.</summary>
    private static readonly uint[] Shades =
        [0xFFE0F8D0, 0xFF88C070, 0xFF346856, 0xFF081820];

    public void Reset()
    {
        control = 0x91;
        status = 0x85;
        line = 0;
        dots = 0;
        windowLine = 0;
        Array.Fill(Frame, Shades[0]);
    }

    public byte ReadVram(ushort address) => vram[VramBank * 0x2000 + (address & 0x1FFF)];

    public void WriteVram(ushort address, byte value) =>
        vram[VramBank * 0x2000 + (address & 0x1FFF)] = value;

    public byte ReadOam(ushort address) => oam[address & 0xFF];

    public void WriteOam(ushort address, byte value) => oam[address & 0xFF] = value;

    public byte ReadRegister(ushort address) => address switch
    {
        0xFF40 => control,
        0xFF41 => (byte)(status | 0x80),
        0xFF42 => scrollY,
        0xFF43 => scrollX,
        0xFF44 => line,
        0xFF45 => compareLine,
        0xFF47 => monochromeBackground,
        0xFF48 => monochromeSprite0,
        0xFF49 => monochromeSprite1,
        0xFF4A => windowY,
        0xFF4B => windowX,
        0xFF4F => (byte)(VramBank | 0xFE),
        0xFF68 => backgroundPaletteIndex,
        0xFF69 => backgroundPalettes[backgroundPaletteIndex & 0x3F],
        0xFF6A => spritePaletteIndex,
        0xFF6B => spritePalettes[spritePaletteIndex & 0x3F],
        _ => 0xFF,
    };

    public void WriteRegister(ushort address, byte value)
    {
        switch (address)
        {
            case 0xFF40:
                var wasEnabled = Enabled;
                control = value;
                if (wasEnabled && !Enabled)
                {
                    // Switching the screen off resets the chip to the top of the
                    // frame and leaves the panel white, which games rely on to
                    // rewrite video memory in one go.
                    line = 0;
                    dots = 0;
                    windowLine = 0;
                    status = (byte)(status & 0xFC);
                    Array.Fill(Frame, console.Colour ? 0xFFFFFFFF : Shades[0]);
                }
                break;
            case 0xFF41:
                status = (byte)(value & 0x78 | status & 0x07);
                break;
            case 0xFF42: scrollY = value; break;
            case 0xFF43: scrollX = value; break;
            case 0xFF45: compareLine = value; break;
            case 0xFF47: monochromeBackground = value; break;
            case 0xFF48: monochromeSprite0 = value; break;
            case 0xFF49: monochromeSprite1 = value; break;
            case 0xFF4A: windowY = value; break;
            case 0xFF4B: windowX = value; break;
            case 0xFF4F: VramBank = value & 1; break;
            case 0xFF68: backgroundPaletteIndex = value; break;
            case 0xFF69:
                backgroundPalettes[backgroundPaletteIndex & 0x3F] = value;
                if ((backgroundPaletteIndex & 0x80) != 0)
                    backgroundPaletteIndex = (byte)(0x80 | (backgroundPaletteIndex + 1) & 0x3F);
                break;
            case 0xFF6A: spritePaletteIndex = value; break;
            case 0xFF6B:
                spritePalettes[spritePaletteIndex & 0x3F] = value;
                if ((spritePaletteIndex & 0x80) != 0)
                    spritePaletteIndex = (byte)(0x80 | (spritePaletteIndex + 1) & 0x3F);
                break;
        }
    }

    public void Step(int cycles)
    {
        if (!Enabled)
            return;

        for (var tick = 0; tick < cycles; tick++)
        {
            dots++;
            if (line < Height)
            {
                if (dots == OamScanDots)
                    SetMode(3);
                else if (dots == OamScanDots + DrawDots)
                {
                    RenderLine();
                    SetMode(0);
                }
            }

            if (dots < LineDots)
                continue;
            dots = 0;
            line++;

            if (line == Height)
            {
                SetMode(1);
                console.RequestInterrupt(GameBoyInterrupt.VBlank);
                FrameComplete = true;
            }
            else if (line > 153)
            {
                line = 0;
                windowLine = 0;
                SetMode(2);
            }
            else if (line < Height)
            {
                SetMode(2);
            }
            UpdateStatLine();
        }
    }

    private void SetMode(int mode)
    {
        status = (byte)(status & 0xFC | mode);
        UpdateStatLine();
    }

    /// <summary>Every source the status register can raise an interrupt from is wired
    /// to one line, so two of them going high together is still one interrupt — and a
    /// game that leaves two sources enabled gets the famous missed interrupt when one
    /// falls as the other rises.</summary>
    private void UpdateStatLine()
    {
        var coincidence = line == compareLine;
        status = (byte)(coincidence ? status | 0x04 : status & ~0x04);

        var raised = (coincidence && (status & 0x40) != 0)
            || (Mode == 0 && (status & 0x08) != 0)
            || (Mode == 1 && (status & 0x10) != 0)
            || (Mode == 2 && (status & 0x20) != 0);
        if (raised && !statLine)
            console.RequestInterrupt(GameBoyInterrupt.LcdStatus);
        statLine = raised;
    }

    private void RenderLine()
    {
        var row = line * Width;
        var backgroundEnabled = (control & 0x01) != 0;
        var windowEnabled = (control & 0x20) != 0 && windowY <= line;
        var drewWindow = false;

        for (var x = 0; x < Width; x++)
        {
            // On a Game Boy, clearing the first bit of control blanks the background;
            // on a Game Boy Color it only takes the background's priority away.
            if (!backgroundEnabled && !console.Colour)
            {
                backgroundIndices[x] = 0;
                backgroundOverSprites[x] = false;
                Frame[row + x] = Shades[0];
                continue;
            }

            var inWindow = windowEnabled && x >= windowX - 7;
            int mapX, mapY, mapBase;
            if (inWindow)
            {
                drewWindow = true;
                mapX = x - (windowX - 7);
                mapY = windowLine;
                mapBase = (control & 0x40) != 0 ? 0x1C00 : 0x1800;
            }
            else
            {
                mapX = (x + scrollX) & 0xFF;
                mapY = (line + scrollY) & 0xFF;
                mapBase = (control & 0x08) != 0 ? 0x1C00 : 0x1800;
            }

            var mapOffset = mapBase + (mapY >> 3) * 32 + (mapX >> 3);
            var tile = vram[mapOffset];
            var attributes = console.Colour ? vram[0x2000 + mapOffset] : (byte)0;

            var tileRow = mapY & 7;
            var tileColumn = mapX & 7;
            if ((attributes & 0x40) != 0)
                tileRow = 7 - tileRow;
            if ((attributes & 0x20) != 0)
                tileColumn = 7 - tileColumn;

            var dataBase = (control & 0x10) != 0
                ? tile * 16
                : 0x1000 + (sbyte)tile * 16;
            var bank = (attributes & 0x08) != 0 ? 0x2000 : 0;
            var low = vram[bank + dataBase + tileRow * 2];
            var high = vram[bank + dataBase + tileRow * 2 + 1];
            var bit = 7 - tileColumn;
            var index = (byte)((low >> bit & 1) | (high >> bit & 1) << 1);

            backgroundIndices[x] = index;
            backgroundOverSprites[x] = backgroundEnabled && (attributes & 0x80) != 0;
            Frame[row + x] = console.Colour
                ? ColourFrom(backgroundPalettes, attributes & 0x07, index)
                : Shades[monochromeBackground >> (index * 2) & 3];
        }

        if (drewWindow)
            windowLine++;
        if ((control & 0x02) != 0)
            RenderSprites(row);
    }

    private void RenderSprites(int row)
    {
        var height = (control & 0x04) != 0 ? 16 : 8;
        Span<int> chosen = stackalloc int[10];
        var count = 0;

        // Ten to a line, taken in table order — the eleventh sprite on a line simply
        // does not appear, which is why games shuffle their sprite table.
        for (var sprite = 0; sprite < 40 && count < 10; sprite++)
        {
            var top = oam[sprite * 4] - 16;
            if (line >= top && line < top + height)
                chosen[count++] = sprite;
        }

        // A Game Boy resolves two overlapping sprites by screen position and only then
        // by table order; a Game Boy Color drops the first half of that rule. Sorting
        // so the winner comes last means drawing over is all the priority needed.
        if (!console.Colour)
        {
            for (var i = 1; i < count; i++)
            {
                var sprite = chosen[i];
                var j = i - 1;
                while (j >= 0 && Precedes(chosen[j], sprite))
                {
                    chosen[j + 1] = chosen[j];
                    j--;
                }
                chosen[j + 1] = sprite;
            }
        }
        else
        {
            chosen[..count].Reverse();
        }

        for (var index = 0; index < count; index++)
            DrawSprite(row, chosen[index], height);
    }

    /// <summary>Whether the first sprite is drawn over the second: nearer the left
    /// edge wins, and earlier in the table breaks a tie.</summary>
    private bool Precedes(int first, int second) =>
        oam[first * 4 + 1] != oam[second * 4 + 1]
            ? oam[first * 4 + 1] < oam[second * 4 + 1]
            : first < second;

    private void DrawSprite(int row, int sprite, int height)
    {
        var top = oam[sprite * 4] - 16;
        var left = oam[sprite * 4 + 1] - 8;
        var tile = oam[sprite * 4 + 2];
        var attributes = oam[sprite * 4 + 3];

        var spriteRow = line - top;
        if ((attributes & 0x40) != 0)
            spriteRow = height - 1 - spriteRow;
        if (height == 16)
            tile &= 0xFE;

        var bank = console.Colour && (attributes & 0x08) != 0 ? 0x2000 : 0;
        var dataOffset = bank + tile * 16 + spriteRow * 2;
        var low = vram[dataOffset];
        var high = vram[dataOffset + 1];

        for (var pixel = 0; pixel < 8; pixel++)
        {
            var x = left + pixel;
            if (x is < 0 or >= Width)
                continue;
            var bit = (attributes & 0x20) != 0 ? pixel : 7 - pixel;
            var index = (byte)((low >> bit & 1) | (high >> bit & 1) << 1);
            if (index == 0)
                continue;
            // Behind-the-background is a per-sprite flag on both machines and a
            // per-tile one on a colour cartridge; either can hide this pixel, and only
            // over a background colour that is not the palette's first.
            if (backgroundIndices[x] != 0
                && ((attributes & 0x80) != 0 || backgroundOverSprites[x]))
                continue;

            Frame[row + x] = console.Colour
                ? ColourFrom(spritePalettes, attributes & 0x07, index)
                : Shades[((attributes & 0x10) != 0 ? monochromeSprite1 : monochromeSprite0)
                    >> (index * 2) & 3];
        }
    }

    /// <summary>A colour cartridge's palettes are five bits a channel, little-endian
    /// pairs. Scaling each to eight bits keeps the hue the artist chose; a real screen
    /// was dimmer and warmer than this, but correcting for a panel nobody is looking
    /// at is a matter of taste rather than accuracy.</summary>
    private static uint ColourFrom(byte[] palettes, int palette, int index)
    {
        var offset = palette * 8 + index * 2;
        var value = palettes[offset] | palettes[offset + 1] << 8;
        var red = (uint)(value & 0x1F) * 255 / 31;
        var green = (uint)(value >> 5 & 0x1F) * 255 / 31;
        var blue = (uint)(value >> 10 & 0x1F) * 255 / 31;
        return 0xFF000000u | red << 16 | green << 8 | blue;
    }
}
