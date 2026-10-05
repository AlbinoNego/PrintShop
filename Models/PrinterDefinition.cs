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
    public PrintJobStatus Status { get; set; } = PrintJobStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public enum PrintJobStatus
{
    Pending,
    Processing,
    Printed,
    Failed,
    Cancelled
}
