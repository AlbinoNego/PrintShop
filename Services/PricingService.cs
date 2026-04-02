using PrintShop.Models;

namespace PrintShop.Services;

public class PricingService
{
    // Preços base por página
    private static readonly Dictionary<PrintColor, decimal> ColorPrices = new()
    {
        { PrintColor.BlackAndWhite, 0.50m },
        { PrintColor.Color, 1.50m }
    };

    private static readonly Dictionary<PaperType, decimal> PaperPrices = new()
    {
        { PaperType.A4_75g,  0.00m },
        { PaperType.A4_90g,  0.20m },
        { PaperType.A3_75g,  0.50m },
        { PaperType.Glossy,  1.00m }
    };

    private const decimal LaminatePrice = 3.50m; // por folha

    public decimal Calculate(PrintOrder order)
    {
        int totalPages = order.Files.Sum(f => f.PageCount);

        // Frente e verso reduz custo
        decimal sideMultiplier = order.Sides == PrintSides.BothSides ? 0.8m : 1.0m;

        decimal pricePerPage = ColorPrices[order.Color] + PaperPrices[order.PaperType];
        decimal printTotal = pricePerPage * totalPages * order.Copies * sideMultiplier;

        decimal laminateTotal = order.Laminate
            ? LaminatePrice * totalPages * order.Copies
            : 0m;

        return Math.Round(printTotal + laminateTotal, 2);
    }

    public PriceBreakdown GetBreakdown(PrintOrder order)
    {
        int totalPages = order.Files.Sum(f => f.PageCount);
        decimal sideMultiplier = order.Sides == PrintSides.BothSides ? 0.8m : 1.0m;
        decimal pricePerPage = ColorPrices[order.Color] + PaperPrices[order.PaperType];

        return new PriceBreakdown
        {
            TotalPages = totalPages,
            PricePerPage = pricePerPage,
            SideMultiplier = sideMultiplier,
            PrintSubtotal = Math.Round(pricePerPage * totalPages * order.Copies * sideMultiplier, 2),
            LaminateSubtotal = order.Laminate ? Math.Round(LaminatePrice * totalPages * order.Copies, 2) : 0m,
            Total = Calculate(order)
        };
    }
}

public class PriceBreakdown
{
    public int TotalPages { get; set; }
    public decimal PricePerPage { get; set; }
    public decimal SideMultiplier { get; set; }
    public decimal PrintSubtotal { get; set; }
    public decimal LaminateSubtotal { get; set; }
    public decimal Total { get; set; }
}
