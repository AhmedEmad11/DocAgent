using System.IO.Compression;
using System.Text;
using DocAgent.Infrastructure.Ingestion;
using DocAgent.TestSupport;

namespace DocAgent.UnitTests.Hardening;

/// <summary>SEC-5: a Word file whose XML expands enormously (entity expansion, "billion laughs") must be refused, not parsed.</summary>
public sealed class DocxXmlBombTests
{
    private static byte[] DocxWithDocumentXml(string xml)
    {
        using var stream = new MemoryStream();
        stream.Write(TestFiles.Docx(["Hello there"]));
        stream.Position = 0;
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            zip.GetEntry("word/document.xml")!.Delete();
            var entry = zip.CreateEntry("word/document.xml");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }

        return stream.ToArray();
    }

    [Fact]
    public async Task Entity_expansion_in_document_xml_is_refused_quickly()
    {
        var entities = new StringBuilder("<!ENTITY a0 \"lol lol lol lol lol lol lol lol lol lol\">");
        for (var i = 1; i <= 9; i++)
        {
            var refs = string.Concat(Enumerable.Repeat($"&a{i - 1};", 10));
            entities.Append($"<!ENTITY a{i} \"{refs}\">");
        }

        var xml = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <!DOCTYPE w:document [{entities}]>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>&a9;</w:t></w:r></w:p></w:body></w:document>
            """;
        var docx = DocxWithDocumentXml(xml);

        var work = Task.Run(() => Assert.Throws<DocumentParseException>(() =>
            DocxTextExtractor.Extract(docx, new IngestionOptions(), CancellationToken.None)));

        await work.WaitAsync(TimeSpan.FromSeconds(20)); // the expansion would be about 10 GB of text
    }

    [Fact]
    public async Task External_entities_are_not_resolved()
    {
        var secret = Path.Combine(Path.GetTempPath(), "docagent-xxe-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(secret, "XXE-SECRET-CONTENT");
        try
        {
            var xml = $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <!DOCTYPE w:document [<!ENTITY xxe SYSTEM "file://{secret}">]>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>&xxe;</w:t></w:r></w:p></w:body></w:document>
                """;
            var docx = DocxWithDocumentXml(xml);

            string? text = null;
            try
            {
                var result = DocxTextExtractor.Extract(docx, new IngestionOptions(), CancellationToken.None);
                text = string.Join(" ", result.Segments.Select(s => s.Text));
            }
            catch (DocumentParseException)
            {
                // refused outright is fine too
            }

            Assert.DoesNotContain("XXE-SECRET-CONTENT", text ?? "");
        }
        finally
        {
            File.Delete(secret);
        }
    }
}
