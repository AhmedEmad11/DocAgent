using DocAgent.Core.Retrieval;
using DocAgent.Infrastructure.Agents;

namespace DocAgent.UnitTests.Agents;

public sealed class SourceRegistryTests
{
    internal static RetrievedChunk Chunk(Guid? id = null, Guid? doc = null, int ordinal = 0, string text = "text", int tokens = 10) =>
        new(id ?? Guid.NewGuid(), doc ?? Guid.NewGuid(), "file.txt", 1, null, ordinal, text, tokens, 0.9);

    [Fact]
    public void Indexes_are_assigned_in_order_starting_at_one()
    {
        var registry = new SourceRegistry();

        Assert.Equal(1, registry.Register(Chunk()));
        Assert.Equal(2, registry.Register(Chunk()));
        Assert.Equal(3, registry.Register(Chunk()));
        Assert.Equal(3, registry.Count);
    }

    [Fact]
    public void The_same_chunk_keeps_its_index_however_often_it_is_registered()
    {
        var registry = new SourceRegistry();
        var chunk = Chunk();
        var other = Chunk();

        var first = registry.Register(chunk, out var firstIsNew);
        registry.Register(other);
        var again = registry.Register(chunk with { Score = 0.5 }, out var againIsNew); // a later search scores it differently

        Assert.Equal(1, first);
        Assert.Equal(first, again);
        Assert.True(firstIsNew);
        Assert.False(againIsNew);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void Lookup_only_resolves_numbers_that_were_handed_out()
    {
        var registry = new SourceRegistry();
        var chunk = Chunk(text: "hello");
        registry.Register(chunk);

        Assert.True(registry.TryGet(1, out var found));
        Assert.Equal("hello", found.Text);
        Assert.False(registry.TryGet(0, out _));
        Assert.False(registry.TryGet(2, out _));
        Assert.False(registry.TryGet(-5, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => registry[2]);
    }

    [Fact]
    public async Task Concurrent_registration_of_the_same_chunks_yields_one_index_each()
    {
        var registry = new SourceRegistry();
        var chunks = Enumerable.Range(0, 50).Select(_ => Chunk()).ToList();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            foreach (var c in chunks)
            {
                registry.Register(c);
            }
        })));

        Assert.Equal(50, registry.Count);
        Assert.Equal(50, chunks.Select(registry.Register).Distinct().Count());
    }
}
