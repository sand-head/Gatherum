using Gatherum.Client.Emulation;

namespace Gatherum.Tests;

/// <summary>The packing is the one convention every layer speaks — the player, the
/// JavaScript, the libretro shim and the Gecko host all unpack the same four bytes —
/// so drift in it would read as a stick leaning somewhere nobody pushed.</summary>
public class StickStateTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(127, -127, 5, -5)]
    [InlineData(-1, 1, -128, 127)]
    public void Packing_round_trips(sbyte leftX, sbyte leftY, sbyte rightX, sbyte rightY)
    {
        var sticks = new StickState(leftX, leftY, rightX, rightY);
        Assert.Equal(sticks, StickState.Unpack(sticks.Packed));
    }

    [Fact]
    public void A_pad_at_rest_packs_to_zero()
    {
        Assert.Equal(0, default(StickState).Packed);
    }

    [Fact]
    public void Each_axis_lands_in_its_own_byte()
    {
        var packed = new StickState(1, 2, 3, 4).Packed;
        Assert.Equal(0x04030201, packed);
    }
}
