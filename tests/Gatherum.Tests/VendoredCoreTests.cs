using Gatherum.Client.Emulation;
using Gatherum.Client.Emulation.Netplay;
using Gatherum.Core.Domain;

namespace Gatherum.Tests;

/// <summary>What can be checked about a core Gatherum did not write, from outside a
/// browser: that a cartridge for it is recognised, routed away from the consoles written
/// in C#, and typed so its page offers a console rather than a download.
///
/// The core itself is not tested here and cannot be. It is fetched at build time and
/// instantiated by the browser, so what it does once running is verified by driving the
/// real app — see DECISIONS.md.</summary>
public class VendoredCoreTests
{
    [Fact]
    public void A_game_boy_advance_cartridge_is_recognised_by_its_bytes()
    {
        Assert.Equal(ConsoleKind.GameBoyAdvance,
            Emulator.Identify(RomFixtures.GameBoyAdvance(), "misnamed.nes"));
    }

    [Fact]
    public void A_cartridge_with_nothing_to_declare_falls_back_to_its_name()
    {
        Assert.Equal(ConsoleKind.GameBoyAdvance, Emulator.Identify(new byte[0x200], "game.gba"));
        Assert.Null(Emulator.Identify(new byte[0x200], "notes.txt"));
    }

    [Fact]
    public void It_is_routed_away_from_the_consoles_written_here()
    {
        var rom = RomFixtures.GameBoyAdvance();
        Assert.True(Emulator.NeedsVendoredCore(rom, "game.gba"));
        Assert.False(Emulator.NeedsVendoredCore(RomFixtures.Nes([0xEA]), "game.nes"));
        Assert.False(Emulator.NeedsVendoredCore(RomFixtures.Sega([0x76]), "game.sms"));
    }

    [Fact]
    public void Loading_one_as_a_console_written_here_says_why_it_cannot()
    {
        var problem = Assert.Throws<NotSupportedException>(
            () => Emulator.Load(RomFixtures.GameBoyAdvance(), "game.gba"));
        Assert.Contains("native/README.md", problem.Message);
    }

    [Fact]
    public void Its_page_offers_a_console_rather_than_a_download()
    {
        Assert.True(MediaTypes.IsRom(MediaTypes.GameBoyAdvanceRom, "game.gba"));
        Assert.True(MediaTypes.IsRom(MediaTypes.Binary, "game.gba"));
        Assert.Equal(MediaTypes.GameBoyAdvanceRom, MediaTypes.Resolve(null, "game.gba"));
    }

    [Fact]
    public void The_two_shoulder_buttons_survive_the_wire()
    {
        // They live above the eighth bit, which is why netplay sends two bytes of
        // buttons rather than one.
        var pressed = GamepadButtons.LeftShoulder | GamepadButtons.RightShoulder
            | GamepadButtons.A | GamepadButtons.Up;
        var message = PlayProtocol.Input(slot: 1, frame: 4242, pressed);
        var (slot, frame, buttons) = PlayProtocol.ReadInput(message);

        Assert.Equal(1, slot);
        Assert.Equal(4242, frame);
        Assert.Equal(pressed, buttons);
    }
}
