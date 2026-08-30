using Gatherum.Client.Emulation;
using Gatherum.Client.Emulation.Nes;

namespace Gatherum.Tests;

/// <summary>The whole console, driven by a program that puts something on screen.
/// A processor that passes its own tests can still produce a black picture: what these
/// check is that the picture chip's address registers, pattern fetches and palettes
/// line up into pixels where the program asked for them.</summary>
public class NesConsoleTests
{
    private const uint White = 0xFFFFFEFF;
    private const uint Black = 0xFF000000;

    /// <summary>Pattern data: tile 0 blank, tiles 1 and 2 solid colour one.</summary>
    private static byte[] Patterns()
    {
        var characters = new byte[8 * 1024];
        for (var row = 0; row < 8; row++)
        {
            characters[16 + row] = 0xFF;
            characters[32 + row] = 0xFF;
        }
        return characters;
    }

    private static byte[] WriteVram(ushort address, params byte[] values)
    {
        var program = new List<byte>
        {
            0xA9, (byte)(address >> 8), 0x8D, 0x06, 0x20,
            0xA9, (byte)address, 0x8D, 0x06, 0x20,
        };
        foreach (var value in values)
        {
            program.AddRange([0xA9, value]);
            program.AddRange([0x8D, 0x07, 0x20]);
        }
        return [.. program];
    }

    /// <summary>Palettes, one background tile, rendering on, then a spin.</summary>
    private static NesConsole DrawOneTile()
    {
        var program = new List<byte>();
        program.AddRange(WriteVram(0x3F00, 0x0F, 0x30, 0x0F, 0x21));
        program.AddRange(WriteVram(0x2000, 0x01));
        program.AddRange([0xA9, 0x00, 0x8D, 0x00, 0x20]);   // PPUCTRL: no NMI
        // Setting the address register also sets the scroll — they are one pair of
        // registers on the chip — so the scroll goes back to nothing before rendering.
        program.AddRange([0xA9, 0x00, 0x8D, 0x05, 0x20, 0x8D, 0x05, 0x20]);
        program.AddRange([0xA9, 0x0A, 0x8D, 0x01, 0x20]);   // PPUMASK: background on
        program.AddRange([0x4C, 0x00, 0x00]);               // placeholder for the spin

        var spin = (ushort)(0x8000 + program.Count - 3);
        program[^2] = (byte)spin;
        program[^1] = (byte)(spin >> 8);

        var console = new NesConsole(RomFixtures.Nes([.. program], Patterns()));
        for (var frame = 0; frame < 3; frame++)
            console.RunFrame();
        return console;
    }

    [Fact]
    public void A_background_tile_lands_where_the_nametable_put_it()
    {
        var console = DrawOneTile();

        // The one tile written is eight pixels square in the top-left corner.
        Assert.Equal(White, console.Frame[0]);
        Assert.Equal(White, console.Frame[7]);
        Assert.Equal(White, console.Frame[7 * NesPpu.Width + 7]);

        // Its neighbours are tile zero, which is transparent, so the backdrop shows.
        Assert.Equal(Black, console.Frame[8]);
        Assert.Equal(Black, console.Frame[8 * NesPpu.Width]);
        Assert.Equal(Black, console.Frame[^1]);
    }

    [Fact]
    public void Scrolling_moves_the_picture_by_a_pixel_at_a_time()
    {
        var program = new List<byte>();
        program.AddRange(WriteVram(0x3F00, 0x0F, 0x30));
        program.AddRange(WriteVram(0x2000, 0x01));
        program.AddRange([0xA9, 0x00, 0x8D, 0x00, 0x20]);
        // Three pixels of fine horizontal scroll, then vertical zero.
        program.AddRange([0xA9, 0x03, 0x8D, 0x05, 0x20]);
        program.AddRange([0xA9, 0x00, 0x8D, 0x05, 0x20]);
        program.AddRange([0xA9, 0x0A, 0x8D, 0x01, 0x20]);
        program.AddRange([0x4C, 0x00, 0x00]);
        var spin = (ushort)(0x8000 + program.Count - 3);
        program[^2] = (byte)spin;
        program[^1] = (byte)(spin >> 8);

        var console = new NesConsole(RomFixtures.Nes([.. program], Patterns()));
        for (var frame = 0; frame < 3; frame++)
            console.RunFrame();

        // The tile has slid three pixels to the left, so it now ends at pixel four.
        Assert.Equal(White, console.Frame[4]);
        Assert.Equal(Black, console.Frame[5]);
    }

