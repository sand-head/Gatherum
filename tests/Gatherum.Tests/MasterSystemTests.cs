using Gatherum.Client.Emulation;
using Gatherum.Client.Emulation.Sega;

namespace Gatherum.Tests;

/// <summary>The picture chip, the paging hardware and the ports, driven the way a game
/// drives them: by writing to them from a program running on the console.</summary>
public class MasterSystemTests
{
    private const byte Halt = 0x76;
    private const uint Red = 0xFFFF0000;
    private const uint Blue = 0xFF0000FF;
    private const uint Black = 0xFF000000;

    /// <summary>LD A,value : OUT ($BF),A twice — the two-byte handshake that every
    /// register write on this chip is made of.</summary>
    private static void Register(List<byte> code, int register, byte value) =>
        code.AddRange([0x3E, value, 0xD3, 0xBF, 0x3E, (byte)(0x80 | register), 0xD3, 0xBF]);

    /// <summary>Points the chip at an address, and says whether what follows is going
    /// into video memory (1) or colour memory (3).</summary>
    private static void Address(List<byte> code, int address, int mode) =>
        code.AddRange([0x3E, (byte)address, 0xD3, 0xBF,
                       0x3E, (byte)(mode << 6 | address >> 8 & 0x3F), 0xD3, 0xBF]);

    private static void Data(List<byte> code, params byte[] values)
    {
        foreach (var value in values)
            code.AddRange([0x3E, value, 0xD3, 0xBE]);
    }

    /// <summary>Eight rows of a tile painted entirely in one colour. A pixel takes one
    /// bit from each of four planes, so a solid colour 1 is a plane of ones and three
    /// of zeroes.</summary>
    private static byte[] SolidTile(int colour)
    {
        var tile = new byte[32];
        for (var row = 0; row < 8; row++)
            for (var plane = 0; plane < 4; plane++)
                tile[row * 4 + plane] = (colour >> plane & 1) != 0 ? (byte)0xFF : (byte)0x00;
        return tile;
    }

    /// <summary>Two frames: the first is spent running the setup, which means the top of
    /// the screen was drawn before the tiles existed. The second is the one to look at.</summary>
    private static MasterSystem Boot(List<byte> program, bool gameGear = false)
    {
        var console = new MasterSystem(
            RomFixtures.Sega([.. program], gameGear), gameGear);
        console.RunFrame();
        console.RunFrame();
        return console;
    }

    private static List<byte> DisplayOn()
    {
        var code = new List<byte>();
        Register(code, 0, 0x04);   // mode 4
        Register(code, 1, 0xC0);   // 16 KB of memory, display on
        Register(code, 2, 0xFF);   // name table at $3800
        Register(code, 5, 0xFF);   // sprite table at $3F00
        Register(code, 6, 0xFB);   // sprite patterns at $0000
        Register(code, 7, 0x00);
        return code;
    }

    [Fact]
    public void A_tile_on_the_name_table_is_drawn_in_the_colour_it_names()
    {
        var code = DisplayOn();
        Address(code, 0x0000, mode: 3);
        Data(code, 0x00, 0x03);              // colour 0 black, colour 1 red
        Address(code, 0x0000, mode: 1);
        Data(code, SolidTile(1));
        code.Add(Halt);

        var console = Boot(code);
        Assert.Equal(Red, console.Frame[0]);
        Assert.Equal(Red, console.Frame[100 * 256 + 100]);
    }

    [Fact]
    public void A_blank_display_shows_the_backdrop_and_nothing_else()
    {
        var code = new List<byte>();
        Register(code, 0, 0x04);
        Register(code, 1, 0x80);             // display off
        Register(code, 7, 0x01);             // backdrop is colour 17
        Address(code, 0x0000, mode: 3);
        Data(code, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                   0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                   0x00, 0x03);              // colour 17 red
        code.Add(Halt);

        var console = Boot(code);
        Assert.All(console.Frame, pixel => Assert.Equal(Red, pixel));
    }

