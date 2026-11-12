using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DocAgent.Infrastructure.Ingestion;

/// <summary>Markdown to plain text per heading section (the heading text becomes the chunk's Section).</summary>
public static class MarkdownTextExtractor
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseGridTables().Build();

    public static ExtractedDocument Extract(byte[] content, CancellationToken ct)
    {
        var markdown = TextCleaner.Clean(FileTypeSniffer.DecodeUtf8(content));
        var document = Markdig.Markdown.Parse(markdown, Pipeline);

        var segments = new List<TextSegment>();
        var current = new StringBuilder();
        string? section = null;

        void Flush()
        {
            var text = current.ToString().Trim();
            if (text.Length > 0)
            {
                segments.Add(new TextSegment(text, Section: section));
            }

            current.Clear();
        }

        foreach (var block in document)
        {
            ct.ThrowIfCancellationRequested();
            if (block is HeadingBlock heading)
            {
                Flush();
                section = Limit(InlineText(heading.Inline).Trim());
                if (section.Length == 0)
                {
                    section = null;
                }
                else
                {
                    current.Append(section).Append("\n\n");
                }

                continue;
            }

            AppendBlock(block, current);
        }

        Flush();
        return new ExtractedDocument(segments);
    }

    private static string Limit(string value) => value.Length <= 200 ? value : value[..200];

    private static void AppendBlock(Block block, StringBuilder sb)
    {
        switch (block)
        {
            case LeafBlock { Inline: { } inline } when block is not CodeBlock:
                sb.Append(InlineText(inline)).Append("\n\n");
                break;
            case FencedCodeBlock or CodeBlock:
                sb.Append(((LeafBlock)block).Lines.ToString()).Append("\n\n");
                break;
            case HtmlBlock or ThematicBreakBlock or LinkReferenceDefinitionGroup:
                break;
            case Markdig.Extensions.Tables.Table table:
                foreach (var row in table.OfType<Markdig.Extensions.Tables.TableRow>())
                {
                    var cells = row.OfType<Markdig.Extensions.Tables.TableCell>().Select(CellText);
                    sb.Append(string.Join(" | ", cells)).Append('\n');
                }

                sb.Append('\n');
                break;
            case ContainerBlock container: // lists, list items, quotes
                foreach (var child in container)
                {
                    AppendBlock(child, sb);
                }

                break;
        }
    }

    private static string CellText(Markdig.Extensions.Tables.TableCell cell)
    {
        var sb = new StringBuilder();
        foreach (var child in cell)
        {
            AppendBlock(child, sb);
        }

        return sb.ToString().Trim();
    }

    private static string InlineText(ContainerInline? inline)
    {
        var sb = new StringBuilder();
        AppendInline(inline, sb);
        return sb.ToString();
    }

    private static void AppendInline(Inline? inline, StringBuilder sb)
    {
        for (; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case LineBreakInline lineBreak:
                    sb.Append(lineBreak.IsHard ? '\n' : ' ');
                    break;
                case AutolinkInline auto:
                    sb.Append(auto.Url);
                    break;
                case HtmlInline or HtmlEntityInline:
                    if (inline is HtmlEntityInline entity)
                    {
                        sb.Append(entity.Transcoded.ToString());
                    }

                    break;
                case ContainerInline container: // emphasis, links, images (alt text)
                    AppendInline(container.FirstChild, sb);
                    break;
            }
        }
    }
}
