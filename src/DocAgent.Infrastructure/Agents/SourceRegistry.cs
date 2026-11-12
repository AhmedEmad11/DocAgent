using DocAgent.Core.Retrieval;

namespace DocAgent.Infrastructure.Agents;

/// <summary>
/// Numbers the chunks an agent run has seen. The same chunk always gets the same number within one request, so a
/// citation marker [n] in the final answer maps back to exactly one chunk, however many tools returned it.
/// Per request; also the only way tools can refer to a chunk (by number), so they cannot reach chunks the run never saw.
/// </summary>
public sealed class SourceRegistry
{
    private readonly object _gate = new();
    private readonly List<RetrievedChunk> _sources = [];
    private readonly Dictionary<Guid, int> _indexes = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _sources.Count;
            }
        }
    }

    /// <summary>Returns the chunk's number (1-based); assigns the next free one when the chunk is new.</summary>
    public int Register(RetrievedChunk chunk) => Register(chunk, out _);

    public int Register(RetrievedChunk chunk, out bool isNew)
    {
        lock (_gate)
        {
            if (_indexes.TryGetValue(chunk.ChunkId, out var existing))
            {
                isNew = false;
                return existing;
            }

            _sources.Add(chunk);
            isNew = true;
            return _indexes[chunk.ChunkId] = _sources.Count;
        }
    }

    public bool TryGet(int index, out RetrievedChunk chunk)
    {
        lock (_gate)
        {
            if (index >= 1 && index <= _sources.Count)
            {
                chunk = _sources[index - 1];
                return true;
            }
        }

        chunk = null!;
        return false;
    }

    public RetrievedChunk this[int index] => TryGet(index, out var c) ? c : throw new ArgumentOutOfRangeException(nameof(index));

    public IReadOnlyList<RetrievedChunk> Snapshot()
    {
        lock (_gate)
        {
            return [.. _sources];
        }
    }
}
