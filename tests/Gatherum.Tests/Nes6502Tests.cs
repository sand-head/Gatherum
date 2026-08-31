using Gatherum.Client.Emulation;
using Gatherum.Client.Emulation.Nes;

namespace Gatherum.Tests;

/// <summary>The processor, one instruction at a time. Everything else in the player
/// stands on this: a flag set the wrong way is a game that branches into the weeds
/// twenty minutes in, which is not something a screenshot would ever show.</summary>
public class Nes6502Tests
{
    private static NesConsole Run(params byte[] program) =>
        new(RomFixtures.Nes(program));

    private static NesConsole Step(int instructions, params byte[] program)
    {
        var console = Run(program);
        for (var i = 0; i < instructions; i++)
            console.Cpu.Step();
        return console;
    }

    [Fact]
    public void The_reset_vector_is_where_it_starts()
    {
        Assert.Equal(0x8000, Run(0xEA).Cpu.PC);
    }

    [Fact]
    public void Loading_and_storing_moves_a_byte_through_memory()
    {
        // LDA #$42; STA $0300; LDX $0300
        var console = Step(3, 0xA9, 0x42, 0x8D, 0x00, 0x03, 0xAE, 0x00, 0x03);

        Assert.Equal(0x42, console.Cpu.A);
        Assert.Equal(0x42, console.Cpu.X);
        Assert.Equal(0x42, console.CpuRead(0x0300));
    }

    [Fact]
    public void Zero_and_negative_follow_what_was_loaded()
    {
        Assert.True(Step(1, 0xA9, 0x00).Cpu.Zero);
        Assert.True(Step(1, 0xA9, 0x80).Cpu.Negative);
        Assert.False(Step(1, 0xA9, 0x01).Cpu.Zero);
    }

    [Fact]
    public void Adding_carries_and_overflows_independently()
    {
        // CLC; LDA #$50; ADC #$50 — two positives making a negative is overflow.
        var console = Step(3, 0x18, 0xA9, 0x50, 0x69, 0x50);
        Assert.Equal(0xA0, console.Cpu.A);
        Assert.True(console.Cpu.Overflow);
        Assert.False(console.Cpu.Carry);

        // CLC; LDA #$FF; ADC #$02 — wrapping is carry, and no overflow at all.
        var wrapped = Step(3, 0x18, 0xA9, 0xFF, 0x69, 0x02);
        Assert.Equal(0x01, wrapped.Cpu.A);
        Assert.True(wrapped.Cpu.Carry);
        Assert.False(wrapped.Cpu.Overflow);
    }

    [Fact]
    public void Subtracting_borrows_through_the_carry_flag()
    {
        // SEC; LDA #$05; SBC #$03
        var console = Step(3, 0x38, 0xA9, 0x05, 0xE9, 0x03);
        Assert.Equal(0x02, console.Cpu.A);
        Assert.True(console.Cpu.Carry);

        // SEC; LDA #$03; SBC #$05 — going below zero clears it.
        var borrowed = Step(3, 0x38, 0xA9, 0x03, 0xE9, 0x05);
        Assert.Equal(0xFE, borrowed.Cpu.A);
        Assert.False(borrowed.Cpu.Carry);
    }

    [Fact]
    public void Comparing_sets_carry_when_the_register_is_the_larger()
    {
        var console = Step(2, 0xA9, 0x40, 0xC9, 0x30);
        Assert.True(console.Cpu.Carry);
        Assert.False(console.Cpu.Zero);

        var equal = Step(2, 0xA9, 0x30, 0xC9, 0x30);
        Assert.True(equal.Cpu.Carry);
        Assert.True(equal.Cpu.Zero);
    }

    [Fact]
    public void A_subroutine_returns_to_the_instruction_after_the_call()
    {
        // JSR $8010; LDA #$11 ... at $8010: LDA #$22; RTS
        var program = new byte[0x20];
        program[0] = 0x20; program[1] = 0x10; program[2] = 0x80;
        program[3] = 0xA9; program[4] = 0x11;
        program[0x10] = 0xA9; program[0x11] = 0x22;
        program[0x12] = 0x60;

        var console = Run(program);
        console.Cpu.Step();
        Assert.Equal(0x8010, console.Cpu.PC);
        console.Cpu.Step();
        console.Cpu.Step();
        Assert.Equal(0x8003, console.Cpu.PC);
        console.Cpu.Step();
        Assert.Equal(0x11, console.Cpu.A);
    }

    [Fact]
    public void The_stack_hands_bytes_back_in_the_order_it_took_them()
    {
        // LDA #$AA; PHA; LDA #$BB; PHA; PLA; TAX; PLA
        var console = Step(7, 0xA9, 0xAA, 0x48, 0xA9, 0xBB, 0x48, 0x68, 0xAA, 0x68);
        Assert.Equal(0xBB, console.Cpu.X);
        Assert.Equal(0xAA, console.Cpu.A);
    }

    [Fact]
    public void Zero_page_indexing_wraps_inside_the_first_page()
    {
        // LDA #$77; STA $10; LDX #$FF; LDA $11,X — $11 + $FF wraps to $10.
        var console = Step(4, 0xA9, 0x77, 0x85, 0x10, 0xA2, 0xFF, 0xB5, 0x11);
        Assert.Equal(0x77, console.Cpu.A);
    }

    [Fact]
    public void An_indirect_jump_reads_its_high_byte_from_the_start_of_the_page()
    {
        // The famous defect: a vector at $02FF takes its high byte from $0200.
        var console = Run(
            0xA9, 0x34, 0x8D, 0xFF, 0x02,   // LDA #$34; STA $02FF
            0xA9, 0x12, 0x8D, 0x00, 0x02,   // LDA #$12; STA $0200
            0xA9, 0xAB, 0x8D, 0x00, 0x03,   // LDA #$AB; STA $0300  (the "correct" byte)
            0x6C, 0xFF, 0x02);              // JMP ($02FF)
        for (var i = 0; i < 7; i++)
            console.Cpu.Step();

        Assert.Equal(0x1234, console.Cpu.PC);
    }

