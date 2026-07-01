using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Newtonsoft.Json;
using PrintShop.Models;
using PrintShop.Services;

namespace PrintShop.Controllers;

public class OrderController : Controller
{
    private readonly OrderQueueService _queue;
    private readonly PricingService _pricing;
    private readonly PrinterService _printer;
    private readonly PixService _pix;
    private readonly PageCountService _pageCounter;
    private readonly FileStorageService _fileStorage;

    private static readonly string[] AllowedExtensions =
        { ".pdf", ".docx", ".doc", ".pptx", ".ppt", ".jpg", ".jpeg", ".png" };
    private const string CustomerOrderIdsSessionKey = "CustomerOrderIds";

    public OrderController(OrderQueueService queue, PricingService pricing,
        PrinterService printer, PixService pix, PageCountService pageCounter,
        FileStorageService fileStorage)
    {
        _queue = queue;
        _pricing = pricing;
        _printer = printer;
        _pix = pix;
        _pageCounter = pageCounter;
        _fileStorage = fileStorage;
    }

    // GET /Order/New
    public async Task<IActionResult> New(string? editId)
    {
        ViewBag.AdminSettings = _pricing.GetSettings();

        if (!string.IsNullOrWhiteSpace(editId))
        {
            var order = await _queue.GetAsync(editId);
            if (order == null) return NotFound();

            if (!CanCustomerEdit(order))
            {
                TempData["Error"] = "Este pedido nao pode mais ser editado.";
                return RedirectToAction("MyOrders");
            }

            ViewBag.EditOrder = order;
        }

        return View();
    }

    // POST /Order/Upload - recebe arquivos e configurações
    [HttpPost]
    [EnableRateLimiting("uploads")]
    public async Task<IActionResult> Upload(
        List<IFormFile> files,
        string color, int copies, string paperType,
        bool laminate, string sides, string orientation,
        string customerName, string customerPhone,
        string fulfillmentMethod, string deliveryAddress,
        string deliveryNumber, string deliveryNeighborhood,
        string deliveryComplement, string? existingOrderId,
        List<int>? removedFileIds,
        List<int>? existingFileIds,
        List<int>? existingFileCopies,
        List<int>? fileCopies)
    {
        var existingOrder = string.IsNullOrWhiteSpace(existingOrderId)
            ? null
            : await _queue.GetAsync(existingOrderId);

        if (existingOrder != null && !CanCustomerEdit(existingOrder))
        {
            TempData["Error"] = "Este pedido nao pode mais ser editado.";
            return RedirectToAction("MyOrders");
        }

        if ((files == null || files.Count == 0) && (existingOrder == null || existingOrder.Files.Count == 0))
        {
            TempData["Error"] = "Envie pelo menos um arquivo.";
            return RedirectToNew(existingOrder?.Id);
        }

        if (string.IsNullOrWhiteSpace(customerPhone))
        {
            TempData["Error"] = "Informe um telefone para contato.";
            return RedirectToNew(existingOrder?.Id);
        }

        var order = existingOrder ?? new PrintOrder();
        var filesToRemove = new List<UploadedFile>();
        if (removedFileIds is { Count: > 0 })
        {
            filesToRemove = order.Files
                .Where(file => removedFileIds.Contains(file.Id))
                .ToList();

            order.Files.RemoveAll(file => removedFileIds.Contains(file.Id));
        }

        order.CustomerName = customerName ?? "";
        order.CustomerPhone = customerPhone ?? "";
        order.Color = Enum.Parse<PrintColor>(color);
        order.Copies = Math.Max(1, Math.Min(copies, 100));
        order.PaperType = Enum.Parse<PaperType>(paperType);
        order.Laminate = laminate;
        order.Sides = Enum.Parse<PrintSides>(sides);
        order.Orientation = Enum.TryParse<PrintOrientation>(orientation, out var parsedOrientation)
            ? parsedOrientation
            : PrintOrientation.Portrait;
        order.FulfillmentMethod = Enum.Parse<FulfillmentMethod>(fulfillmentMethod);
        order.DeliveryAddress = deliveryAddress ?? "";
        order.DeliveryNumber = deliveryNumber ?? "";
        order.DeliveryNeighborhood = deliveryNeighborhood ?? "";
        order.DeliveryComplement = deliveryComplement ?? "";
        order.DeliveryFee = 0m;
        order.PixCode = null;
        order.PaymentConfirmed = false;
        order.Status = OrderStatus.Draft;

        if (order.FulfillmentMethod == FulfillmentMethod.Delivery)
        {
            if (string.IsNullOrWhiteSpace(order.DeliveryAddress) ||
                string.IsNullOrWhiteSpace(order.DeliveryNumber) ||
                string.IsNullOrWhiteSpace(order.DeliveryNeighborhood))
            {
                TempData["Error"] = "Informe endereco, numero e bairro para entrega.";
                return RedirectToNew(existingOrder?.Id);
            }

            order.DeliveryFee = _pricing.GetDeliveryFee();
        }

        ApplyExistingFileCopies(order, existingFileIds, existingFileCopies);

        var uploadedFileIndex = 0;
        foreach (var file in files ?? new List<IFormFile>())
        {
            var ext = Path.GetExtension(file.FileName).ToLower();
            if (!AllowedExtensions.Contains(ext)) continue;
            if (file.Length > 50 * 1024 * 1024) continue; // max 50MB

            var storedName = await _fileStorage.SaveAsync(file, ext);
            var filePath = _fileStorage.GetPath(storedName);
            var pageCount = _pageCounter.CountPages(filePath, file.FileName);

            order.Files.Add(new UploadedFile
            {
                OriginalName = file.FileName,
                StoredName = storedName,
                ContentType = file.ContentType,
                SizeBytes = file.Length,
                PageCount = pageCount,
                Copies = GetCopyAt(fileCopies, uploadedFileIndex, order.Copies)
            });
            uploadedFileIndex++;
        }

        if (order.Files.Count == 0)
        {
            TempData["Error"] = "Nenhum arquivo válido enviado.";
            return RedirectToNew(existingOrder?.Id);
        }

        order.TotalPrice = _pricing.Calculate(order);
        if (existingOrder == null)
        {
            await _queue.AddAsync(order);
        }
        else
        {
            await _queue.UpdateAsync(order);
        }

        foreach (var file in filesToRemove)
        {
            _fileStorage.Delete(file.StoredName);
        }

        HttpContext.Session.SetString("CurrentOrderId", order.Id);
        AddCustomerOrderId(order.Id);

        return RedirectToAction("Review", new { id = order.Id });
    }

