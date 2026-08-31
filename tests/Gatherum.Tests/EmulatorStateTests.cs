using Gatherum.Client.Emulation;
using Gatherum.Client.Emulation.Nes;

namespace Gatherum.Tests;

/// <summary>The contract two people playing the same game over a network stand on: a
/// core is deterministic, and it can say exactly where it is. If either fails, netplay
/// does not desync loudly — it desyncs quietly, twenty minutes in, and both players
/// think the other is cheating.</summary>
public class EmulatorStateTests
{
    /// <summary>A cartridge that reads both pads every frame, keeps a counter, and
    /// draws — so its state depends on input, on time, and on the picture chip.</summary>
    private static byte[] Responsive()
    {
        var program = new List<byte>();
        void Write(ushort address, params byte[] values)
        {
            program.AddRange([0xA9, (byte)(address >> 8), 0x8D, 0x06, 0x20]);
            program.AddRange([0xA9, (byte)address, 0x8D, 0x06, 0x20]);
            foreach (var value in values)
                program.AddRange([0xA9, value, 0x8D, 0x07, 0x20]);
        }

        Write(0x3F00, 0x0F, 0x30);
        Write(0x2000, 0x01);
        program.AddRange([0xA9, 0x00, 0x8D, 0x00, 0x20]);
        program.AddRange([0xA9, 0x00, 0x8D, 0x05, 0x20, 0x8D, 0x05, 0x20]);
        program.AddRange([0xA9, 0x0A, 0x8D, 0x01, 0x20]);

        var loop = (ushort)(0x8000 + program.Count);
        program.AddRange([
            0xA9, 0x01, 0x8D, 0x16, 0x40,       // strobe the pads
            0xA9, 0x00, 0x8D, 0x16, 0x40,
            0xAD, 0x16, 0x40, 0x8D, 0x00, 0x03, // pad one's first bit  -> $0300
            0xAD, 0x17, 0x40, 0x8D, 0x01, 0x03, // pad two's first bit  -> $0301
            0xEE, 0x02, 0x03,                   // and a counter        -> $0302
            0x4C, (byte)loop, (byte)(loop >> 8),
        ]);

        var characters = new byte[8 * 1024];
        for (var row = 0; row < 8; row++)
            characters[16 + row] = 0xFF;
        return RomFixtures.Nes([.. program], characters);
    }

    private static byte[] StateOf(IEmulatorCore core)
    {
        var buffer = new byte[core.SaveStateSize];
        Assert.True(core.SaveState(buffer));
        return buffer;
    }

    /// <summary>Buttons that change from frame to frame, so a core that ignores input
    /// or applies it a frame late cannot pass by accident.</summary>
    private static GamepadButtons ScriptedInput(int frame, int player) =>
        (GamepadButtons)((frame * 7 + player * 3) & 0xFF);

    [Fact]
    public void The_same_cartridge_and_the_same_buttons_reach_the_same_state()
    {
        var first = new NesConsole(Responsive());
        var second = new NesConsole(Responsive());

        for (var frame = 0; frame < 240; frame++)
        {
            foreach (var core in new[] { first, second })
            {
                core.SetButtons(0, ScriptedInput(frame, 0));
                core.SetButtons(1, ScriptedInput(frame, 1));
                core.RunFrame();
            }
        }

        Assert.Equal(StateOf(first), StateOf(second));
        Assert.Equal(first.Frame, second.Frame);
    }

    [Fact]
    public void Different_buttons_reach_a_different_state()
    {
        // The guard on the test above: if the cartridge ignored its pads, the two runs
        // would agree no matter what was pressed and the comparison would prove nothing.
        var pressed = new NesConsole(Responsive());
        var idle = new NesConsole(Responsive());

        for (var frame = 0; frame < 60; frame++)
        {
            pressed.SetButtons(0, GamepadButtons.A | GamepadButtons.Start);
            pressed.RunFrame();
            idle.RunFrame();
        }

        Assert.NotEqual(StateOf(pressed), StateOf(idle));
    }

    [Fact]
    public void Draining_sound_is_not_part_of_the_machine()
    {
        // One player has the sound on and the other has muted it. They are still
        // playing the same game, and their states have to agree byte for byte or the
        // desync check would fire on nothing.
        var listening = new NesConsole(Responsive());
        var muted = new NesConsole(Responsive());
        var samples = new short[4096];

        for (var frame = 0; frame < 120; frame++)
        {
            listening.SetButtons(0, ScriptedInput(frame, 0));
            listening.RunFrame();
            listening.ReadAudio(samples);

            muted.SetButtons(0, ScriptedInput(frame, 0));
            muted.RunFrame();
        }

        Assert.Equal(StateOf(listening), StateOf(muted));
    }