    [Fact]
    public void A_sprite_draws_over_the_backdrop_and_reports_hitting_the_background()
    {
        var program = new List<byte>();
        program.AddRange(WriteVram(0x3F00, 0x0F, 0x30));
        program.AddRange(WriteVram(0x3F11, 0x21));
        program.AddRange(WriteVram(0x2000, 0x01));
        // Sprite zero, on top of the background tile so the hit flag has something to
        // notice: Y = 0, tile 2, attributes 0, X = 0.
        program.AddRange([0xA9, 0x00, 0x8D, 0x03, 0x20]);
        program.AddRange([0xA9, 0x00, 0x8D, 0x04, 0x20]);
        program.AddRange([0xA9, 0x02, 0x8D, 0x04, 0x20]);
        program.AddRange([0xA9, 0x00, 0x8D, 0x04, 0x20]);
        program.AddRange([0xA9, 0x00, 0x8D, 0x04, 0x20]);
        program.AddRange([0xA9, 0x00, 0x8D, 0x00, 0x20]);
        program.AddRange([0xA9, 0x00, 0x8D, 0x05, 0x20, 0x8D, 0x05, 0x20]);
        program.AddRange([0xA9, 0x1E, 0x8D, 0x01, 0x20]);   // background and sprites
        // Read the status register into $0000 forever, so the test can see the flag.
        var loop = (ushort)(0x8000 + program.Count);
        program.AddRange([0xAD, 0x02, 0x20, 0x05, 0x00, 0x85, 0x00]);
        program.AddRange([0x4C, (byte)loop, (byte)(loop >> 8)]);

        var console = new NesConsole(RomFixtures.Nes([.. program], Patterns()));
        for (var frame = 0; frame < 3; frame++)
            console.RunFrame();

        // A sprite is drawn one line below its Y, so row zero is background and row
        // one is the sprite's own colour.
        Assert.Equal(White, console.Frame[0]);
        Assert.Equal(0xFF64B0FF, console.Frame[NesPpu.Width]);
        Assert.Equal(0x40, console.CpuRead(0x0000) & 0x40);
    }

    [Fact]
    public void The_pad_hands_its_buttons_back_one_bit_at_a_time()
    {
        // Strobe the port, then read it eight times into $0300 onwards.
        var program = new List<byte>
        {
            0xA9, 0x01, 0x8D, 0x16, 0x40,
            0xA9, 0x00, 0x8D, 0x16, 0x40,
            0xA2, 0x00,
        };
        var loop = (ushort)(0x8000 + program.Count);
        program.AddRange([
            0xAD, 0x16, 0x40,           // LDA $4016
            0x29, 0x01,                 // AND #$01
            0x9D, 0x00, 0x03,           // STA $0300,X
            0xE8,                       // INX
            0xE0, 0x08,                 // CPX #$08
            0xD0, (byte)(0x100 - 13),   // BNE loop
        ]);
        var spin = (ushort)(0x8000 + program.Count);
        program.AddRange([0x4C, (byte)spin, (byte)(spin >> 8)]);

        var console = new NesConsole(RomFixtures.Nes([.. program]));
        console.SetButtons(0, GamepadButtons.A | GamepadButtons.Start | GamepadButtons.Left);
        for (var step = 0; step < 200; step++)
            console.Cpu.Step();

        // A, B, Select, Start, Up, Down, Left, Right — in that order, one per read.
        Assert.Equal([1, 0, 0, 1, 0, 0, 1, 0],
            Enumerable.Range(0, 8).Select(i => console.CpuRead((ushort)(0x0300 + i))));
    }

    [Fact]
    public void A_cartridge_the_player_cannot_be_says_so()
    {
        var image = RomFixtures.Nes([0xEA]);
        image[6] = 0x50;    // mapper 5, which is not implemented

        var failure = Assert.Throws<NotSupportedException>(() => new NesConsole(image));
        Assert.Contains("mapper 5", failure.Message);
    }

    [Fact]
    public void Something_that_is_not_a_cartridge_is_refused()
    {
        Assert.Throws<NotSupportedException>(() => new NesConsole(new byte[64]));
    }

    [Fact]
    public void A_frame_is_the_right_number_of_cycles_long()
    {
        // 341 dots by 262 lines at three dots a cycle, less the one the odd frame
        // skips — which is what makes the console 60.0988 frames a second and not 60.
        var console = new NesConsole(RomFixtures.Nes([0x4C, 0x00, 0x80]));
        console.RunFrame();
        var start = console.Cycles;
        console.RunFrame();

        Assert.InRange(console.Cycles - start, 29770, 29790);
    }
}
