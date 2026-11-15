using PrintShop.Models;
using PrintShop.Services;

namespace PrintShop.Tests;

public class PricingServiceTests : IDisposable
{
    private readonly TestStorage _storage = new();
    private readonly PricingService _pricing;

    public PricingServiceTests()
    {
        var settings = new AdminSettingsService(_storage.Paths);
        settings.Save(new AdminSettings
        {
            BlackAndWhitePrice = 0.50m,
            ColorPrice = 2.00m,
            A4_90gExtra = 0.10m,
            A3_75gExtra = 1.00m,
            GlossyExtra = 3.00m,
            LaminatePrice = 4.00m,
            DeliveryFee = 8.00m
        });
        _pricing = new PricingService(settings);
    }

    [Fact]
    public void Cobra_paginas_vezes_copias_de_cada_arquivo()
    {
        var order = TestStorage.Order((3, 2), (1, 1));

        Assert.Equal(3.50m, _pricing.Calculate(order)); // 7 paginas x 0,50
    }

    [Fact]
    public void Cor_e_papel_somam_no_preco_por_pagina()
    {
        var order = TestStorage.Order((2, 1));
        order.Color = PrintColor.Color;
        order.PaperType = PaperType.A3_75g;

        Assert.Equal(6.00m, _pricing.Calculate(order)); // 2 x (2,00 + 1,00)
    }

    [Fact]
    public void Plastificacao_frente_e_verso_cobra_por_folha()
    {
        var order = TestStorage.Order((3, 2));
        order.Sides = PrintSides.BothSides;
        order.Laminate = true;

        // 3 paginas em frente e verso = 2 folhas por copia, 2 copias = 4 folhas
        Assert.Equal(4, PricingService.GetTotalSheets(order));
        Assert.Equal(3.00m + 16.00m, _pricing.Calculate(order));
    }

    [Fact]
    public void Plastificacao_so_frente_cobra_uma_folha_por_pagina()
    {
        var order = TestStorage.Order((3, 1));
        order.Laminate = true;

        Assert.Equal(3, PricingService.GetTotalSheets(order));
        Assert.Equal(1.50m + 12.00m, _pricing.Calculate(order));
    }

    [Fact]
    public void Entrega_soma_a_taxa_e_retirada_nao()
    {
        var order = TestStorage.Order((1, 1));
        order.DeliveryFee = 8.00m;

        Assert.Equal(0.50m, _pricing.Calculate(order));

        order.FulfillmentMethod = FulfillmentMethod.Delivery;
        Assert.Equal(8.50m, _pricing.Calculate(order));
    }

    [Fact]
    public void Detalhamento_bate_com_o_total()
    {
        var order = TestStorage.Order((5, 3));
        order.Sides = PrintSides.BothSides;
        order.Laminate = true;

        var breakdown = _pricing.GetBreakdown(order);

        Assert.Equal(15, breakdown.TotalPrintedPages);
        Assert.Equal(9, breakdown.TotalSheets);
        Assert.Equal(breakdown.PrintSubtotal + breakdown.LaminateSubtotal, breakdown.Total);
    }

    public void Dispose() => _storage.Dispose();
}
