using Gatherum.Core.Abstractions;

namespace Gatherum.Core.Services;

/// <summary>A file a console had in silicon and nobody may ship: which machine it is
/// for, what it is called there, what having it buys, and the one size it can be.</summary>
public sealed record FirmwareSpec(string Machine, string File, string Console, string Purpose, long Bytes);

/// <summary>A firmware file the catalogue knows, and what the instance holds for it.</summary>
public sealed record FirmwareStatus(FirmwareSpec Spec, StoredBlob? Stored);

/// <summary>The firmware an instance keeps for its consoles. Every console here boots
/// without any of it — the GameCube on a free IPL of its own, the Game Boy Advance on
/// the emulator's stand-in — so a file is only ever an improvement, never a requirement,
/// and the catalogue is closed: a machine takes exactly the files listed for it, at
/// exactly their size, and nothing else is stored. The files belong to the instance,
/// not to a person: they live under the storage root's own <c>.gatherum/firmware</c>,
/// where no user's root reaches, and whoever can sign in may put one there or take it
/// away, because a wiki has no operator role and the alternative is nobody.</summary>
public class FirmwareService(IFileStorage storage)
{
    /// <summary>What a console here can be given. A machine is listed only once
    /// something reads the file: a Game Boy Advance BIOS belongs here too, and cannot
    /// be listed until the libretro shim can open a file at all — until then an upload
    /// would be a file nobody reads.</summary>
    public static readonly IReadOnlyList<FirmwareSpec> Catalog =
    [
        new("gamecube", "ipl.bin", "GameCube",
            "The IPL, the console's boot ROM. The console boots without one, on the " +
            "emulator's own free replacement, and still does with one — what the file " +
            "adds is what a game finds when it reads the console's font out of it.",
            2 * 1024 * 1024),
    ];

    private const string Directory = "firmware";

    public static FirmwareSpec Spec(string machine, string file) =>
        Catalog.FirstOrDefault(s => s.Machine == machine && s.File == file)
        ?? throw new NotFoundException($"No console here takes a file called {file} for {machine}.");

    public async Task<IReadOnlyList<FirmwareStatus>> ListAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<FirmwareStatus>(Catalog.Count);
        foreach (var spec in Catalog)
            list.Add(new FirmwareStatus(spec, await storage.MeasureInstanceFileAsync(Where(spec), cancellationToken)));
        return list;
    }

    /// <summary>Keeps the file, if it is exactly the size the machine's is. A file of
    /// another size is a different file, whatever it is called.</summary>
    public async Task<StoredBlob> StoreAsync(string machine, string file, Stream content,
        CancellationToken cancellationToken = default)
    {
        var spec = Spec(machine, file);
        using var buffer = new MemoryStream();
        await CopyUpToAsync(content, buffer, spec.Bytes + 1, cancellationToken);
        if (buffer.Length != spec.Bytes)
            throw new ValidationException(
                $"{spec.Console}'s {spec.File} is {spec.Bytes:N0} bytes; this file is " +
                (buffer.Length > spec.Bytes ? "larger." : $"{buffer.Length:N0}."));
        buffer.Position = 0;
        return await storage.WriteInstanceFileAsync(Where(spec), buffer, cancellationToken);
    }

    public Task RemoveAsync(string machine, string file, CancellationToken cancellationToken = default) =>
        storage.DeleteInstanceFileAsync(Where(Spec(machine, file)), cancellationToken);

    /// <summary>The file's bytes, or null when the instance has none.</summary>
    public async Task<Stream?> OpenAsync(string machine, string file, CancellationToken cancellationToken = default)
    {
        var where = Where(Spec(machine, file));
        if (await storage.MeasureInstanceFileAsync(where, cancellationToken) is null)
            return null;
        return await storage.OpenInstanceFileAsync(where, cancellationToken);
    }

    private static string Where(FirmwareSpec spec) => $"{Directory}/{spec.Machine}/{spec.File}";

    private static async Task CopyUpToAsync(Stream from, Stream to, long limit, CancellationToken cancellationToken)
    {
        var chunk = new byte[64 * 1024];
        long total = 0;
        while (total < limit)
        {
            var read = await from.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit - total)), cancellationToken);
            if (read == 0)
                return;
            await to.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            total += read;
        }
    }
}
