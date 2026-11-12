namespace DocAgent.EvalTests;

/// <summary>What the API returned for one eval item.</summary>
public sealed record ApiAnswer(string Answer, bool NotFound, IReadOnlyList<string> CitedFiles, bool Incomplete, int ToolCalls);

public sealed record Grade(bool Passed, string Reason);

/// <summary>
/// SRS D32 grading. Answerable: correct = every key fact present (normalized matching) AND at least one citation to an
/// expected document. Unanswerable: correct = notFound is true and there are no citations.
/// </summary>
public static class Grader
{
    public static Grade Grade(EvalItem item, ApiAnswer answer)
    {
        if (!item.Answerable)
        {
            if (!answer.NotFound)
            {
                return new Grade(false, "answered an unanswerable question (notFound=false)");
            }

            return answer.CitedFiles.Count > 0
                ? new Grade(false, "notFound but citations were returned")
                : new Grade(true, "ok");
        }

        if (answer.NotFound)
        {
            return new Grade(false, "answered notFound");
        }

        var missing = item.Facts
            .Where(alternatives => !alternatives.Any(a => TextNormalizer.Contains(answer.Answer, a)))
            .Select(alternatives => alternatives[0])
            .ToList();
        if (missing.Count > 0)
        {
            return new Grade(false, "missing facts: " + string.Join(", ", missing));
        }

        var expected = item.Documents.Select(d => TextNormalizer.Normalize(d)).ToHashSet();
        if (!answer.CitedFiles.Any(f => expected.Contains(TextNormalizer.Normalize(f))))
        {
            return new Grade(false, "no citation to an expected document (cited: " + (answer.CitedFiles.Count == 0 ? "none" : string.Join(", ", answer.CitedFiles.Distinct())) + ")");
        }

        return new Grade(true, "ok");
    }
}
