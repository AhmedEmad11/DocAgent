using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DocAgent.Infrastructure.Embeddings;

/// <summary>
/// In-process multilingual-e5-small (int8 ONNX) as an <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>.
/// Mean pooling over the attention mask followed by L2 normalization (the model's recipe), 384 dimensions.
/// The caller supplies the "query: " / "passage: " prefix (see <see cref="EmbeddingOptions"/>).
/// Thread-safe: <see cref="InferenceSession"/> supports concurrent Run calls.
/// </summary>
public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public const int Dimensions = 384;

    private readonly InferenceSession _session;
    private readonly EmbeddingTokenizer _tokenizer;
    private readonly int _maxSequenceLength;
    private readonly int _batchSize;
    private readonly string _outputName;
    private readonly bool _needsTokenTypeIds;
    private readonly EmbeddingGeneratorMetadata _metadata;

    public OnnxEmbeddingGenerator(string modelDirectory, EmbeddingTokenizer tokenizer, int maxSequenceLength = 512, int batchSize = 16)
    {
        var onnx = Path.Combine(modelDirectory, EmbeddingOptions.OnnxFileName);
        if (!File.Exists(onnx))
        {
            throw new FileNotFoundException(ModelPathResolver.MissingModelMessage(modelDirectory));
        }

        _tokenizer = tokenizer;
        _maxSequenceLength = maxSequenceLength;
        _batchSize = Math.Max(1, batchSize);
        _session = new InferenceSession(onnx);
        _needsTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");
        // "last_hidden_state" is [batch, sequence, 384] (token embeddings); we pool it ourselves.
        _outputName = _session.OutputMetadata.ContainsKey("last_hidden_state")
            ? "last_hidden_state"
            : _session.OutputMetadata.Keys.First();
        _metadata = new EmbeddingGeneratorMetadata(EmbeddingOptions.ModelName, defaultModelId: EmbeddingOptions.ModelName, defaultModelDimensions: Dimensions);
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var inputs = values.ToList();
        var result = new GeneratedEmbeddings<Embedding<float>>(inputs.Count);
        if (inputs.Count == 0)
        {
            return Task.FromResult(result);
        }

        // Tokenize everything, then run batches of similar length together to limit padding.
        var tokens = inputs.Select(Tokenize).ToArray();
        var order = Enumerable.Range(0, inputs.Count).OrderBy(i => tokens[i].Length).ToArray();
        var vectors = new float[inputs.Count][];

        for (var start = 0; start < order.Length; start += _batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var indices = order.Skip(start).Take(_batchSize).ToArray();
            var batch = RunBatch(indices.Select(i => tokens[i]).ToArray());
            for (var k = 0; k < indices.Length; k++)
            {
                vectors[indices[k]] = batch[k];
            }
        }

        foreach (var vector in vectors)
        {
            result.Add(new Embedding<float>(vector));
        }

        return Task.FromResult(result);
    }

    private int[] Tokenize(string text)
    {
        var ids = _tokenizer.Encode(text);
        if (ids.Length > _maxSequenceLength)
        {
            // Truncate but keep the closing </s>.
            ids = [.. ids.Take(_maxSequenceLength - 1), EmbeddingTokenizer.EosId];
        }

        return ids;
    }

    private float[][] RunBatch(int[][] batch)
    {
        var rows = batch.Length;
        var length = batch.Max(t => t.Length);
        var inputIds = new DenseTensor<long>([rows, length]);
        var attention = new DenseTensor<long>([rows, length]);
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < length; c++)
            {
                var present = c < batch[r].Length;
                inputIds[r, c] = present ? batch[r][c] : EmbeddingTokenizer.PadId;
                attention[r, c] = present ? 1 : 0;
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attention),
        };
        if (_needsTokenTypeIds)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>([rows, length])));
        }

        using var outputs = _session.Run(inputs, [_outputName]);
        var hidden = outputs.First().AsTensor<float>();
        if (hidden.Dimensions.Length != 3 || hidden.Dimensions[2] != Dimensions)
        {
            throw new InvalidOperationException(
                $"Unexpected embedding model output shape [{string.Join(",", hidden.Dimensions.ToArray())}]; expected [batch, sequence, {Dimensions}].");
        }

        var result = new float[rows][];
        for (var r = 0; r < rows; r++)
        {
            var pooled = new float[Dimensions];
            for (var c = 0; c < batch[r].Length; c++)
            {
                for (var d = 0; d < Dimensions; d++)
                {
                    pooled[d] += hidden[r, c, d];
                }
            }

            var norm = 0d;
            for (var d = 0; d < Dimensions; d++)
            {
                pooled[d] /= batch[r].Length;
                norm += (double)pooled[d] * pooled[d];
            }

            var scale = norm > 0 ? (float)(1d / Math.Sqrt(norm)) : 0f;
            for (var d = 0; d < Dimensions; d++)
            {
                pooled[d] *= scale;
            }

            result[r] = pooled;
        }

        return result;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this
        : serviceKey is null && serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata
        : null;

    public void Dispose() => _session.Dispose();
}
