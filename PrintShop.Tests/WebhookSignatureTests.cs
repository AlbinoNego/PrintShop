using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PrintShop.Services;

namespace PrintShop.Tests;

public class WebhookSignatureTests
{
    private const string Secret = "segredo-de-teste";

    [Fact]
    public void Aceita_assinatura_correta()
    {
        var request = SignedRequest("ORD01ABC", "req-1", "1700000000", Secret);

        Assert.True(Service(Secret).IsValidWebhookSignature(request, "ORD01ABC"));
    }

    [Fact]
    public void Recusa_assinatura_de_outro_pagamento()
    {
        var request = SignedRequest("ORD01ABC", "req-1", "1700000000", Secret);

        Assert.False(Service(Secret).IsValidWebhookSignature(request, "ORD99XYZ"));
    }

    [Fact]
    public void Recusa_assinatura_feita_com_outro_segredo()
    {
        var request = SignedRequest("ORD01ABC", "req-1", "1700000000", "outro-segredo");

        Assert.False(Service(Secret).IsValidWebhookSignature(request, "ORD01ABC"));
    }

    [Fact]
    public void Recusa_tudo_quando_o_segredo_nao_esta_configurado()
    {
        var request = SignedRequest("ORD01ABC", "req-1", "1700000000", Secret);

        Assert.False(Service(null).IsValidWebhookSignature(request, "ORD01ABC"));
    }

    private static MercadoPagoPixService Service(string? secret)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MercadoPago:WebhookSecret"] = secret })
            .Build();
        return new MercadoPagoPixService(new HttpClient(), configuration, NullLogger<MercadoPagoPixService>.Instance);
    }

    // Mesmo formato do Mercado Pago: manifesto "id:...;request-id:...;ts:...;" assinado com HMAC-SHA256.
    private static HttpRequest SignedRequest(string id, string requestId, string ts, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(
            $"id:{id.ToLowerInvariant()};request-id:{requestId};ts:{ts};"))).ToLowerInvariant();

        var context = new DefaultHttpContext();
        context.Request.Headers["x-signature"] = $"ts={ts},v1={hash}";
        context.Request.Headers["x-request-id"] = requestId;
        return context.Request;
    }
}