    // GET /Order/MyOrders - acompanhamento do cliente
    public async Task<IActionResult> MyOrders()
    {
        var orderIds = GetCustomerOrderIds();
        var orders = new List<PrintOrder>();

        foreach (var id in orderIds)
        {
            var order = await _queue.GetAsync(id);
            if (order != null && order.Status != OrderStatus.Draft)
            {
                orders.Add(order);
            }
        }

        return View(orders.OrderByDescending(order => order.CreatedAt));
    }

    // POST /Order/FindOrder - recupera pedido por codigo
    [HttpPost]
    public async Task<IActionResult> FindOrder(string id)
    {
        var normalizedId = NormalizeOrderId(id);

        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            TempData["Error"] = "Informe o codigo do pedido.";
            return RedirectToAction("MyOrders");
        }

        var order = await _queue.GetAsync(normalizedId);
        if (order == null)
        {
            TempData["Error"] = "Pedido nao encontrado.";
            return RedirectToAction("MyOrders");
        }

        AddCustomerOrderId(order.Id);
        if (order.Status == OrderStatus.Draft)
        {
            return RedirectToAction("Review", new { id = order.Id });
        }

        return RedirectToAction("Success", new { id = order.Id });
    }

    // GET /Order/Review/{id}
    public async Task<IActionResult> Review(string id)
    {
        var order = await _queue.GetAsync(id);
        if (order == null) return NotFound();

        var breakdown = _pricing.GetBreakdown(order);
        ViewBag.Breakdown = breakdown;
        return View(order);
    }

    // GET /Order/PreviewFile/{id}?fileId=1
    [HttpGet]
    public async Task<IActionResult> PreviewFile(string id, int fileId)
    {
        var order = await _queue.GetAsync(id);
        if (order == null) return NotFound();

        if (!IsAdminLoggedIn() && !GetCustomerOrderIds().Contains(order.Id))
        {
            return Forbid();
        }

        var file = order.Files.FirstOrDefault(item => item.Id == fileId);
        if (file == null) return NotFound();

        var path = _fileStorage.GetPath(file.StoredName);
        if (!System.IO.File.Exists(path)) return NotFound();

        Response.Headers.ContentDisposition = $"inline; filename=\"{file.OriginalName}\"";
        return PhysicalFile(path, string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
    }

    // GET /Order/Payment/{id}
    public async Task<IActionResult> Payment(string id)
    {
        var order = await _queue.GetAsync(id);
        if (order == null) return NotFound();

        if ((order.Status != OrderStatus.Draft && order.Status != OrderStatus.PendingPayment) || order.PaymentConfirmed)
        {
            return RedirectToAction("Success", new { id });
        }

        if (order.FulfillmentMethod == FulfillmentMethod.Delivery)
        {
            order.PaymentMethod = PaymentMethod.Pix;
            order.PixCode = _pix.GeneratePixCode(order.TotalPrice, order.Id);
            order.Status = OrderStatus.PendingPayment;
            await _queue.UpdateAsync(order);

            return RedirectToAction("PixPayment", new { id });
        }

        return View(order);
    }

    // POST /Order/ConfirmPayment
    [HttpPost]
    public async Task<IActionResult> ConfirmPayment(string id, string paymentMethod)
    {
        var order = await _queue.GetAsync(id);
        if (order == null) return NotFound();

        if ((order.Status != OrderStatus.Draft && order.Status != OrderStatus.PendingPayment) || order.PaymentConfirmed)
        {
            return RedirectToAction("Success", new { id });
        }

        order.PaymentMethod = Enum.Parse<PaymentMethod>(paymentMethod);

        if (order.FulfillmentMethod == FulfillmentMethod.Delivery)
        {
            order.PaymentMethod = PaymentMethod.Pix;
        }

        if (order.PaymentMethod == PaymentMethod.Pix)
        {
            order.PixCode = _pix.GeneratePixCode(order.TotalPrice, order.Id);
            order.Status = OrderStatus.PendingPayment;
            await _queue.UpdateAsync(order);
            return RedirectToAction("PixPayment", new { id });
        }

        // Pagamento na loja: o pedido fica aguardando confirmacao da papelaria.
        order.PaymentConfirmed = false;
        order.Status = OrderStatus.PendingPayment;
        await _queue.UpdateAsync(order);

        return RedirectToAction("Success", new { id });
    }

    // GET /Order/PixPayment/{id}
    public async Task<IActionResult> PixPayment(string id)
    {
        var order = await _queue.GetAsync(id);
        if (order == null) return NotFound();
        if (order.Status == OrderStatus.Draft || string.IsNullOrWhiteSpace(order.PixCode))
        {
            return RedirectToAction("Payment", new { id });
        }

        return View(order);
    }

    // GET /Order/Success/{id}
    public async Task<IActionResult> Success(string id)
    {
        var order = await _queue.GetAsync(id);
        if (order == null) return NotFound();
        return View(order);
    }

    // GET /Order/Status/{id} - polling AJAX
    [HttpGet]
    public async Task<IActionResult> Status(string id)
    {
        var order = await _queue.GetAsync(id);
        if (order == null) return NotFound();
        return Json(new { status = order.Status.ToString(), statusLabel = GetStatusLabel(order.Status) });
    }

    // GET /Order/Queue - painel da papelaria
    public async Task<IActionResult> Queue(string? status, string? search, string? selectedId)
    {
        if (!IsAdminLoggedIn())
        {
            return RedirectToAction("Login", "Admin", new { returnUrl = "/Order/Queue" });
        }

        var orders = (await _queue.GetAllAsync())
            .Where(order => order.Status != OrderStatus.Draft)
            .ToList();
        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var parsedStatus))
        {
            orders = orders.Where(order => order.Status == parsedStatus).ToList();
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            orders = orders
                .Where(order =>
                    order.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    order.CustomerName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    order.CustomerPhone.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    order.Files.Any(file => file.OriginalName.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        ViewBag.Status = status ?? "";
        ViewBag.Search = search ?? "";
        ViewBag.SelectedOrder = string.IsNullOrWhiteSpace(selectedId)
            ? null
            : await _queue.GetAsync(selectedId);

        return View(orders);
    }

    // POST /Order/ApprovePayment
    [HttpPost]
    public async Task<IActionResult> ApprovePayment(string id)
    {
        if (!IsAdminLoggedIn()) return Unauthorized();

        var order = await _queue.GetAsync(id);
        if (order == null) return NotFound();
        if (order.Status == OrderStatus.Cancelled || order.Status == OrderStatus.Ready)
        {
            return RedirectToAction("Queue");
        }

        order.PaymentConfirmed = true;
        order.Status = OrderStatus.Printing;
        await _queue.UpdateAsync(order);

        _ = Task.Run(async () =>
        {
            var success = await _printer.PrintOrderAsync(order);
            order.Status = success ? OrderStatus.Ready : OrderStatus.Cancelled;
            await _queue.UpdateAsync(order);
        });

        TempData["Success"] = $"Pedido #{order.Id} autorizado para impressao.";
        return RedirectToAction("Queue");
    }

    // POST /Order/Cancel
    [HttpPost]
    public async Task<IActionResult> Cancel(string id)
    {
        if (!IsAdminLoggedIn()) return Unauthorized();

        var order = await _queue.GetAsync(id);
        if (order != null)
        {
            order.Status = OrderStatus.Cancelled;
            await _queue.UpdateAsync(order);
            TempData["Success"] = $"Pedido #{order.Id} barrado.";
        }

        return RedirectToAction("Queue");
    }

    // POST /Order/MarkReady
    [HttpPost]
    public async Task<IActionResult> MarkReady(string id)
    {
        if (!IsAdminLoggedIn()) return Unauthorized();

        var order = await _queue.GetAsync(id);
        if (order != null) { order.Status = OrderStatus.Ready; await _queue.UpdateAsync(order); }
        return RedirectToAction("Queue");
    }

    [HttpPost]
    public async Task<IActionResult> Reprint(string id)
    {
        if (!IsAdminLoggedIn()) return Unauthorized();

        var order = await _queue.GetAsync(id);
        if (order == null) return NotFound();
        if (order.Status == OrderStatus.Cancelled)
        {
            TempData["Error"] = $"Pedido #{order.Id} esta cancelado.";
            return RedirectToAction("Queue");
        }

        order.Status = OrderStatus.Printing;
        await _queue.UpdateAsync(order);

        _ = Task.Run(async () =>
        {
            var success = await _printer.PrintOrderAsync(order);
            order.Status = success ? OrderStatus.Ready : OrderStatus.Cancelled;
            await _queue.UpdateAsync(order);
        });

        TempData["Success"] = $"Pedido #{order.Id} reenviado para impressao.";
        return RedirectToAction("Queue");
    }

    [HttpPost]
    public async Task<IActionResult> MarkDelivered(string id)
    {
        if (!IsAdminLoggedIn()) return Unauthorized();

        var order = await _queue.GetAsync(id);
        if (order != null && order.Status == OrderStatus.Ready)
        {
            order.Status = OrderStatus.Delivered;
            await _queue.UpdateAsync(order);
            TempData["Success"] = $"Pedido #{order.Id} marcado como entregue.";
        }

        return RedirectToAction("Queue");
    }

    private static string GetStatusLabel(OrderStatus status) => status switch
    {
        OrderStatus.PendingPayment => "Aguardando pagamento",
        OrderStatus.Draft => "Em revisao",
        OrderStatus.PaymentConfirmed => "Pagamento confirmado",
        OrderStatus.Printing => "Imprimindo...",
        OrderStatus.Ready => "Pronto para retirada!",
        OrderStatus.Delivered => "Entregue",
        OrderStatus.Cancelled => "Cancelado",
        _ => status.ToString()
    };

    private bool IsAdminLoggedIn() => AdminAuthService.IsLoggedIn(HttpContext);

    private List<string> GetCustomerOrderIds()
    {
        var json = HttpContext.Session.GetString(CustomerOrderIdsSessionKey);
        return string.IsNullOrWhiteSpace(json)
            ? new List<string>()
            : JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
    }

    private void AddCustomerOrderId(string id)
    {
        var ids = GetCustomerOrderIds();
        if (!ids.Contains(id))
        {
            ids.Add(id);
            HttpContext.Session.SetString(CustomerOrderIdsSessionKey, JsonConvert.SerializeObject(ids));
        }
    }

    private bool CanCustomerEdit(PrintOrder order) =>
        (order.Status == OrderStatus.Draft || order.Status == OrderStatus.PendingPayment) &&
        !order.PaymentConfirmed &&
        GetCustomerOrderIds().Contains(order.Id);

    private IActionResult RedirectToNew(string? editId) =>
        string.IsNullOrWhiteSpace(editId)
            ? RedirectToAction("New")
            : RedirectToAction("New", new { editId });

    private static string NormalizeOrderId(string? id) =>
        (id ?? "").Trim().TrimStart('#').ToUpperInvariant();

    private static void ApplyExistingFileCopies(
        PrintOrder order,
        List<int>? existingFileIds,
        List<int>? existingFileCopies)
    {
        if (existingFileIds is not { Count: > 0 } || existingFileCopies is not { Count: > 0 })
        {
            return;
        }

        for (var index = 0; index < existingFileIds.Count && index < existingFileCopies.Count; index++)
        {
            var file = order.Files.FirstOrDefault(item => item.Id == existingFileIds[index]);
            if (file != null)
            {
                file.Copies = NormalizeCopies(existingFileCopies[index]);
            }
        }
    }

    private static int GetCopyAt(List<int>? copies, int index, int fallback) =>
        copies != null && index >= 0 && index < copies.Count
            ? NormalizeCopies(copies[index])
            : NormalizeCopies(fallback);

    private static int NormalizeCopies(int copies) =>
        Math.Max(1, Math.Min(copies, 100));

}
