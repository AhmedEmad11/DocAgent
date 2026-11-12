namespace DocAgent.EvalTests;

// These run in the default CI test run (no model, no database).
public sealed class GraderTests
{
    [Theory]
    [InlineData("الإجازة", "الاجازه")] // hamza on alef, taa marbuta
    [InlineData("مُدَّةُ الضَّمَانِ", "مده الضمان")] // diacritics and shadda
    [InlineData("الرئيسـيـة", "الرييسيه")] // tatweel, hamza on yaa, taa marbuta
    [InlineData("على", "علي")] // alef maqsura
    [InlineData("١٨٠٠ ريال", "1800 ريال")] // Arabic-Indic digits
    [InlineData("۱۲۳", "123")] // extended Arabic-Indic digits
    [InlineData("12,400 dollars", "12400 dollars")]
    [InlineData("١٢٬٤٠٠", "12400")]
    [InlineData("Dr. HALVORSEN!", "dr halvorsen")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Fact]
    public void Keeps_decimals_and_identifiers_intact()
    {
        Assert.Equal("192.168.7.1", TextNormalizer.Normalize("192.168.7.1."));
        Assert.Equal("err-5043", TextNormalizer.Normalize("ERR-5043,"));
        Assert.Equal("4.2 days", TextNormalizer.Normalize("4.2 days"));
    }

    [Fact]
    public void Contains_matches_across_arabic_spelling_variants()
    {
        Assert.True(TextNormalizer.Contains("مدة الضمان ثلاث سنوات من تاريخ الشراء", "ثلاث سنوات"));
        Assert.True(TextNormalizer.Contains("يحصل الموظف على ٣٠ يوما", "30"));
        Assert.True(TextNormalizer.Contains("أُمُومة", "امومه"));
        Assert.False(TextNormalizer.Contains("مدة الضمان سنتان", "ثلاث سنوات"));
        Assert.False(TextNormalizer.Contains("anything", ""));
    }

    private static EvalItem Answerable(params string[][] facts) => new()
    {
        Id = "x", Answerable = true, Question = "q", Documents = ["a.txt"], Facts = facts.Select(f => f.ToList()).ToList(),
    };

    [Fact]
    public void Answerable_is_correct_only_with_all_facts_and_an_expected_citation()
    {
        var item = Answerable(["25"], ["March", "مارس"]);

        Assert.True(Grader.Grade(item, new ApiAnswer("25 days, until March", false, ["a.txt"], false, 1)).Passed);
        Assert.True(Grader.Grade(item, new ApiAnswer("٢٥ يوما حتى مارس", false, ["a.txt", "b.txt"], false, 1)).Passed);

        var missingFact = Grader.Grade(item, new ApiAnswer("25 days", false, ["a.txt"], false, 1));
        Assert.False(missingFact.Passed);
        Assert.Contains("March", missingFact.Reason);

        var wrongDoc = Grader.Grade(item, new ApiAnswer("25 days, March", false, ["b.txt"], false, 1));
        Assert.False(wrongDoc.Passed);
        Assert.Contains("no citation to an expected document", wrongDoc.Reason);

        Assert.False(Grader.Grade(item, new ApiAnswer("I could not find it.", true, [], false, 1)).Passed);
    }

    [Fact]
    public void File_names_are_compared_after_normalization()
    {
        var item = Answerable(["x"]);
        item.Documents = ["سياسة-الإجازات.txt"];

        Assert.True(Grader.Grade(item, new ApiAnswer("x", false, ["سياسة-الاجازات.txt"], false, 0)).Passed);
    }

    [Fact]
    public void Unanswerable_needs_not_found_and_no_citations()
    {
        var item = new EvalItem { Id = "u", Answerable = false, Question = "q" };

        Assert.True(Grader.Grade(item, new ApiAnswer("not found", true, [], false, 2)).Passed);
        Assert.False(Grader.Grade(item, new ApiAnswer("It is 5.", false, ["a.txt"], false, 2)).Passed);
        Assert.False(Grader.Grade(item, new ApiAnswer("not found", true, ["a.txt"], false, 2)).Passed);
    }

    [Fact]
    public void Eval_set_meets_the_d32_shape()
    {
        var set = EvalSetFile.Load(Path.Combine(AppContext.BaseDirectory, "eval-set.json"));
        var answerable = set.Items.Where(i => i.Answerable).ToList();

        Assert.True(answerable.Count >= 20, $"answerable: {answerable.Count}");
        Assert.True(set.Items.Count(i => !i.Answerable) >= 6);
        Assert.True(answerable.Count(i => i.Lang == "ar") * 100 >= answerable.Count * 40, "at least 40% of answerable questions in Arabic");
        foreach (var type in new[] { "single", "multi", "cross-doc", "code", "summarize", "compare" })
        {
            Assert.Contains(answerable, i => i.Type == type);
        }

        Assert.Equal(set.Items.Count, set.Items.Select(i => i.Id).Distinct().Count());
        foreach (var item in answerable)
        {
            Assert.NotEmpty(item.Facts);
            Assert.NotEmpty(item.Documents);
            Assert.All(item.Documents, d => Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Corpus", d)), d));
        }
    }
}
