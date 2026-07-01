using Microsoft.AspNetCore.Mvc;
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

    public AdminController(
        AdminAuthService auth,
        OrderQueueService queue,
        AdminSettingsService settings,
        PrinterService printer)
    {
        _auth = auth;
        _queue = queue;
        _settings = settings;
        _printer = printer;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (AdminAuthService.IsLoggedIn(HttpContext))
        {
            return Redirect(returnUrl ?? "/Order/Queue");
        }

        ViewBag.ReturnUrl = returnUrl ?? "/Order/Queue";
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> Reports()
    {
        if (!AdminAuthService.IsLoggedIn(HttpContext))
        {
            return RedirectToAction("Login", new { returnUrl = "/Admin/Reports" });
        }

        var orders = await _queue.GetAllAsync();
        var report = BuildReport(orders);
        return View(report);
    }

    [HttpPost]
    public IActionResult Login(string username, string password, string? returnUrl = null)
    {
        if (!_auth.Validate(username, password))
        {
            TempData["Error"] = "Usuario ou senha invalidos.";
            ViewBag.ReturnUrl = returnUrl ?? "/Order/Queue";
            return View();
        }

        HttpContext.Session.SetString(AdminAuthService.SessionKey, "true");
        return Redirect(returnUrl ?? "/Order/Queue");
    }

    [HttpPost]
    public IActionResult Logout()
    {
        HttpContext.Session.Remove(AdminAuthService.SessionKey);
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
        TempData["Success"] = "Configuracoes salvas.";
        return RedirectToAction("Settings");
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
        OrderStatus.Draft => "Em revisao",
        OrderStatus.PendingPayment => "Aguardando pagamento",
        OrderStatus.PaymentConfirmed => "Pagamento confirmado",
        OrderStatus.Printing => "Imprimindo",
        OrderStatus.Ready => "Pronto",
        OrderStatus.Delivered => "Entregue",
        OrderStatus.Cancelled => "Cancelado",
        _ => status.ToString()
    };
}
