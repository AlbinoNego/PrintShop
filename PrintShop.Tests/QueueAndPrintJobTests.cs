using Microsoft.Data.Sqlite;
using PrintShop.Models;
using PrintShop.Services;

namespace PrintShop.Tests;

public class QueueAndPrintJobTests : IDisposable
{
    private readonly TestStorage _storage = new();
    private readonly OrderQueueService _queue;
    private readonly PrinterRegistryService _jobs;

    public QueueAndPrintJobTests()
    {
        _queue = new OrderQueueService(_storage.Paths);
        _jobs = new PrinterRegistryService(_storage.Paths);
    }

    [Fact]
    public async Task Pedido_e_arquivos_voltam_iguais_do_banco()
    {
        var order = TestStorage.Order((4, 2), (1, 3));
        order.TotalPrice = 12.34m;
        await _queue.AddAsync(order);

        var saved = await _queue.GetAsync(order.Id);

        Assert.NotNull(saved);
        Assert.Equal(12.34m, saved!.TotalPrice);
        Assert.Equal(new[] { 2, 3 }, saved.Files.OrderBy(file => file.PageCount).Select(file => file.Copies).Reverse().ToArray());
    }

    [Fact]
    public async Task Banco_usa_wal_e_foreign_keys()
    {
        using var connection = new SqliteConnection(PrintShopDatabase.ConnectionString(_storage.Paths, 5));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string?)command.ExecuteScalar());
        command.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Apagar_pedido_remove_arquivos_e_trabalhos_em_cascata()
    {
        var order = await AddPaidOrderAsync((2, 1));
        await _jobs.EnqueueOrderAsync(order);

        await _queue.DeleteAsync(order.Id);

        Assert.Null(await _queue.GetAsync(order.Id));
        Assert.Empty(_jobs.GetRecentJobs());
    }

    [Fact]
    public async Task Enfileirar_de_novo_nao_duplica_trabalhos_abertos()
    {
        var order = await AddPaidOrderAsync((1, 1), (2, 1));

        await _jobs.EnqueueOrderAsync(order);
        await _jobs.EnqueueOrderAsync(order);

        Assert.Equal(2, _jobs.GetRecentJobs().Count);
    }

    [Fact]
    public async Task Pedido_colorido_so_vai_para_impressora_colorida()
    {
        var order = await AddPaidOrderAsync((1, 1));
        order.Color = PrintColor.Color;
        await _queue.UpdateAsync(order);
        await _jobs.EnqueueOrderAsync(order);

        _jobs.AddPrinter(new PrinterDefinition { Name = "Laser PB", SystemName = "PB" });
        Assert.Null(await _jobs.TryClaimNextJobAsync());

        _jobs.AddPrinter(new PrinterDefinition { Name = "Jato colorida", SystemName = "COR", SupportsColor = true });
        var job = await _jobs.TryClaimNextJobAsync();

        Assert.NotNull(job);
        Assert.Equal("COR", job!.PrinterSystemName);
        Assert.Equal(PrintJobStatus.Processing, job.Status);
    }

    [Fact]
    public async Task Trabalho_reivindicado_nao_e_entregue_duas_vezes()
    {
        var order = await AddPaidOrderAsync((1, 1));
        await _jobs.EnqueueOrderAsync(order);
        _jobs.AddPrinter(new PrinterDefinition { Name = "Laser", SystemName = "L" });

        Assert.NotNull(await _jobs.TryClaimNextJobAsync());
        Assert.Null(await _jobs.TryClaimNextJobAsync());
    }

    [Fact]
    public async Task Concessao_do_agente_e_exclusiva_ate_expirar()
    {
        Assert.True(await _jobs.TryAcquireAgentLeaseAsync("agente-a", TimeSpan.FromMinutes(2)));
        Assert.False(await _jobs.TryAcquireAgentLeaseAsync("agente-b", TimeSpan.FromMinutes(2)));
        Assert.True(await _jobs.TryAcquireAgentLeaseAsync("agente-a", TimeSpan.FromMinutes(2)));

        await _jobs.ReleaseAgentLeaseAsync("agente-a");
        Assert.True(await _jobs.TryAcquireAgentLeaseAsync("agente-b", TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public async Task Trabalho_preso_imprimindo_vira_falha_ao_reiniciar_agente()
    {
        var order = await AddPaidOrderAsync((1, 1));
        await _jobs.EnqueueOrderAsync(order);
        _jobs.AddPrinter(new PrinterDefinition { Name = "Laser", SystemName = "L" });
        await _jobs.TryClaimNextJobAsync();

        var stale = await _jobs.FailStaleProcessingJobsAsync(TimeSpan.Zero);

        Assert.Equal(new[] { order.Id }, stale);
        Assert.Equal(PrintJobStatus.Failed, _jobs.GetRecentJobs().Single().Status);
    }

    [Fact]
    public void Nao_cadastra_a_mesma_impressora_do_windows_duas_vezes()
    {
        _jobs.AddPrinter(new PrinterDefinition { Name = "Preto e branco", SystemName = "Brother HL-5370DW series" });

        var error = Assert.Throws<ArgumentException>(() =>
            _jobs.AddPrinter(new PrinterDefinition { Name = "Outra", SystemName = "brother hl-5370dw series " }));

        Assert.Contains("Preto e branco", error.Message);
        Assert.Single(_jobs.GetPrinters());
    }

    [Fact]
    public void Nao_cadastra_dois_nomes_iguais_no_painel()
    {
        _jobs.AddPrinter(new PrinterDefinition { Name = "Preto e branco", SystemName = "Brother" });

        Assert.Throws<ArgumentException>(() =>
            _jobs.AddPrinter(new PrinterDefinition { Name = "preto e branco", SystemName = "HP" }));
    }

    [Fact]
    public async Task Remove_impressora_e_mantem_o_historico_de_trabalhos()
    {
        var order = await AddPaidOrderAsync((1, 1));
        await _jobs.EnqueueOrderAsync(order);
        _jobs.AddPrinter(new PrinterDefinition { Name = "Laser", SystemName = "L" });
        var job = await _jobs.TryClaimNextJobAsync();
        await _jobs.CompleteJobAsync(job!.Id, success: true);
        var printerId = _jobs.GetPrinters().Single().Id;

        Assert.Equal(PrinterRemovalResult.Removed, _jobs.RemovePrinter(printerId));

        Assert.Empty(_jobs.GetPrinters());
        var history = _jobs.GetRecentJobs().Single();
        Assert.Equal(PrintJobStatus.Printed, history.Status);
        Assert.Null(history.PrinterId);
    }

    [Fact]
    public async Task Nao_remove_impressora_que_esta_imprimindo()
    {
        var order = await AddPaidOrderAsync((1, 1));
        await _jobs.EnqueueOrderAsync(order);
        _jobs.AddPrinter(new PrinterDefinition { Name = "Laser", SystemName = "L" });
        await _jobs.TryClaimNextJobAsync();

        Assert.Equal(PrinterRemovalResult.Printing, _jobs.RemovePrinter(_jobs.GetPrinters().Single().Id));
        Assert.Single(_jobs.GetPrinters());
    }

    [Fact]
    public void Remover_impressora_inexistente_avisa()
    {
        Assert.Equal(PrinterRemovalResult.NotFound, _jobs.RemovePrinter("nao-existe"));
    }

    private async Task<PrintOrder> AddPaidOrderAsync(params (int Pages, int Copies)[] files)
    {
        var order = TestStorage.Order(files);
        order.Status = OrderStatus.PaymentConfirmed;
        order.PaymentConfirmed = true;
        await _queue.AddAsync(order);
        return (await _queue.GetAsync(order.Id))!;
    }

    public void Dispose() => _storage.Dispose();
}
