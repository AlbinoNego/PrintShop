using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PrintShop.Models;
using PrintShop.Services;
using System.Globalization;

namespace PrintShop.Controllers;

public class AdminController : Controller
{
    private readonly AdminAuthService _auth;
    private readonly OrderQueueService _queue;
    private readonly AdminSettingsService _settings;
    private readonly PrinterService _printer;
    private readonly PrinterRegistryService _printerRegistry;
    private readonly AdminAuditService _audit;

    public AdminController(
        AdminAuthService auth,
        OrderQueueService queue,
        AdminSettingsService settings,
        PrinterService printer,
        PrinterRegistryService printerRegistry,
        AdminAuditService audit)
    {
        _auth = auth;
        _audit = audit;
        _queue = queue;
        _settings = settings;
        _printer = printer;
        _printerRegistry = printerRegistry;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        returnUrl = SafeReturnUrl(returnUrl);
        if (AdminAuthService.IsLoggedIn(HttpContext))
        {
            return LocalRedirect(returnUrl);
        }

        ViewBag.ReturnUrl = returnUrl;
        return View();
    }

    // Aceita somente caminhos do proprio site para evitar redirecionamento para dominio externo.
    private string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/Order/Queue";

    [HttpGet]
    public async Task<IActionResult> Reports(DateTime? from, DateTime? to)
    {
        if (!AdminAuthService.IsLoggedIn(HttpContext))
        {
            return RedirectToAction("Login", new { returnUrl = "/Admin/Reports" });
        }

        var orders = FilterByPeriod(await _queue.GetAllAsync(), from, to);
        var report = BuildReport(orders);
        report.From = from?.Date;
        report.To = to?.Date;
        return View(report);
    }

    // GET /Admin/ExportOrders?from=2026-11-01&to=2026-11-30 - planilha CSV para abrir no Excel
    [HttpGet]
    public async Task<IActionResult> ExportOrders(DateTime? from, DateTime? to)
    {
        if (!AdminAuthService.IsLoggedIn(HttpContext)) return Unauthorized();

        var orders = FilterByPeriod(await _queue.GetAllAsync(), from, to)
            .Where(order => order.Status != OrderStatus.Draft)
            .OrderBy(order => order.CreatedAt)
            .ToList();
        // A planilha leva nome e telefone dos clientes: fica registrado quem exportou.
        _audit.Log(HttpContext, "Exportou pedidos", "", $"{from:dd/MM/yyyy} a {to:dd/MM/yyyy} ({orders.Count} pedidos)");

        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var csv = new System.Text.StringBuilder();
        csv.AppendLine(string.Join(";", "Código", "Data", "Status", "Cliente", "Telefone", "Recebimento", "Pagamento",
            "Pago", "Arquivos", "Páginas", "Páginas impressas", "Cor", "Papel", "Lados", "Plastificação", "Valor"));
        foreach (var order in orders)
        {
            csv.AppendLine(string.Join(";", new[]
            {
                order.Id,
                order.CreatedAt.ToString("dd/MM/yyyy HH:mm", culture),
                GetStatusLabel(order.Status),
                order.CustomerName,
                order.CustomerPhone,
                order.FulfillmentMethod == FulfillmentMethod.Delivery ? "Entrega" : "Retirada",
                order.PaymentMethod == PaymentMethod.Pix ? "PIX" : "Na loja",
                order.PaymentConfirmed ? "Sim" : "Não",
                order.Files.Count.ToString(culture),
                GetTotalPages(order).ToString(culture),
                GetTotalPrintedPages(order).ToString(culture),
                order.Color == PrintColor.Color ? "Colorido" : "Preto e branco",
                order.PaperType.ToString().Replace("_", " "),
                order.Sides == PrintSides.BothSides ? "Frente e verso" : "Apenas frente",
                order.Laminate ? "Sim" : "Não",
                order.TotalPrice.ToString("F2", culture)
            }.Select(CsvCell)));
        }

        // BOM para o Excel reconhecer acentos em UTF-8.
        var bytes = System.Text.Encoding.UTF8.GetPreamble()
            .Concat(System.Text.Encoding.UTF8.GetBytes(csv.ToString()))
            .ToArray();
        var period = from.HasValue || to.HasValue
            ? $"{from:yyyy-MM-dd}_a_{to:yyyy-MM-dd}"
            : "todos";
        return File(bytes, "text/csv; charset=utf-8", $"pedidos_{period}.csv");
    }

