using PrintShop.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

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

                for (int copy = 0; copy < order.Copies; copy++)
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
                await PrintWithWindowsShellAsync(filePath, file);
                return;
            }

            var executable = ResolvePrintExecutable(ext);

            if (string.IsNullOrWhiteSpace(executable))
            {
                await PrintWithWindowsShellAsync(filePath, file);
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

    private async Task PrintWithWindowsShellAsync(string filePath, UploadedFile file)
    {
        _logger.LogInformation(
            "Tentando impressao pelo Windows: {File}",
            file.OriginalName);

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

    private static string BuildPrintArguments(string filePath, string ext, string executable, string printerName)
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
}
