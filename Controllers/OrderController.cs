using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using PrintShop.Models;
using PrintShop.Services;
using System.Net.Mail;

namespace PrintShop.Controllers;

public class OrderController : Controller
{
    private readonly OrderQueueService _queue;
    private readonly PricingService _pricing;
    private readonly MercadoPagoPixService _mercadoPagoPix;
    private readonly PageCountService _pageCounter;
    private readonly FileStorageService _fileStorage;
    private readonly PrinterRegistryService _printJobs;

    private static readonly string[] AllowedExtensions =
        { ".pdf", ".docx", ".doc", ".pptx", ".ppt", ".jpg", ".jpeg", ".png" };
    private const string CustomerOrderIdsSessionKey = "CustomerOrderIds";

    public OrderController(OrderQueueService queue, PricingService pricing,
        MercadoPagoPixService mercadoPagoPix, PageCountService pageCounter,
        FileStorageService fileStorage, PrinterRegistryService printJobs)
    {
        _queue = queue;
        _pricing = pricing;
        _mercadoPagoPix = mercadoPagoPix;
        _pageCounter = pageCounter;
        _fileStorage = fileStorage;
        _printJobs = printJobs;
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

        // A edicao apaga a cobranca PIX do pedido. Se ela continuasse valida no Mercado Pago,
        // um pagamento feito depois seria rejeitado pelo webhook e o pedido nunca ficaria pago.
        if (existingOrder != null && !string.IsNullOrWhiteSpace(existingOrder.MercadoPagoPaymentId))
        {
            var cancelResult = await _mercadoPagoPix.CancelPixOrderAsync(existingOrder.MercadoPagoPaymentId);
            if (cancelResult == PixCancelResult.AlreadyPaid)
            {
                TempData["Error"] = "O PIX deste pedido ja foi pago. Aguarde a confirmacao; o pedido nao pode mais ser alterado.";
                return RedirectToAction("Success", new { id = existingOrder.Id });
            }

            if (cancelResult == PixCancelResult.Failed)
            {
                TempData["Error"] = "Nao foi possivel cancelar o PIX gerado antes. Tente editar novamente em instantes.";
                return RedirectToNew(existingOrder.Id);
            }
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
        order.PixQrCodeBase64 = null;
        order.PixExpiresAt = null;
        order.MercadoPagoPaymentId = null;
        order.MercadoPagoStatus = null;
        order.MercadoPagoIdempotencyKey = null;
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

        // fileCopies vem na mesma ordem dos arquivos enviados, inclusive os rejeitados;
        // o indice precisa avancar para todos, senao as copias deslizam para o arquivo errado.
        var uploadedFileIndex = -1;
        var rejectedFiles = new List<string>();
        foreach (var file in files ?? new List<IFormFile>())
        {
            uploadedFileIndex++;
            var ext = Path.GetExtension(file.FileName).ToLower();
            if (!AllowedExtensions.Contains(ext) ||
                file.Length > 50 * 1024 * 1024 || // max 50MB
                !await HasExpectedSignatureAsync(file, ext))
            {
                rejectedFiles.Add(file.FileName);
                continue;
            }

            var storedName = await _fileStorage.SaveAsync(file, ext);
            var filePath = _fileStorage.GetPath(storedName);
            int pageCount;
            try
            {
                pageCount = _pageCounter.CountPages(filePath, file.FileName);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException)
            {
                // Arquivo corrompido: antes virava erro 500 e deixava o arquivo orfao no disco.
                _fileStorage.Delete(storedName);
                rejectedFiles.Add(file.FileName);
                continue;
            }

            order.Files.Add(new UploadedFile
            {
                OriginalName = file.FileName,
                StoredName = storedName,
                ContentType = file.ContentType,
                SizeBytes = file.Length,
                PageCount = pageCount,
                Copies = GetCopyAt(fileCopies, uploadedFileIndex, order.Copies)
            });
        }

        if (order.Files.Count == 0)
        {
            TempData["Error"] = "Nenhum arquivo válido enviado.";
            return RedirectToNew(existingOrder?.Id);
        }

        if (rejectedFiles.Count > 0)
        {
            TempData["Error"] = $"Arquivos ignorados (formato invalido, corrompido ou acima de 50MB): {string.Join(", ", rejectedFiles)}";
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
    [EnableRateLimiting("order-lookup")]
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
        if (!CanCustomerView(order)) return RedirectToLookup();

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

        // Forbid() exige um esquema de autenticacao, que o site nao usa; daria erro 500.
        if (!CanCustomerView(order)) return NotFound();

        var file = order.Files.FirstOrDefault(item => item.Id == fileId);
        if (file == null) return NotFound();

        var path = _fileStorage.GetPath(file.StoredName);
        if (!System.IO.File.Exists(path)) return NotFound();

        // O tipo vem da extensao, nunca do Content-Type enviado pelo cliente: um "pdf" com
        // text/html seria renderizado no dominio do site. So PDF abre no navegador.
        var ext = Path.GetExtension(file.StoredName).ToLowerInvariant();
        var contentType = PreviewContentTypes.GetValueOrDefault(ext, "application/octet-stream");
        var disposition = new ContentDispositionHeaderValue(ext == ".pdf" ? "inline" : "attachment");
        disposition.SetHttpFileName(file.OriginalName);
        Response.Headers.ContentDisposition = disposition.ToString();
        // Arquivo do cliente: sem isso o navegador reaproveita a copia em cache mesmo sem sessao.
        Response.Headers.CacheControl = "private, no-store";
        return PhysicalFile(path, contentType);
    }

    private static readonly Dictionary<string, string> PreviewContentTypes = new()
    {
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png"
    };

    // GET /Order/Payment/{id}
    public async Task<IActionResult> Payment(string id)
    {
        var order = await _queue.GetAsync(id);
        if (order == null) return NotFound();
        if (!CanCustomerView(order)) return RedirectToLookup();

        if ((order.Status != OrderStatus.Draft && order.Status != OrderStatus.PendingPayment) || order.PaymentConfirmed)
        {
            return RedirectToAction("Success", new { id });
        }

        if (HasUsablePixPayment(order))
        {
            return RedirectToAction("PixPayment", new { id });
        }

        return View(order);
    }

    // POST /Order/ConfirmPayment
    [HttpPost]
    public async Task<IActionResult> ConfirmPayment(
        string id,
        string paymentMethod,
        string? payerEmail)
    {
        var order = await _queue.GetAsync(id);
        if (order == null) return NotFound();
        if (!CanCustomerView(order)) return RedirectToLookup();

        if ((order.Status != OrderStatus.Draft && order.Status != OrderStatus.PendingPayment) || order.PaymentConfirmed)
        {
            return RedirectToAction("Success", new { id });
        }

        if (!Enum.TryParse<PaymentMethod>(paymentMethod, out var parsedPaymentMethod))
        {
            TempData["Error"] = "Forma de pagamento inválida.";
            return RedirectToAction("Payment", new { id });
        }

        order.PaymentMethod = parsedPaymentMethod;

        if (order.FulfillmentMethod == FulfillmentMethod.Delivery)
        {
            order.PaymentMethod = PaymentMethod.Pix;
        }

        if (order.PaymentMethod == PaymentMethod.Pix)
        {
            if (!TryNormalizePayer(payerEmail, out var email, out var error))
            {
                TempData["Error"] = error;
                return RedirectToAction("Payment", new { id });
            }

            order.CustomerEmail = email;
            order.Status = OrderStatus.PendingPayment;
            order.PaymentConfirmed = false;
            order.MercadoPagoIdempotencyKey ??= Guid.NewGuid().ToString("D");
            await _queue.UpdateAsync(order);

            if (HasUsablePixPayment(order))
            {
                return RedirectToAction("PixPayment", new { id });
            }

            ClearGeneratedPixPayment(order);
            order.MercadoPagoIdempotencyKey = Guid.NewGuid().ToString("D");
            await _queue.UpdateAsync(order);

            try
            {
                var payment = await _mercadoPagoPix.CreatePixOrderAsync(order, email);
                order.MercadoPagoPaymentId = payment.Id;
                order.MercadoPagoStatus = payment.Status;
                order.MercadoPagoIdempotencyKey = payment.IdempotencyKey;
                order.PixCode = payment.PixCode;
                order.PixQrCodeBase64 = payment.QrCodeBase64;
                order.PixExpiresAt = payment.ExpiresAt;
                await _queue.UpdateAsync(order);
            }
            catch (MercadoPagoPixException ex)
            {
                TempData["Error"] = ex.Message;
                return RedirectToAction("Payment", new { id });
            }

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
        if (!CanCustomerView(order)) return RedirectToLookup();
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
        if (!CanCustomerView(order)) return RedirectToLookup();
        return View(order);
    }

    // GET /Order/Status/{id} - polling AJAX
    [HttpGet]
    public async Task<IActionResult> Status(string id)
    {
        var order = await _queue.GetAsync(id);
        if (order == null || !CanCustomerView(order)) return NotFound();
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
        // Autorizar so vale para pedido aguardando pagamento; antes um pedido entregue
        // ou com falha voltava para a fila e era impresso de novo.
        if (order.Status != OrderStatus.PendingPayment)
        {
            TempData["Error"] = $"Pedido #{order.Id} nao esta aguardando pagamento.";
            return RedirectToAction("Queue");
        }

        order.PaymentConfirmed = true;
        order.Status = OrderStatus.PaymentConfirmed;
        await _queue.UpdateAsync(order);
        await _printJobs.EnqueueOrderAsync(order);

        TempData["Success"] = $"Pedido #{order.Id} encaminhado para a fila de impressao.";
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
            await _printJobs.CancelOpenJobsAsync(order.Id, "Pedido barrado pelo administrador.");
            TempData["Success"] = $"Pedido #{order.Id} barrado.";
        }

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
        if (order.Status is OrderStatus.PaymentConfirmed or OrderStatus.Printing)
        {
            TempData["Error"] = $"Pedido #{order.Id} ja esta na fila de impressao.";
            return RedirectToAction("Queue");
        }
        if (order.Status is not (OrderStatus.Ready or OrderStatus.Delivered or OrderStatus.PrintFailed))
        {
            TempData["Error"] = $"Pedido #{order.Id} ainda nao pode ser reimpresso.";
            return RedirectToAction("Queue");
        }

        order.Status = OrderStatus.PaymentConfirmed;
        await _queue.UpdateAsync(order);
        await _printJobs.EnqueueOrderAsync(order, forceNewJobs: true);

        TempData["Success"] = $"Pedido #{order.Id} reenviado para a fila de impressao.";
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
        OrderStatus.PrintFailed => "Falha na impressao",
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

    private bool CanCustomerView(PrintOrder order) =>
        IsAdminLoggedIn() || GetCustomerOrderIds().Contains(order.Id);

    private IActionResult RedirectToLookup()
    {
        TempData["Error"] = "Informe o codigo do pedido para acompanha-lo.";
        return RedirectToAction("MyOrders");
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

    // Confere os primeiros bytes do arquivo, para que um HTML ou executavel renomeado
    // para .pdf/.docx nao seja aceito so pela extensao.
    private static async Task<bool> HasExpectedSignatureAsync(IFormFile file, string ext)
    {
        var header = new byte[8];
        await using var stream = file.OpenReadStream();
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false);
        return MatchesSignature(header.AsSpan(0, read), ext);
    }

    private static bool MatchesSignature(ReadOnlySpan<byte> bytes, string ext)
    {
        return ext switch
        {
            ".pdf" => bytes.StartsWith("%PDF-"u8),
            ".docx" or ".pptx" => bytes.StartsWith("PK\u0003\u0004"u8),
            ".doc" or ".ppt" => bytes.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }),
            ".jpg" or ".jpeg" => bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            ".png" => bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            _ => false
        };
    }

    private static int GetCopyAt(List<int>? copies, int index, int fallback) =>
        copies != null && index >= 0 && index < copies.Count
            ? NormalizeCopies(copies[index])
            : NormalizeCopies(fallback);

    private static int NormalizeCopies(int copies) =>
        Math.Max(1, Math.Min(copies, 100));

    private static bool HasUsablePixPayment(PrintOrder order)
    {
        if (string.IsNullOrWhiteSpace(order.MercadoPagoPaymentId) || string.IsNullOrWhiteSpace(order.PixCode))
        {
            return false;
        }

        if (order.PixExpiresAt.HasValue && order.PixExpiresAt.Value <= DateTime.Now)
        {
            return false;
        }

        return !string.Equals(order.MercadoPagoStatus, "cancelled", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(order.MercadoPagoStatus, "canceled", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(order.MercadoPagoStatus, "rejected", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(order.MercadoPagoStatus, "failed", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(order.MercadoPagoStatus, "expired", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(order.MercadoPagoStatus, "refunded", StringComparison.OrdinalIgnoreCase);
    }

    private static void ClearGeneratedPixPayment(PrintOrder order)
    {
        order.PixCode = null;
        order.PixQrCodeBase64 = null;
        order.PixExpiresAt = null;
        order.MercadoPagoPaymentId = null;
        order.MercadoPagoStatus = null;
    }

    private static bool TryNormalizePayer(
        string? payerEmail,
        out string email,
        out string error)
    {
        email = (payerEmail ?? "").Trim();
        error = "";

        try
        {
            var parsedEmail = new MailAddress(email);
            if (!string.Equals(parsedEmail.Address, email, StringComparison.OrdinalIgnoreCase)) throw new FormatException();
        }
        catch (FormatException)
        {
            error = "Informe um e-mail válido para gerar o PIX.";
            return false;
        }

        return true;
    }

}
