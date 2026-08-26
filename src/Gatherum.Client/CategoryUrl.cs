namespace Gatherum.Client;

/// <summary>A category's readable URL. A category is a node, so it has a
/// <c>/nodes/{id}</c> like everything else — but a wiki's categories are the one place
/// a reader types the address, and a name is what they would type. Names are unique
/// among categories, which is what makes this addressable at all.</summary>
public static class CategoryUrl
{
    public static string For(string name) => Uri.EscapeDataString(name);

    public static string Page(string name) => $"/categories/{For(name)}";
}
