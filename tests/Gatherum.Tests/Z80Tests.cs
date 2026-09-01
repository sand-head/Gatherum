using Gatherum.Client.Emulation;
using Gatherum.Client.Emulation.Sega;

namespace Gatherum.Tests;

/// <summary>The Z80, run against hand-assembled programs. Each one ends in a HALT, so a
/// test is "run until the processor stops, then look at it" — and a program that never
/// halts fails on the step budget rather than hanging the suite.</summary>
public class Z80Tests
{
    private const byte Halt = 0x76;

    private static MasterSystem Run(params byte[] program)
    {
        var console = new MasterSystem(RomFixtures.Sega(program), gameGear: false);
        for (var step = 0; step < 200_000 && !console.Cpu.Halted; step++)
            console.Cpu.Step();
        Assert.True(console.Cpu.Halted, "the program never reached its HALT");
        return console;
    }

    private const byte Sign = 0x80;
    private const byte Zero = 0x40;
    private const byte Half = 0x10;
    private const byte Parity = 0x04;
    private const byte Subtract = 0x02;
    private const byte Carry = 0x01;

    [Fact]
    public void Eight_bit_arithmetic_adds_through_the_accumulator()
    {
        var console = Run(0x3E, 0x12, 0x06, 0x34, 0x80, Halt);
        Assert.Equal(0x46, console.Cpu.A);
    }

    [Fact]
    public void Adding_past_the_sign_bit_is_an_overflow_and_not_a_carry()
    {
        var console = Run(0x3E, 0x7F, 0xC6, 0x01, Halt);
        Assert.Equal(0x80, console.Cpu.A);
        Assert.Equal(Sign | Half | Parity, console.Cpu.F & (Sign | Zero | Half | Parity | Carry));
    }

    [Fact]
    public void Subtracting_below_zero_borrows()
    {
        var console = Run(0x3E, 0x00, 0xD6, 0x01, Halt);
        Assert.Equal(0xFF, console.Cpu.A);
        Assert.Equal(Sign | Half | Subtract | Carry,
            console.Cpu.F & (Sign | Zero | Half | Subtract | Carry));
    }

    [Fact]
    public void Decimal_adjust_turns_a_binary_sum_back_into_two_digits()
    {
        // 15 + 27 is 42 in the decimal a scoreboard keeps, and $3C in the binary the
        // adder produced.
        var console = Run(0x3E, 0x15, 0x06, 0x27, 0x80, 0x27, Halt);
        Assert.Equal(0x42, console.Cpu.A);
    }

    [Fact]
    public void Decimal_adjust_knows_a_subtraction_from_an_addition()
    {
        // 42 - 15 = 27, and only the N flag tells the adjustment which way to go.
        var console = Run(0x3E, 0x42, 0x06, 0x15, 0x90, 0x27, Halt);
        Assert.Equal(0x27, console.Cpu.A);
    }

    [Fact]
    public void Sixteen_bit_addition_lands_in_the_pair_it_names()
    {
        var console = Run(0x21, 0x34, 0x12, 0x11, 0x11, 0x11, 0x19, Halt);
        Assert.Equal(0x2345, console.Cpu.HL);
    }

    [Fact]
    public void Subtracting_with_carry_reaches_below_zero_in_sixteen_bits()
    {
        // SCF then SBC HL,DE: nought minus one minus the carry.
        var console = Run(0x21, 0x00, 0x00, 0x11, 0x01, 0x00, 0x37, 0xED, 0x52, Halt);
        Assert.Equal(0xFFFE, console.Cpu.HL);
        Assert.True((console.Cpu.F & Carry) != 0);
    }

