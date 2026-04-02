using PrintShop.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PrintShop.Services;

public class PrinterService
{
    private readonly ILogger<PrinterService> _logger;
    private readonly IWebHostEnvironment _env;

    public PrinterService(ILogger<PrinterService> logger, IWebHostEnvironment env)
    {
        _logger = logger;
        _env = env;
    }

    public async Task<bool> PrintOrderAsync(PrintOrder order)
    {
        try
        {
            foreach (var file in order.Files)
            {
                var filePath = Path.Combine(_env.WebRootPath, "uploads", file.StoredName);

                if (!File.Exists(filePath))
                {
                    _logger.LogWarning("Arquivo não encontrado: {File}", filePath);
                    continue;
                }

                for (int copy = 0; copy < order.Copies; copy++)
                {
                    await PrintFileAsync(filePath, file, order);
                }
            }

            return true;
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
        string arguments = BuildPrintArguments(filePath, ext, order);

        var startInfo = new ProcessStartInfo
        {
            FileName = GetPrintExecutable(ext),
            Arguments = arguments,
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        _logger.LogInformation("Enviando para impressora: {File} | Args: {Args}",
            file.OriginalName, arguments);

        using var process = Process.Start(startInfo);
        if (process != null)
        {
            await process.WaitForExitAsync();
        }
    }

    private string GetPrintExecutable(string ext) => ext switch
    {
        ".pdf" => "AcroRd32.exe", // Adobe Acrobat Reader
        ".docx" or ".doc" => "WINWORD.EXE",
        ".pptx" or ".ppt" => "POWERPNT.EXE",
        ".jpg" or ".jpeg" or ".png" => "mspaint.exe",
        _ => "notepad.exe"
    };

    private string BuildPrintArguments(string filePath, string ext, PrintOrder order)
    {
        // PDF via Adobe Reader
        if (ext == ".pdf")
        {
            var colorFlag = order.Color == PrintColor.BlackAndWhite ? "/grayscale" : "";
            return $"/t \"{filePath}\" {colorFlag}";
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
}
