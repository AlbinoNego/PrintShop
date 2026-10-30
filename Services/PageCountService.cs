using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PrintShop.Services;

public class PageCountService
{
    public int CountPages(string filePath, string originalFileName)
    {
        var ext = Path.GetExtension(originalFileName).ToLowerInvariant();

        return ext switch
        {
            ".pdf" => CountPdfPages(filePath),
            ".jpg" or ".jpeg" or ".png" => 1,
            ".pptx" => CountPowerPointSlides(filePath),
            ".docx" => CountWordPages(filePath),
            _ => 1
        };
    }

    private static int CountPdfPages(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, System.Text.Encoding.Latin1);
        var content = reader.ReadToEnd();
        var pages = Regex.Matches(content, @"/Type\s*/Page\b").Count;

        // PDFs modernos podem guardar os objetos de pagina comprimidos em object streams;
        // sem descompactar, um documento de muitas paginas seria cobrado como 1.
        foreach (var objectStream in ReadCompressedObjectStreams(content))
        {
            pages += Regex.Matches(objectStream, @"/Type\s*/Page\b").Count;
        }

        return Math.Max(1, pages);
    }

    private static IEnumerable<string> ReadCompressedObjectStreams(string content)
    {
        foreach (Match type in Regex.Matches(content, @"/Type\s*/ObjStm\b"))
        {
            var dictionaryStart = content.LastIndexOf(" obj", type.Index, StringComparison.Ordinal);
            var streamKeyword = content.IndexOf("stream", type.Index, StringComparison.Ordinal);
            if (dictionaryStart < 0 || streamKeyword < 0) continue;
            if (content.IndexOf("/FlateDecode", dictionaryStart, streamKeyword - dictionaryStart, StringComparison.Ordinal) < 0) continue;

            var start = streamKeyword + "stream".Length;
            if (start < content.Length && content[start] == '\r') start++;
            if (start < content.Length && content[start] == '\n') start++;
            var end = content.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) continue;

            string? decoded = null;
            try
            {
                var compressed = System.Text.Encoding.Latin1.GetBytes(content.Substring(start, end - start));
                using var input = new MemoryStream(compressed);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new StreamReader(zlib, System.Text.Encoding.Latin1);
                decoded = output.ReadToEnd();
            }
            catch (InvalidDataException)
            {
                // Stream danificado: fica so com a contagem dos objetos nao comprimidos.
            }

            if (decoded != null) yield return decoded;
        }
    }

    private static int CountPowerPointSlides(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var slides = archive.Entries.Count(entry =>
            entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
            entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

        return Math.Max(1, slides);
    }

    private static int CountWordPages(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var appProperties = archive.GetEntry("docProps/app.xml");
        if (appProperties == null) return 1;

        using var stream = appProperties.Open();
        var document = XDocument.Load(stream);
        var pages = document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Pages")
            ?.Value;

        return int.TryParse(pages, out var pageCount)
            ? Math.Max(1, pageCount)
            : 1;
    }
}
