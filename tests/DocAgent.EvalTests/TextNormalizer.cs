using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocAgent.EvalTests;

/// <summary>
/// Normalization for matching expected key facts in answers (English and Arabic): case-insensitive; Arabic diacritics and
/// tatweel removed; alef variants, alef maqsura and taa marbuta unified; Arabic-Indic digits turned into ASCII digits;
/// thousands separators inside numbers removed; whitespace and punctuation collapsed.
/// </summary>
public static partial class TextNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var sb = new StringBuilder(text.Length);
        foreach (var raw in text.Normalize(NormalizationForm.FormKC))
        {
            var c = raw;
            if (c is >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ' or >= 'ۖ' and <= 'ۭ')
            {
                continue; // tashkeel, superscript alef, tatweel, Quranic marks
            }

            c = c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' or 'ئ' => 'ي',
                'ة' => 'ه',
                'ؤ' => 'و',
                _ => c,
            };
            if (char.IsDigit(c) && c > '9')
            {
                c = (char)('0' + (int)char.GetNumericValue(c)); // Arabic-Indic and extended digits
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        var s = ThousandsSeparator().Replace(sb.ToString(), "");
        s = Punctuation().Replace(s, " ");
        return Whitespace().Replace(s, " ").Trim();
    }

    /// <summary>True when the normalized text contains the normalized fact.</summary>
    public static bool Contains(string? text, string fact)
    {
        var normalizedFact = Normalize(fact);
        return normalizedFact.Length > 0 && Normalize(text).Contains(normalizedFact, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"(?<=\d)[,،٬](?=\d{3}(?!\d))")]
    private static partial Regex ThousandsSeparator();

    // keeps letters, digits and the characters that are part of identifiers and numbers (. - _)
    [GeneratedRegex(@"[^\p{L}\p{N}\.\-_\s]|(?<!\d)\.|\.(?!\d)")]
    private static partial Regex Punctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
