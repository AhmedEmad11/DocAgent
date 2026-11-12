using System.Globalization;
using System.Text;

namespace DocAgent.Infrastructure.Ingestion;

public static class FileNameSanitizer
{
    public const int MaxLength = 255;
    public const string Fallback = "document";

    /// <summary>
    /// Makes a client-supplied file name safe to store and show: removes any directory part, control
    /// characters and bidi-override/format characters (used to spoof extensions), collapses whitespace
    /// and limits the length while keeping the extension.
    /// </summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Fallback;
        }

        var name = raw[(raw.LastIndexOfAny(['/', '\\']) + 1)..];
        var sb = new StringBuilder(name.Length);
        var lastWasSpace = false;
        foreach (var rune in name.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (rune.Value is '\t' or '\n' or '\r')
            {
                category = UnicodeCategory.SpaceSeparator; // whitespace controls become a plain space
            }

            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.PrivateUse)
            {
                continue;
            }

            if (rune.Value is '<' or '>' or ':' or '"' or '|' or '?' or '*')
            {
                continue;
            }

            if (Rune.IsWhiteSpace(rune) || category == UnicodeCategory.SpaceSeparator)
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            lastWasSpace = false;
            sb.Append(rune.ToString());
        }

        var cleaned = sb.ToString().Trim().Trim('.').Trim();
        if (cleaned.Length == 0)
        {
            return Fallback;
        }

        if (cleaned.Length > MaxLength)
        {
            var extension = Path.GetExtension(cleaned);
            if (extension.Length is > 0 and <= 16)
            {
                cleaned = TrimToLength(cleaned[..^extension.Length], MaxLength - extension.Length) + extension;
            }
            else
            {
                cleaned = TrimToLength(cleaned, MaxLength);
            }
        }

        return cleaned;
    }

    private static string TrimToLength(string value, int length)
    {
        if (value.Length <= length)
        {
            return value;
        }

        // Do not cut a surrogate pair in half.
        return char.IsHighSurrogate(value[length - 1]) ? value[..(length - 1)] : value[..length];
    }
}
