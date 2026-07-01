using PrintShop.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrintShop.Services;

public class PrinterService
{
    private readonly ILogger<PrinterService> _logger;
    private readonly FileStorageService _fileStorage;
    private readonly IConfiguration _configuration;
    private readonly AdminSettingsService _settings;

    public PrinterService(
        ILogger<PrinterService> logger,
        FileStorageService fileStorage,
        IConfiguration configuration,
        AdminSettingsService settings)
    {
        _logger = logger;
        _fileStorage = fileStorage;
        _configuration = configuration;
        _settings = settings;
    }

    public async Task<bool> PrintOrderAsync(PrintOrder order)
    {
        try
        {
            var settings = _settings.Get();
            if (!settings.AutomaticPrintingEnabled)
            {
                _logger.LogWarning("Impressao automatica pausada. Pedido {OrderId} nao foi enviado.", order.Id);
                return false;
            }

            var printedFiles = 0;

            foreach (var file in order.Files)
            {
                var filePath = _fileStorage.GetPath(file.StoredName);

                if (!File.Exists(filePath))
                {
                    _logger.LogWarning("Arquivo não encontrado: {File}", filePath);
                    continue;
                }

                var fileCopies = Math.Max(1, file.Copies);
                for (int copy = 0; copy < fileCopies; copy++)
                {
                    await PrintFileAsync(filePath, file, order);
                    printedFiles++;
                }
            }

            return printedFiles > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao imprimir pedido {OrderId}", order.Id);
            return false;
        }
    }

