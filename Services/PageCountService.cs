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
        var matches = Regex.Matches(content, @"/Type\s*/Page\b");

        return Math.Max(1, matches.Count);
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
