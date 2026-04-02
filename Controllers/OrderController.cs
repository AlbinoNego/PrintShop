using Microsoft.AspNetCore.Mvc;
using PrintShop.Models;
using PrintShop.Services;
using System.Text.Json;

namespace PrintShop.Controllers;

public class OrderController : Controller
{
    private readonly OrderQueueService _queue;
    private readonly PricingService _pricing;
    private readonly PrinterService _printer;
    private readonly PixService _pix;
    private readonly IWebHostEnvironment _env;

    private static readonly string[] AllowedExtensions =
        { ".pdf", ".docx", ".doc", ".pptx", ".ppt", ".jpg", ".jpeg", ".png" };

    public OrderController(OrderQueueService queue, PricingService pricing,
        PrinterService printer, PixService pix, IWebHostEnvironment env)
    {
        _queue = queue;
        _pricing = pricing;
        _printer = printer;
        _pix = pix;
        _env = env;
    }

    // GET /Order/New
    public IActionResult New() => View();

    // POST /Order/Upload - recebe arquivos e configurações
    [HttpPost]
    public async Task<IActionResult> Upload(
        List<IFormFile> files,
        string color, int copies, string paperType,
        bool laminate, string sides,
        string customerName, string customerPhone)
    {
        if (files == null || files.Count == 0)
        {
            TempData["Error"] = "Envie pelo menos um arquivo.";
            return RedirectToAction("New");
        }

        var order = new PrintOrder
        {
            CustomerName = customerName,
            CustomerPhone = customerPhone,
            Color = Enum.Parse<PrintColor>(color),
            Copies = Math.Max(1, Math.Min(copies, 100)),
            PaperType = Enum.Parse<PaperType>(paperType),
            Laminate = laminate,
            Sides = Enum.Parse<PrintSides>(sides)
        };

        // Salvar arquivos
        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads");
        foreach (var file in files)
        {
            var ext = Path.GetExtension(file.FileName).ToLower();
            if (!AllowedExtensions.Contains(ext)) continue;
            if (file.Length > 50 * 1024 * 1024) continue; // max 50MB

            var storedName = $"{Guid.NewGuid():N}{ext}";
            var filePath = Path.Combine(uploadsDir, storedName);

            using var stream = new FileStream(filePath, FileMode.Create);
            await file.CopyToAsync(stream);

            order.Files.Add(new UploadedFile
            {
                OriginalName = file.FileName,
                StoredName = storedName,
                ContentType = file.ContentType,
                SizeBytes = file.Length,
                PageCount = EstimatePageCount(file.FileName, file.Length)
            });
        }

        if (order.Files.Count == 0)
        {
            TempData["Error"] = "Nenhum arquivo válido enviado.";
            return RedirectToAction("New");
        }

        order.TotalPrice = _pricing.Calculate(order);
        _queue.Add(order);

        HttpContext.Session.SetString("CurrentOrderId", order.Id);

        return RedirectToAction("Review", new { id = order.Id });
    }

    // GET /Order/Review/{id}
    public IActionResult Review(string id)
    {
        var order = _queue.Get(id);
        if (order == null) return NotFound();

        var breakdown = _pricing.GetBreakdown(order);
        ViewBag.Breakdown = breakdown;
        return View(order);
    }

    // GET /Order/Payment/{id}
    public IActionResult Payment(string id)
    {
        var order = _queue.Get(id);
        if (order == null) return NotFound();
        return View(order);
    }

    // POST /Order/ConfirmPayment
    [HttpPost]
    public async Task<IActionResult> ConfirmPayment(string id, string paymentMethod)
    {
        var order = _queue.Get(id);
        if (order == null) return NotFound();

        order.PaymentMethod = Enum.Parse<PaymentMethod>(paymentMethod);

        if (order.PaymentMethod == PaymentMethod.Pix)
        {
            order.PixCode = _pix.GeneratePixCode(order.TotalPrice, order.Id);
            order.Status = OrderStatus.PendingPayment;
            _queue.Update(order);
            return RedirectToAction("PixPayment", new { id });
        }

        // Pagamento na loja: confirma e imprime
        order.PaymentConfirmed = true;
        order.Status = OrderStatus.Printing;
        _queue.Update(order);

        _ = Task.Run(async () =>
        {
            var success = await _printer.PrintOrderAsync(order);
            order.Status = success ? OrderStatus.Ready : OrderStatus.Cancelled;
            _queue.Update(order);
        });

        return RedirectToAction("Success", new { id });
    }

    // GET /Order/PixPayment/{id}
    public IActionResult PixPayment(string id)
    {
        var order = _queue.Get(id);
        if (order == null) return NotFound();
        return View(order);
    }

    // POST /Order/ConfirmPixPayment - webhook ou confirmação manual
    [HttpPost]
    public async Task<IActionResult> ConfirmPixPayment(string id)
    {
        var order = _queue.Get(id);
        if (order == null) return NotFound();

        order.PaymentConfirmed = true;
        order.Status = OrderStatus.Printing;
        _queue.Update(order);

        _ = Task.Run(async () =>
        {
            var success = await _printer.PrintOrderAsync(order);
            order.Status = success ? OrderStatus.Ready : OrderStatus.Cancelled;
            _queue.Update(order);
        });

        return RedirectToAction("Success", new { id });
    }

    // GET /Order/Success/{id}
    public IActionResult Success(string id)
    {
        var order = _queue.Get(id);
        if (order == null) return NotFound();
        return View(order);
    }

    // GET /Order/Status/{id} - polling AJAX
    [HttpGet]
    public IActionResult Status(string id)
    {
        var order = _queue.Get(id);
        if (order == null) return NotFound();
        return Json(new { status = order.Status.ToString(), statusLabel = GetStatusLabel(order.Status) });
    }

    // GET /Order/Queue - painel da papelaria
    public IActionResult Queue() => View(_queue.GetAll());

    // POST /Order/MarkReady
    [HttpPost]
    public IActionResult MarkReady(string id)
    {
        var order = _queue.Get(id);
        if (order != null) { order.Status = OrderStatus.Ready; _queue.Update(order); }
        return RedirectToAction("Queue");
    }

    private static int EstimatePageCount(string fileName, long sizeBytes)
    {
        var ext = Path.GetExtension(fileName).ToLower();
        return ext switch
        {
            ".jpg" or ".jpeg" or ".png" => 1,
            ".pdf" => Math.Max(1, (int)(sizeBytes / 75_000)),  // ~75KB por página
            ".docx" or ".doc" => Math.Max(1, (int)(sizeBytes / 30_000)),
            ".pptx" or ".ppt" => Math.Max(1, (int)(sizeBytes / 100_000)),
            _ => 1
        };
    }

    private static string GetStatusLabel(OrderStatus status) => status switch
    {
        OrderStatus.PendingPayment => "Aguardando pagamento",
        OrderStatus.PaymentConfirmed => "Pagamento confirmado",
        OrderStatus.Printing => "Imprimindo...",
        OrderStatus.Ready => "Pronto para retirada!",
        OrderStatus.Delivered => "Entregue",
        OrderStatus.Cancelled => "Cancelado",
        _ => status.ToString()
    };
}
