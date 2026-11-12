# Test fixtures

| File | What / provenance | License |
|---|---|---|
| `NotoNaskhArabic-Regular.ttf` | Noto Naskh Arabic (hinted, Regular) from https://github.com/notofonts/notofonts.github.io, used by the tests to build Arabic PDFs with PdfPig's `PdfDocumentBuilder`. | SIL Open Font License 1.1, Copyright 2022 The Noto Project Authors (https://openfontlicense.org) |
| `arabic-chromium.pdf` | A realistic Arabic PDF produced by Chromium (`page.pdf()` via Playwright) from an RTL HTML page: shaped glyphs in **visual order** with Arabic presentation forms, like most real-world PDFs. Page 1 is right-to-left (headline, wrapped paragraphs, a mixed Arabic/English/number line, parentheses, diacritics); page 2 is a left-to-right English page quoting one Arabic word. Embeds a subset of DejaVu Sans. | Document content: public domain / written for this project. Embedded DejaVu Sans glyphs: Bitstream Vera license (free to embed and redistribute). |

To regenerate `arabic-chromium.pdf`, render the HTML shown in `Ingestion/ArabicPdfTests.cs` (the `ExpectedPage1Lines` constants hold the text) with Chromium to PDF, format A5.
