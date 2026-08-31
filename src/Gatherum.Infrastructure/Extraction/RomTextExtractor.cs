using Gatherum.Core.Abstractions;
using Gatherum.Core.Domain;
using Gatherum.Core.Roms;

namespace Gatherum.Infrastructure.Extraction;

/// <summary>Makes a cartridge findable by what its header says: the console, the name
/// printed in it, the mapper hardware, and whether it saves. A ROM has no prose in it,
/// so this is the whole of what search can honestly know about one.</summary>
public class RomTextExtractor : ITextExtractor
{
    /// <summary>Enough of the file to reach the Game Boy header at 0x100; the iNES one
    /// is in the first sixteen bytes. Nothing past this is ever looked at, so a 4 MB
    /// cartridge costs the same as a 32 KB one.</summary>
    private const int HeaderBytes = 0x150;

    public bool CanExtract(string mediaType, string fileName) =>
        MediaTypes.IsRom(mediaType, fileName);

    public async Task<string> ExtractAsync(Stream content, string mediaType, string fileName,
        CancellationToken cancellationToken = default)
    {
        var head = new byte[HeaderBytes];
        var read = await content.ReadAtLeastAsync(head, HeaderBytes, throwOnEndOfStream: false,
            cancellationToken);
        return RomHeader.Read(head.AsSpan(0, read))?.Describe() ?? "";
    }
}
