using System.IO.Compression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PrintShop.Models;
using PrintShop.Services;

namespace PrintShop.Tests;

public class MaintenanceAndAuditTests : IDisposable
{
    private readonly TestStorage _storage = new();
    private readonly OrderQueueService _queue;
    private readonly MaintenanceService _maintenance;

    public MaintenanceAndAuditTests()
    {
        _queue = new OrderQueueService(_storage.Paths);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PrintShop:Maintenance:DraftRetentionDays"] = "7",
                ["PrintShop:Maintenance:FileRetentionDays"] = "90",
                ["PrintShop:Maintenance:BackupKeep"] = "2"
            })
            .Build();
        _maintenance = new MaintenanceService(
            _storage.Paths, _queue, new FileStorageService(_storage.Paths), configuration,
            NullLogger<MaintenanceService>.Instance);
    }

    [Fact]
    public async Task Remove_rascunho_antigo_com_arquivo_e_mantem_o_recente()
    {
        var now = DateTime.Now;
        var oldDraft = await AddAsync(OrderStatus.Draft, now.AddDays(-10));
        var newDraft = await AddAsync(OrderStatus.Draft, now.AddDays(-1));

        var result = await _maintenance.RunOnceAsync(now);

        Assert.Equal(1, result.DraftsRemoved);
        Assert.Null(await _queue.GetAsync(oldDraft.Id));
        Assert.False(File.Exists(Upload(oldDraft)));
        Assert.NotNull(await _queue.GetAsync(newDraft.Id));
        Assert.True(File.Exists(Upload(newDraft)));
    }

    [Fact]
    public async Task Apaga_arquivo_de_pedido_entregue_antigo_mas_mantem_o_pedido()
    {
        var now = DateTime.Now;
        var delivered = await AddAsync(OrderStatus.Delivered, now.AddDays(-120));
        var pending = await AddAsync(OrderStatus.PendingPayment, now.AddDays(-120));

        await _maintenance.RunOnceAsync(now);

        Assert.NotNull(await _queue.GetAsync(delivered.Id));
        Assert.False(File.Exists(Upload(delivered)));
        Assert.True(File.Exists(Upload(pending)));
    }

    [Fact]
    public async Task Apaga_arquivo_orfao_antigo_e_preserva_o_recem_enviado()
    {
        var now = DateTime.Now;
        var oldOrphan = _storage.WriteUpload("orfao-antigo.pdf");
        File.SetLastWriteTime(oldOrphan, now.AddDays(-3));
        var freshOrphan = _storage.WriteUpload("orfao-novo.pdf");

        await _maintenance.RunOnceAsync(now);

        Assert.False(File.Exists(oldOrphan));
        Assert.True(File.Exists(freshOrphan));
    }

    [Fact]
    public async Task Backup_tem_banco_e_chaves_e_respeita_o_limite()
    {
        File.WriteAllText(Path.Combine(_storage.Paths.KeysPath, "key-teste.xml"), "<key/>");
        await AddAsync(OrderStatus.Delivered, DateTime.Now);

        string? last = null;
        for (var i = 0; i < 3; i++)
        {
            last = (await _maintenance.RunOnceAsync(DateTime.Now.AddMinutes(i))).BackupFile;
        }

        using (var zip = ZipFile.OpenRead(last!))
        {
            Assert.Contains(zip.Entries, entry => entry.FullName == "printshop.db" && entry.Length > 0);
            Assert.Contains(zip.Entries, entry => entry.FullName == "keys/key-teste.xml");
        }
        Assert.Equal(2, Directory.GetFiles(_maintenance.BackupPath, "printshop-*.zip").Length);
    }

    [Fact]
    public void Auditoria_registra_e_busca_acoes()
    {
        var audit = new AdminAuditService(_storage.Paths);
        audit.Log("ana", "Autorizou pagamento", "Pedido #ABC123", "Na loja, R$ 10,00", "192.168.1.5");
        audit.Log("bruno", "Barrou pedido", "Pedido #XYZ999", "", "192.168.1.6");

        var all = audit.GetRecent();
        var found = audit.GetRecent(search: "ABC123");

        Assert.Equal(2, all.Count);
        Assert.Equal("Barrou pedido", all[0].Action); // mais recente primeiro
        Assert.Equal("ana", Assert.Single(found).Username);
    }

    private async Task<PrintOrder> AddAsync(OrderStatus status, DateTime createdAt)
    {
        var order = TestStorage.Order((1, 1));
        order.Status = status;
        order.CreatedAt = createdAt;
        await _queue.AddAsync(order);
        _storage.WriteUpload(order.Files[0].StoredName);
        File.SetLastWriteTime(Upload(order), createdAt);
        return order;
    }

    private string Upload(PrintOrder order) => Path.Combine(_storage.Paths.UploadsPath, order.Files[0].StoredName);

    public void Dispose() => _storage.Dispose();
}
