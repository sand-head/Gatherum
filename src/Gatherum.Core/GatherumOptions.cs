namespace Gatherum.Core;

public class GatherumOptions
{
    public const string Section = "Gatherum";

    public DatabaseOptions Database { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public OidcOptions Oidc { get; set; } = new();
}

public class DatabaseOptions
{
    public string ConnectionString { get; set; } =
        "Host=localhost;Database=gatherum;Username=gatherum;Password=gatherum";
    public bool Migrate { get; set; } = true;
}

public class StorageOptions
{
    public string Root { get; set; } = "data/files";
}

public class OidcOptions
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Scopes { get; set; } = "openid profile email";
    public bool RequestOfflineAccess { get; set; }
}
