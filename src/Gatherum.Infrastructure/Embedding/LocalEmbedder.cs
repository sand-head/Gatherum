using Gatherum.Core;
using Gatherum.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Gatherum.Infrastructure.Embedding;

/// <summary>The embedding model that ships in the box: MiniLM, quantized to eight bits,
/// run in this process on the CPU. It exists so that semantic search is what Gatherum
/// does rather than what Gatherum can be configured to do — twenty-three megabytes and a
/// few milliseconds a passage is a smaller price than asking someone to stand a second
/// inference server up before their notes become searchable by meaning. A better model
/// you run yourself still wins: configure an endpoint and this one is never
/// loaded.</summary>
public sealed class LocalEmbedder : IEmbedder, IDisposable
{
    public const string ModelName = "all-MiniLM-L6-v2";
    public const int ModelDimensions = 384;

    /// <summary>What this model was trained to read at once. Longer input is truncated
    /// rather than refused, and <see cref="EmbeddingOptions.MaxChunkChars"/> is set low
    /// enough that it shouldn't come to that.</summary>
    private const int MaxTokens = 256;

    private const string WeightsFile = "model.onnx";
    private const string VocabularyFile = "vocab.txt";

    private readonly InferenceSession session;
    private readonly BertTokenizer tokenizer;

    public LocalEmbedder(IOptions<GatherumOptions> options, ILogger<LocalEmbedder> logger)
    {
        var directory = ResolvePath(options.Value.Embedding.ModelPath);
        var settings = new SessionOptions
        {
            // Left to itself ONNX Runtime takes every core, which on a two-core box means
            // a background sweep and the web app fighting over the same CPU. Indexing is
            // never the urgent work here.
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
            InterOpNumThreads = 1,
        };
        session = new InferenceSession(Path.Combine(directory, WeightsFile), settings);
        tokenizer = BertTokenizer.Create(Path.Combine(directory, VocabularyFile));
        logger.LogInformation("Embedding locally with {Model} from {Directory}",
            ModelName, directory);
    }

    public string Model => ModelName;

    /// <summary>Whether the model is where it should be. The build fetches it, so a "no"
    /// here means a publish that skipped the fetch or a stripped image — worth answering
    /// rather than throwing at startup, since search still works without it.</summary>
    public static bool IsAvailable(string modelPath)
    {
        var directory = ResolvePath(modelPath);
        return File.Exists(Path.Combine(directory, WeightsFile))
            && File.Exists(Path.Combine(directory, VocabularyFile));
    }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default) =>
        texts.Count == 0
            ? Task.FromResult<IReadOnlyList<float[]>>([])
            // Inference is seconds of CPU for a full batch; doing it on the caller's
            // thread would hold a request thread for the whole of it.
            : Task.Run(() => Embed(texts, cancellationToken), cancellationToken);

    /// <summary>One passage per inference call, deliberately, though the caller hands
    /// them over in batches. This model's weights are quantized to eight bits and its
    /// activations are scaled at run time across the whole input tensor — so a passage
    /// batched beside a different one embeds measurably differently (about 0.97 cosine)
    /// than the same passage batched beside anything else. Left alone that would be a
    /// systematic error rather than noise: a search box is always a batch of one, while
    /// passages would arrive sixteen at a time, putting every query and every document in
    /// a different regime. One at a time costs about half again as much wall clock —
    /// six milliseconds a passage instead of four — and buys back the property the rest
    /// of the design assumes: a vector is a function of its text and of nothing
    /// else.</summary>
    private IReadOnlyList<float[]> Embed(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var vectors = new List<float[]>(texts.Count);
        foreach (var text in texts)
        {
            ct.ThrowIfCancellationRequested();
            vectors.Add(EmbedOne(text));
        }
        return vectors;
    }

    private float[] EmbedOne(string text)
    {
        var ids = tokenizer.EncodeToIds(text, MaxTokens, out _, out _);
        var shape = new[] { 1, ids.Count };
        var inputIds = new DenseTensor<long>(shape);
        var mask = new DenseTensor<long>(shape);
        for (var token = 0; token < ids.Count; token++)
        {
            inputIds[0, token] = ids[token];
            mask[0, token] = 1;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };
        if (session.InputMetadata.ContainsKey("token_type_ids"))
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(shape)));

        using var results = session.Run(inputs);
        var hidden = results.First(result => result.Name == "last_hidden_state").AsTensor<float>();
        return Pool(hidden, ids.Count);
    }

    /// <summary>MiniLM's sentence vector is the mean of its token vectors, normalized to
    /// unit length so that cosine distance is all there is left to measure.</summary>
    private static float[] Pool(Tensor<float> hidden, int tokens)
    {
        var width = hidden.Dimensions[2];
        var vector = new float[width];
        for (var token = 0; token < tokens; token++)
            for (var i = 0; i < width; i++)
                vector[i] += hidden[0, token, i];

        var norm = 0f;
        for (var i = 0; i < width; i++)
        {
            vector[i] /= tokens;
            norm += vector[i] * vector[i];
        }
        norm = MathF.Sqrt(norm);
        if (norm > 0)
            for (var i = 0; i < width; i++)
                vector[i] /= norm;
        return vector;
    }

    /// <summary>Relative paths hang off the app's own directory, which is where the build
    /// puts the model — not off the working directory, which is whatever systemd or a
    /// shell happened to be in.</summary>
    private static string ResolvePath(string modelPath) =>
        Path.IsPathRooted(modelPath)
            ? modelPath
            : Path.Combine(AppContext.BaseDirectory, modelPath);

    public void Dispose() => session.Dispose();
}