    private static List<PrintOrder> FilterByPeriod(List<PrintOrder> orders, DateTime? from, DateTime? to) =>
        orders
            .Where(order => !from.HasValue || order.CreatedAt.Date >= from.Value.Date)
            .Where(order => !to.HasValue || order.CreatedAt.Date <= to.Value.Date)
            .ToList();

    // Nome de cliente vem do formulario publico: celulas iniciadas por = + - @ virariam formula no Excel.
    private static string CsvCell(string value)
    {
        value ??= "";
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    [HttpPost]
    [EnableRateLimiting("admin-login")]
    public IActionResult Login(string username, string password, string? returnUrl = null)
    {
        returnUrl = SafeReturnUrl(returnUrl);
        var user = _auth.Validate(username, password);
        if (user == null)
        {
            _audit.Log(username ?? "", "Login recusado", "", "", HttpContext.Connection.RemoteIpAddress?.ToString() ?? "");
            TempData["Error"] = "Usuário ou senha inválidos.";
            ViewBag.ReturnUrl = returnUrl;
            return View();
        }

        AdminAuthService.SignIn(HttpContext, user.Username);
        _audit.Log(HttpContext, "Login");
        return LocalRedirect(returnUrl);
    }

    [HttpPost]
    public IActionResult Logout()
    {
        if (AdminAuthService.IsLoggedIn(HttpContext)) _audit.Log(HttpContext, "Logout");
        HttpContext.Session.Clear();
        return RedirectToAction("Index", "Home");
    }

    [HttpGet]
    public IActionResult Settings()
    {
        if (!AdminAuthService.IsLoggedIn(HttpContext))
        {
            return RedirectToAction("Login", new { returnUrl = "/Admin/Settings" });
        }

        ViewBag.Printers = _printer.GetAvailablePrinters();
        return View(_settings.Get());
    }

    [HttpGet]
    public IActionResult Printers(PrintJobStatus? status = null)
    {
        if (!AdminAuthService.IsLoggedIn(HttpContext))
        {
            return RedirectToAction("Login", new { returnUrl = "/Admin/Printers" });
        }

        return View(new PrinterManagementViewModel
        {
            Printers = _printerRegistry.GetPrinters(),
            Jobs = _printerRegistry.GetRecentJobs(status),
            SystemPrinters = _printer.GetAvailablePrinters(),
            Summary = _printerRegistry.GetJobSummary(),
            SelectedStatus = status
        });
    }

    [HttpPost]
    public IActionResult AddPrinter(string name, string systemName, bool supportsColor, bool supportsA3, bool supportsDuplex)
    {
        if (!AdminAuthService.IsLoggedIn(HttpContext)) return Unauthorized();

        try
        {
            _printerRegistry.AddPrinter(new PrinterDefinition
            {
                Name = name,
                SystemName = systemName,
                SupportsColor = supportsColor,
                SupportsA3 = supportsA3,
                SupportsDuplex = supportsDuplex
            });
            _audit.Log(HttpContext, "Cadastrou impressora", name ?? "",
                $"Windows: {systemName}; cor={supportsColor}; A3={supportsA3}; frente e verso={supportsDuplex}");
            TempData["Success"] = "Impressora cadastrada.";
        }
        catch (ArgumentException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Printers));
    }

    [HttpPost]
    public IActionResult TogglePrinterPause(string id)
    {
        if (!AdminAuthService.IsLoggedIn(HttpContext)) return Unauthorized();

        _printerRegistry.TogglePause(id);
        var printer = _printerRegistry.GetPrinters().FirstOrDefault(item => item.Id == id);
        _audit.Log(HttpContext, printer?.IsPaused == true ? "Pausou impressora" : "Reativou impressora", printer?.Name ?? id);
        TempData["Success"] = "Status da impressora atualizado.";
        return RedirectToAction(nameof(Printers));
    }

    [HttpPost]
    public IActionResult RemovePrinter(string id)
    {
        if (!AdminAuthService.IsLoggedIn(HttpContext)) return Unauthorized();

        var printer = _printerRegistry.GetPrinters().FirstOrDefault(item => item.Id == id);
        switch (_printerRegistry.RemovePrinter(id))
        {
            case PrinterRemovalResult.Removed:
                _audit.Log(HttpContext, "Removeu impressora", printer?.Name ?? id, $"Windows: {printer?.SystemName}");
                TempData["Success"] = $"Impressora \"{printer?.Name}\" removida.";
                break;
            case PrinterRemovalResult.Printing:
                TempData["Error"] = "Esta impressora está imprimindo agora. Pause-a e aguarde o trabalho terminar antes de remover.";
                break;
            default:
                TempData["Error"] = "Impressora não encontrada.";
                break;
        }

        return RedirectToAction(nameof(Printers));
    }

