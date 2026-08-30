using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Gatherum.Core.Markdown;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Services;

/// <summary>
/// Collaborative collectible lists: what exists to collect, and what each person has.
///
/// The two are separate documents on purpose. The catalog is a page with a
/// <c>:::collection</c> fence on it — one author, occasionally edited, shared once. A
/// tally is a page per person with a fence tracking that catalog: its owner's file,
/// under its owner's root, written by nobody else.
///
/// <b>The catalog's audience is the grid's audience.</b> Whoever may read the list may
/// see everyone's answers against it — answering is joining in, and a shared list whose
/// participants each had to publish a second page before their column counted would be
/// asking for a permission nobody meant to withhold. So authorization happens once, at
/// the door this service already knocks on: <see cref="ResolveAsync"/> reads the
/// catalog through <see cref="NodeService.GetWithBodyAsync"/>, which is
/// <see cref="INodeAuthorizer"/>'s answer, and a reader who got past it gets the whole
/// grid. Nothing here re-asks a visibility question, and nothing here spells one.
///
/// What that does <em>not</em> do is publish the tally page. Its own
/// <see cref="AccessMode"/> is untouched and still governs the node — whether it opens
/// at its own URL, whether it is in anybody's tree, whether search finds it — so a tally
/// stays private as a page while the answers on it count in the list they were made
/// against. The exposure is exactly the row keys somebody answered and the name they answer
/// under; the notes and orphans in their file are their own.
/// </summary>
public class CollectionService(
    GatherumDbContext db,
    NodeService nodes,
    FileService files)
{
    /// <summary>Where a tally lands when somebody answers their first item. A page, like
    /// everything else — the tree has one kind of thing in it.</summary>
    public const string TallyFolder = "Collections";

    /// <summary>A catalog's rows with every visible tally's answers against them.
    /// <paramref name="nodeId"/> is whichever page the reader is looking at: a catalog
    /// aggregates itself, a tally aggregates the catalog it tracks, and both answer
    /// with the same grid.</summary>
    public async Task<CollectionView> GetAsync(Guid? userId, Guid nodeId, string? list = null,
        CancellationToken ct = default)
    {
        var (catalog, declared) = await ResolveAsync(userId, nodeId, list, ct);
        var rows = Rows(declared.Items, parent: null);
        var leaves = rows.SelectMany(Leaves).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);

        var columns = new List<CollectionColumn>();
        var answers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tally in await TalliesAsync(catalog, declared, ct))
        {
            var read = Reconcile(declared.Items, tally.Tracking.Items, parent: null);
            var isViewer = userId is { } viewer && tally.OwnerId == viewer;
            foreach (var key in read.Held)
                answers[key] = answers.GetValueOrDefault(key) + 1;
            // Orphans are their owner's business — only they can act on one, and the
            // catalog's readers were shown answers, not the state of somebody's file.
            columns.Add(new CollectionColumn(tally.Id, tally.OwnerId, tally.DisplayName,
                isViewer, read.Held, isViewer ? Orphans(read.Orphans, "") : [],
                read.Held.Count(leaves.Contains)));
        }
        var participants = columns.Count;

        // Every row's total is counted before anybody's column is withheld, so a secret
        // ballot still reports honestly: the tally is of everyone who answered, not of
        // whoever this reader is allowed to see.
        rows = WithAnswers(rows, answers);

        if (!CollectionSyntax.NamesAnswers(declared.Word))
        {
            // A poll reports how many, never who — and it withholds them here rather than
            // in the markup, because a name the response still carries is a name anybody
            // can read. The reader keeps their own column: they have to see their answer
            // to change it, and it was never a secret from them.
            columns.RemoveAll(c => !c.IsViewer);
        }

        // The reader's own column first, then the fullest, then by name: a grid is read
        // to find out how you are doing, and then how everybody else is.
        columns.Sort((a, b) => a.IsViewer != b.IsViewer
            ? a.IsViewer ? -1 : 1
            : a.Count != b.Count
                ? b.Count - a.Count
                : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        return new CollectionView(catalog.Id, catalog.Title, declared.Word,
            declared.Argument, rows, columns, participants,
            columns.FirstOrDefault(c => c.IsViewer)?.TallyId, userId is not null,
            leaves.Count);
    }

    /// <summary>Records one collectible against the caller's own tally, writing their
    /// file into being the first time they answer anything. Never anybody else's: a tally
    /// is a node, and a node is written by its owner.</summary>
    public async Task<CollectionView> SetAsync(Guid userId, Guid nodeId, string rowKey,
        bool collected, string? list = null, CancellationToken ct = default)
    {
        var (catalog, declared) = await ResolveAsync(userId, nodeId, list, ct);
        var rows = Rows(declared.Items, parent: null);
        if (!rows.SelectMany(Leaves).Any(r => r.Key == rowKey))
            throw new NotFoundException($"Nothing in this list is keyed '{rowKey}'.");

        var mine = (await TalliesAsync(catalog, declared, ct))
            .FirstOrDefault(t => t.OwnerId == userId);
        // A tally names its catalog by id, not by title: an id is permission, so the
        // spelling that works for an unlisted catalog is the one written by default.
        // A tally that already exists keeps whatever spelling its owner chose.
        var argument = mine?.Tracking.Argument
            ?? $"[{catalog.Title}](node://{catalog.Id})"
                + (MoreThanOneList(catalog) && declared.Argument.Length > 0
                    ? $" {declared.Argument}"
                    : "");

        var read = mine is null
            ? new TallyReading(new HashSet<string>(StringComparer.Ordinal), [],
                new Dictionary<string, string>(StringComparer.Ordinal))
            : Reconcile(declared.Items, mine.Tracking.Items, parent: null);
        if (!collected)
        {
            read.Held.Remove(rowKey);
        }
        else if (CollectionSyntax.PicksOne(declared.Word))
        {
            // One answer each: picking is moving, not adding. Enforced here rather than
            // in a component because the file is what anybody else reads, and a tally
            // saying somebody picked two would be wrong wherever it was opened.
            read.Held.Clear();
            read.Held.Add(rowKey);
        }
        else
        {
            read.Held.Add(rowKey);
        }

        // The tally opens with the catalog's own word, so a list of nights reads as
        // nights on both pages rather than as a collection of them.
        var fence = CollectionSyntax.Write(mine?.Tracking.Word ?? declared.Word, argument,
            [.. Mirror(declared.Items, parent: null, read.Held, read.Notes), .. read.Orphans],
            answered: true);

        if (mine is null)
        {
            var folder = await FolderAsync(userId, ct);
            await files.CreateTextNodeAsync(userId, folder.Id, catalog.Title, fence + "\n", ct: ct);
        }
        else
        {
            await files.SaveTextAsync(userId, mine.Id,
                CollectionSyntax.Replace(mine.Body, mine.Tracking, fence), ct);
        }
        db.ChangeTracker.Clear();
        return await GetAsync(userId, nodeId, list, ct);
    }

    /// <summary>Which catalog a page means, and the fence on it that declares the list.
    /// A page that declares one is its own catalog; a page that tracks one names
    /// another node — by id, which is permission, or by title, which is a search.</summary>
    private async Task<(Node Catalog, CollectionBlock Declared)> ResolveAsync(Guid? userId,
        Guid nodeId, string? list, CancellationToken ct)
    {
        var node = await nodes.GetWithBodyAsync(userId, nodeId, ct);
        var block = CollectionSyntax.Find(Body(node), list)
            ?? throw new NotFoundException($"Node {nodeId} has no collection list.");
        if (block.Declares)
            return (node, block);

        var target = block.Tracks!;
        var catalogId = target.NodeId;
        if (catalogId is null && target.Title is { Length: > 0 } title)
        {
            var resolved = await nodes.ResolveTitlesAsync(userId, [title], ct);
            catalogId = resolved.TryGetValue(title, out var found) ? found : null;
        }
        if (catalogId is not { } id)
            throw new NotFoundException($"Node {nodeId} tracks a list nothing here declares.");

        var catalog = await nodes.GetWithBodyAsync(userId, id, ct);
        var declared = Declared(catalog, target.List)
            ?? throw new NotFoundException($"Node {id} declares no such collection list.");
        return (catalog, declared);
    }

    /// <summary>Every tally of this catalog — all of them, because the reader already
    /// proved they may read the list and that is the only question there is here. A tally
    /// is found by the link its fence already made, since naming a node is what put the
    /// row in <c>NodeLinks</c>, and confirmed by reading the fence, so a page that merely
    /// mentions the catalog is not somebody's column.</summary>
    private async Task<List<Tally>> TalliesAsync(Node catalog, CollectionBlock declared,
        CancellationToken ct)
    {
        // The head version's text and nothing else: a tally is rewritten on every answer, so
        // materializing its whole history to read one body would make the commonest page
        // view in this feature the most expensive one.
        var candidates = await db.Nodes
            .Where(n => n.MediaType == MediaTypes.Markdown && n.Id != catalog.Id
                && n.File!.Versions.Any()
                && n.OutboundLinks.Any(l => l.TargetId == catalog.Id))
            .Select(n => new
            {
                n.Id,
                n.OwnerId,
                Owner = n.Owner!.DisplayName.Length > 0 ? n.Owner.DisplayName : n.Owner.Username,
                Body = n.File!.Versions.OrderByDescending(v => v.Number).First().ExtractedText,
            })
            .ToListAsync(ct);

        var tallies = new List<Tally>();
        foreach (var node in candidates)
        {
            foreach (var block in CollectionSyntax.Read(node.Body))
            {
                if (block.Tracks is not { } target || !Names(target, catalog)
                    || !SameList(target.List, declared.Argument))
                    continue;
                tallies.Add(new Tally(node.Id, node.OwnerId, node.Owner, node.Body, block));
                break;
            }
        }
        return tallies;
    }

    /// <summary>Whether a tracking fence names this catalog. An id says so outright; a
    /// title is checked against the catalog's own rather than resolved, because the
    /// resolution that matters already happened — the page is a backlink at all only
    /// because its author's <c>[[title]]</c> found this node when they saved. Asking again
    /// here would answer with the <em>reader's</em> search instead of the writer's, which
    /// is a different question and the wrong one.</summary>
    private static bool Names(CollectionTarget target, Node catalog) =>
        target.NodeId is { } id
            ? id == catalog.Id
            : string.Equals(target.Title, catalog.Title, StringComparison.OrdinalIgnoreCase);

    /// <summary>A tally that names no list follows the catalog's first, which is the
    /// only one most pages have.</summary>
    private static bool SameList(string tracked, string declared) =>
        tracked.Length == 0
        || string.Equals(CollectionSyntax.Words(tracked), CollectionSyntax.Words(declared),
            StringComparison.OrdinalIgnoreCase);

    private static CollectionBlock? Declared(Node catalog, string list)
    {
        var blocks = CollectionSyntax.Read(Body(catalog)).Where(b => b.Declares);
        return list.Length == 0
            ? blocks.FirstOrDefault()
            : blocks.FirstOrDefault(b => SameList(list, b.Argument));
    }

    private static bool MoreThanOneList(Node catalog) =>
        CollectionSyntax.Read(Body(catalog)).Count(b => b.Declares) > 1;

    private static string Body(Node node) =>
        node.File is { Versions.Count: > 0 } file ? file.Current.ExtractedText : "";

    /// <summary>The catalog's lines as the grid's rows. A row's key is the id it links
    /// or the text it says, and a variant's is its parent's and its own — which is what
    /// makes "Sonic's Gold" nameable without "Gold" colliding with every other item's.</summary>
    private static List<CollectionRow> Rows(IReadOnlyList<CollectionEntry> items, string? parent) =>
        [.. items.Select(item =>
        {
            var key = KeyOf(item, parent);
            return new CollectionRow(key, item.Text, item.NodeId, item.Note,
                Rows(item.Variants, key));
        })];

    /// <summary>The rows again, each carrying how many people answered yes to it. Counted
    /// on the server because it is the one number a reader cannot check by looking at the
    /// columns — on a poll there are no columns to check it against.</summary>
    private static List<CollectionRow> WithAnswers(IReadOnlyList<CollectionRow> rows,
        IReadOnlyDictionary<string, int> answers) =>
        [.. rows.Select(row => row with
        {
            Answers = answers.GetValueOrDefault(row.Key),
            Variants = WithAnswers(row.Variants, answers),
        })];

    private static string KeyOf(CollectionEntry item, string? parent)
    {
        var own = item.NodeId is { } id ? $"node:{id:N}" : $"text:{CollectionSyntax.Normalize(item.Label)}";
        return parent is null ? own : $"{parent}/{own}";
    }

    /// <summary>The rows an answer can actually be made against: an item with variants is
    /// a group, and "give me all three" is a different statement from the three answers it
    /// would stand in for.</summary>
    private static IEnumerable<CollectionRow> Leaves(CollectionRow row) =>
        row.Variants.Count == 0 ? [row] : row.Variants.SelectMany(Leaves);

    /// <summary>Reads one tally against the catalog: which rows it holds, what it
    /// noted about each, and the answers that no longer match anything. An orphan is kept
    /// whole rather than dropped — Alice cannot rewrite Bob's file to follow her rename,
    /// so the answers simply stop matching, and silence is the one unacceptable answer.</summary>
    private static TallyReading Reconcile(IReadOnlyList<CollectionEntry> catalog,
        IReadOnlyList<CollectionEntry> tally, string? parent)
    {
        var reading = new TallyReading(new HashSet<string>(StringComparer.Ordinal), [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        foreach (var mine in tally)
        {
            var match = catalog.FirstOrDefault(c => CollectionSyntax.Matches(c, mine));
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
            // appending it back preserves which sprite the answer was about.
            if (nested.Orphans.Count > 0)
                reading.Orphans.Add(mine with { Checked = false, Variants = nested.Orphans });
        }
        return reading;
    }

    /// <summary>Orphaned answers flattened for reporting, each said the way its file says
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

    /// <summary>The tally's fence rebuilt from the catalog it tracks: the catalog's
    /// current wording and links, this person's answers and notes. Adopting the catalog's
    /// labels is what carries a promotion — an item that gained a page — into a tally
    /// without anybody having to edit it.</summary>
    private static List<CollectionEntry> Mirror(IReadOnlyList<CollectionEntry> catalog,
        string? parent, IReadOnlySet<string> held, IReadOnlyDictionary<string, string> notes)
    {
        var mirrored = new List<CollectionEntry>();
        foreach (var item in catalog)
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
    private sealed record Tally(Guid Id, Guid OwnerId, string DisplayName, string Body,
        CollectionBlock Tracking);

    /// <summary>What one tally says once it has been read against the catalog.</summary>
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

/// <summary>A catalog and everybody's answers against it — the whole of what a grid
/// draws, decided on the server so no two surfaces can disagree about who has what.</summary>
public sealed record CollectionView(
    Guid CatalogId,
    string CatalogTitle,
    /// <summary>The word the catalog's fence opened with — which question this list
    /// asks, and so which words a reading view puts around it. The catalog's, never the
    /// tally's: a grid read from either page says the same thing.</summary>
    string Kind,
    string List,
    IReadOnlyList<CollectionRow> Rows,
    IReadOnlyList<CollectionColumn> Columns,
    /// <summary>How many people have answered this list at all — which is not
    /// <c>Columns.Count</c> on a list that reports totals without naming anybody.</summary>
    int Participants,
    Guid? TallyId,
    bool CanAnswer,
    int Collectibles);

/// <summary>One line of the catalog, with the variants nested under it, and how many
/// people said yes to it. <c>Answers</c> counts everybody who did, whether or not this
/// reader is shown which of them.</summary>
public sealed record CollectionRow(string Key, string Text, Guid? NodeId, string Note,
    IReadOnlyList<CollectionRow> Variants, int Answers = 0);

/// <summary>One participant's column: their tally, and what it holds. <c>Orphans</c> —
/// answers the catalog no longer has an item for — is filled in for the reader's own
/// column and empty for everybody else's, because only its owner can do anything about
/// one.</summary>
public sealed record CollectionColumn(
    Guid TallyId,
    Guid OwnerId,
    string DisplayName,
    bool IsViewer,
    IReadOnlySet<string> Held,
    IReadOnlyList<CollectionOrphan> Orphans,
    int Count);

/// <summary>An answer that no longer matches an item — kept in the file, shown in the
/// grid, and never quietly dropped.</summary>
public sealed record CollectionOrphan(string Text, string Note);
