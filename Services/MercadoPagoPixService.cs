using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrintShop.Models;

namespace PrintShop.Services;

public class MercadoPagoPixService
{
    private const string ApiBaseUrl = "https://api.mercadopago.com";
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MercadoPagoPixService> _logger;

    public MercadoPagoPixService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<MercadoPagoPixService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _httpClient.BaseAddress = new Uri(ApiBaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(GetAccessToken());

    public async Task<MercadoPagoPixPayment> CreatePixOrderAsync(
        PrintOrder order,
        string email,
        CancellationToken cancellationToken = default)
    {
        var accessToken = GetAccessToken()?.Trim();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new MercadoPagoPixException("O PIX do Mercado Pago ainda não foi configurado no servidor.");
        }

        EnsureOrdersAccessToken(accessToken);

        var idempotencyKey = string.IsNullOrWhiteSpace(order.MercadoPagoIdempotencyKey)
            ? Guid.NewGuid().ToString("D")
            : order.MercadoPagoIdempotencyKey;
        var expirationMinutes = GetPixExpirationMinutes();
        var amount = order.TotalPrice.ToString("F2", CultureInfo.InvariantCulture);

        var requestBody = new MercadoPagoCreateOrderRequest
        {
            Type = "online",
            TotalAmount = amount,
            ExternalReference = order.Id,
            ProcessingMode = "automatic",
            Transactions = new MercadoPagoTransactionsRequest
            {
                Payments = new List<MercadoPagoOrderPaymentRequest>
                {
                    new()
                    {
                        Amount = amount,
                        PaymentMethod = new MercadoPagoPaymentMethodRequest
                        {
                            Id = "pix",
                            Type = "bank_transfer"
                        },
                        ExpirationTime = GetIsoDuration(expirationMinutes)
                    }
                }
            },
            Payer = new MercadoPagoOrderPayerRequest { Email = email }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/orders")
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("X-Idempotency-Key", idempotencyKey);
        _logger.LogInformation("Criando order PIX para o pedido {OrderId} com validade de {ExpirationMinutes} minutos.", order.Id, expirationMinutes);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Mercado Pago recusou a criação da order PIX. Status HTTP: {StatusCode}. Motivo: {Reason}. Request ID: {RequestId}",
                    response.StatusCode,
                    GetErrorReason(responseContent),
                    GetRequestId(response));
                throw new MercadoPagoPixException("Não foi possível gerar a cobrança PIX. Revise os dados e tente novamente.");
            }

            var payment = ParseOrder(responseContent);
            if (string.IsNullOrWhiteSpace(payment.Id) || string.IsNullOrWhiteSpace(payment.PixCode))
            {
                _logger.LogError("Resposta inesperada ao criar PIX no Mercado Pago para o pedido {OrderId}.", order.Id);
                throw new MercadoPagoPixException("O Mercado Pago não retornou um código PIX válido.");
            }

