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
        // of it — a stream that refuses to be read past the header proves it.
        var image = RomFixtures.GameBoy([0x00], title: "METROID");
        Array.Resize(ref image, 4 * 1024 * 1024);
        await using var stream = new HeaderOnlyStream(image, 0x150);

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
}
