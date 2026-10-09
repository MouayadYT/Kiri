using System.Text;

namespace Assistant.Documents.Tests;

/// <summary>PDFs written by hand that a parser cannot read all of.</summary>
internal static class BrokenPdf
{
    private const string GoodBox = "/MediaBox [0 0 300 300]";

    // A page size of three numbers: a parser cannot make a page of it, and throws when it is asked for that page.
    private const string BadBox = "/MediaBox [0 0 300]";

    /// <summary>
    /// A two page PDF whose second page cannot be read (its page size is malformed). The first page says
    /// <paramref name="firstPageText"/>, or cannot be read either when <paramref name="firstPageReadable"/> is false.
    /// </summary>
    public static byte[] WithUnreadableSecondPage(string firstPageText, bool firstPageReadable = true)
    {
        var first = $"BT /F1 12 Tf 50 700 Td ({firstPageText}) Tj ET";
        var second = "BT /F1 12 Tf 50 700 Td (Second page) Tj ET";
        var resources = "/Resources << /Font << /F1 7 0 R >> >>";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            $"<< /Type /Page /Parent 2 0 R {(firstPageReadable ? GoodBox : BadBox)} {resources} /Contents 5 0 R >>",
            $"<< /Type /Page /Parent 2 0 R {BadBox} {resources} /Contents 6 0 R >>",
            $"<< /Length {first.Length} >>\nstream\n{first}\nendstream",
            $"<< /Length {second.Length} >>\nstream\n{second}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
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

        body.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(body.ToString());
    }
}
