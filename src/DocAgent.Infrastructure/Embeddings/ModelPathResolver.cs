namespace DocAgent.Infrastructure.Embeddings;

public static class ModelPathResolver
{
    /// <summary>
    /// Finds the model directory. Absolute paths are used as is. Relative paths are looked up in each
    /// start directory and its parents, so it works from `dotnet run` (content root = project folder,
    /// models/ at the repo root), from test output folders, and from a container (models/ next to the app).
    /// </summary>
    public static string Resolve(string configuredPath, params string?[] startDirectories)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        var tried = new List<string>();
        foreach (var start in startDirectories.Append(AppContext.BaseDirectory).Where(d => !string.IsNullOrEmpty(d)))
        {
            var dir = new DirectoryInfo(start!);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, configuredPath);
                tried.Add(candidate);
                if (File.Exists(Path.Combine(candidate, EmbeddingOptions.OnnxFileName)))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }
        }

        // Not found: return the most likely location so the error message names a concrete path.
        return tried.Count > 0 ? tried[0] : Path.GetFullPath(configuredPath);
    }

    public static string MissingModelMessage(string modelDirectory) =>
        $"Embedding model files not found in '{modelDirectory}'. Run scripts/download-models.sh " +
        "(from the repository root) to download them, or set Embeddings:ModelPath.";
}
