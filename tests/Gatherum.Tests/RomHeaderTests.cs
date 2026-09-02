using Gatherum.Core.Domain;
using Gatherum.Core.Roms;
using Gatherum.Infrastructure.Extraction;

namespace Gatherum.Tests;

/// <summary>What a cartridge says about itself, which is the whole of what search can
/// honestly know about one — a ROM has no prose in it.</summary>
public class RomHeaderTests
{
    [Fact]
    public void The_extension_names_the_console_when_the_browser_will_not()
    {
        Assert.Equal(MediaTypes.NesRom,
            MediaTypes.Resolve("application/octet-stream", "game.nes"));
        Assert.Equal(MediaTypes.GameBoyRom, MediaTypes.Resolve(null, "game.gb"));
        Assert.Equal(MediaTypes.GameBoyColorRom, MediaTypes.Resolve("", "game.gbc"));
        Assert.True(MediaTypes.IsRom(MediaTypes.Binary, "game.NES"));
        Assert.False(MediaTypes.IsRom(MediaTypes.Binary, "notes.txt"));
        // A cartridge is not text, whatever else the upload claimed.
        Assert.False(MediaTypes.IsText(MediaTypes.NesRom, "game.nes"));
    }

    [Fact]
    public void An_ines_header_names_the_board_and_the_sizes()
    {
        var image = RomFixtures.Nes([0xEA]);
        image[6] = 0x43;    // mapper 4, vertical mirroring, battery

        var header = RomHeader.Read(image);

        Assert.NotNull(header);
        Assert.Equal(RomSystem.Nes, header.System);
        Assert.Equal("MMC3 (mapper 4)", header.Cartridge);
        Assert.Equal(16 * 1024, header.ProgramBytes);
        Assert.Equal(8 * 1024, header.GraphicsBytes);
        Assert.True(header.Battery);
        Assert.Contains("Nintendo Entertainment System", header.Describe());
        Assert.Contains("battery-backed", header.Describe());
    }

    [Fact]
    public void The_high_nibble_of_the_second_flags_byte_is_the_top_of_the_mapper()
    {
        var image = RomFixtures.Nes([0xEA]);
        image[6] = 0x10;
        image[7] = 0x40;

        Assert.Equal("Mapper 65", RomHeader.Read(image)!.Cartridge);
    }

    [Fact]
    public void A_game_boy_header_carries_the_name_printed_on_the_cartridge()
    {
        var image = RomFixtures.GameBoy([0x00], cartridgeType: 0x13, title: "ZELDA");

        var header = RomHeader.Read(image);

        Assert.NotNull(header);
        Assert.Equal(RomSystem.GameBoy, header.System);
        Assert.Equal("ZELDA", header.Title);
        Assert.Equal("MBC3 with battery-backed RAM", header.Cartridge);
        Assert.Equal(32 * 1024, header.ProgramBytes);
        Assert.Equal(8 * 1024, header.SaveRamBytes);
        Assert.True(header.Battery);
    }

    [Fact]
    public void A_colour_cartridge_says_which_machine_it_wants()
    {
        var image = RomFixtures.GameBoy([0x00]);
        image[0x143] = 0xC0;
        Assert.Equal("Game Boy Color", RomHeader.Read(image)!.SystemName);

        image[0x143] = 0x80;
        Assert.Contains("also plays on Game Boy", RomHeader.Read(image)!.SystemName);
    }

    [Fact]
    public void Something_that_is_not_a_cartridge_reads_as_nothing()
    {
        Assert.Null(RomHeader.Read(new byte[512]));
        Assert.Null(RomHeader.Read([]));
        Assert.Null(RomHeader.Read("NES but not really"u8));
    }

    [Fact]
    public async Task The_extractor_gives_search_the_header_and_reads_no_further()
    {
        var extractor = new RomTextExtractor();
        Assert.True(extractor.CanExtract(MediaTypes.Binary, "sonic.gb"));
        Assert.False(extractor.CanExtract(MediaTypes.PlainText, "sonic.txt"));

        // Four megabytes of cartridge, and the extractor only ever looks at the head
        // of it — a stream that refuses to be read past the header proves it. The head
        // reaches the last place any of these machines hid one: Sega at the end of the
        // first bank, a Super Nintendo at the end of the first 64 KB, plus the 512 bytes
        // a copier may have written in front of everything.
        var image = RomFixtures.GameBoy([0x00], title: "METROID");
        Array.Resize(ref image, 4 * 1024 * 1024);
        await using var stream = new HeaderOnlyStream(image, 0x10200);

        var text = await extractor.ExtractAsync(stream, MediaTypes.GameBoyRom, "metroid.gb");

        Assert.Contains("Title: METROID", text);
        Assert.Contains("System: Game Boy", text);
    }

