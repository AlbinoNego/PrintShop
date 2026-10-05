namespace PrintShop.Models;

public class PrinterManagementViewModel
{
    public List<PrinterDefinition> Printers { get; set; } = new();
    public List<PrintJob> Jobs { get; set; } = new();
    public List<string> SystemPrinters { get; set; } = new();
}
