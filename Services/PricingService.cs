using PrintShop.Models;

namespace PrintShop.Services;

public class PricingService
{
    public const decimal DefaultDeliveryFee = 8.00m;

    private readonly AdminSettingsService _settings;

    public PricingService(AdminSettingsService settings)
    {
        _settings = settings;
    }

    public decimal Calculate(PrintOrder order)
    {
        int totalPrintedPages = GetTotalPrintedPages(order);
        var settings = _settings.Get();

        decimal pricePerPage = GetColorPrice(order.Color, settings) + GetPaperExtra(order.PaperType, settings);
        decimal printTotal = pricePerPage * totalPrintedPages;

        decimal laminateTotal = order.Laminate
            ? settings.LaminatePrice * totalPrintedPages
            : 0m;

        decimal deliveryTotal = order.FulfillmentMethod == FulfillmentMethod.Delivery
            ? order.DeliveryFee
            : 0m;

        return Math.Round(printTotal + laminateTotal + deliveryTotal, 2);
    }

    public PriceBreakdown GetBreakdown(PrintOrder order)
    {
        int totalPages = order.Files.Sum(f => f.PageCount);
        int totalPrintedPages = GetTotalPrintedPages(order);
        var settings = _settings.Get();
        decimal pricePerPage = GetColorPrice(order.Color, settings) + GetPaperExtra(order.PaperType, settings);

        return new PriceBreakdown
        {
            TotalPages = totalPages,
            TotalPrintedPages = totalPrintedPages,
            PricePerPage = pricePerPage,
            SideMultiplier = 1.0m,
            PrintSubtotal = Math.Round(pricePerPage * totalPrintedPages, 2),
            LaminateSubtotal = order.Laminate ? Math.Round(settings.LaminatePrice * totalPrintedPages, 2) : 0m,
            DeliverySubtotal = order.FulfillmentMethod == FulfillmentMethod.Delivery ? order.DeliveryFee : 0m,
            Total = Calculate(order)
        };
    }

    public decimal GetDeliveryFee() => _settings.Get().DeliveryFee;

    public Models.AdminSettings GetSettings() => _settings.Get();

    private static decimal GetColorPrice(PrintColor color, Models.AdminSettings settings) =>
        color == PrintColor.Color ? settings.ColorPrice : settings.BlackAndWhitePrice;

    private static decimal GetPaperExtra(PaperType paper, Models.AdminSettings settings) => paper switch
    {
        PaperType.A4_90g => settings.A4_90gExtra,
        PaperType.A3_75g => settings.A3_75gExtra,
        PaperType.Glossy => settings.GlossyExtra,
        _ => 0m
    };

    private static int GetTotalPrintedPages(PrintOrder order) =>
        order.Files.Sum(file => file.PageCount * Math.Max(1, file.Copies));
}

public class PriceBreakdown
{
    public int TotalPages { get; set; }
    public int TotalPrintedPages { get; set; }
    public decimal PricePerPage { get; set; }
    public decimal SideMultiplier { get; set; }
    public decimal PrintSubtotal { get; set; }
    public decimal LaminateSubtotal { get; set; }
    public decimal DeliverySubtotal { get; set; }
    public decimal Total { get; set; }
}
