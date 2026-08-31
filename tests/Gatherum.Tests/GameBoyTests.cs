using Gatherum.Client.Emulation;
using Gatherum.Client.Emulation.GameBoy;

namespace Gatherum.Tests;

/// <summary>The Game Boy core: its processor's flags and cycle costs, and a program
/// that actually paints something.</summary>
public class GameBoyTests
{
    private const uint Lightest = 0xFFE0F8D0;
    private const uint Darkest = 0xFF081820;

    private static GameBoyConsole Run(params byte[] program) =>
        new(RomFixtures.GameBoy(program));

    /// <summary>Runs the two instructions every cartridge starts with — the entry
    /// point at $100 is three bytes wide, so it is a nop and a jump past the header —
    /// and hands back a console sitting on the program's first instruction.</summary>
    private static GameBoyConsole Start(params byte[] program)
    {
        var console = Run(program);
        console.Cpu.Step();
        console.Cpu.Step();
        return console;
    }

    private static GameBoyConsole Step(int instructions, params byte[] program)
    {
        var console = Start(program);
        for (var i = 0; i < instructions; i++)
            console.Cpu.Step();
        return console;
    }

    [Fact]
    public void It_starts_where_the_boot_rom_would_have_left_it()
    {
        var console = Run(0x00);
        Assert.Equal(0x0100, console.Cpu.PC);
        Assert.Equal(0xFFFE, console.Cpu.SP);

        // The entry point jumps past the header to the program.
        console.Cpu.Step();
        console.Cpu.Step();
        Assert.Equal(0x0150, console.Cpu.PC);
    }

    [Fact]
    public void Adding_sets_the_half_carry_out_of_the_low_nibble()
    {
        // LD A,$0F; LD B,$01; ADD A,B
        var console = Step(3, 0x3E, 0x0F, 0x06, 0x01, 0x80);
        Assert.Equal(0x10, console.Cpu.A);
        Assert.Equal(0x20, console.Cpu.Flags & 0x20);
        Assert.Equal(0, console.Cpu.Flags & 0x10);

        // LD A,$FF; LD B,$01; ADD A,B — wrapping is a carry and a zero.
        var wrapped = Step(3, 0x3E, 0xFF, 0x06, 0x01, 0x80);
        Assert.Equal(0x00, wrapped.Cpu.A);
        Assert.Equal(0xB0, wrapped.Cpu.Flags);
    }

    [Fact]
    public void Subtracting_sets_the_subtract_flag_that_decimal_adjust_reads()
    {
        // LD A,$10; SUB $01
        var console = Step(2, 0x3E, 0x10, 0xD6, 0x01);
        Assert.Equal(0x0F, console.Cpu.A);
        Assert.Equal(0x60, console.Cpu.Flags & 0x60);
    }

    [Fact]
    public void Decimal_adjust_turns_a_binary_sum_back_into_digits()
    {
        // LD A,$09; ADD A,$01; DAA — nine plus one is sixteen, and ten in decimal.
        var console = Step(3, 0x3E, 0x09, 0xC6, 0x01, 0x27);
        Assert.Equal(0x10, console.Cpu.A);

        // LD A,$19; ADD A,$28; DAA
        var carried = Step(3, 0x3E, 0x19, 0xC6, 0x28, 0x27);
        Assert.Equal(0x47, carried.Cpu.A);
    }

    [Fact]
    public void Sixteen_bit_registers_are_two_eight_bit_ones()
    {
        // LD HL,$1234; INC HL; LD A,H; LD B,L
        var console = Step(4, 0x21, 0x34, 0x12, 0x23, 0x7C, 0x45);
        Assert.Equal(0x1235, console.Cpu.HL);
        Assert.Equal(0x12, console.Cpu.A);
        Assert.Equal(0x35, console.Cpu.B);
    }

    [Fact]
    public void The_stack_survives_a_round_trip_through_a_call()
    {
        // CALL $0160; ... at $0160: LD A,$42; RET
        var program = new byte[0x30];
        program[0] = 0xCD; program[1] = 0x60; program[2] = 0x01;
        program[3] = 0x06; program[4] = 0x99;
        program[0x10] = 0x3E; program[0x11] = 0x42;
        program[0x12] = 0xC9;

        var console = Start(program);
        console.Cpu.Step();
        Assert.Equal(0x0160, console.Cpu.PC);
        console.Cpu.Step();
        console.Cpu.Step();
        Assert.Equal(0x0153, console.Cpu.PC);
        Assert.Equal(0xFFFE, console.Cpu.SP);
        Assert.Equal(0x42, console.Cpu.A);
    }

    [Fact]
    public void The_bit_instructions_test_set_and_clear_without_touching_the_rest()
    {
        // LD A,$08; BIT 3,A
        var set = Step(2, 0x3E, 0x08, 0xCB, 0x5F);
        Assert.Equal(0, set.Cpu.Flags & 0x80);

        // LD A,$00; BIT 3,A — an unset bit is what sets the zero flag.
        var clear = Step(2, 0x3E, 0x00, 0xCB, 0x5F);
        Assert.Equal(0x80, clear.Cpu.Flags & 0x80);

        // LD A,$00; SET 7,A; RES 7,A; SET 0,A
        var written = Step(4, 0x3E, 0x00, 0xCB, 0xFF, 0xCB, 0xBF, 0xCB, 0xC7);
        Assert.Equal(0x01, written.Cpu.A);
    }

