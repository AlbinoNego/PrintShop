using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PrintShop.Models;
using PrintShop.Services;

namespace PrintShop.Controllers;

[Route("Pix")]
public class PixController : Controller
{
    private const string WebhookSecretHeader = "X-PrintShop-Webhook-Secret";

    private readonly IConfiguration _configuration;
    private readonly OrderQueueService _queue;
    private readonly PrinterService _printer;
    private readonly ILogger<PixController> _logger;

    public PixController(
        IConfiguration configuration,
        OrderQueueService queue,
        PrinterService printer,
        ILogger<PixController> logger)
    {
        _configuration = configuration;
        _queue = queue;
        _printer = printer;
        _logger = logger;
    }

    [HttpPost("Webhook")]
    [EnableRateLimiting("pix-webhook")]
    public async Task<IActionResult> Webhook([FromBody] PixWebhookRequest request)
    {
        if (!IsAuthorizedWebhook())
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.OrderId))
        {
            return BadRequest(new { error = "orderId obrigatorio." });
        }

        if (!IsPaidStatus(request.Status))
        {
            return Ok(new { ignored = true, reason = "Status nao representa pagamento confirmado." });
        }

        var order = await _queue.GetAsync(NormalizeOrderId(request.OrderId));
        if (order == null)
        {
            return NotFound(new { error = "Pedido nao encontrado." });
        }

        if (order.PaymentMethod != PaymentMethod.Pix)
        {
            return BadRequest(new { error = "Pedido nao usa PIX." });
        }

        if (request.Amount.HasValue && Math.Round(request.Amount.Value, 2) != Math.Round(order.TotalPrice, 2))
        {
            _logger.LogWarning(
                "Webhook PIX com valor divergente. Pedido {OrderId}. Esperado {Expected}. Recebido {Received}.",
                order.Id,
                order.TotalPrice,
                request.Amount.Value);

            return BadRequest(new { error = "Valor divergente." });
        }

        if (order.PaymentConfirmed || order.Status is OrderStatus.Printing or OrderStatus.Ready or OrderStatus.Delivered)
        {
            return Ok(new { confirmed = true, duplicate = true, orderId = order.Id });
        }

        if (order.Status == OrderStatus.Cancelled)
        {
            return BadRequest(new { error = "Pedido cancelado." });
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

        return Ok(new { confirmed = true, orderId = order.Id });
    }

    private bool IsAuthorizedWebhook()
    {
        var configuredSecret = _configuration["PrintShop:PixWebhookSecret"];
        if (string.IsNullOrWhiteSpace(configuredSecret))
        {
            _logger.LogWarning("Webhook PIX chamado, mas PrintShop:PixWebhookSecret nao esta configurado.");
            return false;
        }

        var receivedSecret = Request.Headers[WebhookSecretHeader].FirstOrDefault();
        return string.Equals(receivedSecret, configuredSecret, StringComparison.Ordinal);
    }

    private static bool IsPaidStatus(string? status) =>
        string.Equals(status, "paid", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "confirmed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeOrderId(string? id) =>
        (id ?? "").Trim().TrimStart('#').ToUpperInvariant();
}

public class PixWebhookRequest
{
    public string? OrderId { get; set; }
    public string? Status { get; set; }
    public decimal? Amount { get; set; }
    public string? PaymentId { get; set; }
}
