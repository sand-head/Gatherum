namespace Gatherum.Core.Domain;

public class Tag
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public List<NodeTag> Nodes { get; init; } = [];
}

public class NodeTag
{
    public Guid NodeId { get; init; }
    public Node? Node { get; init; }
    public Guid TagId { get; init; }
    public Tag? Tag { get; init; }
}
