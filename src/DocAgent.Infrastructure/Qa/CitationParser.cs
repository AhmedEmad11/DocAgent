using System.Globalization;
using System.Text.RegularExpressions;

namespace DocAgent.Infrastructure.Qa;

public static partial class CitationParser
{
    public sealed record Result(string Answer, IReadOnlyList<int> Indexes);

    /// <summary>
    /// Finds [n] / [n, m] markers (Latin or Arabic-Indic digits). Returns the distinct valid source numbers
    /// (1..<paramref name="sourceCount"/>) in ascending order and the answer with invalid numbers removed from the markers.
    /// </summary>
    public static Result Parse(string answer, int sourceCount)
    {
        var valid = new SortedSet<int>();
        var cleaned = Marker().Replace(answer, match =>
        {
            var keep = new List<int>();
            foreach (Match number in Number().Matches(match.Groups[2].Value))
            {
                var value = ToInt(number.Value);
                if (value >= 1 && value <= sourceCount)
                {
                    valid.Add(value);
                    keep.Add(value);
                }
            }

            // A marker that ends up empty is removed together with the space before it.
            return keep.Count == 0 ? "" : match.Groups[1].Value + "[" + string.Join(", ", keep) + "]";
        });

        return new Result(cleaned, valid.ToList());
    }

    private static int ToInt(string digits)
    {
        long value = 0;
        foreach (var ch in digits)
        {
            value = value * 10 + CharUnicodeInfo.GetDecimalDigitValue(ch);
            if (value > 100_000)
            {
                return int.MaxValue;
            }
        }

        return (int)value;
    }

    [GeneratedRegex(@"( ?)\[\s*(\p{Nd}+(?:\s*[,،;]\s*\p{Nd}+)*)\s*\]")]
    private static partial Regex Marker();

    [GeneratedRegex(@"\p{Nd}+")]
    private static partial Regex Number();
}
