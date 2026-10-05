namespace PrintShop.Models;

public class PrinterDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string SystemName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool IsPaused { get; set; }
    public bool SupportsColor { get; set; }
    public bool SupportsA3 { get; set; }
    public bool SupportsDuplex { get; set; }
    public int PendingJobs { get; set; }
    public int ProcessingJobs { get; set; }
    public int FailedJobs { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public class PrintJob
{
    public long Id { get; set; }
    public string PrintOrderId { get; set; } = "";
    public int UploadedFileId { get; set; }
    public string FileName { get; set; } = "";
    public string? PrinterId { get; set; }
    public string? PrinterName { get; set; }
    public string? PrinterSystemName { get; set; }
    public PrintJobStatus Status { get; set; } = PrintJobStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public class PrintJobSummary
{
    public int Pending { get; set; }
    public int Processing { get; set; }
    public int Printed { get; set; }
    public int Failed { get; set; }
    public int Cancelled { get; set; }
}

public enum PrintJobStatus
{
    Pending,
    Processing,
    Printed,
    Failed,
    Cancelled
}
