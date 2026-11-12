using System.Text;
using System.Text.RegularExpressions;

namespace DocAgent.Infrastructure.Qa;

public static partial class QuoteTrimmer
{
    /// <summary>
    /// Shortens a chunk to at most <paramref name="maxChars"/> characters for use as a citation quote: it starts at the
    /// sentence that shares the most words with <paramref name="relevantText"/> (question and answer sentence) and adds the
    /// sentences that follow while they fit. A chunk that already fits is returned whole (whitespace collapsed).
    /// </summary>
    public static string Trim(string chunkText, string relevantText, int maxChars = 300)
    {
        var text = Whitespace().Replace(chunkText, " ").Trim();
        if (text.Length <= maxChars)
        {
            return text;
        }

        var sentences = SplitSentences(text);
        var wanted = Words(relevantText);
        var best = 0;
        var bestScore = -1;
        for (var i = 0; i < sentences.Count; i++)
        {
            var score = Words(sentences[i]).Count(wanted.Contains);
            if (score > bestScore)
            {
                (best, bestScore) = (i, score);
            }
        }

        var sb = new StringBuilder();
        for (var i = best; i < sentences.Count; i++)
        {
            var sentence = sentences[i];
            if (sb.Length + sentence.Length + (sb.Length > 0 ? 1 : 0) > maxChars)
            {
                if (sb.Length == 0)
                {
                    sb.Append(CutAtWord(sentence, maxChars - 1)).Append('…'); // a single sentence longer than the limit
                }

                break;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(sentence);
        }

        return sb.ToString();
    }

    private static string CutAtWord(string s, int max)
    {
        if (s.Length <= max)
        {
            return s;
        }

        var cut = s.LastIndexOf(' ', max);
        return s[..(cut > max / 2 ? cut : max)].TrimEnd();
    }

    private static List<string> SplitSentences(string text) =>
        SentenceEnd().Split(text).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    private static HashSet<string> Words(string text) =>
        WordPattern().Matches(text).Select(m => m.Value.ToLowerInvariant()).Where(w => w.Length > 2).ToHashSet();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?<=[.!?؟।。])\s+")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();
}