            payment.IdempotencyKey = idempotencyKey;
            payment.ExpiresAt = DateTime.Now.AddMinutes(expirationMinutes);
            return payment;
        }
        catch (MercadoPagoPixException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogError(ex, "Falha de comunicação ao criar PIX para o pedido {OrderId}.", order.Id);
            throw new MercadoPagoPixException("Não foi possível comunicar com o Mercado Pago. Tente novamente em alguns instantes.");
        }
    }

    public async Task<MercadoPagoPixPayment> GetPixOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        var accessToken = GetAccessToken()?.Trim();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new MercadoPagoPixException("O PIX do Mercado Pago ainda não foi configurado no servidor.");
        }

        EnsureOrdersAccessToken(accessToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/orders/{Uri.EscapeDataString(orderId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Mercado Pago não retornou a order {OrderId}. Status HTTP: {StatusCode}", orderId, response.StatusCode);
                throw new MercadoPagoPixException("Não foi possível consultar a cobrança PIX.");
            }

            return ParseOrder(responseContent);
        }
        catch (MercadoPagoPixException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogError(ex, "Falha de comunicação ao consultar a order {OrderId}.", orderId);
            throw new MercadoPagoPixException("Não foi possível consultar a cobrança PIX.");
        }
    }

    public async Task<PixCancelResult> CancelPixOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        var accessToken = GetAccessToken()?.Trim();
        if (string.IsNullOrWhiteSpace(accessToken)) return PixCancelResult.Failed;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/orders/{Uri.EscapeDataString(orderId)}/cancel");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("X-Idempotency-Key", Guid.NewGuid().ToString("D"));
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Order PIX {OrderId} cancelada antes da edicao do pedido.", orderId);
                return PixCancelResult.Cancelled;
            }

            // O cancelamento falha quando a order ja esta paga, expirada ou cancelada: consulta para saber qual.
            var current = await GetPixOrderAsync(orderId, cancellationToken);
            if (current.IsApproved) return PixCancelResult.AlreadyPaid;
            if (IsTerminalStatus(current.Status)) return PixCancelResult.Cancelled;

            _logger.LogWarning("Mercado Pago recusou cancelar a order {OrderId}. Status HTTP: {StatusCode}", orderId, response.StatusCode);
            return PixCancelResult.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or MercadoPagoPixException)
        {
            _logger.LogWarning(ex, "Falha ao cancelar a order PIX {OrderId}.", orderId);
            return PixCancelResult.Failed;
        }
    }

    private static bool IsTerminalStatus(string? status) =>
        status is not null &&
        new[] { "canceled", "cancelled", "expired", "failed", "rejected", "refunded" }
            .Contains(status, StringComparer.OrdinalIgnoreCase);

    public bool IsValidWebhookSignature(HttpRequest request, string paymentId)
    {
        var secret = _configuration["MercadoPago:WebhookSecret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogError("Webhook do Mercado Pago recebido sem MercadoPago:WebhookSecret configurado.");
            return false;
        }

        var signature = request.Headers["x-signature"].FirstOrDefault();
        var requestId = request.Headers["x-request-id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(requestId)) return false;

        var values = signature
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

        if (!values.TryGetValue("ts", out var timestamp) || !values.TryGetValue("v1", out var receivedHash)) return false;

        var manifest = $"id:{paymentId.ToLowerInvariant()};request-id:{requestId};ts:{timestamp};";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expectedHash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHash),
                Convert.FromHexString(receivedHash));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private string? GetAccessToken() => _configuration["MercadoPago:AccessToken"];

    private static void EnsureOrdersAccessToken(string accessToken)
    {
        if (!accessToken.StartsWith("APP_USR-", StringComparison.OrdinalIgnoreCase))
        {
            throw new MercadoPagoPixException(
                "A credencial configurada não é compatível com o Checkout Transparente via Orders. " +
                "Use o Access Token de teste iniciado por APP_USR da aplicação Mercado Pago configurada para Checkout Transparente.");
        }
    }

    private int GetPixExpirationMinutes()
    {
        var configuredMinutes = _configuration.GetValue<int?>("MercadoPago:PixExpirationMinutes") ?? 1440;
        return Math.Clamp(configuredMinutes, 30, 43200);
    }

    private static MercadoPagoPixPayment ParseOrder(string content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var payment = GetFirstPayment(root);
        var paymentMethod = payment.ValueKind == JsonValueKind.Object &&
                            payment.TryGetProperty("payment_method", out var value)
            ? value
            : default;

        return new MercadoPagoPixPayment
        {
            Id = ReadString(root, "id"),
            Status = payment.ValueKind == JsonValueKind.Object ? ReadString(payment, "status") : ReadString(root, "status"),
            ExternalReference = ReadString(root, "external_reference"),
            CurrencyId = ReadString(root, "currency_id") ?? ReadString(root, "currency") ?? "BRL",
            Amount = root.TryGetProperty("total_amount", out var amount) && TryReadDecimal(amount, out var decimalAmount)
                ? decimalAmount
                : 0m,
            PixCode = paymentMethod.ValueKind == JsonValueKind.Object ? ReadString(paymentMethod, "qr_code") : null,
            QrCodeBase64 = paymentMethod.ValueKind == JsonValueKind.Object ? ReadString(paymentMethod, "qr_code_base64") : null
        };
    }

    private static JsonElement GetFirstPayment(JsonElement root)
    {
        if (root.TryGetProperty("transactions", out var transactions) &&
            transactions.TryGetProperty("payments", out var payments) &&
            payments.ValueKind == JsonValueKind.Array)
        {
            return payments.EnumerateArray().FirstOrDefault();
        }

        return default;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString()
            : null;

    private static bool TryReadDecimal(JsonElement element, out decimal value) =>
        element.ValueKind == JsonValueKind.Number
            ? element.TryGetDecimal(out value)
            : decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static string GetIsoDuration(int minutes)
    {
        var duration = TimeSpan.FromMinutes(minutes);
        var days = (int)duration.TotalDays;
        var hours = duration.Hours;
        var remainingMinutes = duration.Minutes;
        var datePart = days > 0 ? $"{days}D" : "";
        var timePart = hours > 0 || remainingMinutes > 0
            ? $"T{(hours > 0 ? $"{hours}H" : "")}{(remainingMinutes > 0 ? $"{remainingMinutes}M" : "")}"
            : "";
        return $"P{datePart}{timePart}";
    }

    private static string GetErrorReason(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var message = ReadString(root, "message");
            var error = ReadString(root, "error");
            var causes = new List<string>();

            if (root.TryGetProperty("cause", out var cause) && cause.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cause.EnumerateArray())
                {
                    var code = ReadString(item, "code");
                    var description = ReadString(item, "description");
                    if (!string.IsNullOrWhiteSpace(code) || !string.IsNullOrWhiteSpace(description))
                    {
                        causes.Add(string.Join(": ", new[] { code, description }.Where(value => !string.IsNullOrWhiteSpace(value))));
                    }
                }
            }

            var details = string.Join(" | ", new[] { error, message }.Concat(causes).Where(value => !string.IsNullOrWhiteSpace(value)));
            return string.IsNullOrWhiteSpace(details) ? "Resposta sem detalhe legível." : details;
        }
        catch (JsonException)
        {
            return "Resposta não estruturada.";
        }
    }

    private static string GetRequestId(HttpResponseMessage response)
    {
        foreach (var headerName in new[] { "x-request-id", "x-mp-request-id" })
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                var value = values.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }

        return "não informado";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}