    /// <summary>A stream that throws if anything reads past the header, so "cheap" is
    /// a property the test can hold the extractor to rather than a claim in a comment.</summary>
    private sealed class HeaderOnlyStream(byte[] content, int limit) : Stream
    {
        private int position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= limit)
                throw new InvalidOperationException("Read past the cartridge header.");
            var taken = Math.Min(count, limit - position);
            Array.Copy(content, position, buffer, offset, taken);
            position += taken;
            return taken;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void A_sega_header_names_the_console_it_was_sold_for()
    {
        var master = RomHeader.Read(RomFixtures.Sega([0x00], gameGear: false));
        Assert.NotNull(master);
        Assert.Equal(RomSystem.MasterSystem, master.System);
        Assert.Equal("Master System", master.SystemName);
        Assert.Equal("Export", master.Region);

        var handheld = RomHeader.Read(RomFixtures.Sega([0x00], gameGear: true));
        Assert.NotNull(handheld);
        Assert.Equal(RomSystem.GameGear, handheld.System);
        Assert.Equal("Game Gear", handheld.SystemName);
    }

    [Fact]
    public void A_sega_header_carries_a_product_code_where_a_title_would_be()
    {
        // Sega's header has no room for a name, so the catalogue number is the only
        // thing in the file that identifies a particular game.
        var header = RomHeader.Read(RomFixtures.Sega([0x00], productCode: 12345));
        Assert.NotNull(header);
        Assert.Null(header.Title);
        Assert.Contains("12345", header.Cartridge);
        Assert.Contains("Product 12345", header.Describe());
    }

    [Fact]
    public void A_cartridge_with_no_sega_header_is_not_mistaken_for_one()
    {
        Assert.Null(RomHeader.Read(new byte[0x8000]));
    }

    [Fact]
    public void A_game_boy_advance_header_carries_a_title_and_a_game_code()
    {
        var header = RomHeader.Read(RomFixtures.GameBoyAdvance("METROID4", "AMTE"));
        Assert.NotNull(header);
        Assert.Equal(RomSystem.GameBoyAdvance, header.System);
        Assert.Equal("Game Boy Advance", header.SystemName);
        Assert.Equal("METROID4", header.Title);
        Assert.Contains("AMTE", header.Cartridge);
        // The fourth letter of the code is where the cartridge was sold.
        Assert.Equal("North America", header.Region);
    }

    [Fact]
    public void A_game_boy_advance_header_says_nothing_about_saving_so_neither_do_we()
    {
        // Nothing in the header declares a save chip: the hardware is worked out by
        // looking for a marker in the program itself. Printing "Saves: none" would be
        // a guess dressed up as a fact.
        var header = RomHeader.Read(RomFixtures.GameBoyAdvance());
        Assert.NotNull(header);
        Assert.Null(header.Battery);
        Assert.DoesNotContain("Saves:", header.Describe());
    }

    [Fact]
    public void A_super_nintendo_header_is_found_at_whichever_end_of_a_bank_it_sits()
    {
        var low = RomHeader.Read(RomFixtures.SuperNintendo("ZELDA III"));
        Assert.NotNull(low);
        Assert.Equal(RomSystem.SuperNintendo, low.System);
        Assert.Equal("Super Nintendo", low.SystemName);
        Assert.Equal("ZELDA III", low.Title);
        Assert.Equal("LoROM", low.Cartridge);

        var high = RomHeader.Read(RomFixtures.SuperNintendo("ZELDA III", hiRom: true));
        Assert.NotNull(high);
        Assert.Equal("ZELDA III", high.Title);
        Assert.Equal("HiROM", high.Cartridge);
    }

