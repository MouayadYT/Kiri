using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Assistant.Documents.Tests;

/// <summary>Makes PDFs to read: pages of lines of text in a standard font, with the properties the test sets.</summary>
internal static class PdfBuilder
{
    /// <summary>A PDF with one page for each entry of <paramref name="pages"/>; an entry is its lines, none for a page with no text.</summary>
    public static byte[] Create(string[][] pages, string? title = null, string? author = null)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        if (title is not null)
        {
            builder.DocumentInformation.Title = title;
        }

        if (author is not null)
        {
            builder.DocumentInformation.Author = author;
        }

        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            var y = 780.0;
            foreach (var line in lines)
            {
                page.AddText(line, 12, new PdfPoint(50, y), font);
                y -= 18;
            }
        }

        return builder.Build();
    }

    public static byte[] Create(params string[] pageTexts) => Create(pageTexts.Select(t => t.Length == 0 ? [] : t.Split('\n')).ToArray());

    /// <summary>
    /// A PDF that asks for a password: a standard security handler, with owner and user entries that no password fits. It has a page and
    /// no text a reader could get without the key.
    /// </summary>
    public static byte[] CreateEncrypted()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Filter /Standard /V 1 /R 2 /P -4 /O <" + new string('A', 64) + "> /U <" + new string('B', 64) + "> >>",
        };

        var body = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(body.Length);
            body.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xref = body.Length;
        body.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            body.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        body.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R /Encrypt 4 0 R /ID [<")
            .Append(new string('C', 32)).Append("> <").Append(new string('C', 32)).Append(">] >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(body.ToString());
    }
}
