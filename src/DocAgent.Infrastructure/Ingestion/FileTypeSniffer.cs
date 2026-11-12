using System.IO.Compression;
using System.Text;
using System.Text.Unicode;

namespace DocAgent.Infrastructure.Ingestion;

public sealed record FileSniffResult(DetectedFileType? Type, string? Error)
{
    public bool IsSupported => Type is not null;
}

/// <summary>
/// Detects the real file type from the content (SRS DOC-2). The extension is only used to tell
/// .txt from .md and to reject files whose extension contradicts their content.
/// </summary>
public static class FileTypeSniffer
{
    public const string SupportedTypesText = "PDF (.pdf), Word (.docx), plain text (.txt) and Markdown (.md)";

    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];

    public static FileSniffResult Detect(ReadOnlySpan<byte> content, string fileName, long maxDocxUncompressedBytes = 50L * 1024 * 1024, int maxDocxEntries = 2000)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (content.Length == 0)
        {
            return Reject("The file is empty.");
        }

        if (content.StartsWith(PdfMagic))
        {
            return extension == ".pdf"
                ? new(DetectedFileType.Pdf, null)
                : Reject($"The file content is a PDF but the file name ends with '{extension}'. Rename it to .pdf.");
        }

        if (content.StartsWith(ZipMagic))
        {
            if (!IsDocx(content, maxDocxUncompressedBytes, maxDocxEntries, out var zipError))
            {
                return Reject(zipError!);
            }

            return extension == ".docx"
                ? new(DetectedFileType.Docx, null)
                : Reject($"The file content is a Word document but the file name ends with '{extension}'. Rename it to .docx.");
        }

        if (!IsPlainUtf8Text(content))
        {
            return Reject($"The file content is not a supported type. Supported types: {SupportedTypesText}.");
        }

        return extension switch
        {
            ".txt" => new(DetectedFileType.PlainText, null),
            ".md" or ".markdown" => new(DetectedFileType.Markdown, null),
            ".pdf" => Reject("The file is named .pdf but its content is not a PDF."),
            ".docx" => Reject("The file is named .docx but its content is not a Word document."),
            _ => Reject($"Unsupported file extension '{extension}'. Supported types: {SupportedTypesText}."),
        };
    }

    private static FileSniffResult Reject(string error) => new(null, error);

    /// <summary>Valid UTF-8 without NUL bytes (binary files nearly always contain NULs or invalid sequences).</summary>
    public static bool IsPlainUtf8Text(ReadOnlySpan<byte> content) =>
        content.IndexOf((byte)0) < 0 && Utf8.IsValid(content);

    private static bool IsDocx(ReadOnlySpan<byte> content, long maxUncompressedBytes, int maxEntries, out string? error)
    {
        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.Entries.Count > maxEntries)
            {
                error = "The Word document contains too many parts.";
                return false;
            }

            long total = 0;
            var hasContentTypes = false;
            var hasDocument = false;
            foreach (var entry in archive.Entries)
            {
                total += entry.Length;
                if (total > maxUncompressedBytes)
                {
                    error = "The Word document expands to an unreasonable size.";
                    return false;
                }

                hasContentTypes |= entry.FullName == "[Content_Types].xml";
                hasDocument |= entry.FullName == "word/document.xml";
            }

            if (!hasContentTypes || !hasDocument)
            {
                error = $"The ZIP file is not a Word document. Supported types: {SupportedTypesText}.";
                return false;
            }

            error = null;
            return true;
        }
        catch (InvalidDataException)
        {
            error = "The file is a corrupt ZIP archive.";
            return false;
        }
    }

    /// <summary>Decodes UTF-8 text, dropping a leading byte order mark.</summary>
    public static string DecodeUtf8(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith(Encoding.UTF8.Preamble))
        {
            content = content[Encoding.UTF8.Preamble.Length..];
        }

        return Encoding.UTF8.GetString(content);
    }
}
