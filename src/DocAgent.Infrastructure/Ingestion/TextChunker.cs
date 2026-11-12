using System.Text;
using System.Text.RegularExpressions;
using DocAgent.Infrastructure.Embeddings;

namespace DocAgent.Infrastructure.Ingestion;

public sealed record TextChunk(int Ordinal, string Text, int TokenCount, int? PageNumber, string? Section);

/// <summary>
/// Splits extracted text into chunks of about <see cref="ChunkingOptions.TargetTokens"/> tokens that never
/// exceed <see cref="ChunkingOptions.MaxTokens"/>, measured with the embedding model's tokenizer (the model
/// silently truncates beyond 512 tokens). Splits recursively on paragraph, line, sentence, word and finally
/// token boundaries. Each chunk after the first of a segment starts with up to
/// <see cref="ChunkingOptions.OverlapTokens"/> tokens repeated from the end of the previous chunk.
/// A chunk never crosses a segment boundary (PDF page, heading section) so its metadata stays exact.
/// </summary>
public sealed partial class TextChunker
{
    private const int MaxCharsPerToken = 40; // a longer text is certainly over budget; avoids tokenizing huge strings

    private readonly ITokenCounter _tokens;
    private readonly int _target;
    private readonly int _max;
    private readonly int _overlap;

    public TextChunker(ITokenCounter tokens, ChunkingOptions options)
    {
        if (options.TargetTokens < 1 || options.MaxTokens < options.TargetTokens || options.OverlapTokens < 0
            || options.OverlapTokens + options.TargetTokens > options.MaxTokens)
        {
            throw new ArgumentException("Chunking options must satisfy OverlapTokens + TargetTokens <= MaxTokens.", nameof(options));
        }

        _tokens = tokens;
        _target = options.TargetTokens;
        _max = options.MaxTokens;
        _overlap = options.OverlapTokens;
    }

    [GeneratedRegex(@"\n[ \t]*\n\s*")]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex(@"(?<=[.!?؟۔…。])\s+")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private readonly record struct Unit(string Text, int Tokens, string SeparatorBefore);

    public IReadOnlyList<TextChunk> Chunk(IEnumerable<TextSegment> segments, CancellationToken ct = default)
    {
        var chunks = new List<TextChunk>();
        foreach (var segment in segments)
        {
            foreach (var text in ChunkText(segment.Text, ct))
            {
                chunks.Add(new TextChunk(chunks.Count, text, _tokens.CountTokens(text), segment.PageNumber, segment.Section));
            }
        }

        return chunks;
    }

    private int Count(string text) =>
        text.Length > _target * MaxCharsPerToken ? int.MaxValue : _tokens.CountTokens(text);

    private List<string> ChunkText(string text, CancellationToken ct)
    {
        var units = new List<Unit>();
        Split(text, 0, string.Empty, units, ct);

        var result = new List<string>();
        var current = new StringBuilder();
        var currentTokens = 0;
        string? previous = null;

        void Emit()
        {
            if (current.Length == 0)
            {
                return;
            }

            var chunk = current.ToString();
            result.AddRange(EnforceMax(chunk));
            previous = chunk;
            current.Clear();
            currentTokens = 0;
        }

        foreach (var unit in units)
        {
            ct.ThrowIfCancellationRequested();
            if (current.Length > 0 && currentTokens + 1 + unit.Tokens > _target)
            {
                Emit();
            }

            if (current.Length == 0)
            {
                var overlap = previous is null ? string.Empty : OverlapSuffix(previous);
                current.Append(overlap);
                if (overlap.Length > 0)
                {
                    current.Append(overlap.EndsWith('\n') ? "" : "\n");
                }

                currentTokens = 0;
                current.Append(unit.Text);
            }
            else
            {
                current.Append(unit.SeparatorBefore).Append(unit.Text);
                currentTokens += 1;
            }

            currentTokens += unit.Tokens;
        }

        Emit();
        return result;
    }

    /// <summary>The last whole words of the previous chunk, at most the configured number of tokens.</summary>
    private string OverlapSuffix(string previous)
    {
        if (_overlap == 0)
        {
            return string.Empty;
        }

        var words = Whitespace().Split(previous.Trim());
        var take = 0;
        var candidate = string.Empty;
        for (var i = words.Length - 1; i > 0 && take < words.Length - 1; i--) // never the whole previous chunk
        {
            var next = string.Join(' ', words[i..]);
            if (Count(next) > _overlap)
            {
                break;
            }

            candidate = next;
            take++;
        }

        return candidate;
    }

    /// <summary>Safety net: the merged text of a chunk is re-measured and cut if it is (unexpectedly) above the hard cap.</summary>
    private IEnumerable<string> EnforceMax(string chunk)
    {
        if (Count(chunk) <= _max)
        {
            yield return chunk;
            yield break;
        }

        var rest = chunk;
        while (rest.Length > 0)
        {
            if (Count(rest) <= _max)
            {
                yield return rest;
                yield break;
            }

            var length = HardCut(rest, _target);
            yield return rest[..length].TrimEnd();
            rest = rest[length..].TrimStart();
        }
    }

    private void Split(string text, int level, string separatorBefore, List<Unit> output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        text = text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var tokens = Count(text);
        if (tokens <= _target)
        {
            output.Add(new Unit(text, tokens, separatorBefore));
            return;
        }

        if (level >= 4)
        {
            var rest = text;
            var first = true;
            while (rest.Length > 0)
            {
                var length = HardCut(rest, _target);
                var piece = rest[..length].Trim();
                if (piece.Length > 0)
                {
                    output.Add(new Unit(piece, Count(piece), first ? separatorBefore : " "));
                    first = false;
                }

                rest = rest[length..];
            }

            return;
        }

        var pieces = level switch
        {
            0 => ParagraphBreak().Split(text),
            1 => text.Split('\n'),
            2 => SentenceBreak().Split(text),
            _ => Whitespace().Split(text),
        };

        if (pieces.Length <= 1)
        {
            Split(text, level + 1, separatorBefore, output, ct);
            return;
        }

        var separator = level switch { 0 => "\n\n", 1 => "\n", _ => " " };
        for (var i = 0; i < pieces.Length; i++)
        {
            Split(pieces[i], level + 1, i == 0 ? separatorBefore : separator, output, ct);
        }
    }

    /// <summary>Characters to cut so that the piece has at most <paramref name="maxTokens"/> tokens (at least 1 character).</summary>
    private int HardCut(string text, int maxTokens)
    {
        var window = text.Length > maxTokens * MaxCharsPerToken ? text[..(maxTokens * MaxCharsPerToken)] : text;
        var length = _tokens.GetPrefixLength(window, maxTokens);
        if (length <= 0)
        {
            length = 1;
        }

        // Do not split a surrogate pair.
        if (length < text.Length && char.IsHighSurrogate(text[length - 1]))
        {
            length++;
        }

        return Math.Min(length, text.Length);
    }
}
