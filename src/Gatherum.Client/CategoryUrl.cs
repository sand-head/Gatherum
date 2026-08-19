namespace Gatherum.Client;

/// <summary>A category path in a URL. Segments are escaped one at a time: the slashes
/// between them are structure the route keeps, not text inside a name.</summary>
public static class CategoryUrl
{
    public static string For(string path) => string.Join('/',
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    public static string Page(string path) => $"/categories/{For(path)}";
}