public class MercadoPagoPixPayment
{
    public string? Id { get; set; }
    public string? Status { get; set; }
    public string? ExternalReference { get; set; }
    public string? CurrencyId { get; set; }
    public decimal Amount { get; set; }
    public string? PixCode { get; set; }
    public string? QrCodeBase64 { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? IdempotencyKey { get; set; }
    public bool IsApproved => string.Equals(Status, "processed", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(Status, "approved", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(Status, "accredited", StringComparison.OrdinalIgnoreCase);
}

public enum PixCancelResult
{
    Cancelled,
    AlreadyPaid,
    Failed
}

public class MercadoPagoPixException : Exception
{
    public MercadoPagoPixException(string message) : base(message) { }
}

internal class MercadoPagoCreateOrderRequest
{
    public string Type { get; set; } = "online";
    public string TotalAmount { get; set; } = "";
    public string ExternalReference { get; set; } = "";
    public string ProcessingMode { get; set; } = "automatic";
    public MercadoPagoTransactionsRequest Transactions { get; set; } = new();
    public MercadoPagoOrderPayerRequest Payer { get; set; } = new();
}

internal class MercadoPagoTransactionsRequest
{
    public List<MercadoPagoOrderPaymentRequest> Payments { get; set; } = new();
}

internal class MercadoPagoOrderPaymentRequest
{
    public string Amount { get; set; } = "";
    public MercadoPagoPaymentMethodRequest PaymentMethod { get; set; } = new();
    public string ExpirationTime { get; set; } = "P1D";
}

internal class MercadoPagoPaymentMethodRequest
{
    public string Id { get; set; } = "pix";
    public string Type { get; set; } = "bank_transfer";
}

internal class MercadoPagoOrderPayerRequest
{
    public string Email { get; set; } = "";
}