    private async Task PrintFileAsync(string filePath, UploadedFile file, PrintOrder order)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await PrintOnWindowsAsync(filePath, file, order);
        }
        else
        {
            // Fallback para Linux/Mac (desenvolvimento)
            _logger.LogInformation("[SIMULAÇÃO] Imprimindo: {File} | Cor: {Color} | Papel: {Paper} | Plastificar: {Lam}",
                file.OriginalName, order.Color, order.PaperType, order.Laminate);
            await Task.Delay(500);
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task PrintOnWindowsAsync(string filePath, UploadedFile file, PrintOrder order)
    {
        var ext = Path.GetExtension(file.OriginalName).ToLower();
        var selectedPrinter = ResolvePrinterName(ext);
        var originalPrinter = string.IsNullOrWhiteSpace(selectedPrinter) ? "" : GetDefaultPrinterName();

        try
        {
            if (!string.IsNullOrWhiteSpace(selectedPrinter))
            {
                SetDefaultPrinter(selectedPrinter);
            }

            if (ext is ".doc" or ".docx")
            {
                if (await PrintWordWithOfficeAsync(filePath, file, order, selectedPrinter))
                {
                    return;
                }

                await PrintWithWindowsShellAsync(filePath, file, order);
                return;
            }

            if (ext is ".ppt" or ".pptx" &&
                await PrintPowerPointWithOfficeAsync(filePath, file, order, selectedPrinter))
            {
                return;
            }

            var executable = ResolvePrintExecutable(ext);

            if (string.IsNullOrWhiteSpace(executable))
            {
                await PrintWithWindowsShellAsync(filePath, file, order);
                return;
            }

            string arguments = BuildPrintArguments(filePath, ext, executable, selectedPrinter);

            _logger.LogInformation("Enviando para impressora: {File} | Impressora: {Printer} | Args: {Args}",
                file.OriginalName,
                string.IsNullOrWhiteSpace(selectedPrinter) ? "Padrao do Windows" : selectedPrinter,
                arguments);

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(startInfo);
            if (process != null)
            {
                await process.WaitForExitAsync();
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(originalPrinter))
            {
                SetDefaultPrinter(originalPrinter);
            }
        }
    }

    private async Task PrintWithWindowsShellAsync(string filePath, UploadedFile file, PrintOrder order)
    {
        _logger.LogInformation(
            "Tentando impressao pelo Windows: {File}. Orientacao solicitada: {Orientation}.",
            file.OriginalName,
            order.Orientation);

        var startInfo = new ProcessStartInfo
        {
            FileName = filePath,
            Verb = "print",
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = Process.Start(startInfo);
        if (process != null)
        {
            await process.WaitForExitAsync();
        }

        await Task.Delay(1000);
    }

    [SupportedOSPlatform("windows")]
    private Task<bool> PrintWordWithOfficeAsync(
        string filePath,
        UploadedFile file,
        PrintOrder order,
        string printerName)
    {
        return Task.Run(() =>
        {
            dynamic? word = null;
            dynamic? document = null;
            string? tempFilePath = null;
            string? tempPdfPath = null;

            try
            {
                var wordType = Type.GetTypeFromProgID("Word.Application");
                if (wordType == null)
                {
                    return false;
                }

                var wordInstance = Activator.CreateInstance(wordType);
                if (wordInstance == null)
                {
                    return false;
                }

                word = wordInstance;
                word.Visible = false;
                if (!string.IsNullOrWhiteSpace(printerName))
                {
                    word.ActivePrinter = printerName;
                }

                tempFilePath = CreateTemporaryOfficeFile(filePath);
                File.Copy(filePath, tempFilePath, overwrite: true);

                document = word.Documents.Open(tempFilePath, ReadOnly: false, Visible: false);
                if (!ShouldChangeWordOrientation(document, order.Orientation))
                {
                    _logger.LogInformation(
                        "Word ja esta em {Orientation}. Imprimindo diretamente pelo fluxo antigo: {File}",
                        order.Orientation,
                        file.OriginalName);

                    document.Close(SaveChanges: 0);
                    word.Quit(SaveChanges: 0);
                    return false;
                }

                ApplyWordOrientation(document, order.Orientation);
                document.Repaginate();
                document.Save();

                tempPdfPath = CreateTemporaryFile(".pdf");
                document.ExportAsFixedFormat(tempPdfPath, 17);
                document.Close(SaveChanges: 0);
                word.Quit(SaveChanges: 0);

                _logger.LogInformation(
                    "Imprimindo Word convertido para PDF temporario com orientacao {Orientation}: {File}",
                    order.Orientation,
                    file.OriginalName);

                PrintPdfFile(tempPdfPath, printerName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Nao foi possivel aplicar orientacao no Word para {File}. Usando fallback do Windows.",
                    file.OriginalName);
                TryCloseOfficeDocument(document);
                TryQuitOfficeApp(word);
                return false;
            }
            finally
            {
                ReleaseComObject(document);
                ReleaseComObject(word);
                DeleteTemporaryFile(tempFilePath);
                DeleteTemporaryFile(tempPdfPath);
            }
        });
    }

    [SupportedOSPlatform("windows")]
    private Task<bool> PrintPowerPointWithOfficeAsync(
        string filePath,
        UploadedFile file,
        PrintOrder order,
        string printerName)
    {
        return Task.Run(() =>
        {
            dynamic? powerPoint = null;
            dynamic? presentation = null;
            string? tempFilePath = null;
            string? tempPdfPath = null;

            try
            {
                var powerPointType = Type.GetTypeFromProgID("PowerPoint.Application");
                if (powerPointType == null)
                {
                    return false;
                }

                var powerPointInstance = Activator.CreateInstance(powerPointType);
                if (powerPointInstance == null)
                {
                    return false;
                }

                tempFilePath = CreateTemporaryOfficeFile(filePath);
                File.Copy(filePath, tempFilePath, overwrite: true);

                powerPoint = powerPointInstance;
                presentation = powerPoint.Presentations.Open(tempFilePath, -1, 0, 0);
                if (!ShouldChangePowerPointOrientation(presentation, order.Orientation))
                {
                    _logger.LogInformation(
                        "PowerPoint ja esta em {Orientation}. Imprimindo diretamente pelo fluxo antigo: {File}",
                        order.Orientation,
                        file.OriginalName);

                    presentation.Close();
                    powerPoint.Quit();
                    return false;
                }

                ApplyPowerPointOrientation(presentation, order.Orientation);
                presentation.Save();
                tempPdfPath = CreateTemporaryFile(".pdf");
                presentation.SaveAs(tempPdfPath, 32);

                if (!string.IsNullOrWhiteSpace(printerName))
                {
                    presentation.PrintOptions.ActivePrinter = printerName;
                }

                _logger.LogInformation(
                    "Imprimindo PowerPoint convertido para PDF temporario com orientacao {Orientation}: {File}",
                    order.Orientation,
                    file.OriginalName);

                presentation.Close();
                powerPoint.Quit();
                PrintPdfFile(tempPdfPath, printerName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Nao foi possivel aplicar orientacao no PowerPoint para {File}. Usando fallback configurado.",
                    file.OriginalName);
                TryCloseOfficeDocument(presentation);
                TryQuitOfficeApp(powerPoint);
                return false;
            }
            finally
            {
                ReleaseComObject(presentation);
                ReleaseComObject(powerPoint);
                DeleteTemporaryFile(tempFilePath);
                DeleteTemporaryFile(tempPdfPath);
            }
        });
    }

    private string? ResolvePrintExecutable(string ext)
    {
        var configuredPath = ext switch
        {
            ".pdf" => _configuration["PrintShop:PrintExecutables:Pdf"],
            ".docx" or ".doc" => _configuration["PrintShop:PrintExecutables:Word"],
            ".pptx" or ".ppt" => _configuration["PrintShop:PrintExecutables:PowerPoint"],
            ".jpg" or ".jpeg" or ".png" => _configuration["PrintShop:PrintExecutables:Image"],
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? configuredPath : configuredPath;
        }

        return ext switch
        {
            ".pdf" => FindFirstExistingPath(GetPdfExecutableCandidates()),
            ".docx" or ".doc" => null,
            ".pptx" or ".ppt" => "POWERPNT.EXE",
            ".jpg" or ".jpeg" or ".png" => "mspaint.exe",
            _ => null
        };
    }

    private static IEnumerable<string> GetPdfExecutableCandidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Path.Combine(programFiles, "Adobe", "Acrobat DC", "Acrobat", "Acrobat.exe");
        yield return Path.Combine(programFiles, "Adobe", "Acrobat Reader DC", "Reader", "AcroRd32.exe");
        yield return Path.Combine(programFiles, "Adobe", "Acrobat Reader", "Reader", "AcroRd32.exe");
        yield return Path.Combine(programFilesX86, "Adobe", "Acrobat Reader DC", "Reader", "AcroRd32.exe");
        yield return Path.Combine(programFilesX86, "Adobe", "Acrobat Reader", "Reader", "AcroRd32.exe");
        yield return Path.Combine(programFiles, "SumatraPDF", "SumatraPDF.exe");
        yield return Path.Combine(programFilesX86, "SumatraPDF", "SumatraPDF.exe");
        yield return Path.Combine(localAppData, "SumatraPDF", "SumatraPDF.exe");
    }

    private static string? FindFirstExistingPath(IEnumerable<string> candidates) =>
        candidates.FirstOrDefault(File.Exists);

    private string ResolvePrinterName(string ext)
    {
        var settings = _settings.Get();
        var specific = ext switch
        {
            ".pdf" => settings.PdfPrinter,
            ".docx" or ".doc" => settings.WordPrinter,
            ".pptx" or ".ppt" => settings.PowerPointPrinter,
            ".jpg" or ".jpeg" or ".png" => settings.ImagePrinter,
            _ => ""
        };

        return string.IsNullOrWhiteSpace(specific) ? settings.DefaultPrinter : specific;
    }

    private static string BuildPrintArguments(
        string filePath,
        string ext,
        string executable,
        string printerName)
    {
        if (ext == ".pdf")
        {
            var executableName = Path.GetFileName(executable);
            if (string.Equals(executableName, "SumatraPDF.exe", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(printerName)
                    ? $"-print-to-default -silent \"{filePath}\""
                    : $"-print-to \"{printerName}\" -silent \"{filePath}\"";
            }

            return string.IsNullOrWhiteSpace(printerName)
                ? $"/t \"{filePath}\""
                : $"/t \"{filePath}\" \"{printerName}\"";
        }

        // Word, PowerPoint e imagens via shell print
        return $"/p \"{filePath}\"";
    }

    private void PrintPdfFile(string filePath, string printerName)
    {
        var executable = ResolvePrintExecutable(".pdf");
        if (string.IsNullOrWhiteSpace(executable))
        {
            var shellStartInfo = new ProcessStartInfo
            {
                FileName = filePath,
                Verb = "print",
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var shellProcess = Process.Start(shellStartInfo);
            shellProcess?.WaitForExit();
            Thread.Sleep(5000);
            return;
        }

        var arguments = BuildPrintArguments(filePath, ".pdf", executable, printerName);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = Process.Start(startInfo);
        process?.WaitForExit();
        Thread.Sleep(5000);
    }

    /// <summary>
    /// Retorna a lista de impressoras disponíveis no sistema Windows.
    /// </summary>
    public List<string> GetAvailablePrinters()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new List<string> { "[Simulação - não Windows]" };

        try
        {
            // Usa WMI via PowerShell para listar impressoras
            var psi = new ProcessStartInfo("powershell",
                "-Command \"Get-Printer | Select-Object -ExpandProperty Name\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd() ?? "";
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.Trim())
                         .Where(s => !string.IsNullOrEmpty(s))
                         .ToList();
        }
        catch
        {
            return new List<string> { "Impressora Padrão" };
        }
    }

    private static string GetDefaultPrinterName()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -Command \"(Get-CimInstance Win32_Printer | Where-Object Default -eq $true | Select-Object -First 1 -ExpandProperty Name)\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            var output = process?.StandardOutput.ReadToEnd() ?? "";
            process?.WaitForExit();
            return output.Trim();
        }
        catch
        {
            return "";
        }
    }

    private static void SetDefaultPrinter(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName)) return;

        var escaped = printerName.Replace("'", "''");
        var psi = new ProcessStartInfo("powershell",
            $"-NoProfile -Command \"(New-Object -ComObject WScript.Network).SetDefaultPrinter('{escaped}')\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        process?.WaitForExit();
    }

    private static void ApplyWordOrientation(dynamic document, PrintOrientation orientation)
    {
        var officeOrientation = orientation == PrintOrientation.Landscape ? 1 : 0;

        foreach (dynamic section in document.Sections)
        {
            section.PageSetup.Orientation = officeOrientation;
        }

        document.PageSetup.Orientation = officeOrientation;
    }

    private static bool ShouldChangeWordOrientation(dynamic document, PrintOrientation requestedOrientation)
    {
        var targetOrientation = requestedOrientation == PrintOrientation.Landscape ? 1 : 0;

        foreach (dynamic section in document.Sections)
        {
            if ((int)section.PageSetup.Orientation != targetOrientation)
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyPowerPointOrientation(dynamic presentation, PrintOrientation orientation)
    {
        var targetOrientation = orientation == PrintOrientation.Landscape ? 2 : 1;
        presentation.PageSetup.SlideOrientation = targetOrientation;

        if (orientation == PrintOrientation.Landscape &&
            presentation.PageSetup.SlideHeight > presentation.PageSetup.SlideWidth)
        {
            SwapPowerPointSlideSize(presentation);
        }
        else if (orientation == PrintOrientation.Portrait &&
            presentation.PageSetup.SlideWidth > presentation.PageSetup.SlideHeight)
        {
            SwapPowerPointSlideSize(presentation);
        }
    }

    private static bool ShouldChangePowerPointOrientation(dynamic presentation, PrintOrientation requestedOrientation)
    {
        var slideOrientation = (int)presentation.PageSetup.SlideOrientation;
        var isLandscapeByFlag = slideOrientation == 2;
        var isLandscapeBySize = presentation.PageSetup.SlideWidth >= presentation.PageSetup.SlideHeight;
        var requestedLandscape = requestedOrientation == PrintOrientation.Landscape;

        return isLandscapeByFlag != requestedLandscape || isLandscapeBySize != requestedLandscape;
    }

    private static void SwapPowerPointSlideSize(dynamic presentation)
    {
        var width = presentation.PageSetup.SlideWidth;
        presentation.PageSetup.SlideWidth = presentation.PageSetup.SlideHeight;
        presentation.PageSetup.SlideHeight = width;
    }

    private static string CreateTemporaryOfficeFile(string sourcePath)
    {
        return CreateTemporaryFile(Path.GetExtension(sourcePath), Path.GetFileNameWithoutExtension(sourcePath));
    }

    private static string CreateTemporaryFile(string extension, string? baseName = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PrintShop");
        Directory.CreateDirectory(directory);

        if (!extension.StartsWith('.'))
        {
            extension = $".{extension}";
        }

        var safeBaseName = string.IsNullOrWhiteSpace(baseName) ? "printshop" : baseName;
        var fileName = $"{safeBaseName}-{Guid.NewGuid():N}{extension}";
        return Path.Combine(directory, fileName);
    }

    private static void DeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary files are best-effort cleanup.
        }
    }

    private static void TryCloseOfficeDocument(dynamic? document)
    {
        if (document == null) return;

        try
        {
            document.Close(SaveChanges: 0);
        }
        catch
        {
            try
            {
                document.Close();
            }
            catch
            {
                // Best effort cleanup for Office COM automation.
            }
        }
    }

    private static void TryQuitOfficeApp(dynamic? app)
    {
        if (app == null) return;

        try
        {
            app.Quit(SaveChanges: 0);
        }
        catch
        {
            try
            {
                app.Quit();
            }
            catch
            {
                // Best effort cleanup for Office COM automation.
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReleaseComObject(object? value)
    {
        if (value == null) return;

        try
        {
            if (Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }
        catch
        {
            // Avoid failing the print flow while releasing unmanaged COM objects.
        }
    }
}