    [Fact]
    public void A_block_move_copies_until_the_counter_runs_out()
    {
        var console = Run(
            0x21, 0x00, 0xC0,        // LD HL,$C000
            0x36, 0xAA,              // LD (HL),$AA
            0x23,                    // INC HL
            0x36, 0xBB,              // LD (HL),$BB
            0x21, 0x00, 0xC0,        // LD HL,$C000
            0x11, 0x10, 0xC0,        // LD DE,$C010
            0x01, 0x02, 0x00,        // LD BC,2
            0xED, 0xB0,              // LDIR
            Halt);
        Assert.Equal(0xAA, console.Read(0xC010));
        Assert.Equal(0xBB, console.Read(0xC011));
        Assert.Equal(0, console.Cpu.BC);
    }

    [Fact]
    public void A_block_compare_stops_on_the_byte_it_was_looking_for()
    {
        var console = Run(
            0x21, 0x00, 0xC0,        // LD HL,$C000
            0x36, 0x11, 0x23,        // LD (HL),$11 : INC HL
            0x36, 0x22, 0x23,        // LD (HL),$22 : INC HL
            0x36, 0x33,              // LD (HL),$33
            0x21, 0x00, 0xC0,        // LD HL,$C000
            0x01, 0x04, 0x00,        // LD BC,4
            0x3E, 0x22,              // LD A,$22
            0xED, 0xB1,              // CPIR
            Halt);
        // It stops having stepped past the match, so HL points at the byte after it.
        Assert.Equal(0xC002, console.Cpu.HL);
        Assert.True((console.Cpu.F & Zero) != 0);
    }

    [Fact]
    public void An_index_register_reaches_memory_through_a_displacement()
    {
        var console = Run(
            0xDD, 0x21, 0x00, 0xC0,  // LD IX,$C000
            0xDD, 0x36, 0x02, 0x99,  // LD (IX+2),$99
            0xDD, 0x7E, 0x02,        // LD A,(IX+2)
            Halt);
        Assert.Equal(0x99, console.Cpu.A);
        Assert.Equal(0x99, console.Read(0xC002));
    }

    [Fact]
    public void An_index_prefix_renames_the_halves_of_the_pair_it_replaces()
    {
        var console = Run(0xDD, 0x21, 0x34, 0x12, 0xDD, 0x7C, Halt);
        Assert.Equal(0x12, console.Cpu.A);
    }

    [Fact]
    public void Only_one_operand_is_ever_the_index_register()
    {
        // LD H,(IX+0) loads the *real* H — the substitution has already been spent on
        // the memory operand, which is the rule that makes the prefix decodable at all.
        var console = Run(
            0xDD, 0x21, 0x00, 0xC0,  // LD IX,$C000
            0xDD, 0x36, 0x00, 0x5A,  // LD (IX+0),$5A
            0xDD, 0x66, 0x00,        // LD H,(IX+0)
            Halt);
        Assert.Equal(0x5A, console.Cpu.H);
        Assert.Equal(0xC000, console.Cpu.IX);
    }

    [Fact]
    public void A_displaced_bit_instruction_writes_its_answer_to_a_register_as_well()
    {
        // SET 0,(IX+0) with the low three bits naming B: undocumented, and the reason
        // the displaced form cannot share the plain one's decoding.
        var console = Run(
            0xDD, 0x21, 0x00, 0xC0,  // LD IX,$C000
            0xDD, 0x36, 0x00, 0x00,  // LD (IX+0),0
            0xDD, 0xCB, 0x00, 0xC0,  // SET 0,(IX+0) -> B
            Halt);
        Assert.Equal(0x01, console.Read(0xC000));
        Assert.Equal(0x01, console.Cpu.B);
    }

    [Fact]
    public void Rotating_moves_the_top_bit_into_the_carry_and_back_round()
    {
        var console = Run(0x3E, 0x80, 0xCB, 0x07, Halt);
        Assert.Equal(0x01, console.Cpu.A);
        Assert.True((console.Cpu.F & Carry) != 0);
    }

    [Fact]
    public void Testing_a_bit_reports_only_through_the_zero_flag()
    {
        var set = Run(0x3E, 0x08, 0xCB, 0x5F, Halt);
        Assert.True((set.Cpu.F & Zero) == 0);
        var clear = Run(0x3E, 0x00, 0xCB, 0x5F, Halt);
        Assert.True((clear.Cpu.F & Zero) != 0);
    }

