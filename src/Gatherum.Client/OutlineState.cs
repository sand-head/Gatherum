namespace Gatherum.Client;

/// <summary>The sidebar's view of the open article's headings. The editor island
/// publishes them and answers jump requests; the sidebar renders them. The two are
/// separate islands sharing one circuit or one WebAssembly runtime, the same way
/// TreeState is shared.</summary>
public sealed class OutlineState
{
    private object? owner;

    public IReadOnlyList<OutlineEntry> Entries { get; private set; } = [];
    public event Action? Changed;
    public event Func<OutlineEntry, Task>? JumpRequested;

    public void Publish(object publisher, IReadOnlyList<OutlineEntry> entries)
    {
        if (owner == publisher && entries.SequenceEqual(Entries))
            return;
        owner = publisher;
        Entries = entries;
        Changed?.Invoke();
    }

    /// <summary>Clears only the publisher's own entries: when navigating between two
    /// articles the outgoing editor can dispose after the incoming one has published,
    /// and must not wipe the new outline.</summary>
    public void Clear(object publisher)
    {
        if (owner != publisher)
            return;
        owner = null;
        Entries = [];
        Changed?.Invoke();
    }

    public Task JumpAsync(OutlineEntry entry) =>
        JumpRequested?.Invoke(entry) ?? Task.CompletedTask;
}

/// <summary>One heading. <see cref="Block"/> is the rich document's block index in
/// document mode, or the line index in source mode — whichever the publisher uses
/// to find the heading again when a jump comes back.</summary>
public record OutlineEntry(int Block, int Level, string Text);
