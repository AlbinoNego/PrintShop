using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PrintShop.Services;

public class PageCountService
{
    // Word e PowerPoint nao aguentam varias automacoes ao mesmo tempo; uma contagem por vez.
    private static readonly SemaphoreSlim OfficeLock = new(1, 1);
    private static readonly TimeSpan OfficeTimeout = TimeSpan.FromSeconds(60);

    private readonly ILogger<PageCountService>? _logger;

    public PageCountService(ILogger<PageCountService>? logger = null)
    {
        _logger = logger;
    }

    public int CountPages(string filePath, string originalFileName)
    {
        var ext = Path.GetExtension(originalFileName).ToLowerInvariant();

        return ext switch
        {
            ".pdf" => CountPdfPages(filePath),
            ".jpg" or ".jpeg" or ".png" => 1,
            ".pptx" => CountPowerPointSlides(filePath),
            // O numero de paginas do Word depende da paginacao feita pelo proprio Word;
            // docProps/app.xml pode estar desatualizado ou editado pelo cliente.
            ".docx" => CountWithWord(filePath) ?? CountWordPages(filePath),
            ".doc" => CountWithWord(filePath) ?? LogFallback(originalFileName),
            ".ppt" => CountWithPowerPoint(filePath) ?? LogFallback(originalFileName),
            _ => 1
        };
    }

    private int LogFallback(string originalFileName)
    {
        _logger?.LogWarning(
            "Nao foi possivel contar as paginas de {File} pelo Office; cobrando 1 pagina. Confira no balcao.",
            originalFileName);
        return 1;
    }

    private int? CountWithWord(string filePath) => RunOffice("Word.Application", filePath, (app, path) =>
    {
        dynamic? document = null;
        try
        {
            document = app.Documents.Open(path, ReadOnly: true, AddToRecentFiles: false, Visible: false);
            return (int)document.ComputeStatistics(2); // wdStatisticPages
        }
        finally
        {
            try { document?.Close(SaveChanges: 0); } catch { /* documento ja fechado */ }
            ReleaseCom(document);
        }
    });

    private int? CountWithPowerPoint(string filePath) => RunOffice("PowerPoint.Application", filePath, (app, path) =>
    {
        dynamic? presentation = null;
        try
        {
            presentation = app.Presentations.Open(path, -1, 0, 0); // ReadOnly, Untitled=false, WithWindow=false
            return (int)presentation.Slides.Count;
        }
        finally
        {
            try { presentation?.Close(); } catch { /* apresentacao ja fechada */ }
            ReleaseCom(presentation);
        }
    });

    private int? RunOffice(string progId, string filePath, Func<dynamic, string, int> count)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var appType = Type.GetTypeFromProgID(progId);
        if (appType == null)
        {
            _logger?.LogWarning("{ProgId} nao esta instalado neste servidor.", progId);
            return null;
        }

        if (!OfficeLock.Wait(OfficeTimeout)) return null;
        try
        {
            var task = Task.Run(() =>
            {
                dynamic? app = null;
                try
                {
                    app = Activator.CreateInstance(appType);
                    if (app == null) return (int?)null;
                    try { app.DisplayAlerts = 0; } catch { /* PowerPoint usa outro tipo de enum */ }
                    return Math.Max(1, count(app, filePath));
                }
                finally
                {
                    try { app?.Quit(); } catch { /* aplicativo ja encerrado */ }
                    ReleaseCom(app);
                }
            });

            if (task.Wait(OfficeTimeout)) return task.Result;
            _logger?.LogWarning("{ProgId} demorou mais de {Seconds}s para contar paginas.", progId, OfficeTimeout.TotalSeconds);
            return null;
        }
        catch (AggregateException ex)
        {
            _logger?.LogWarning(ex.InnerException, "Falha ao contar paginas com {ProgId}.", progId);
            return null;
        }
        finally
        {
            OfficeLock.Release();
        }
    }

    private static void ReleaseCom(object? comObject)
    {
        if (comObject != null && OperatingSystem.IsWindows() && System.Runtime.InteropServices.Marshal.IsComObject(comObject))
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(comObject);
        }
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