    [Fact]
    public void A_sprite_is_drawn_over_the_background()
    {
        var code = DisplayOn();
        Address(code, 0x0000, mode: 3);
        Data(code, 0x00, 0x00);                              // background colours black
        // Sprites read the second half of colour memory, so their colour 1 is entry 17.
        Address(code, 0x0011, mode: 3);
        Data(code, 0x30);                                    // sprite colour 1 is blue
        Address(code, 0x0020, mode: 1);
        Data(code, SolidTile(1));                            // pattern 1
        Address(code, 0x3F00, mode: 1);
        Data(code, 0x0F, 0xD0);                              // sprite at y=16, list ends
        Address(code, 0x3F80, mode: 1);
        Data(code, 0x20, 0x01);                              // x=32, pattern 1
        code.Add(Halt);

        var console = Boot(code);
        Assert.Equal(Blue, console.Frame[16 * 256 + 32]);
        Assert.Equal(Blue, console.Frame[23 * 256 + 39]);
        // Just outside the eight-by-eight it occupies, the background shows again.
        Assert.Equal(Black, console.Frame[16 * 256 + 40]);
        Assert.Equal(Black, console.Frame[24 * 256 + 32]);
    }

    [Fact]
    public void The_lower_numbered_of_two_overlapping_sprites_is_the_one_in_front()
    {
        var code = DisplayOn();
        Address(code, 0x0000, mode: 3);
        Data(code, 0x00, 0x00);                              // background is black
        Address(code, 0x0011, mode: 3);
        Data(code, 0x30, 0x03);                              // sprite colour 1 blue, 2 red
        Address(code, 0x0020, mode: 1);
        Data(code, SolidTile(1));                            // pattern 1 is colour 1
        Address(code, 0x0040, mode: 1);
        Data(code, SolidTile(2));                            // pattern 2 is colour 2
        // Two sprites in the same place. The first one listed has to win.
        Address(code, 0x3F00, mode: 1);
        Data(code, 0x0F, 0x0F, 0xD0);
        Address(code, 0x3F80, mode: 1);
        Data(code, 0x20, 0x01, 0x20, 0x02);
        code.Add(Halt);

        var console = Boot(code);
        Assert.Equal(Blue, console.Frame[16 * 256 + 32]);
    }

    [Fact]
    public void A_background_tile_marked_to_stay_in_front_keeps_the_sprite_behind_it()
    {
        var code = DisplayOn();
        Address(code, 0x0000, mode: 3);
        Data(code, 0x00, 0x03);                              // colour 1 red
        Address(code, 0x0011, mode: 3);
        Data(code, 0x30);                                    // sprite colour 1 blue
        Address(code, 0x0000, mode: 1);
        Data(code, SolidTile(1));                            // background tile 0
        Address(code, 0x0020, mode: 1);
        Data(code, SolidTile(1));                            // sprite pattern 1
        // Every name table entry gets the priority bit, so the whole background is in
        // front of everything.
        Address(code, 0x3800, mode: 1);
        code.AddRange([0x06, 0x00]);                         // LD B,0 -> 256 iterations
        code.AddRange([0x3E, 0x00, 0xD3, 0xBE]);             // LD A,0 : OUT ($BE),A
        code.AddRange([0x3E, 0x10, 0xD3, 0xBE]);             // LD A,$10 : OUT ($BE),A
        code.AddRange([0x10, 0xF6]);                         // DJNZ back over all ten bytes
        Address(code, 0x3F00, mode: 1);
        Data(code, 0x0F, 0xD0);
        Address(code, 0x3F80, mode: 1);
        Data(code, 0x20, 0x01);
        code.Add(Halt);

        var console = Boot(code);
        Assert.Equal(Red, console.Frame[16 * 256 + 32]);
    }

