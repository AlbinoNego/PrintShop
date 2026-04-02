namespace PrintShop.Models;

public class PrintOrder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8].ToUpper();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public OrderStatus Status { get; set; } = OrderStatus.PendingPayment;

    // Arquivos
    public List<UploadedFile> Files { get; set; } = new();

    // Opções de impressão
    public PrintColor Color { get; set; } = PrintColor.BlackAndWhite;
    public int Copies { get; set; } = 1;
    public PaperType PaperType { get; set; } = PaperType.A4_75g;
    public bool Laminate { get; set; } = false;
    public PrintSides Sides { get; set; } = PrintSides.OneSide;

    // Pagamento
    public PaymentMethod PaymentMethod { get; set; }
    public decimal TotalPrice { get; set; }
    public string? PixCode { get; set; }
    public bool PaymentConfirmed { get; set; } = false;

    // Contato
    public string CustomerName { get; set; } = "";
    public string CustomerPhone { get; set; } = "";
}

public class UploadedFile
{
    public string OriginalName { get; set; } = "";
    public string StoredName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public int PageCount { get; set; } = 1;
}

public enum OrderStatus
{
    PendingPayment,
    PaymentConfirmed,
    Printing,
    Ready,
    Delivered,
    Cancelled
}

public enum PrintColor
{
    BlackAndWhite,
    Color
}

public enum PaperType
{
    A4_75g,
    A4_90g,
    A3_75g,
    Glossy
}

public enum PrintSides
{
    OneSide,
    BothSides
}

public enum PaymentMethod
{
    Pix,
    InStore
}
