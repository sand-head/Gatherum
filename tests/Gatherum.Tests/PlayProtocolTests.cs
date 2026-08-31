using Gatherum.Client.Emulation;
using Gatherum.Client.Emulation.Netplay;

namespace Gatherum.Tests;

/// <summary>The few bytes two people playing the same game send each other. Positional
/// and untagged, so the only thing standing between the two ends agreeing and a game
/// quietly falling apart is that these round trip.</summary>
public class PlayProtocolTests
{
    [Fact]
    public void A_join_carries_the_cartridge_and_the_shape_of_the_console()
    {
        var hash = new string('a', 64);
        var (romHash, players) = PlayProtocol.ReadJoin(PlayProtocol.Join(hash, 2));

        Assert.Equal(hash, romHash);
        Assert.Equal(2, players);
    }

    [Fact]
    public void A_join_claiming_an_impossible_number_of_players_is_brought_back_in_range()
    {
        Assert.Equal(1, PlayProtocol.ReadJoin(PlayProtocol.Join("x", 0)).PlayerCount);
        Assert.Equal(PlayProtocol.MaxPlayers,
            PlayProtocol.ReadJoin(PlayProtocol.Join("x", 99)).PlayerCount);
    }

    [Fact]
    public void An_input_round_trips_its_slot_frame_and_buttons()
    {
        var buttons = GamepadButtons.A | GamepadButtons.Left | GamepadButtons.Start;
        var (slot, frame, pressed) = PlayProtocol.ReadInput(
            PlayProtocol.Input(1, 123456, buttons));

        Assert.Equal(1, slot);
        Assert.Equal(123456, frame);
        Assert.Equal(buttons, pressed);
    }

    [Fact]
    public void A_checksum_round_trips_a_whole_sixty_four_bit_fingerprint()
    {
        var (slot, frame, hash) = PlayProtocol.ReadChecksum(
            PlayProtocol.Checksum(0, 60, 0xFEDCBA9876543210UL));

        Assert.Equal(0, slot);
        Assert.Equal(60, frame);
        Assert.Equal(0xFEDCBA9876543210UL, hash);
    }

    [Fact]
    public void A_state_round_trips_the_whole_machine()
    {
        var machine = new byte[5000];
        Random.Shared.NextBytes(machine);

        var (frame, restored) = PlayProtocol.ReadState(PlayProtocol.State(4242, machine));

        Assert.Equal(4242, frame);
        Assert.Equal(machine, restored.ToArray());
    }

    [Fact]
    public void A_roster_round_trips_names_that_are_not_ascii()
    {
        IReadOnlyList<PlaySeat> seats = [new(0, "Ríona"), new(1, "さくら")];

        Assert.Equal(seats, PlayProtocol.ReadRoster(PlayProtocol.Roster(seats)));
    }

    [Fact]
    public void The_server_stamps_the_slot_a_client_claimed()
    {
        // A client says what it pressed; who pressed it is never the client's word.
        var message = PlayProtocol.Input(slot: 0, frame: 7, GamepadButtons.B);
        PlayProtocol.StampSlot(message, 1);

        Assert.Equal(1, PlayProtocol.ReadInput(message).Slot);
    }

    [Fact]
    public void Stamping_leaves_alone_the_messages_that_carry_no_slot()
    {
        var state = PlayProtocol.State(9, [1, 2, 3]);
        var before = state.ToArray();
        PlayProtocol.StampSlot(state, 3);

        Assert.Equal(before, state);
    }

    [Fact]
    public void A_truncated_message_reads_as_nothing_rather_than_throwing()
    {
        Assert.Equal(-1, PlayProtocol.ReadInput([(byte)PlayMessage.Input, 0]).Frame);
        Assert.Equal(-1, PlayProtocol.ReadChecksum([(byte)PlayMessage.Checksum]).Frame);
        Assert.Equal(-1, PlayProtocol.ReadState(new byte[] { 6, 0 }).Frame);
        Assert.Empty(PlayProtocol.ReadRoster([(byte)PlayMessage.Roster]));
        Assert.Equal("", PlayProtocol.ReadJoin([(byte)PlayMessage.Join]).RomHash);
    }

    [Fact]
    public void A_fingerprint_notices_a_single_bit()
    {
        var state = new byte[1024];
        Random.Shared.NextBytes(state);
        var before = PlayProtocol.Fingerprint(state);

        state[500] ^= 0x01;

        Assert.NotEqual(before, PlayProtocol.Fingerprint(state));
    }
}
