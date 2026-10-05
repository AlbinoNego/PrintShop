namespace PrintShop.Models;

public class PrinterManagementViewModel
{
    public List<PrinterDefinition> Printers { get; set; } = new();
    public List<PrintJob> Jobs { get; set; } = new();
    public List<string> SystemPrinters { get; set; } = new();
    public PrintJobSummary Summary { get; set; } = new();
    public PrintJobStatus? SelectedStatus { get; set; }
}
