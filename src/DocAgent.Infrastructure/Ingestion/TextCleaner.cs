using System.Text;

namespace DocAgent.Infrastructure.Ingestion;

public static class TextCleaner
{
    /// <summary>
    /// Makes extracted text safe to store: PostgreSQL rejects NUL characters and invalid UTF-16, and stray
    /// control characters only add noise. Keeps tabs and newlines, normalizes line endings.
    /// </summary>
    public static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    continue;
                }

                sb.Append('\n');
            }
            else if (c is '\n' or '\t')
            {
                sb.Append(c);
            }
            else if (c == ' ')
            {
                sb.Append(' ');
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c).Append(text[++i]);
            }
            else if (char.IsSurrogate(c))
            {
                sb.Append('�');
            }
            else if (!char.IsControl(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
