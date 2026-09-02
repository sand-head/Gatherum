using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Roms;

namespace Gatherum.Core.Services;

/// <summary>The instance's console system files: what <see cref="SystemFiles"/> says a
/// console can take, and which of those this instance has. The files are the record —
/// listing them is looking at the directory — and nothing about them is in a table.
///
/// <para>Reading is any signed-in person's: the files reach a console in the reader's
/// own browser, so a player has to be able to fetch them, and a stranger reading a
/// public page does not — a boot ROM is somebody's copyrighted silicon and this instance's
/// members are who it was uploaded for. Writing is an admin's alone, because the file
/// every member's console boots from is the instance's business rather than
/// anybody's.</para></summary>
public class SystemFileService(GatherumDbContext db, IFileStorage storage)
{
    public async Task<IReadOnlyList<SystemConsoleStatus>> ListAsync(CancellationToken ct = default)
    {
        var consoles = new List<SystemConsoleStatus>();
        foreach (var console in SystemFiles.Consoles)
        {
            var files = new List<SystemFileStatus>();
            foreach (var slot in console.Files)
            {
                var stored = await storage.MeasureSystemAsync(Relative(console, slot), ct);
                files.Add(new SystemFileStatus(slot.Name, slot.Bytes, slot.Purpose,
                    stored is not null, stored?.SizeBytes, stored?.Hash));
            }
            consoles.Add(new SystemConsoleStatus(console.Key, console.Name, files));
        }
        return consoles;
    }

    /// <summary>The names present for one console — what a player fetches before the
    /// cartridge, and nothing about the ones that are not there.</summary>
    public async Task<IReadOnlyList<string>> PresentAsync(string consoleKey,
        CancellationToken ct = default)
    {
        var console = SystemFiles.FindConsole(consoleKey)
            ?? throw new NotFoundException($"No console '{consoleKey}'.");
        var present = new List<string>();
        foreach (var slot in console.Files)
        {
            if (await storage.MeasureSystemAsync(Relative(console, slot), ct) is not null)
                present.Add(slot.Name);
        }
        return present;
    }

    public async Task<Stream> OpenAsync(string consoleKey, string name, CancellationToken ct = default)
    {
        var (console, slot) = Locate(consoleKey, name);
        var relative = Relative(console, slot);
        if (await storage.MeasureSystemAsync(relative, ct) is null)
            throw new NotFoundException($"{console.Name} has no {slot.Name} uploaded.");
        return await storage.OpenSystemAsync(relative, ct);
    }

    /// <summary>Stores a file in its slot, replacing what was there. The length is the
    /// one check made: a ROM is a fixed piece of hardware, and a file of another length
    /// is something else with its name.</summary>
    public async Task<SystemFileStatus> PutAsync(Guid userId, string consoleKey, string name,
        Stream content, CancellationToken ct = default)
    {
        await EnsureAdminAsync(userId, ct);
        var (console, slot) = Locate(consoleKey, name);
        // Bounded by the slot before anything is written: the biggest slot is a boot
        // ROM at two megabytes, so the whole file fits in memory and the length can be
        // known before a byte lands on disk.
        var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length != slot.Bytes)
            throw new ValidationException(
                $"{slot.Name} is {slot.Bytes:N0} bytes; this file is {buffer.Length:N0}.");
        buffer.Position = 0;
        var stored = await storage.WriteSystemAsync(Relative(console, slot), buffer, ct);
        return new SystemFileStatus(slot.Name, slot.Bytes, slot.Purpose, true,
            stored.SizeBytes, stored.Hash);
    }

    public async Task DeleteAsync(Guid userId, string consoleKey, string name,
        CancellationToken ct = default)
    {
        await EnsureAdminAsync(userId, ct);
        var (console, slot) = Locate(consoleKey, name);
        await storage.DeleteSystemAsync(Relative(console, slot), ct);
    }

    private async Task EnsureAdminAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is not { IsAdmin: true })
            throw new ForbiddenException("Only an admin changes the console system files.");
    }

    private static (SystemConsole Console, SystemFileSlot Slot) Locate(string consoleKey, string name)
    {
        var console = SystemFiles.FindConsole(consoleKey)
            ?? throw new NotFoundException($"No console '{consoleKey}'.");
        var slot = console.Files.FirstOrDefault(f => f.Name == name)
            ?? throw new NotFoundException($"A {console.Name} takes no file called '{name}'.");
        return (console, slot);
    }

    private static string Relative(SystemConsole console, SystemFileSlot slot) =>
        $"{console.Key}/{slot.Name}";
}

public sealed record SystemConsoleStatus(string Key, string Name, IReadOnlyList<SystemFileStatus> Files);

/// <param name="Bytes">What the slot expects.</param>
/// <param name="SizeBytes">What is there, when something is.</param>
public sealed record SystemFileStatus(string Name, long Bytes, string Purpose, bool Present,
    long? SizeBytes, string? Sha256);
