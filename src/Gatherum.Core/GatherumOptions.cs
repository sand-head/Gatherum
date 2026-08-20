namespace Gatherum.Core;

public class GatherumOptions
{
    public const string Section = "Gatherum";

    public DatabaseOptions Database { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public OidcOptions Oidc { get; set; } = new();
    public AnalysisOptions Analysis { get; set; } = new();
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

    public bool IsConfigured => Authority.Length > 0 && ClientId.Length > 0;
}

/// <summary>Where uploaded media goes to be read, heard, and described. Everything here
/// points at a model *you* run: Gatherum ships with this off, and with no endpoint set
/// nothing is ever sent anywhere — images keep indexing as bare EXIF and recordings as
/// nothing at all, exactly as before.</summary>
public class AnalysisOptions
{
    /// <summary>Base URL of an OpenAI-compatible API — llama.cpp's server, or anything
    /// else speaking that shape. The <c>/chat/completions</c> path is appended.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Sent as a bearer token when set. Local runners usually want no key.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The model that looks at images and writes summaries.</summary>
    public string Model { get; set; } = "";

    /// <summary>The model that listens, when the one above has no ears. Defaults to
    /// <see cref="Model"/>, which is the right answer for an any-to-any model.</summary>
    public string AudioModel { get; set; } = "";

    /// <summary>Ceiling on one analysis call. Transcribing an hour of video is minutes
    /// of work, so this is generous by default.</summary>
    public int TimeoutSeconds { get; set; } = 900;

    /// <summary>Media past this size is refused rather than base64'd into a request
    /// body — the upload still succeeds and still stores, it just goes undescribed.</summary>
    public long MaxBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Frames sampled across a video for the summary. Zero describes it from
    /// the transcript alone, which is the cheap setting for a text-only model.</summary>
    public int VideoFrames { get; set; } = 4;

    /// <summary>How to invoke ffmpeg, which is what takes a video apart into an audio
    /// track and frames. Absent, video uploads record a failure and stay searchable by
    /// title and tags; images and audio are unaffected.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>On the first start after an endpoint is configured, queue the media
    /// already in the tree — the photos and recordings uploaded back when nothing could
    /// read them. Off, only new uploads are ever analyzed.</summary>
    public bool BackfillExisting { get; set; } = true;

    public string AudioModelOrDefault => AudioModel.Length > 0 ? AudioModel : Model;

    public bool IsConfigured => Endpoint.Length > 0 && Model.Length > 0;
}