    [Fact]
    public void A_super_nintendo_cartridge_says_whether_it_remembers_anything()
    {
        // Type 2 is RAM behind a battery, and the size byte is a power of two in
        // kilobytes — so this one keeps eight.
        var saves = RomHeader.Read(
            RomFixtures.SuperNintendo(cartridgeType: 0x02, saveRamSize: 0x03));
        Assert.NotNull(saves);
        Assert.Equal(8 * 1024, saves.SaveRamBytes);
        Assert.True(saves.Battery);

        // RAM with nothing behind it forgets when the console is switched off.
        var forgets = RomHeader.Read(
            RomFixtures.SuperNintendo(cartridgeType: 0x01, saveRamSize: 0x03));
        Assert.NotNull(forgets);
        Assert.False(forgets.Battery);
    }

    [Fact]
    public void A_second_processor_on_the_board_is_named()
    {
        var superFx = RomHeader.Read(RomFixtures.SuperNintendo(cartridgeType: 0x15));
        Assert.NotNull(superFx);
        Assert.Contains("Super FX", superFx.Cartridge);

        // Nothing but ROM and RAM is not a coprocessor, whatever the nibble says.
        var plain = RomHeader.Read(RomFixtures.SuperNintendo(cartridgeType: 0x02));
        Assert.NotNull(plain);
        Assert.Equal("LoROM", plain.Cartridge);
    }

    [Fact]
    public void A_pile_of_bytes_with_no_checksum_in_it_is_not_a_super_nintendo_cartridge()
    {
        // The checksum beside its own complement is the whole of what makes the header
        // findable, so breaking it has to be enough to lose it.
        var broken = RomFixtures.SuperNintendo();
        broken[0x7FDE] ^= 0xFF;
        Assert.Null(RomHeader.Read(broken));
    }

    [Fact]
    public void Something_that_is_not_a_cartridge_is_not_read_as_one()
    {
        // Both of the two bytes a Game Boy Advance cartridge is recognised by, and
        // neither on its own.
        var almost = RomFixtures.GameBoyAdvance();
        almost[0xB2] = 0x00;
        Assert.Null(RomHeader.Read(almost));

        var alsoAlmost = RomFixtures.GameBoyAdvance();
        alsoAlmost[3] = 0x00;
        Assert.Null(RomHeader.Read(alsoAlmost));
    }

    [Fact]
    public void A_gamecube_disc_header_names_the_game_and_where_it_was_sold()
    {
        var header = RomHeader.Read(RomFixtures.Disc("The Legend of Zelda: The Wind Waker", "GZLE", disc: 0, revision: 2));
        Assert.NotNull(header);
        Assert.Equal(RomSystem.GameCube, header.System);
        Assert.Equal("GameCube", header.SystemName);
        Assert.Equal("The Legend of Zelda: The Wind Waker", header.Title);
        Assert.Contains("GZLE", header.Cartridge);
        Assert.Contains("disc 1", header.Cartridge);
        Assert.Contains("revision 2", header.Cartridge);
        Assert.Equal("North America", header.Region);
        // A GameCube saves to a memory card, so the disc has nothing to say about it.
        Assert.Null(header.Battery);
    }

    [Fact]
    public void An_rvz_reports_the_disc_it_holds_and_the_size_it_would_be_uncompressed()
    {
        var header = RomHeader.Read(RomFixtures.Rvz("PIKMIN", "GPIP", discBytes: 1_459_978_240));
        Assert.NotNull(header);
        Assert.Equal(RomSystem.GameCube, header.System);
        Assert.Equal("PIKMIN", header.Title);
        Assert.Equal("Europe", header.Region);
        Assert.Equal(1_459_978_240, header.ProgramBytes);
    }

    [Fact]
    public void A_wii_disc_header_is_read_as_a_wii_one()
    {
        var header = RomHeader.Read(RomFixtures.Disc("Wii Sports", "RSPE", wii: true));
        Assert.NotNull(header);
        Assert.Equal(RomSystem.Wii, header.System);
        Assert.Equal("Wii", header.SystemName);
        Assert.Equal("Wii Sports", header.Title);
    }

    [Fact]
    public void A_block_with_neither_magic_word_is_not_a_disc()
    {
        Assert.Null(RomHeader.Read(new byte[0x800]));
    }
}
