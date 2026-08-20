namespace Gatherum.Core;

public class GatherumOptions
{
    public const string Section = "Gatherum";

    public DatabaseOptions Database { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public OidcOptions Oidc { get; set; } = new();
    public AnalysisOptions Analysis { get; set; } = new();
    public EmbeddingOptions Embedding { get; set; } = new();
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

    /// <summary>Whether an endpoint of your own is configured. The packaged model needs
    /// no configuration at all, so this is not the same question as "is semantic search
    /// on" — see <c>AddEmbedding</c>, which decides that.</summary>
    public bool IsConfigured => Endpoint.Length > 0 && Model.Length > 0;
}

/// <summary>Where text goes to be turned into a vector, so search can answer a question
/// nobody spelled the way the page did. Unlike analysis, this needs nothing of you:
/// Gatherum ships with a small embedding model and runs it in-process, so semantic search
/// works out of the box. Point <see cref="Endpoint"/> at a better model you run and it
/// takes over; turn <see cref="Local"/> off with no endpoint set and search is the
/// full-text search it has always been.</summary>
public class EmbeddingOptions
{
    /// <summary>Base URL of an OpenAI-compatible API — llama.cpp's server started with
    /// <c>--embeddings</c>, or anything else speaking that shape. The <c>/embeddings</c>
    /// path is appended. Usually a different port from the analysis endpoint: a chat
    /// model asked for embeddings gives poor ones. Set, this wins over the packaged
    /// model — and <see cref="Dimensions"/> and <see cref="MaxDistance"/> then have to be
    /// set to suit whatever you pointed it at.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Use the packaged model when no endpoint is configured. Off, and with no
    /// endpoint, nothing is embedded at all.</summary>
    public bool Local { get; set; } = true;

    /// <summary>Where the packaged model lives. Relative paths hang off the application
    /// directory, which is where the build puts it.</summary>
    public string ModelPath { get; set; } = "models/all-MiniLM-L6-v2";

    /// <summary>Sent as a bearer token when set. Local runners usually want no key.</summary>
    public string ApiKey { get; set; } = "";

    public string Model { get; set; } = "";

    /// <summary>Width of the vectors the model returns; the default is the packaged
    /// model's. It has to be stated rather than discovered because it is the width of a
    /// database column: startup reconciles the column to this number, and changing it
    /// re-embeds everything from scratch.</summary>
    public int Dimensions { get; set; } = 384;

    /// <summary>Ceiling on one batch of passages. Generous — indexing runs in the
    /// background, where slow is only slow.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Ceiling on embedding a *search box*, which is a different promise
    /// entirely: past this, the search returns its full-text half rather than making
    /// someone wait on a model that is swapping or asleep.</summary>
    public int QueryTimeoutMs { get; set; } = 2000;

    /// <summary>Longest passage handed to the model in one piece. The default keeps a
    /// passage inside the packaged model's 256-token window, past which it would be
    /// truncated silently; raise it for a model with more room.</summary>
    public int MaxChunkChars { get; set; } = 800;

    /// <summary>Passages embedded per request.</summary>
    public int BatchSize { get; set; } = 16;

    /// <summary>Ceiling on passages per node. A five-megabyte PDF would otherwise spend
    /// an afternoon of a local model on its own; past this the tail goes unembedded and
    /// the log says so — it is still findable by full-text search, which has no such
    /// ceiling.</summary>
    public int MaxChunksPerNode { get; set; } = 200;

    /// <summary>How far apart two texts can be and still be called an answer, as a
    /// cosine distance. Without a ceiling the vector half always returns its nearest
    /// handful, so a search for something nobody has written comes back full of the
    /// least-unrelated pages instead of empty. The default is measured against the
    /// packaged model, which answers a little under 0.8 and misses a little over it. It
    /// is a property of the model and not of Gatherum, so an endpoint pointed at
    /// something else needs its own number — raise it if searches feel too literal, lower
    /// it if they wander.</summary>
    public double MaxDistance { get; set; } = 0.8;

    /// <summary>How often the worker looks for nodes whose text has changed since they
    /// were last embedded. Short, because it is one indexed query against a table two
    /// people are writing to.</summary>
    public int SweepSeconds { get; set; } = 15;

    /// <summary>Whether an endpoint of your own is configured. The packaged model needs
    /// no configuration at all, so this is not the same question as "is semantic search
    /// on" — see <c>AddEmbedding</c>, which decides that.</summary>
    public bool IsConfigured => Endpoint.Length > 0 && Model.Length > 0;
}