    [Fact]
    public void The_counted_loop_runs_its_body_the_number_of_times_it_says()
    {
        var console = Run(
            0x06, 0x05,              // LD B,5
            0xAF,                    // XOR A
            0x3C,                    // INC A
            0x10, 0xFD,              // DJNZ -3
            Halt);
        Assert.Equal(5, console.Cpu.A);
        Assert.Equal(0, console.Cpu.B);
    }

    [Fact]
    public void A_call_comes_back_to_the_instruction_after_it()
    {
        var console = Run(
            0x31, 0xF0, 0xDF,        // LD SP,$DFF0
            0xCD, 0x07, 0x00,        // CALL $0007
            Halt,                    // $0006
            0x3E, 0x77,              // $0007: LD A,$77
            0xC9);                   // RET
        Assert.Equal(0x77, console.Cpu.A);
        Assert.Equal(0xDFF0, console.Cpu.SP);
    }

    [Fact]
    public void The_shadow_registers_are_a_second_set_and_not_a_copy()
    {
        var console = Run(
            0x3E, 0x11, 0x08,        // LD A,$11 : EX AF,AF'
            0x3E, 0x22, 0x08,        // LD A,$22 : EX AF,AF'
            Halt);
        Assert.Equal(0x11, console.Cpu.A);
        Assert.Equal(0x22, console.Cpu.Aa);
    }

    [Fact]
    public void Exchanging_the_whole_bank_swaps_three_pairs_at_once()
    {
        var console = Run(
            0x01, 0x11, 0x11,        // LD BC,$1111
            0x11, 0x22, 0x22,        // LD DE,$2222
            0x21, 0x33, 0x33,        // LD HL,$3333
            0xD9,                    // EXX
            0x01, 0x44, 0x44,        // LD BC,$4444
            0xD9,                    // EXX
            Halt);
        Assert.Equal(0x1111, console.Cpu.BC);
        Assert.Equal(0x2222, console.Cpu.DE);
        Assert.Equal(0x3333, console.Cpu.HL);
    }

    [Fact]
    public void The_decimal_rotate_moves_one_nibble_between_memory_and_the_accumulator()
    {
        var console = Run(
            0x21, 0x00, 0xC0,        // LD HL,$C000
            0x36, 0x34,              // LD (HL),$34
            0x3E, 0x12,              // LD A,$12
            0xED, 0x6F,              // RLD
            Halt);
        // $12/$34 becomes $13/$42: the accumulator's low nibble goes in at the bottom
        // and memory's top nibble comes out.
        Assert.Equal(0x13, console.Cpu.A);
        Assert.Equal(0x42, console.Read(0xC000));
    }

    [Fact]
    public void Enabling_interrupts_does_not_take_hold_until_after_the_next_instruction()
    {
        // Otherwise the RET that every handler ends with could be interrupted before it
        // ran, and the stack would grow until nothing was left.
        //
        // The line has to be held down by the picture chip rather than poked at from
        // here: it is a level the console re-reads on every tick, so a value set by
        // hand would be gone again before the next instruction started.
        var console = new MasterSystem(RomFixtures.Sega([
            0x3E, 0x20, 0xD3, 0xBF,      // $0000: LD A,$20 : OUT ($BF),A
            0x3E, 0x81, 0xD3, 0xBF,      // $0004: LD A,$81 : OUT ($BF),A  -> frame ints on
            0xED, 0x56,                  // $0008: IM 1
            0xDB, 0x7E,                  // $000A: IN A,($7E)   -- the line counter
            0xFE, 0xC1,                  // $000C: CP 193
            0x20, 0xFA,                  // $000E: JR NZ,$000A  -- wait for the frame's end
            0xFB,                        // $0010: EI
            0x00,                        // $0011: NOP
            Halt,                        // $0012
        ]), gameGear: false);

        for (var step = 0; step < 200_000 && console.Cpu.PC != 0x0010; step++)
            console.Cpu.Step();
        Assert.Equal(0x0010, console.Cpu.PC);

        console.Cpu.Step();                       // EI
        Assert.Equal(0x0011, console.Cpu.PC);
        console.Cpu.Step();                       // the NOP runs regardless
        Assert.Equal(0x0012, console.Cpu.PC);
        console.Cpu.Step();                       // and only now is the interrupt taken
        Assert.Equal(0x38, console.Cpu.PC);
    }