    [Fact]
    public void Swapping_exchanges_the_nibbles()
    {
        var console = Step(2, 0x3E, 0xAB, 0xCB, 0x37);
        Assert.Equal(0xBA, console.Cpu.A);
    }

    [Fact]
    public void Instructions_cost_the_clocks_the_hardware_charged_for()
    {
        Assert.Equal(4, CyclesOfLast(1, 0x00));                 // NOP
        Assert.Equal(8, CyclesOfLast(1, 0x3E, 0x00));           // LD A,d8
        Assert.Equal(12, CyclesOfLast(1, 0x01, 0x00, 0x00));    // LD BC,d16
        Assert.Equal(8, CyclesOfLast(2, 0x21, 0x00, 0xC0, 0x7E));   // LD A,(HL)
        Assert.Equal(8, CyclesOfLast(2, 0x21, 0x00, 0xC0, 0x77));   // LD (HL),A
        Assert.Equal(16, CyclesOfLast(1, 0xC3, 0x60, 0x01));    // JP a16
        Assert.Equal(12, CyclesOfLast(1, 0x18, 0x00));          // JR e8, taken
        Assert.Equal(16, CyclesOfLast(1, 0xC5));                // PUSH BC
        Assert.Equal(12, CyclesOfLast(1, 0xC1));                // POP BC
        Assert.Equal(24, CyclesOfLast(1, 0xCD, 0x60, 0x01));    // CALL a16
        Assert.Equal(16, CyclesOfLast(1, 0xC7));                // RST $00
        Assert.Equal(16, CyclesOfLast(2, 0x21, 0x00, 0xC0, 0xCB, 0x36));  // SWAP (HL)
        Assert.Equal(12, CyclesOfLast(2, 0x21, 0x00, 0xC0, 0xCB, 0x46));  // BIT 0,(HL)
    }

    [Fact]
    public void A_program_that_writes_a_tile_puts_it_on_the_screen()
    {
        var console = Run(DrawOneTile());
        for (var frame = 0; frame < 3; frame++)
            console.RunFrame();

        Assert.Equal(Darkest, console.Frame[0]);
        Assert.Equal(Darkest, console.Frame[7]);
        Assert.Equal(Darkest, console.Frame[7 * GameBoyPpu.Width + 7]);
        Assert.Equal(Lightest, console.Frame[8]);
        Assert.Equal(Lightest, console.Frame[8 * GameBoyPpu.Width]);
    }

    [Fact]
    public void Scrolling_moves_the_whole_background()
    {
        var program = DrawOneTile(scrollX: 3);
        var console = Run(program);
        for (var frame = 0; frame < 3; frame++)
            console.RunFrame();

        // Three pixels to the left, so the tile now ends at pixel four and the pixel
        // beyond it is background.
        Assert.Equal(Darkest, console.Frame[4]);
        Assert.Equal(Lightest, console.Frame[5]);
    }

    /// <summary>Fills tile one with the darkest colour, puts it at the top left of the
    /// map, sets the palette and switches the screen on.</summary>
    private static byte[] DrawOneTile(byte scrollX = 0)
    {
        var program = new List<byte>
        {
            0x21, 0x10, 0x80,       // LD HL,$8010 — tile one's sixteen bytes
            0x0E, 0x10,             // LD C,$10
            0x3E, 0xFF,             // LD A,$FF
            0x22,                   // LD (HL+),A
            0x0D,                   // DEC C
            0x20, 0xFC,             // JR NZ,-4
            0x21, 0x00, 0x98,       // LD HL,$9800 — the first entry of the map
            0x3E, 0x01,             // LD A,$01
            0x77,                   // LD (HL),A
            0x3E, 0xE4,             // LD A,$E4 — the identity palette
            0xE0, 0x47,             // LDH ($47),A
            0xAF,                   // XOR A
            0xE0, 0x42,             // LDH ($42),A — scroll Y
            0x3E, scrollX,          // LD A,scrollX
            0xE0, 0x43,             // LDH ($43),A — scroll X
            0x3E, 0x91,             // LD A,$91 — screen on, background on
            0xE0, 0x40,             // LDH ($40),A
            0x18, 0xFE,             // JR -2
        };
        return [.. program];
    }

    private static long CyclesOfLast(int instructions, params byte[] program)
    {
        var console = Start(program);
        for (var i = 0; i < instructions - 1; i++)
            console.Cpu.Step();
        var before = console.Cycles;
        console.Cpu.Step();
        return console.Cycles - before;
    }

    [Fact]
    public void Something_that_is_not_a_cartridge_is_refused()
    {
        var failure = Assert.Throws<NotSupportedException>(
            () => Emulator.Load(new byte[1024], "mystery.bin"));
        Assert.Contains("cartridge image", failure.Message);
    }

    [Fact]
    public void The_bytes_pick_the_console_before_the_name_does()
    {
        Assert.Equal("Game Boy",
            Emulator.Load(RomFixtures.GameBoy([0x00]), "misnamed.nes").SystemName);
        Assert.Equal("Nintendo Entertainment System",
            Emulator.Load(RomFixtures.Nes([0xEA]), "misnamed.gb").SystemName);
    }
}