    [Fact]
    public void Scrolling_moves_the_picture_under_the_screen()
    {
        // One red tile at the top-left of the name table and nothing else; scrolling
        // right by eight should put it in the second column instead of the first.
        var code = DisplayOn();
        Register(code, 8, 0x08);                             // scroll right by eight
        Address(code, 0x0000, mode: 3);
        Data(code, 0x00, 0x03);
        Address(code, 0x0020, mode: 1);
        Data(code, SolidTile(1));                            // tile 1 is solid red
        Address(code, 0x3800, mode: 1);
        Data(code, 0x01, 0x00);                              // first entry names tile 1
        code.Add(Halt);

        var console = Boot(code);
        Assert.Equal(Black, console.Frame[0]);
        Assert.Equal(Red, console.Frame[8]);
        Assert.Equal(Red, console.Frame[15]);
        Assert.Equal(Black, console.Frame[16]);
    }

    [Fact]
    public void The_left_column_can_be_blanked_while_a_screen_scrolls_in()
    {
        var code = DisplayOn();
        Register(code, 0, 0x24);                             // mode 4, mask column zero
        Register(code, 7, 0x00);                             // backdrop is colour 16
        Address(code, 0x0000, mode: 3);
        Data(code, 0x00, 0x03);
        Address(code, 0x0000, mode: 1);
        Data(code, SolidTile(1));
        code.Add(Halt);

        var console = Boot(code);
        Assert.Equal(Black, console.Frame[0]);
        Assert.Equal(Black, console.Frame[7]);
        Assert.Equal(Red, console.Frame[8]);
    }

    [Fact]
    public void The_line_interrupt_arrives_once_a_line()
    {
        // The handler has to read the status port, because that is the only thing that
        // lets the line go — a handler that forgets is called again immediately.
        var program = new byte[0x40];
        var handler = new byte[] { 0xDB, 0xBF, 0x34, 0xFB, 0xED, 0x4D };
        handler.CopyTo(program, 0x38);

        var code = new List<byte> { 0xC3, 0x40, 0x00 };      // JP $0040 over the handler
        var main = DisplayOn();
        Register(main, 0, 0x14);                             // mode 4, line interrupts on
        Register(main, 10, 0x00);                            // every line
        main.AddRange([0x21, 0x00, 0xC0]);                   // LD HL,$C000
        main.AddRange([0x36, 0x00]);                         // LD (HL),0
        main.AddRange([0xED, 0x56, 0xFB]);                   // IM 1 : EI
        main.AddRange([0x18, 0xFE]);                         // JR $ — sit and be interrupted

        var image = new List<byte>(program);
        image.RemoveRange(0, 3);
        image.InsertRange(0, code);
        image.AddRange(main);

        var console = new MasterSystem(RomFixtures.Sega([.. image]), gameGear: false);
        console.RunFrame();
        console.RunFrame();
        var before = console.Read(0xC000);
        console.RunFrame();
        var after = console.Read(0xC000);

        // 192 visible lines and the one after them, give or take where the frame was cut.
        var fired = (after - before + 256) % 256;
        Assert.InRange(fired, 190, 195);
    }

    [Fact]
    public void A_game_gear_shows_the_middle_of_the_same_picture()
    {
        var code = DisplayOn();
        Address(code, 0x0002, mode: 3);
        // Twelve bits of colour, in two bytes that only commit together.
        Data(code, 0x0F, 0x00);
        Address(code, 0x0000, mode: 1);
        Data(code, SolidTile(1));
        code.Add(Halt);

        var console = Boot(code, gameGear: true);
        Assert.Equal(160, console.Width);
        Assert.Equal(144, console.Height);
        Assert.Equal(160 * 144, console.Frame.Length);
        Assert.Equal(Red, console.Frame[0]);
    }

