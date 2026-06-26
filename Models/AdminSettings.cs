namespace PrintShop.Models;

public class AdminSettings
{
    public decimal BlackAndWhitePrice { get; set; } = 0.20m;
    public decimal ColorPrice { get; set; } = 0.20m;
    public decimal A4_90gExtra { get; set; } = 0.20m;
    public decimal A3_75gExtra { get; set; } = 0.50m;
    public decimal GlossyExtra { get; set; } = 1.00m;
    public decimal LaminatePrice { get; set; } = 3.50m;
    public decimal DeliveryFee { get; set; } = 8.00m;
    public string DefaultPrinter { get; set; } = "";
    public string PdfPrinter { get; set; } = "";
    public string WordPrinter { get; set; } = "";
    public string PowerPointPrinter { get; set; } = "";
    public string ImagePrinter { get; set; } = "";
    public bool AutomaticPrintingEnabled { get; set; } = true;
}
