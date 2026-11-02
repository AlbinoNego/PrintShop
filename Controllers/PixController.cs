using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PrintShop.Models;
using PrintShop.Services;

namespace PrintShop.Controllers;

[Route("Pix")]
public class PixController : Controller
{
    private readonly OrderQueueService _queue;
    private readonly PrinterRegistryService _printJobs;
    private readonly MercadoPagoPixService _mercadoPagoPix;
    private readonly ILogger<PixController> _logger;

    public PixController(
        OrderQueueService queue,
        PrinterRegistryService printJobs,
        MercadoPagoPixService mercadoPagoPix,
        ILogger<PixController> logger)
    {
        _queue = queue;
        _printJobs = printJobs;
        _mercadoPagoPix = mercadoPagoPix;
        _logger = logger;
    }

    [HttpPost("MercadoPagoWebhook")]
    [EnableRateLimiting("pix-webhook")]
    public async Task<IActionResult> MercadoPagoWebhook()
    {
        var paymentId = Request.Query["data.id"].FirstOrDefault() ?? Request.Query["data_id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(paymentId))
        {
            paymentId = await ReadPaymentIdFromBodyAsync();
        }

        if (string.IsNullOrWhiteSpace(paymentId))
        {
            return BadRequest(new { error = "Identificador do pagamento ausente." });
        }

        if (!_mercadoPagoPix.IsValidWebhookSignature(Request, paymentId))
        {
            _logger.LogWarning("Webhook do Mercado Pago rejeitado por assinatura inválida.");
            return Unauthorized();
        }

        MercadoPagoPixPayment payment;
        try
        {
            payment = await _mercadoPagoPix.GetPixOrderAsync(paymentId);
        }
        catch (MercadoPagoPixException ex)
        {
            _logger.LogWarning("Não foi possível consultar o pagamento {PaymentId}: {Reason}", paymentId, ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (string.IsNullOrWhiteSpace(payment.ExternalReference))
        {
            return Ok(new { ignored = true, reason = "Pagamento sem referência do pedido." });
        }

        var order = await _queue.GetAsync(payment.ExternalReference);
        if (order == null || order.PaymentMethod != PaymentMethod.Pix)
        {
            return Ok(new { ignored = true, reason = "Pedido não encontrado ou sem PIX." });
        }

        if (!string.Equals(order.MercadoPagoPaymentId, payment.Id, StringComparison.Ordinal))
        {
            // Cobranca substituida (pedido editado ou PIX regenerado). Reenviar o webhook nao muda nada,
            // entao responde 200; se ela foi paga, o dinheiro entrou e precisa de conferencia manual.
            if (payment.IsApproved)
            {
                _logger.LogCritical(
                    "PIX {PaymentId} aprovado para cobrança substituída do pedido {OrderId}. Conferir e estornar ou confirmar manualmente.",
                    paymentId, order.Id);
            }

            return Ok(new { ignored = true, reason = "Cobrança substituída." });
        }

        if (!string.Equals(payment.CurrencyId, "BRL", StringComparison.OrdinalIgnoreCase) ||
            Math.Round(order.TotalPrice, 2) != Math.Round(payment.Amount, 2))
        {
            _logger.LogWarning("Webhook PIX divergente para pedido {OrderId} e pagamento {PaymentId}.", order.Id, paymentId);
            return BadRequest(new { error = "Dados do pagamento divergentes." });
        }

        order.MercadoPagoStatus = payment.Status;
        if (!payment.IsApproved)
        {
            await _queue.UpdateAsync(order);
            return Ok(new { received = true, approved = false });
        }

        if (order.Status == OrderStatus.Cancelled)
        {
            _logger.LogWarning("Pagamento aprovado para pedido cancelado {OrderId}.", order.Id);
            await _queue.UpdateAsync(order);
            return Ok(new { received = true, cancelled = true });
        }

        if (order.PaymentConfirmed)
        {
            return Ok(new { received = true, duplicate = true });
        }

        order.PaymentConfirmed = true;
        order.Status = OrderStatus.PaymentConfirmed;
        await _queue.UpdateAsync(order);
        await _printJobs.EnqueueOrderAsync(order);

        _logger.LogInformation("PIX aprovado e pedido {OrderId} enviado à fila.", order.Id);
        return Ok(new { received = true, approved = true });
    }

    private async Task<string?> ReadPaymentIdFromBodyAsync()
    {
        if (Request.ContentLength is null or 0) return null;

        try
        {
            using var document = await JsonDocument.ParseAsync(Request.Body);
            var root = document.RootElement;
            if (root.TryGetProperty("data", out var data) && data.TryGetProperty("id", out var id))
            {
                return id.ToString();
            }

            return root.TryGetProperty("id", out var directId) ? directId.ToString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
