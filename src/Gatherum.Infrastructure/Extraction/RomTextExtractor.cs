using Gatherum.Core.Abstractions;
using Gatherum.Core.Domain;
using Gatherum.Core.Roms;

namespace Gatherum.Infrastructure.Extraction;

/// <summary>Makes a cartridge findable by what its header says: the console, the name
/// printed in it, the mapper hardware, and whether it saves. A ROM has no prose in it,
/// so this is the whole of what search can honestly know about one.</summary>
public class RomTextExtractor : ITextExtractor
{
    /// <summary>Enough of the file to reach the last place a header hides. Sega put
    /// theirs at the end of the first bank rather than the start of the file, where the
    /// iNES and Game Boy ones are, and a Super Nintendo cartridge wired for the high half
    /// of memory puts its at the end of the first 64 KB — plus the 512 bytes a copier may
    /// have written in front. Nothing past this is ever looked at, so a 4 MB cartridge
    /// costs the same as a 32 KB one.</summary>
    private const int HeaderBytes = 0x10200;

    public bool CanExtract(string mediaType, string fileName) =>
        MediaTypes.IsRom(mediaType, fileName);

    public async Task<string> ExtractAsync(Stream content, string mediaType, string fileName,
        CancellationToken cancellationToken = default)
    {
        var head = new byte[HeaderBytes];
        var read = await content.ReadAtLeastAsync(head, HeaderBytes, throwOnEndOfStream: false,
            cancellationToken);
        var header = RomHeader.Read(head.AsSpan(0, read));
        if (header is null && NamedVirtualBoy(fileName))
            header = await ReadTailAsync(content, cancellationToken);
        return header?.Describe() ?? "";
    }

    /// <summary>The one cartridge whose header is at the far end of the file. Still
    /// cheap — a seek and half a kilobyte — and only ever asked of a file whose name
    /// says so, because the tail of anything else is just its last bytes.</summary>
    private static async Task<RomHeader?> ReadTailAsync(Stream content,
        CancellationToken cancellationToken)
    {
        if (!content.CanSeek || content.Length < RomHeader.VirtualBoyHeaderBytes)
            return null;
        content.Seek(-RomHeader.VirtualBoyHeaderBytes, SeekOrigin.End);
        var tail = new byte[RomHeader.VirtualBoyHeaderBytes];
        var read = await content.ReadAtLeastAsync(tail, tail.Length, throwOnEndOfStream: false,
            cancellationToken);
        return RomHeader.ReadVirtualBoy(tail.AsSpan(0, read),
            (int)Math.Min(content.Length, int.MaxValue));
    }

    private static bool NamedVirtualBoy(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".vb" or ".vboy";
}