    [Fact]
    public void A_halted_processor_starts_again_where_an_interrupt_leaves_it()
    {
        var console = new MasterSystem(RomFixtures.Sega([0xFB, 0x00, Halt]), gameGear: false);
        console.Cpu.InterruptMode = 1;
        console.Cpu.Step();
        console.Cpu.Step();
        console.Cpu.Step();
        Assert.True(console.Cpu.Halted);
        console.Cpu.IrqLine = true;
        console.Cpu.Step();
        Assert.False(console.Cpu.Halted);
        Assert.Equal(0x38, console.Cpu.PC);
    }

    [Fact]
    public void Pause_reaches_a_game_that_has_switched_interrupts_off()
    {
        // The pause switch is wired to the non-maskable line for exactly this reason.
        var console = new MasterSystem(RomFixtures.Sega([0xF3, 0x00, 0x00, Halt]), gameGear: false);
        console.Cpu.Step();                    // DI
        console.SetButtons(0, GamepadButtons.Start);
        console.Cpu.Step();
        Assert.Equal(0x66, console.Cpu.PC);
    }

    [Fact]
    public void Holding_pause_down_is_not_a_second_press()
    {
        var console = new MasterSystem(RomFixtures.Sega([0x00, 0x00, 0x00, Halt]), gameGear: false);
        console.SetButtons(0, GamepadButtons.Start);
        console.Cpu.Step();
        Assert.Equal(0x66, console.Cpu.PC);
        console.SetButtons(0, GamepadButtons.Start);
        var before = console.Cpu.PC;
        console.Cpu.Step();
        Assert.NotEqual(0x66, console.Cpu.PC);
        Assert.NotEqual(before, console.Cpu.PC);
    }

    [Fact]
    public void Parity_counts_the_bits_that_are_set()
    {
        // Three bits set is odd parity, so the flag is clear.
        var odd = Run(0x3E, 0x07, 0xB7, Halt);
        Assert.True((odd.Cpu.F & Parity) == 0);
        var even = Run(0x3E, 0x03, 0xB7, Halt);
        Assert.True((even.Cpu.F & Parity) != 0);
    }

    [Fact]
    public void Negation_subtracts_the_accumulator_from_nothing()
    {
        var console = Run(0x3E, 0x01, 0xED, 0x44, Halt);
        Assert.Equal(0xFF, console.Cpu.A);
        Assert.True((console.Cpu.F & Carry) != 0);
        Assert.True((console.Cpu.F & Subtract) != 0);
    }

    [Fact]
    public void The_stack_pointer_walks_down_and_back_up()
    {
        var console = Run(
            0x31, 0xF0, 0xDF,        // LD SP,$DFF0
            0x21, 0x34, 0x12,        // LD HL,$1234
            0xE5,                    // PUSH HL
            0x21, 0x00, 0x00,        // LD HL,0
            0xE1,                    // POP HL
            Halt);
        Assert.Equal(0x1234, console.Cpu.HL);
        Assert.Equal(0xDFF0, console.Cpu.SP);
    }

    [Fact]
    public void Exchanging_with_the_stack_top_swaps_both_ways()
    {
        var console = Run(
            0x31, 0xF0, 0xDF,        // LD SP,$DFF0
            0x21, 0x34, 0x12,        // LD HL,$1234
            0xE5,                    // PUSH HL
            0x21, 0x78, 0x56,        // LD HL,$5678
            0xE3,                    // EX (SP),HL
            0xE1,                    // POP HL
            Halt);
        Assert.Equal(0x5678, console.Cpu.HL);
    }
}
