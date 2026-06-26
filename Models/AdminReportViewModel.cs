namespace PrintShop.Models;

public class AdminReportViewModel
{
    public int TotalOrders { get; set; }
    public int PaidOrders { get; set; }
    public int PendingOrders { get; set; }
    public int CancelledOrders { get; set; }
    public int ReadyOrders { get; set; }
    public int TotalPages { get; set; }
    public int PrintedPages { get; set; }
    public int TotalFiles { get; set; }
    public decimal CompletedRevenue { get; set; }
    public decimal TotalRegisteredRevenue { get; set; }
    public decimal ActiveRevenue { get; set; }
    public List<ReportItem> StatusBreakdown { get; set; } = new();
    public List<ReportItem> ColorBreakdown { get; set; } = new();
    public List<ReportItem> PaperBreakdown { get; set; } = new();
    public List<ReportItem> PaymentBreakdown { get; set; } = new();
    public List<CustomerReportItem> TopCustomers { get; set; } = new();
}

public class ReportItem
{
    public string Label { get; set; } = "";
    public int Count { get; set; }
    public decimal Amount { get; set; }
}

public class CustomerReportItem
{
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public int Orders { get; set; }
    public int Pages { get; set; }
    public decimal Amount { get; set; }
}
