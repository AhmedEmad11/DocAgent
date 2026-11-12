using System.Text.RegularExpressions;

namespace DocAgent.Infrastructure.Retrieval;

/// <summary>Turns a natural-language question into a websearch_to_tsquery string: significant words joined by OR.</summary>
public static partial class FullTextQuery
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        "the", "and", "for", "are", "was", "were", "what", "which", "who", "whom", "whose", "when", "where", "why", "how",
        "does", "did", "has", "have", "had", "not", "this", "that", "these", "those", "with", "from", "about", "into",
        "can", "could", "should", "would", "will", "shall", "may", "might", "you", "your", "our", "their", "its", "any",
        "all", "per", "than", "then", "there", "here", "also", "such", "been", "being", "tell", "give", "say", "says",
        "or", "is", "it", "of", "to", "in", "on", "at", "by", "an", "as", "be", "do", "if", "no", "so", "we", "he", "she",
        // Arabic
        "ما", "ماذا", "من", "متى", "أين", "اين", "كيف", "لماذا", "هل", "هو", "هي", "هذا", "هذه", "ذلك", "تلك", "في", "على",
        "إلى", "الى", "عن", "مع", "أو", "او", "التي", "الذي", "الذين", "كان", "كانت", "يكون", "تكون", "كم", "أي", "اي",
        "لا", "إن", "ان", "أن", "قد", "لم", "لن", "ثم", "بعد", "قبل", "عند", "هنا", "هناك",
    };

    /// <summary>
    /// <see cref="All"/>: every significant word of the question (AND). <see cref="Identifiers"/>: only the words that contain
    /// a digit, such as codes and numbers (AND), or null when there are none. Both are websearch_to_tsquery strings.
    /// Full-text search is used for exact-term evidence only: a chunk must match all the words or all the identifiers.
    /// (Postgres full-text ranking has no inverse-document-frequency, so loose OR matching on common words is just noise.)
    /// </summary>
    public sealed record Terms(string All, string? Identifiers);

    /// <summary>The query terms, or null when the question has no significant word.</summary>
    public static Terms? Build(string question, int maxTerms = 12)
    {
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TokenPattern().Matches(question))
        {
            var token = match.Value.Trim('-', '_', '.', '/').ToLowerInvariant();
            if (token.Length < 2 || StopWords.Contains(token) || !seen.Add(token))
            {
                continue;
            }

            terms.Add(token);
            if (terms.Count == maxTerms)
            {
                break;
            }
        }

        if (terms.Count == 0)
        {
            return null;
        }

        var identifiers = terms.Where(t => t.Any(char.IsDigit)).ToList();
        return new Terms(string.Join(' ', terms), identifiers.Count == 0 ? null : string.Join(' ', identifiers));
    }

    // Letters/digits (any script), with inner hyphens, dots, slashes or underscores kept so codes like "XJ-4471" stay whole.
    [GeneratedRegex(@"[\p{L}\p{N}\p{M}]+(?:[-_./][\p{L}\p{N}\p{M}]+)*")]
    private static partial Regex TokenPattern();
}