    [Fact]
    public void A_state_puts_the_machine_back_where_it_was()
    {
        var core = new NesConsole(Responsive());
        for (var frame = 0; frame < 90; frame++)
        {
            core.SetButtons(0, ScriptedInput(frame, 0));
            core.RunFrame();
        }

        var snapshot = StateOf(core);

        // Run on, then rewind and run the same frames again: the second pass has to
        // land exactly where the first did.
        var expected = Continue(core, from: 90, frames: 45);
        Assert.True(core.LoadState(snapshot));
        var replayed = Continue(core, from: 90, frames: 45);

        Assert.Equal(expected.State, replayed.State);
        Assert.Equal(expected.Picture, replayed.Picture);
    }

    [Fact]
    public void A_state_carries_across_to_a_fresh_console()
    {
        // What joining a game in progress needs: the host's state, loaded into a
        // console that has only ever seen the cartridge.
        var host = new NesConsole(Responsive());
        for (var frame = 0; frame < 75; frame++)
        {
            host.SetButtons(0, ScriptedInput(frame, 0));
            host.RunFrame();
        }

        var joiner = new NesConsole(Responsive());
        Assert.True(joiner.LoadState(StateOf(host)));

        var theirs = Continue(host, from: 75, frames: 30);
        var ours = Continue(joiner, from: 75, frames: 30);
        Assert.Equal(theirs.State, ours.State);
        Assert.Equal(theirs.Picture, ours.Picture);
    }

    private static (byte[] State, uint[] Picture) Continue(IEmulatorCore core, int from, int frames)
    {
        for (var frame = from; frame < from + frames; frame++)
        {
            core.SetButtons(0, ScriptedInput(frame, 0));
            core.RunFrame();
        }
        return (StateOf(core), core.Frame.ToArray());
    }

    [Fact]
    public void A_state_from_another_console_is_refused()
    {
        var nes = new NesConsole(Responsive());
        var gameBoy = Emulator.Load(RomFixtures.GameBoy([0x00]), "cart.gb");

        Assert.False(nes.LoadState(StateOf(gameBoy)));
        Assert.False(gameBoy.LoadState(StateOf(nes)));
    }

    [Fact]
    public void A_truncated_state_is_refused_rather_than_half_loaded()
    {
        var core = new NesConsole(Responsive());
        for (var frame = 0; frame < 30; frame++)
            core.RunFrame();

        var snapshot = StateOf(core);
        Assert.False(core.LoadState(snapshot.AsSpan(0, snapshot.Length / 2)));

        // Refused means reset, not somewhere in between — a machine wearing half of
        // somebody else's state would run, and would be wrong. Reset is where the
        // processor starts, not a wiped console: the picture chip's memory survives a
        // reset on the real hardware too.
        Assert.Equal(0x8000, core.Cpu.PC);
        core.RunFrame();

        // And the refusal leaves nothing sticky: the whole state still loads.
        Assert.True(core.LoadState(snapshot));
        Assert.Equal(snapshot, StateOf(core));
    }

    [Fact]
    public void The_game_boy_round_trips_too()
    {
        var core = Emulator.Load(RomFixtures.GameBoy([
            0x3E, 0x91, 0xE0, 0x40,     // LD A,$91 ; LDH ($40),A — screen on
            0x0C,                       // INC C
            0x18, 0xFC,                 // JR -4
        ]), "cart.gb");

        for (var frame = 0; frame < 40; frame++)
            core.RunFrame();
        var snapshot = StateOf(core);

        for (var frame = 0; frame < 20; frame++)
            core.RunFrame();
        var expected = StateOf(core);

        Assert.True(core.LoadState(snapshot));
        for (var frame = 0; frame < 20; frame++)
            core.RunFrame();

        Assert.Equal(expected, StateOf(core));
    }

    [Fact]
    public void Both_consoles_report_how_many_can_play()
    {
        Assert.Equal(2, new NesConsole(Responsive()).PlayerCount);
        Assert.Equal(1, Emulator.Load(RomFixtures.GameBoy([0x00]), "cart.gb").PlayerCount);
    }

    [Fact]
    public void The_second_pad_answers_on_its_own_port()
    {
        var core = new NesConsole(Responsive());
        core.SetButtons(0, GamepadButtons.None);
        core.SetButtons(1, GamepadButtons.A);
        for (var frame = 0; frame < 3; frame++)
            core.RunFrame();

        // The cartridge stores each pad's first bit — A — in its own byte.
        Assert.Equal(0x40, core.CpuRead(0x0300));
        Assert.Equal(0x41, core.CpuRead(0x0301));
    }
}
