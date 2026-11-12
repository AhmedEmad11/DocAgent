using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocAgent.EvalTests;

public sealed class EvalItem
{
    public string Id { get; set; } = "";
    public string Lang { get; set; } = "en";

    /// <summary>single, code, multi, cross-doc, cross-lingual, reformulation, summarize, compare, unanswerable.</summary>
    public string Type { get; set; } = "single";
    public bool Answerable { get; set; }
    public string? Question { get; set; }

    /// <summary>Key facts: every inner list is one fact, written as alternatives (any one of them counts).</summary>
    public List<List<string>> Facts { get; set; } = [];

    /// <summary>File names of the documents the answer must cite (at least one).</summary>
    public List<string> Documents { get; set; } = [];

    // summarize / compare
    public string? Document { get; set; }
    public string? DocumentA { get; set; }
    public string? DocumentB { get; set; }
    public string? Focus { get; set; }

    public override string ToString() => Id;
}

public sealed class EvalSetFile
{
    public List<EvalItem> Items { get; set; } = [];

    public static EvalSetFile Load(string path) =>
        JsonSerializer.Deserialize<EvalSetFile>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
        }) ?? new EvalSetFile();
}
