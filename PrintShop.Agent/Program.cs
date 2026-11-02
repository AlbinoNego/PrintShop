using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrintShop.Models;
using PrintShop.Services;

var siteRoot = GetOption(args, "--site-root");
if (string.IsNullOrWhiteSpace(siteRoot) || !Directory.Exists(siteRoot))
{
    Console.Error.WriteLine("Uso: PrintShop.Agent --site-root \"C:\\Sites\\PrintShop\" [--poll-seconds 3]");
    return 1;
}

siteRoot = Path.GetFullPath(siteRoot);
var pollSeconds = int.TryParse(GetOption(args, "--poll-seconds"), out var value)
    ? Math.Clamp(value, 1, 60)
    : 3;

var configuration = new ConfigurationBuilder()
    .SetBasePath(siteRoot)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Production.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

using var loggerFactory = LoggerFactory.Create(logging =>
{
    logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
    logging.SetMinimumLevel(LogLevel.Information);
});

var paths = new AppStoragePathService(siteRoot);
var settings = new AdminSettingsService(paths);
var queue = new OrderQueueService(paths);
var jobs = new PrinterRegistryService(paths);
var files = new FileStorageService(paths);
var printer = new PrinterService(
    loggerFactory.CreateLogger<PrinterService>(),
    files,
    configuration,
    settings);
var logger = loggerFactory.CreateLogger("PrintShop.Agent");
var agentId = Guid.NewGuid().ToString("N");
var leaseDuration = TimeSpan.FromMinutes(30);

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Cancel();
};

logger.LogInformation("Agente de impressao iniciado. Pasta do site: {SiteRoot}", siteRoot);

if (!await jobs.TryAcquireAgentLeaseAsync(agentId, leaseDuration))
{
    logger.LogCritical("Outro agente de impressao ja esta ativo para esta pasta do site.");
    return 1;
}

try
{
    var staleOrderIds = await jobs.FailStaleProcessingJobsAsync(TimeSpan.FromMinutes(30));
    foreach (var orderId in staleOrderIds.Distinct())
    {
        var staleOrder = await queue.GetAsync(orderId);
        if (staleOrder == null || staleOrder.Status == OrderStatus.Cancelled) continue;

        await jobs.CancelOpenJobsAsync(orderId, "Cancelado após interrupção anterior do agente de impressão.");
        staleOrder.Status = OrderStatus.PrintFailed;
        await queue.UpdateAsync(staleOrder);
    }

    if (staleOrderIds.Count > 0)
    {
        logger.LogWarning("{Count} trabalho(s) interrompido(s) foram marcados como falha para revisão.", staleOrderIds.Count);
    }

    while (!stopping.IsCancellationRequested)
    {
        if (!await jobs.TryAcquireAgentLeaseAsync(agentId, leaseDuration))
        {
            logger.LogCritical("O agente perdeu a concessão exclusiva de impressão e será encerrado.");
            return 1;
        }

        if (!settings.Get().AutomaticPrintingEnabled)
        {
            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stopping.Token);
            continue;
        }

        var job = await jobs.TryClaimNextJobAsync();
        if (job == null)
        {
            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stopping.Token);
            continue;
        }

        await ProcessJobAsync(job, queue, jobs, printer, logger);
    }
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested)
{
    logger.LogInformation("Agente de impressao finalizado.");
}
catch (Exception ex)
{
    logger.LogCritical(ex, "O agente de impressao foi interrompido por um erro nao tratado.");
    return 1;
}
finally
{
    await jobs.ReleaseAgentLeaseAsync(agentId);
}

return 0;

static async Task ProcessJobAsync(
    PrintJob job,
    OrderQueueService queue,
    PrinterRegistryService jobs,
    PrinterService printer,
    ILogger logger)
{
    var order = await queue.GetAsync(job.PrintOrderId);
    var file = order?.Files.FirstOrDefault(item => item.Id == job.UploadedFileId);

    if (order == null || file == null)
    {
        const string reason = "Pedido ou arquivo não encontrado para impressão.";
        await jobs.CompleteJobAsync(job.Id, success: false, reason);
        if (order != null)
        {
            order.Status = OrderStatus.PrintFailed;
            await queue.UpdateAsync(order);
        }
        logger.LogWarning("Trabalho {JobId} ignorado: {Reason}", job.Id, reason);
        return;
    }

    order.Status = OrderStatus.Printing;
    await queue.UpdateAsync(order);

    logger.LogInformation("Imprimindo trabalho {JobId}: pedido {OrderId}, arquivo {FileName}, impressora {Printer}",
        job.Id, order.Id, file.OriginalName, job.PrinterName ?? "nao definida");
    var success = await printer.PrintFileAsync(file, order, job.PrinterSystemName);

    if (!success)
    {
        const string reason = "A impressão não foi concluída pelo Windows (falha ou tempo esgotado).";
        await jobs.CompleteJobAsync(job.Id, success: false, reason);
        await jobs.CancelOpenJobsAsync(order.Id, "Cancelado após falha de outro arquivo.", job.Id);
        order.Status = OrderStatus.PrintFailed;
        await queue.UpdateAsync(order);
        logger.LogError("Falha ao imprimir trabalho {JobId} do pedido {OrderId}", job.Id, order.Id);
        return;
    }

    await jobs.CompleteJobAsync(job.Id, success: true);
    var currentOrder = await queue.GetAsync(order.Id);
    if (currentOrder?.Status == OrderStatus.Cancelled)
    {
        logger.LogInformation("Trabalho {JobId} terminou apos o cancelamento do pedido {OrderId}", job.Id, order.Id);
        return;
    }

    order.Status = await jobs.HasOpenJobsAsync(order.Id)
        ? OrderStatus.Printing
        : OrderStatus.Ready;
    await queue.UpdateAsync(order);
    logger.LogInformation("Trabalho {JobId} concluido. Pedido {OrderId}: {Status}", job.Id, order.Id, order.Status);
}

static string? GetOption(string[] arguments, string name)
{
    var index = Array.FindIndex(arguments, argument =>
        string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));

    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}
