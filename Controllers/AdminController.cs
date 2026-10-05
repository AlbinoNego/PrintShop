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
    private readonly AdminUserService _users;

    public AdminController(
        AdminAuthService auth,
        OrderQueueService queue,
        AdminSettingsService settings,
        PrinterService printer,
        PrinterRegistryService printerRegistry,
        AdminUserService users)
    {
        _auth = auth;
        _queue = queue;
        _settings = settings;
        _printer = printer;
        _printerRegistry = printerRegistry;
        _users = users;
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
    [EnableRateLimiting("admin-login")]
    public IActionResult Login(string username, string password, string? returnUrl = null)
    {
        var user = _auth.Validate(username, password);
        if (user == null)
        {
            TempData["Error"] = "Usuario ou senha invalidos.";
            ViewBag.ReturnUrl = returnUrl ?? "/Order/Queue";
            return View();
        }

        HttpContext.Session.Clear();
        HttpContext.Session.SetString(AdminAuthService.SessionKey, "true");
        HttpContext.Session.SetString(AdminAuthService.UserIdSessionKey, user.Id);
        HttpContext.Session.SetString(AdminAuthService.UsernameSessionKey, user.Username);
        return Redirect(returnUrl ?? "/Order/Queue");
    }

    [HttpPost]
    public IActionResult Logout()
    {
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
        TempData["Success"] = "Status da impressora atualizado.";
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

        var order = await _queue.GetAsync(orderId);
        if (order != null && order.Status != OrderStatus.Cancelled)
        {
            order.Status = OrderStatus.PaymentConfirmed;
            await _queue.UpdateAsync(order);
        }

        TempData["Success"] = "Arquivos pendentes deste pedido foram reenviados para a fila de impressão.";
        return RedirectToAction(nameof(Printers));
    }

    [HttpGet]
    public IActionResult Access()
    {
        if (!AdminAuthService.IsLoggedIn(HttpContext))
        {
            return RedirectToAction("Login", new { returnUrl = "/Admin/Access" });
        }

        var user = _users.GetById(HttpContext.Session.GetString(AdminAuthService.UserIdSessionKey) ?? "");
        if (user == null)
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Login");
        }

        return View(new AdminAccessViewModel { Username = user.Username });
    }

    [HttpPost]
    public IActionResult Access(string currentPassword, string username, string newPassword)
    {
        if (!AdminAuthService.IsLoggedIn(HttpContext)) return Unauthorized();

        var userId = HttpContext.Session.GetString(AdminAuthService.UserIdSessionKey);
        var error = "";
        if (string.IsNullOrWhiteSpace(userId) || !_users.UpdateCredentials(userId, currentPassword, username, newPassword, out error))
        {
            TempData["Error"] = string.IsNullOrWhiteSpace(error) ? "Não foi possível atualizar o acesso." : error;
            return RedirectToAction(nameof(Access));
        }

        HttpContext.Session.SetString(AdminAuthService.UsernameSessionKey, username.Trim());
        TempData["Success"] = "Dados de acesso atualizados.";
        return RedirectToAction(nameof(Access));
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
        OrderStatus.PrintFailed => "Falha na impressao",
        OrderStatus.Ready => "Pronto",
        OrderStatus.Delivered => "Entregue",
        OrderStatus.Cancelled => "Cancelado",
        _ => status.ToString()
    };
}
