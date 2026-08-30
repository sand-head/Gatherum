using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Gatherum.Core.Markdown;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Services;

/// <summary>
/// Collaborative collectible lists: what exists to collect, and what each person has.
///
/// The two are separate documents on purpose. The catalogue is a page with a
/// <c>:::collection</c> fence on it — one author, occasionally edited, shared once. A
/// tally is a page per person with a fence tracking that catalogue — its owner's file,
/// under its owner's root, carrying its owner's <see cref="AccessMode"/>. So nobody's
/// sharing gesture publishes anybody else's ticks, and the aggregate is nothing more
/// than "the tallies this reader may enumerate", which
/// <see cref="INodeAuthorizer.VisibleTo"/> already answers.
///
/// A column in a grid is enumeration, so it is <c>VisibleTo</c> throughout and never
/// <c>CanSee</c>: an unlisted tally must not appear in a column merely because somebody
/// holds its link.
/// </summary>
public class CollectionService(
    GatherumDbContext db,
    INodeAuthorizer authorizer,
    NodeService nodes,
    FileService files)
{
    /// <summary>Where a tally lands when somebody ticks their first item. A page, like
    /// everything else — the tree has one kind of thing in it.</summary>
    public const string TallyFolder = "Collections";

    /// <summary>A catalogue's rows with every visible tally's ticks against them.
    /// <paramref name="nodeId"/> is whichever page the reader is looking at: a catalogue
    /// aggregates itself, a tally aggregates the catalogue it tracks, and both answer
    /// with the same grid.</summary>
    public async Task<CollectionView> GetAsync(Guid? userId, Guid nodeId, string? list = null,
        CancellationToken ct = default)
    {
        var (catalogue, declared) = await ResolveAsync(userId, nodeId, list, ct);
        var rows = Rows(declared.Items, parent: null);
        var leaves = rows.SelectMany(Leaves).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);

        var columns = new List<CollectionColumn>();
        foreach (var tally in await TalliesAsync(userId, catalogue.Id, declared, ct))
        {
            var read = Reconcile(declared.Items, tally.Tracking.Items, parent: null);
            columns.Add(new CollectionColumn(tally.Id, tally.OwnerId, tally.DisplayName,
                userId is { } viewer && tally.OwnerId == viewer,
                tally.Access, read.Held, Orphans(read.Orphans, ""),
                read.Held.Count(leaves.Contains)));
        }
        // The reader's own column first, then the fullest, then by name: a grid is read
        // to find out how you are doing, and then how everybody else is.
        columns.Sort((a, b) => a.IsViewer != b.IsViewer
            ? a.IsViewer ? -1 : 1
            : a.Count != b.Count
                ? b.Count - a.Count
                : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        return new CollectionView(catalogue.Id, catalogue.Title, declared.Argument, rows,
            columns, columns.FirstOrDefault(c => c.IsViewer)?.TallyId, userId is not null,
            leaves.Count);
    }

    /// <summary>Records one collectible against the caller's own tally, writing their
    /// file into being the first time they tick anything. Never anybody else's: a tally
    /// is a node, and a node is written by its owner.</summary>
    public async Task<CollectionView> SetAsync(Guid userId, Guid nodeId, string rowKey,
        bool collected, string? list = null, CancellationToken ct = default)
    {
        var (catalogue, declared) = await ResolveAsync(userId, nodeId, list, ct);
        var rows = Rows(declared.Items, parent: null);
        if (!rows.SelectMany(Leaves).Any(r => r.Key == rowKey))
            throw new NotFoundException($"Nothing in this list is keyed '{rowKey}'.");

        var mine = (await TalliesAsync(userId, catalogue.Id, declared, ct))
            .FirstOrDefault(t => t.OwnerId == userId);
        // A tally names its catalogue by id, not by title: an id is permission, so the
        // spelling that works for an unlisted catalogue is the one written by default.
        // A tally that already exists keeps whatever spelling its owner chose.
        var argument = mine?.Tracking.Argument
            ?? $"[{catalogue.Title}](node://{catalogue.Id})"
                + (MoreThanOneList(catalogue) && declared.Argument.Length > 0
                    ? $" {declared.Argument}"
                    : "");

        var read = mine is null
            ? new TallyReading(new HashSet<string>(StringComparer.Ordinal), [],
                new Dictionary<string, string>(StringComparer.Ordinal))
            : Reconcile(declared.Items, mine.Tracking.Items, parent: null);
        if (collected)
            read.Held.Add(rowKey);
        else
            read.Held.Remove(rowKey);

        var fence = CollectionSyntax.Write(argument,
            [.. Mirror(declared.Items, parent: null, read.Held, read.Notes), .. read.Orphans],
            ticked: true);

        if (mine is null)
        {
            var folder = await FolderAsync(userId, ct);
            await files.CreateTextNodeAsync(userId, folder.Id, catalogue.Title, fence + "\n", ct: ct);
        }
        else
        {
            await files.SaveTextAsync(userId, mine.Id,
                CollectionSyntax.Replace(mine.Body, mine.Tracking, fence), ct);
        }
        db.ChangeTracker.Clear();
        return await GetAsync(userId, nodeId, list, ct);
    }

    /// <summary>Which catalogue a page means, and the fence on it that declares the list.
    /// A page that declares one is its own catalogue; a page that tracks one names
    /// another node — by id, which is permission, or by title, which is a search.</summary>
    private async Task<(Node Catalogue, CollectionBlock Declared)> ResolveAsync(Guid? userId,
        Guid nodeId, string? list, CancellationToken ct)
    {
        var node = await nodes.GetWithBodyAsync(userId, nodeId, ct);
        var block = CollectionSyntax.Find(Body(node), list)
            ?? throw new NotFoundException($"Node {nodeId} has no collection list.");
        if (block.Declares)
            return (node, block);

        var target = block.Tracks!;
        var catalogueId = target.NodeId;
        if (catalogueId is null && target.Title is { Length: > 0 } title)
        {
            var resolved = await nodes.ResolveTitlesAsync(userId, [title], ct);
            catalogueId = resolved.TryGetValue(title, out var found) ? found : null;
        }
        if (catalogueId is not { } id)
            throw new NotFoundException($"Node {nodeId} tracks a list nothing here declares.");

        var catalogue = await nodes.GetWithBodyAsync(userId, id, ct);
        var declared = Declared(catalogue, target.List)
            ?? throw new NotFoundException($"Node {id} declares no such collection list.");
        return (catalogue, declared);
    }

    /// <summary>Every tally of this catalogue the reader may enumerate, each with the
    /// fence that does the tracking. A tally is found by the link its fence already
    /// made — naming a node is what put the row in <c>NodeLinks</c> — and confirmed by
    /// reading the fence, so a page that merely mentions the catalogue is not somebody's
    /// column.</summary>
    private async Task<List<Tally>> TalliesAsync(Guid? userId,
        Guid catalogueId, CollectionBlock declared, CancellationToken ct)
    {
        // The head version's text and nothing else: a tally is rewritten on every tick, so
        // materializing its whole history to read one body would make the commonest page
        // view in this feature the most expensive one.
        var candidates = await authorizer.VisibleTo(db.Nodes, userId)
            .Where(n => n.MediaType == MediaTypes.Markdown && n.Id != catalogueId
                && n.File!.Versions.Any()
                && n.OutboundLinks.Any(l => l.TargetId == catalogueId))
            .Select(n => new
            {
                n.Id,
                n.OwnerId,
                n.Access,
                Owner = n.Owner!.DisplayName.Length > 0 ? n.Owner.DisplayName : n.Owner.Username,
                Body = n.File!.Versions.OrderByDescending(v => v.Number).First().ExtractedText,
            })
            .ToListAsync(ct);

        var byTitle = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var titles = candidates
            .SelectMany(n => CollectionSyntax.Read(n.Body))
            .Select(b => b.Tracks?.Title)
            .Where(t => t is { Length: > 0 })
            .Select(t => t!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (titles.Count > 0)
            byTitle = new Dictionary<string, Guid>(
                await nodes.ResolveTitlesAsync(userId, titles, ct), StringComparer.OrdinalIgnoreCase);

        var tallies = new List<Tally>();
        foreach (var node in candidates)
        {
            foreach (var block in CollectionSyntax.Read(node.Body))
            {
                if (block.Tracks is not { } target)
                    continue;
                var named = target.NodeId
                    ?? (target.Title is { Length: > 0 } title && byTitle.TryGetValue(title, out var id)
                        ? id
                        : null);
                if (named != catalogueId || !SameList(target.List, declared.Argument))
                    continue;
                tallies.Add(new Tally(node.Id, node.OwnerId, node.Owner, node.Access, node.Body,
                    block));
                break;
            }
        }
        return tallies;
    }

    /// <summary>A tally that names no list follows the catalogue's first, which is the
    /// only one most pages have.</summary>
    private static bool SameList(string tracked, string declared) =>
        tracked.Length == 0
        || string.Equals(CollectionSyntax.Words(tracked), CollectionSyntax.Words(declared),
            StringComparison.OrdinalIgnoreCase);

    private static CollectionBlock? Declared(Node catalogue, string list)
    {
        var blocks = CollectionSyntax.Read(Body(catalogue)).Where(b => b.Declares);
        return list.Length == 0
            ? blocks.FirstOrDefault()
            : blocks.FirstOrDefault(b => SameList(list, b.Argument));
    }

    private static bool MoreThanOneList(Node catalogue) =>
        CollectionSyntax.Read(Body(catalogue)).Count(b => b.Declares) > 1;

    private static string Body(Node node) =>
        node.File is { Versions.Count: > 0 } file ? file.Current.ExtractedText : "";

    /// <summary>The catalogue's lines as the grid's rows. A row's key is the id it links
    /// or the text it says, and a variant's is its parent's and its own — which is what
    /// makes "Sonic's Gold" nameable without "Gold" colliding with every other item's.</summary>
    private static List<CollectionRow> Rows(IReadOnlyList<CollectionEntry> items, string? parent) =>
        [.. items.Select(item =>
        {
            var key = KeyOf(item, parent);
            return new CollectionRow(key, item.Text, item.NodeId, item.Note,
                Rows(item.Variants, key));
        })];

    private static string KeyOf(CollectionEntry item, string? parent)
    {
        var own = item.NodeId is { } id ? $"node:{id:N}" : $"text:{CollectionSyntax.Normalize(item.Label)}";
        return parent is null ? own : $"{parent}/{own}";
    }

    /// <summary>The rows a tick can actually be made against: an item with variants is
    /// a group, and "give me all three" is a different statement from the three ticks it
    /// would stand in for.</summary>
    private static IEnumerable<CollectionRow> Leaves(CollectionRow row) =>
        row.Variants.Count == 0 ? [row] : row.Variants.SelectMany(Leaves);

    /// <summary>Reads one tally against the catalogue: which rows it holds, what it
    /// noted about each, and the ticks that no longer match anything. An orphan is kept
    /// whole rather than dropped — Alice cannot rewrite Bob's file to follow her rename,
    /// so the ticks simply stop matching, and silence is the one unacceptable answer.</summary>
    private static TallyReading Reconcile(IReadOnlyList<CollectionEntry> catalogue,
        IReadOnlyList<CollectionEntry> tally, string? parent)
    {
        var reading = new TallyReading(new HashSet<string>(StringComparer.Ordinal), [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        foreach (var mine in tally)
        {
            var match = catalogue.FirstOrDefault(c => CollectionSyntax.Matches(c, mine));
            if (match is null)
            {
                if (mine.Checked || mine.Variants.Any(v => v.Checked))
                    reading.Orphans.Add(mine);
                continue;
            }
            var key = KeyOf(match, parent);
            if (mine.Note.Length > 0)
                reading.Notes.TryAdd(key, mine.Note);
            if (match.Variants.Count == 0)
            {
                if (mine.Checked)
                    reading.Held.Add(key);
                continue;
            }
            var nested = Reconcile(match.Variants, mine.Variants, key);
            reading.Held.UnionWith(nested.Held);
            foreach (var (noted, note) in nested.Notes)
                reading.Notes.TryAdd(noted, note);
            // A variant nobody recognizes is kept under the item it was written under, so
            // appending it back preserves which sprite the tick was about.
            if (nested.Orphans.Count > 0)
                reading.Orphans.Add(mine with { Checked = false, Variants = nested.Orphans });
        }
        return reading;
    }

    /// <summary>Orphaned ticks flattened for reporting, each said the way its file says
    /// it — a variant under the item it hangs from.</summary>
    private static List<CollectionOrphan> Orphans(IReadOnlyList<CollectionEntry> entries,
        string parent)
    {
        var flat = new List<CollectionOrphan>();
        foreach (var entry in entries)
        {
            var text = parent.Length > 0 ? $"{parent} — {entry.Text}" : entry.Text;
            var nested = Orphans(entry.Variants, text);
            if (nested.Count > 0)
                flat.AddRange(nested);
            else if (entry.Checked)
                flat.Add(new CollectionOrphan(text, entry.Note));
        }
        return flat;
    }

    /// <summary>The tally's fence rebuilt from the catalogue it tracks: the catalogue's
    /// current wording and links, this person's ticks and notes. Adopting the catalogue's
    /// labels is what carries a promotion — an item that gained a page — into a tally
    /// without anybody having to edit it.</summary>
    private static List<CollectionEntry> Mirror(IReadOnlyList<CollectionEntry> catalogue,
        string? parent, IReadOnlySet<string> held, IReadOnlyDictionary<string, string> notes)
    {
        var mirrored = new List<CollectionEntry>();
        foreach (var item in catalogue)
        {
            var key = KeyOf(item, parent);
            var variants = Mirror(item.Variants, key, held, notes);
            mirrored.Add(new CollectionEntry(item.Label, item.NodeId, item.Text,
                notes.GetValueOrDefault(key, ""),
                item.Variants.Count == 0 ? held.Contains(key) : variants.All(v => v.Checked),
                variants));
        }
        return mirrored;
    }

    /// <summary>One person's column: their page as the grid needs it, and the fence on
    /// it that does the tracking.</summary>
    private sealed record Tally(Guid Id, Guid OwnerId, string DisplayName, AccessMode Access,
        string Body, CollectionBlock Tracking);

    /// <summary>What one tally says once it has been read against the catalogue.</summary>
    private sealed record TallyReading(HashSet<string> Held, List<CollectionEntry> Orphans,
        Dictionary<string, string> Notes);

    private async Task<Node> FolderAsync(Guid userId, CancellationToken ct)
    {
        var existing = await db.Nodes.FirstOrDefaultAsync(
            n => n.OwnerId == userId && n.ParentId == null && n.Title == TallyFolder
                && n.MediaType == MediaTypes.Markdown, ct);
        return existing ?? await files.CreateTextNodeAsync(userId, null, TallyFolder,
            "The lists I am collecting against.\n", ct: ct);
    }
}

/// <summary>A catalogue and everybody's ticks against it — the whole of what a grid
/// draws, decided on the server so no two surfaces can disagree about who has what.</summary>
public sealed record CollectionView(
    Guid CatalogueId,
    string CatalogueTitle,
    string List,
    IReadOnlyList<CollectionRow> Rows,
    IReadOnlyList<CollectionColumn> Columns,
    Guid? TallyId,
    bool CanTick,
    int Collectibles);

/// <summary>One line of the catalogue, with the variants nested under it.</summary>
public sealed record CollectionRow(string Key, string Text, Guid? NodeId, string Note,
    IReadOnlyList<CollectionRow> Variants);

/// <summary>One participant's column: their tally, what it holds, what it holds that the
/// catalogue no longer has, and who may see it — because a tally is private until its
/// owner says otherwise, and a column nobody else can read should say so.</summary>
public sealed record CollectionColumn(
    Guid TallyId,
    Guid OwnerId,
    string DisplayName,
    bool IsViewer,
    AccessMode Access,
    IReadOnlySet<string> Held,
    IReadOnlyList<CollectionOrphan> Orphans,
    int Count);

/// <summary>A tick that no longer matches an item — kept in the file, shown in the
/// grid, and never quietly dropped.</summary>
public sealed record CollectionOrphan(string Text, string Note);