    [HttpPost]
    public async Task<IActionResult> RetryPrintJob(long id)
    {
        if (!AdminAuthService.IsLoggedIn(HttpContext)) return Unauthorized();

        var orderId = await _printerRegistry.RetryJobAsync(id);
        if (orderId == null)
        {
            TempData["Error"] = "Somente trabalhos com falha ou cancelados podem ser reenviados.";
            return RedirectToAction(nameof(Printers));
        }

        _audit.Log(HttpContext, "Reenviou trabalho de impressão", $"Pedido #{orderId}", $"Trabalho {id}");
        var order = await _queue.GetAsync(orderId);
        if (order != null && order.Status != OrderStatus.Cancelled)
        {
            order.Status = OrderStatus.PaymentConfirmed;
            await _queue.UpdateAsync(order);
        }

        TempData["Success"] = "Arquivos pendentes deste pedido foram reenviados para a fila de impressão.";
        return RedirectToAction(nameof(Printers));
    }

    [HttpPost]
    public IActionResult Settings(IFormCollection form)
    {
        if (!AdminAuthService.IsLoggedIn(HttpContext)) return Unauthorized();

        var current = _settings.Get();
        var settings = new AdminSettings
        {
            BlackAndWhitePrice = ReadDecimal(form, nameof(AdminSettings.BlackAndWhitePrice), current.BlackAndWhitePrice),
            ColorPrice = ReadDecimal(form, nameof(AdminSettings.ColorPrice), current.ColorPrice),
            A4_90gExtra = ReadDecimal(form, nameof(AdminSettings.A4_90gExtra), current.A4_90gExtra),
            A3_75gExtra = ReadDecimal(form, nameof(AdminSettings.A3_75gExtra), current.A3_75gExtra),
            GlossyExtra = ReadDecimal(form, nameof(AdminSettings.GlossyExtra), current.GlossyExtra),
            LaminatePrice = ReadDecimal(form, nameof(AdminSettings.LaminatePrice), current.LaminatePrice),
            DeliveryFee = ReadDecimal(form, nameof(AdminSettings.DeliveryFee), current.DeliveryFee),
            DefaultPrinter = form[nameof(AdminSettings.DefaultPrinter)].ToString(),
            PdfPrinter = form[nameof(AdminSettings.PdfPrinter)].ToString(),
            WordPrinter = form[nameof(AdminSettings.WordPrinter)].ToString(),
            PowerPointPrinter = form[nameof(AdminSettings.PowerPointPrinter)].ToString(),
            ImagePrinter = form[nameof(AdminSettings.ImagePrinter)].ToString(),
            AutomaticPrintingEnabled = form[nameof(AdminSettings.AutomaticPrintingEnabled)].Any(value => value == "true")
        };

        _settings.Save(settings);
        _audit.Log(HttpContext, "Alterou configurações", "", DescribeSettingsChanges(current, settings));
        TempData["Success"] = "Configurações salvas.";
        return RedirectToAction("Settings");
    }

    [HttpGet]
    public IActionResult Audit(string? search)
    {
        if (!AdminAuthService.IsLoggedIn(HttpContext))
        {
            return RedirectToAction("Login", new { returnUrl = "/Admin/Audit" });
        }

        ViewBag.Search = search ?? "";
        return View(_audit.GetRecent(300, search));
    }

    // Guarda o antes e depois de cada campo alterado, para precos e impressoras.
    private static string DescribeSettingsChanges(AdminSettings before, AdminSettings after)
    {
        var changes = typeof(AdminSettings).GetProperties()
            .Select(property => (property.Name, Before: property.GetValue(before)?.ToString() ?? "", After: property.GetValue(after)?.ToString() ?? ""))
            .Where(item => item.Before != item.After)
            .Select(item => $"{item.Name}: {item.Before} -> {item.After}")
            .ToList();
        return changes.Count == 0 ? "Sem alterações" : string.Join("; ", changes);
    }

