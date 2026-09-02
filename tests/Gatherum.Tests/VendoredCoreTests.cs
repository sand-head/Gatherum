using Gatherum.Client.Emulation;
using Gatherum.Client.Emulation.Netplay;
using Gatherum.Core.Domain;
using Gatherum.Core.Roms;

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
    public void A_super_nintendo_cartridge_is_recognised_by_its_bytes()
    {
        Assert.Equal(ConsoleKind.SuperNintendo,
            Emulator.Identify(RomFixtures.SuperNintendo(), "misnamed.bin"));
        Assert.Equal(ConsoleKind.SuperNintendo,
            Emulator.Identify(RomFixtures.SuperNintendo(hiRom: true), "misnamed.bin"));
    }

    [Fact]
    public void The_512_bytes_a_copier_wrote_do_not_hide_the_header()
    {
        Assert.Equal(ConsoleKind.SuperNintendo,
            Emulator.Identify(RomFixtures.SuperNintendo(copierHeader: true), "misnamed.bin"));
        var header = RomHeader.Read(RomFixtures.SuperNintendo("DEMO", copierHeader: true));
        Assert.NotNull(header);
        Assert.Equal("DEMO", header.Title);
    }

    [Fact]
    public void A_super_nintendo_cartridge_goes_to_a_core_from_elsewhere()
    {
        Assert.True(Emulator.NeedsVendoredCore(RomFixtures.SuperNintendo(), "game.sfc"));
        Assert.True(MediaTypes.IsRom(MediaTypes.SuperNintendoRom, "game.sfc"));
        Assert.Equal(MediaTypes.SuperNintendoRom, MediaTypes.Resolve(null, "game.smc"));
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

    [Fact]
    public void So_do_the_second_pair_of_face_buttons()
    {
        // X and Y live above the shoulders, at bits ten and eleven, which is what the
        // second byte of a netplay input message is for.
        var pressed = GamepadButtons.X | GamepadButtons.Y | GamepadButtons.RightShoulder;
        var (_, _, buttons) = PlayProtocol.ReadInput(
            PlayProtocol.Input(slot: 0, frame: 1, pressed));

        Assert.Equal(pressed, buttons);
    }

    [Fact]
    public void A_gamecube_disc_is_recognised_by_its_magic_word_in_either_container()
    {
        Assert.Equal(ConsoleKind.GameCube, Emulator.Identify(RomFixtures.Disc(), "misnamed.bin"));
        Assert.Equal(ConsoleKind.GameCube, Emulator.Identify(RomFixtures.Rvz(), "misnamed.bin"));
    }

    [Fact]
    public void A_disc_is_told_from_its_head_alone()
    {
        // The player reads this much of a file before deciding whether to fetch the
        // rest, and a disc is decided there or not at all.
        Assert.Equal(ConsoleKind.GameCube,
            Emulator.Identify(RomFixtures.Disc().AsSpan(0, Emulator.HeadBytes), "game.iso"));
        Assert.True(Emulator.IsDisc(ConsoleKind.GameCube));
        Assert.True(Emulator.IsDisc(ConsoleKind.Wii));
        Assert.False(Emulator.IsDisc(ConsoleKind.GameBoyAdvance));
    }

    [Fact]
    public void A_disc_name_is_trusted_only_where_it_cannot_mean_anything_else()
    {
        Assert.Equal(ConsoleKind.GameCube, Emulator.Identify(new byte[0x200], "game.gcm"));
        Assert.Equal(ConsoleKind.GameCube, Emulator.Identify(new byte[0x200], "game.rvz"));
        // An .iso is any disc at all, so a header-less one is nothing.
        Assert.Null(Emulator.Identify(new byte[0x200], "linux.iso"));
        Assert.True(Emulator.NamedLikeADisc("linux.iso"));
        Assert.False(Emulator.NamedLikeADisc("game.gba"));
    }

    [Fact]
    public void A_wii_disc_is_recognised_and_refused_by_name()
    {
        Assert.Equal(ConsoleKind.Wii, Emulator.Identify(RomFixtures.Disc(wii: true), "game.iso"));
        Assert.Equal(ConsoleKind.Wii, Emulator.Identify(RomFixtures.Rvz(wii: true), "game.rvz"));
        Assert.False(VendoredCore.Handles(ConsoleKind.Wii));
        var problem = Assert.Throws<NotSupportedException>(
            () => Emulator.Load(RomFixtures.Disc(wii: true), "game.iso"));
        Assert.Contains("Wii", problem.Message);
    }

    [Fact]
    public void A_gamecube_disc_goes_to_the_core_by_address_rather_than_by_bytes()
    {
        Assert.True(Emulator.NeedsVendoredCore(RomFixtures.Disc(), "game.iso"));
        Assert.True(VendoredCore.LoadsByUrl(ConsoleKind.GameCube));
        Assert.False(VendoredCore.LoadsByUrl(ConsoleKind.GameBoyAdvance));
        Assert.False(VendoredCore.LoadsByUrl(ConsoleKind.SuperNintendo));
    }

    [Fact]
    public void A_mega_drive_cartridge_is_recognised_by_the_word_at_the_head_of_its_header()
    {
        Assert.Equal(ConsoleKind.MegaDrive, Emulator.Identify(RomFixtures.MegaDrive(), "misnamed.bin"));
        Assert.Equal(ConsoleKind.MegaDrive,
            Emulator.Identify(RomFixtures.MegaDrive(console: "SEGA GENESIS"), "misnamed.bin"));
        Assert.Equal(ConsoleKind.Sega32X,
            Emulator.Identify(RomFixtures.MegaDrive(console: "SEGA 32X"), "misnamed.bin"));
        // An interleaved dump spells the word differently, and is left to its name.
        Assert.Equal(ConsoleKind.MegaDrive, Emulator.Identify(new byte[0x400], "game.smd"));
        Assert.Equal(ConsoleKind.Sega32X, Emulator.Identify(new byte[0x400], "game.32x"));
    }

    [Fact]
    public void A_virtual_boy_cartridge_is_known_by_its_name_alone()
    {
        // Its header is at the end of the file and carries no magic word, so the bytes
        // say nothing and the extension has the last word.
        Assert.Null(Emulator.Identify(RomFixtures.VirtualBoy(), "misnamed.bin"));
        Assert.Equal(ConsoleKind.VirtualBoy, Emulator.Identify(RomFixtures.VirtualBoy(), "game.vb"));
        Assert.Equal(ConsoleKind.VirtualBoy, Emulator.Identify(new byte[0x400], "game.vboy"));
    }

    [Fact]
    public void The_three_go_to_cores_from_elsewhere_and_are_typed_for_a_console()
    {
        Assert.True(Emulator.NeedsVendoredCore(RomFixtures.MegaDrive(), "game.gen"));
        Assert.True(Emulator.NeedsVendoredCore(RomFixtures.VirtualBoy(), "game.vb"));
        Assert.True(VendoredCore.Handles(ConsoleKind.MegaDrive));
        Assert.True(VendoredCore.Handles(ConsoleKind.Sega32X));
        Assert.True(VendoredCore.Handles(ConsoleKind.VirtualBoy));
        Assert.False(VendoredCore.LoadsByUrl(ConsoleKind.MegaDrive));

        Assert.Equal(MediaTypes.MegaDriveRom, MediaTypes.Resolve(null, "game.gen"));
        Assert.Equal(MediaTypes.MegaDriveRom, MediaTypes.Resolve(null, "game.smd"));
        Assert.Equal(MediaTypes.Sega32XRom, MediaTypes.Resolve(null, "game.32x"));
        Assert.Equal(MediaTypes.VirtualBoyRom, MediaTypes.Resolve(null, "game.vboy"));
        Assert.True(MediaTypes.IsRom(MediaTypes.Binary, "game.vb"));
        Assert.True(MediaTypes.IsRom(MediaTypes.MegaDriveRom, "game.gen"));
    }

    [Fact]
    public void A_mega_drive_cartridge_named_md_is_still_a_page()
    {
        // The commonest name for a Mega Drive dump is the wiki's own name for a page,
        // and a wiki that mistook its pages for cartridges would be no wiki at all.
        Assert.Equal(MediaTypes.Markdown, MediaTypes.Resolve(null, "notes.md"));
        Assert.False(MediaTypes.IsRom(MediaTypes.Markdown, "notes.md"));
        Assert.Null(Emulator.Identify(new byte[0x400], "notes.md"));
    }

    [Fact]
    public void A_disc_page_offers_a_console_rather_than_a_download()
    {
        Assert.True(MediaTypes.IsRom(MediaTypes.GameCubeRom, "game.iso"));
        Assert.True(MediaTypes.IsRom(MediaTypes.Binary, "game.rvz"));
        Assert.Equal(MediaTypes.GameCubeRom, MediaTypes.Resolve(null, "game.gcm"));
        Assert.Equal(MediaTypes.GameCubeRom, MediaTypes.Resolve(null, "game.iso"));
    }
}
