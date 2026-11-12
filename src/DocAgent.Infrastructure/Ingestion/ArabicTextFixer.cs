using System.Text;

namespace DocAgent.Infrastructure.Ingestion;

/// <summary>
/// Repairs right-to-left (Arabic) text extracted from PDFs (SRS D13).
/// Many PDF producers write glyphs in visual (left-to-right on the page) order and use positional
/// presentation forms (U+FB50..U+FEFF). Extractors such as PdfPig return them as stored, so the text
/// looks reversed and uses presentation forms that never match a normal search or embedding.
/// This class (1) decides per page whether the Arabic lines are in visual or in logical order,
/// (2) for visual order converts each line back to logical order, and (3) maps presentation forms to
/// the standard letters. It is a heuristic, not a full Unicode bidi implementation.
/// </summary>
public static class ArabicTextFixer
{
    private enum CharClass { Rtl, Ltr, Digit, Neutral }

    public static string Fix(string pageText) => Fix(pageText, out _);

    /// <param name="reordered">True when the page's Arabic was stored in reversed (visual) order and had to be reconstructed (SRS D31).</param>
    public static string Fix(string pageText, out bool reordered)
    {
        reordered = false;
        if (!ContainsArabic(pageText))
        {
            return pageText;
        }

        var lines = pageText.Split('\n');
        var visualOrder = LooksVisualOrder(lines);
        reordered = visualOrder;
        var rtlPage = IsRtlDominant(lines);

        var result = new StringBuilder(pageText.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (visualOrder && ContainsArabic(line))
            {
                line = rtlPage ? VisualToLogicalRtl(line) : FixRtlRunsInLtrLine(line);
            }

            result.Append(NormalizePresentationForms(line));
            if (i < lines.Length - 1)
            {
                result.Append('\n');
            }
        }

        return result.ToString();
    }