    [Fact]
    public void A_game_gear_has_one_pad_and_a_master_system_has_two()
    {
        Assert.Equal(1, new MasterSystem(RomFixtures.Sega([Halt], gameGear: true), true).PlayerCount);
        Assert.Equal(2, new MasterSystem(RomFixtures.Sega([Halt]), false).PlayerCount);
    }

    [Fact]
    public void A_pressed_button_pulls_its_line_down()
    {
        var console = new MasterSystem(RomFixtures.Sega([Halt]), gameGear: false);
        Assert.Equal(0xFF, console.ReadPort(0xDC));

        console.SetButtons(0, GamepadButtons.Up | GamepadButtons.A);
        Assert.Equal(0xFF & ~0x01 & ~0x10, console.ReadPort(0xDC));

        // The second pad is split across both ports, which is why two people on one
        // console is a wiring detail rather than a feature.
        console.SetButtons(1, GamepadButtons.Down | GamepadButtons.Left);
        Assert.Equal(0xFF & ~0x01 & ~0x10 & ~0x80, console.ReadPort(0xDC));
        Assert.Equal(0xFF & ~0x01, console.ReadPort(0xDD));
    }

    [Fact]
    public void The_two_directions_of_an_axis_are_never_pressed_at_once()
    {
        var console = new MasterSystem(RomFixtures.Sega([Halt]), gameGear: false);
        console.SetButtons(0, GamepadButtons.Left | GamepadButtons.Right);
        Assert.Equal(0xFF & ~0x04, console.ReadPort(0xDC));
    }

    [Fact]
    public void The_start_button_on_a_game_gear_is_beside_the_screen_and_not_on_the_pad()
    {
        var console = new MasterSystem(RomFixtures.Sega([Halt], gameGear: true), gameGear: true);
        Assert.Equal(0xC0, console.ReadPort(0x00));
        console.SetButtons(0, GamepadButtons.Start);
        Assert.Equal(0x40, console.ReadPort(0x00));
    }

    [Fact]
    public void The_header_picks_the_console_before_the_name_does()
    {
        Assert.Equal("Master System",
            Emulator.Load(RomFixtures.Sega([Halt]), "misnamed.gg").SystemName);
        Assert.Equal("Game Gear",
            Emulator.Load(RomFixtures.Sega([Halt], gameGear: true), "misnamed.sms").SystemName);
    }

    [Fact]
    public void A_cartridge_with_no_header_falls_back_to_what_it_is_called()
    {
        // Plenty of cartridges shipped without Sega's header, so a file that declares
        // nothing is still perfectly playable — the name is simply all there is to go on.
        var bare = new byte[0x8000];
        Assert.Equal("Master System", Emulator.Load(bare, "game.sms").SystemName);
        Assert.Equal("Game Gear", Emulator.Load(bare, "game.gg").SystemName);
    }

    [Fact]
    public void The_ears_stay_the_right_way_round_after_the_queue_has_overflowed()
    {
        // Nothing drains the sound while it is switched off, so the queue fills and has
        // to throw some away. Dropping an odd number of values would hand every sample
        // after it to the wrong ear — silently, and for the rest of the game.
        var psg = new SegaPsg(gameGear: true);
        psg.Reset();
        psg.WriteStereo(0x10);          // the first channel reaches the left ear only
        psg.Write(0x80);                // channel one, low four bits of the divider
        psg.Write(0x10);                // and the high six: a tone somewhere audible
        psg.Write(0x90);                // full volume

        // Three seconds of sound into a queue that holds one.
        psg.Step(3579545 * 3);

        var buffer = new short[4096];
        var total = 0;
        int count;
        while ((count = psg.ReadAudio(buffer)) > 0)
        {
            Assert.Equal(0, count % 2);
            for (var index = 1; index < count; index += 2)
                Assert.Equal(0, buffer[index]);
            for (var index = 0; index < count; index += 2)
                total += Math.Abs(buffer[index]);
        }

        Assert.True(total > 0, "the left ear should have carried the sound");
    }
}
