using DocAgent.Infrastructure.Ingestion;
using DocAgent.TestSupport;

namespace DocAgent.UnitTests.Ingestion;

/// <summary>
/// Arabic PDF extraction (SRS D13). Two kinds of PDF: one produced by Chromium (shaped glyphs in visual order with
/// presentation forms, like most real PDFs) and one written in logical order by PdfPig's builder.
/// </summary>
public sealed class ArabicPdfTests
{
    private static readonly IngestionOptions Options = new();

    private static ExtractedDocument Extract(byte[] pdf) =>
        PdfTextExtractor.Extract(pdf, Options, CancellationToken.None);

    [Fact]
    public void Chromium_pdf_page_one_is_returned_in_logical_order()
    {
        var result = Extract(TestFiles.ReadFixture("arabic-chromium.pdf"));
        Assert.Equal(2, result.PageCount);
        var page = result.Segments[0].Text;

        Assert.Contains("خطة الاشتراك", page);
        Assert.Contains("مرحبا بكم في وكيل المستندات. تكلفة الخطة الشهرية عشرون دولارا، وتشمل", page);
        Assert.Contains("تخزين مائة مستند ودعما فنيا على مدار الساعة طوال أيام الأسبوع.", page);
        Assert.Contains("عاصمة فرنسا هي باريس، وهي أكبر مدنها وأهم مراكزها الثقافية والاقتصادية.", page);
        Assert.Contains("يزور المدينة ملايين السياح كل عام.", page);
    }

    [Fact]
    public void Chromium_pdf_mixed_arabic_english_and_numbers_keep_their_order()
    {
        var page = Extract(TestFiles.ReadFixture("arabic-chromium.pdf")).Segments[0].Text;

        Assert.Contains("Mixed line: السعر 20 USD شهريا for the Pro plan.", page);
        Assert.Contains("الرقم (مهم) هو 2024", page); // brackets mirrored back, number intact
    }

    [Fact]
    public void Chromium_pdf_has_no_presentation_forms_or_nul_characters()
    {
        var all = string.Concat(Extract(TestFiles.ReadFixture("arabic-chromium.pdf")).Segments.Select(s => s.Text));

        Assert.DoesNotContain(all, c => c is >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿');
        Assert.DoesNotContain('\0', all);
    }

    [Fact]
    public void Chromium_pdf_english_page_with_one_arabic_word_is_not_scrambled()
    {
        var page2 = Extract(TestFiles.ReadFixture("arabic-chromium.pdf")).Segments[1].Text;

        Assert.Contains("This English page quotes one Arabic word, شهريا, which means", page2);
    }

    [Fact]
    public void Logical_order_pdf_is_left_untouched()
    {
        var pdf = TestFiles.ArabicLogicalPdf(
            $"{TestFiles.ArabicPage1} {TestFiles.ArabicSentence1} {TestFiles.ArabicSentence2} الرجاء قراءة هذه الوثيقة بعناية");

        var text = string.Join("\n", Extract(pdf).Segments.Select(s => s.Text));

        Assert.Contains(TestFiles.ArabicSentence1.Split(' ')[0], text);
        Assert.Contains("الاشتراك", text);
        Assert.Contains("عاصمة فرنسا هي", text);
        Assert.DoesNotContain("كارتشلاا", text); // reversed form must not appear
    }

    [Fact]
    public void Presentation_forms_are_mapped_to_standard_letters()
    {
        var mapped = ArabicTextFixer.NormalizePresentationForms("\uFE8D\uFEDF\uFEE3\uFEAE\uFE91\uFE8E"); // positional glyph forms
        Assert.Equal("\u0627\u0644\u0645\u0631\u0628\u0627", mapped);
    }

    [Fact]
    public void Lam_alef_ligature_expands_in_the_right_order()
    {
        Assert.Equal("لا", ArabicTextFixer.NormalizePresentationForms("ﻻ"));
    }

    [Fact]
    public void Text_without_arabic_is_returned_unchanged()
    {
        const string english = "Chapter 1: Intro (draft)\nSecond line";
        Assert.Same(english, ArabicTextFixer.Fix(english));
    }
}