    [Fact]
    public void Rotating_moves_the_carry_through_the_accumulator()
    {
        // SEC; LDA #$80; ROL — the carry comes in at the bottom and out at the top.
        var console = Step(3, 0x38, 0xA9, 0x80, 0x2A);
        Assert.Equal(0x01, console.Cpu.A);
        Assert.True(console.Cpu.Carry);

        // SEC; LDA #$01; ROR
        var right = Step(3, 0x38, 0xA9, 0x01, 0x6A);
        Assert.Equal(0x80, right.Cpu.A);
        Assert.True(right.Cpu.Carry);
    }

    [Fact]
    public void Bit_tests_against_the_accumulator_without_changing_it()
    {
        // LDA #$C0; STA $20; LDA #$0F; BIT $20
        var console = Step(4, 0xA9, 0xC0, 0x85, 0x20, 0xA9, 0x0F, 0x24, 0x20);
        Assert.Equal(0x0F, console.Cpu.A);
        Assert.True(console.Cpu.Zero);
        Assert.True(console.Cpu.Negative);
        Assert.True(console.Cpu.Overflow);
    }

    [Fact]
    public void A_taken_branch_moves_and_an_untaken_one_falls_through()
    {
        // LDA #$00; BEQ +2; LDA #$FF (skipped); LDA #$01
        var console = Step(3, 0xA9, 0x00, 0xF0, 0x02, 0xA9, 0xFF, 0xA9, 0x01);
        Assert.Equal(0x01, console.Cpu.A);

        var fallen = Step(3, 0xA9, 0x01, 0xF0, 0x02, 0xA9, 0xFF, 0xA9, 0x01);
        Assert.Equal(0xFF, fallen.Cpu.A);
    }

    [Fact]
    public void Undocumented_lax_loads_both_registers_at_once()
    {
        // LDA #$5A; STA $30; LAX $30
        var console = Step(3, 0xA9, 0x5A, 0x85, 0x30, 0xA7, 0x30);
        Assert.Equal(0x5A, console.Cpu.A);
        Assert.Equal(0x5A, console.Cpu.X);
    }

    [Fact]
    public void Undocumented_sax_stores_both_registers_anded()
    {
        // LDA #$CC; LDX #$AA; SAX $31
        var console = Step(3, 0xA9, 0xCC, 0xA2, 0xAA, 0x87, 0x31);
        Assert.Equal(0x88, console.CpuRead(0x0031));
    }

    [Fact]
    public void An_interrupt_pushes_the_return_address_and_the_flags()
    {
        // The break flag is set on the copy BRK pushes and clear on the one an
        // interrupt pushes; software tells them apart by nothing else.
        var console = Run(0x00, 0xEA);
        console.Cpu.Step();

        Assert.Equal(0xFA, console.Cpu.S);
        Assert.True(console.Cpu.InterruptDisable);
        Assert.Equal(0x80, console.CpuRead(0x01FD));
        Assert.Equal(0x02, console.CpuRead(0x01FC));
        Assert.Equal(0x10, console.CpuRead(0x01FB) & 0x10);
    }

    [Fact]
    public void Instructions_cost_the_cycles_the_hardware_charged_for()
    {
        // Every memory access ticks the console, so a frame's worth of timing is only
        // as good as this. LDA immediate is two and LDA absolute is four.
        Assert.Equal(2, CyclesOfLast(1, 0xA9, 0x00));
        Assert.Equal(4, CyclesOfLast(1, 0xAD, 0x00, 0x03));

        // LDA absolute,X is four unless the index carries into the next page.
        Assert.Equal(4, CyclesOfLast(2, 0xA2, 0x01, 0xBD, 0x00, 0x03));
        Assert.Equal(5, CyclesOfLast(2, 0xA2, 0xFF, 0xBD, 0x01, 0x03));

        // A store pays the penalty either way, because it cannot un-write a guess.
        Assert.Equal(5, CyclesOfLast(2, 0xA2, 0x01, 0x9D, 0x00, 0x03));

        // A branch is two, three when taken, four when it leaves the page.
        Assert.Equal(2, CyclesOfLast(2, 0xA9, 0x01, 0xF0, 0x02));
        Assert.Equal(3, CyclesOfLast(2, 0xA9, 0x00, 0xF0, 0x02));

        // Read-modify-write writes the old value back before the new one.
        Assert.Equal(5, CyclesOfLast(1, 0xE6, 0x20));
        Assert.Equal(6, CyclesOfLast(1, 0xEE, 0x00, 0x03));
        Assert.Equal(7, CyclesOfLast(2, 0xA2, 0x00, 0xFE, 0x00, 0x03));

        // Subroutine and stack traffic.
        Assert.Equal(6, CyclesOfLast(1, 0x20, 0x00, 0x90));
        Assert.Equal(3, CyclesOfLast(1, 0x48));
        Assert.Equal(4, CyclesOfLast(1, 0x68));
        Assert.Equal(7, CyclesOfLast(1, 0x00));
    }

    /// <summary>Runs <paramref name="instructions"/> instructions and reports what the
    /// last of them cost.</summary>
    private static long CyclesOfLast(int instructions, params byte[] program)
    {
        var console = Run(program);
        for (var i = 0; i < instructions - 1; i++)
            console.Cpu.Step();
        var before = console.Cycles;
        console.Cpu.Step();
        return console.Cycles - before;
    }
}