    public static bool ContainsArabic(string text)
    {
        foreach (var c in text)
        {
            if (IsArabic(c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsArabic(char c) =>
        c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' or >= 'ࢠ' and <= 'ࣿ'
          or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿';

    private static bool IsPresentationForm(char c) => c is >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿';

    /// <summary>Maps Arabic presentation forms (including lam-alef ligatures) to standard letters; other text is left alone.</summary>
    public static string NormalizePresentationForms(string text)
    {
        if (!text.Any(IsPresentationForm))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (IsPresentationForm(c))
            {
                sb.Append(c.ToString().Normalize(NormalizationForm.FormKC));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    // ---- Direction detection ----

    private static bool IsRtlDominant(string[] lines)
    {
        int rtl = 0, ltr = 0;
        foreach (var line in lines)
        {
            foreach (var c in line)
            {
                switch (Classify(c))
                {
                    case CharClass.Rtl: rtl++; break;
                    case CharClass.Ltr: ltr++; break;
                }
            }
        }

        return rtl >= ltr / 3; // Arabic pages usually carry some Latin text; require it to clearly dominate.
    }

    /// <summary>
    /// True when the Arabic text of the page looks reversed. Arabic words start with the definite article
    /// "al-" and many end in teh marbuta or alef maqsura; those letters are never in the other position
    /// in reading order. Presentation-form glyphs (not plain letters) are also a strong sign of shaped,
    /// visual-order output.
    /// </summary>
    private static bool LooksVisualOrder(string[] lines)
    {
        var score = 0; // > 0: logical order evidence, < 0: reversed evidence
        foreach (var line in lines)
        {
            foreach (var word in ArabicWords(NormalizePresentationForms(line)))
            {
                if (word.Length < 3)
                {
                    continue;
                }

                if (word.StartsWith("ال", StringComparison.Ordinal))
                {
                    score++;
                }

                if (word.EndsWith("لا", StringComparison.Ordinal) && word is not ("إلا" or "ولا" or "كلا" or "بلا" or "فلا" or "ألا" or "مهلا" or "أهلا" or "سهلا" or "حبا"))
                {
                    score--;
                }

                score += word[^1] is 'ة' or 'ى' ? 1 : 0;      // ة / ى at the end
                score -= word[0] is 'ة' or 'ى' ? 1 : 0;       // ... never at the start
                score -= word[^1] == 'ـ' ? 0 : 0;
            }
        }

        if (score != 0)
        {
            return score < 0;
        }

        // No letter statistics (very short text): fall back to the glyph encoding.
        var arabic = lines.SelectMany(l => l).Where(IsArabic).ToList();
        return arabic.Count > 0 && arabic.Count(IsPresentationForm) > arabic.Count / 2;
    }

    private static IEnumerable<string> ArabicWords(string line)
    {
        var sb = new StringBuilder();
        foreach (var c in line)
        {
            if (IsArabic(c) && !IsMark(c))
            {
                sb.Append(c);
            }
            else if (!IsMark(c))
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
            }
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    private static bool IsMark(char c) => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark;

    // ---- Visual -> logical conversion ----

    private static CharClass Classify(char c)
    {
        if (IsArabic(c) || c is >= '֐' and <= '׿')
        {
            return char.IsDigit(c) ? CharClass.Digit : (char.IsLetter(c) || IsMark(c) || c == 'ـ' ? CharClass.Rtl : CharClass.Neutral);
        }

        if (char.IsDigit(c))
        {
            return CharClass.Digit;
        }

        return char.IsLetter(c) ? CharClass.Ltr : CharClass.Neutral;
    }

    /// <summary>
    /// A line of an RTL paragraph, as stored in visual order (left to right on the page), back to logical order:
    /// reverse the whole line, then restore the internal order of left-to-right runs (Latin words, numbers),
    /// and swap mirrored brackets in right-to-left context.
    /// </summary>
    private static string VisualToLogicalRtl(string line)
    {
        var chars = line.ToCharArray();
        var classes = chars.Select(Classify).ToArray();

        // Marks (diacritics) are part of the letter before them in logical order; keep that attachment.
        var runs = new List<(int Start, int End, bool Ltr)>(); // inclusive-exclusive over the visual string
        var i = 0;
        while (i < chars.Length)
        {
            if (classes[i] is CharClass.Ltr or CharClass.Digit)
            {
                var end = i + 1;
                while (end < chars.Length)
                {
                    if (classes[end] is CharClass.Ltr or CharClass.Digit)
                    {
                        end++;
                        continue;
                    }

                    // A neutral between two letters (spaces, apostrophes, hyphens) or a number separator between digits.
                    if (classes[end] == CharClass.Neutral && end + 1 < chars.Length)
                    {
                        var before = classes[end - 1];
                        var after = classes[end + 1];
                        var joinsLetters = before == CharClass.Ltr && after == CharClass.Ltr;
                        var joinsDigits = before == CharClass.Digit && after == CharClass.Digit && chars[end] is '.' or ',' or ':' or '/' or '-' or '+';
                        if (joinsLetters || joinsDigits)
                        {
                            end += 2;
                            continue;
                        }
                    }

                    break;
                }

                runs.Add((i, end, true));
                i = end;
            }
            else
            {
                var end = i + 1;
                while (end < chars.Length && classes[end] is not (CharClass.Ltr or CharClass.Digit))
                {
                    end++;
                }

                runs.Add((i, end, false));
                i = end;
            }
        }

        var sb = new StringBuilder(line.Length);
        for (var r = runs.Count - 1; r >= 0; r--)
        {
            var (start, end, ltr) = runs[r];
            if (ltr)
            {
                sb.Append(chars, start, end - start); // internal order is already logical
            }
            else
            {
                AppendReversed(sb, chars, start, end);
            }
        }

        return sb.ToString();
    }

    /// <summary>A line of an LTR paragraph containing an RTL phrase: only the Arabic runs are reversed.</summary>
    private static string FixRtlRunsInLtrLine(string line)
    {
        var chars = line.ToCharArray();
        var sb = new StringBuilder(line.Length);
        var i = 0;
        while (i < chars.Length)
        {
            if (Classify(chars[i]) != CharClass.Rtl)
            {
                sb.Append(chars[i]);
                i++;
                continue;
            }

            // Extend over Arabic letters and the spaces/neutrals between Arabic words.
            var end = i;
            var lastRtl = i;
            while (end < chars.Length && Classify(chars[end]) is CharClass.Rtl or CharClass.Neutral or CharClass.Digit)
            {
                if (Classify(chars[end]) == CharClass.Rtl)
                {
                    lastRtl = end;
                }

                end++;
            }

            AppendReversed(sb, chars, i, lastRtl + 1);
            i = lastRtl + 1;
        }

        return sb.ToString();
    }

    private static void AppendReversed(StringBuilder sb, char[] chars, int start, int end)
    {
        for (var k = end - 1; k >= start; k--)
        {
            sb.Append(Mirror(chars[k]));
        }

        FixMarkOrder(sb, sb.Length - (end - start));
    }

    private static char Mirror(char c) => c switch
    {
        '(' => ')', ')' => '(', '[' => ']', ']' => '[', '{' => '}', '}' => '{',
        '<' => '>', '>' => '<', '«' => '»', '»' => '«',
        _ => c,
    };

    /// <summary>A diacritic must follow its base letter; after reversing, swap a mark that ended up before its base.</summary>
    private static void FixMarkOrder(StringBuilder sb, int start)
    {
        for (var k = start; k < sb.Length - 1; k++)
        {
            if (IsMark(sb[k]) && (k == start || !IsArabic(sb[k - 1]) || sb[k - 1] == ' ') && IsArabic(sb[k + 1]) && !IsMark(sb[k + 1]))
            {
                (sb[k], sb[k + 1]) = (sb[k + 1], sb[k]);
                k++;
            }
        }
    }
}