    private static AdminReportViewModel BuildReport(List<PrintOrder> orders)
    {
        orders = orders
            .Where(order => order.Status != OrderStatus.Draft)
            .ToList();

        var activeOrders = orders.Where(order => order.Status != OrderStatus.Cancelled).ToList();
        var confirmedOrders = orders.Where(order => order.PaymentConfirmed).ToList();
        var completedOrders = orders
            .Where(order => order.Status == OrderStatus.Ready || order.Status == OrderStatus.Delivered)
            .ToList();

        return new AdminReportViewModel
        {
            TotalOrders = orders.Count,
            PaidOrders = confirmedOrders.Count,
            PendingOrders = orders.Count(order => order.Status == OrderStatus.PendingPayment),
            CancelledOrders = orders.Count(order => order.Status == OrderStatus.Cancelled),
            ReadyOrders = orders.Count(order => order.Status == OrderStatus.Ready),
            TotalPages = activeOrders.Sum(GetTotalPages),
            PrintedPages = orders
                .Where(order => order.Status == OrderStatus.Printing || order.Status == OrderStatus.Ready || order.Status == OrderStatus.Delivered)
                .Sum(GetTotalPrintedPages),
            TotalFiles = activeOrders.Sum(order => order.Files.Count),
            CompletedRevenue = completedOrders.Sum(order => order.TotalPrice),
            TotalRegisteredRevenue = orders.Sum(order => order.TotalPrice),
            ActiveRevenue = activeOrders.Sum(order => order.TotalPrice),
            StatusBreakdown = orders
                .GroupBy(order => GetStatusLabel(order.Status))
                .Select(group => new ReportItem { Label = group.Key, Count = group.Count(), Amount = group.Sum(order => order.TotalPrice) })
                .OrderByDescending(item => item.Count)
                .ToList(),
            ColorBreakdown = activeOrders
                .GroupBy(order => order.Color == PrintColor.Color ? "Colorido" : "Preto e branco")
                .Select(group => new ReportItem { Label = group.Key, Count = group.Count(), Amount = group.Sum(order => order.TotalPrice) })
                .OrderByDescending(item => item.Count)
                .ToList(),
            PaperBreakdown = activeOrders
                .GroupBy(order => order.PaperType.ToString().Replace("_", " "))
                .Select(group => new ReportItem { Label = group.Key, Count = group.Count(), Amount = group.Sum(order => order.TotalPrice) })
                .OrderByDescending(item => item.Count)
                .ToList(),
            PaymentBreakdown = activeOrders
                .GroupBy(order => order.PaymentMethod == PaymentMethod.Pix ? "PIX" : "Na loja")
                .Select(group => new ReportItem { Label = group.Key, Count = group.Count(), Amount = group.Sum(order => order.TotalPrice) })
                .OrderByDescending(item => item.Count)
                .ToList(),
            TopCustomers = activeOrders
                .Where(order => !string.IsNullOrWhiteSpace(order.CustomerName) || !string.IsNullOrWhiteSpace(order.CustomerPhone))
                .GroupBy(order => new { order.CustomerName, order.CustomerPhone })
                .Select(group => new CustomerReportItem
                {
                    Name = string.IsNullOrWhiteSpace(group.Key.CustomerName) ? "Sem nome" : group.Key.CustomerName,
                    Phone = group.Key.CustomerPhone,
                    Orders = group.Count(),
                    Pages = group.Sum(GetTotalPages),
                    Amount = group.Sum(order => order.TotalPrice)
                })
                .OrderByDescending(item => item.Orders)
                .ThenByDescending(item => item.Amount)
                .Take(10)
                .ToList()
        };
    }

    private static int GetTotalPages(PrintOrder order) =>
        order.Files.Sum(file => file.PageCount);

    private static int GetTotalPrintedPages(PrintOrder order) =>
        order.Files.Sum(file => file.PageCount * Math.Max(1, file.Copies));

    private static decimal ReadDecimal(IFormCollection form, string key, decimal fallback)
    {
        var value = form[key].ToString();
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var invariantResult))
        {
            return invariantResult;
        }

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out var currentCultureResult))
        {
            return currentCultureResult;
        }

        return fallback;
    }

    private static string GetStatusLabel(OrderStatus status) => status switch
    {
        OrderStatus.Draft => "Em revisão",
        OrderStatus.PendingPayment => "Aguardando pagamento",
        OrderStatus.PaymentConfirmed => "Pagamento confirmado",
        OrderStatus.Printing => "Imprimindo",
        OrderStatus.PrintFailed => "Falha na impressão",
        OrderStatus.Ready => "Pronto",
        OrderStatus.Delivered => "Entregue",
        OrderStatus.Cancelled => "Cancelado",
        _ => status.ToString()
    };
}
