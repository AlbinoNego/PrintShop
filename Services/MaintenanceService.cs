using System.IO.Compression;
using Microsoft.Data.Sqlite;
using PrintShop.Models;

namespace PrintShop.Services;

public class MaintenanceOptions
{
    // Rascunhos que o cliente nunca levou ao pagamento.
    public int DraftRetentionDays { get; set; } = 7;
    // Arquivos de pedidos entregues ou cancelados; 0 mantem para sempre. O pedido continua nos relatorios.
    public int FileRetentionDays { get; set; } = 90;
    public int BackupKeep { get; set; } = 14;
    // Pasta extra (outro disco ou compartilhamento) para uma copia de cada backup.
    public string? BackupCopyPath { get; set; }
    public int IntervalHours { get; set; } = 24;
}

public record MaintenanceResult(int DraftsRemoved, int FilesRemoved, string? BackupFile);

// Roda dentro do site: backup do banco e das chaves, e limpeza de rascunhos e arquivos antigos.
public class MaintenanceService : BackgroundService
{
    private static readonly OrderStatus[] FinishedStatuses = { OrderStatus.Delivered, OrderStatus.Cancelled };

    private readonly AppStoragePathService _paths;
    private readonly OrderQueueService _queue;
    private readonly FileStorageService _files;
    private readonly MaintenanceOptions _options;
    private readonly ILogger<MaintenanceService> _logger;

    public MaintenanceService(
        AppStoragePathService paths,
        OrderQueueService queue,
        FileStorageService files,
        IConfiguration configuration,
        ILogger<MaintenanceService> logger)
    {
        _paths = paths;
        _queue = queue;
        _files = files;
        _options = configuration.GetSection("PrintShop:Maintenance").Get<MaintenanceOptions>() ?? new MaintenanceOptions();
        _logger = logger;
    }

    public string BackupPath => Path.Combine(_paths.DataPath, "backups");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Espera o site terminar de subir antes da primeira rodada.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await RunOnceAsync(DateTime.Now, stoppingToken);
                _logger.LogInformation(
                    "Manutencao concluida: {Drafts} rascunho(s) e {Files} arquivo(s) removidos; backup {Backup}.",
                    result.DraftsRemoved, result.FilesRemoved, result.BackupFile ?? "nao gerado");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Falha na manutencao automatica.");
            }

            await Task.Delay(TimeSpan.FromHours(Math.Clamp(_options.IntervalHours, 1, 168)), stoppingToken);
        }
    }

    // O backup vem antes da limpeza, para que nada seja apagado sem uma copia do dia.
    public async Task<MaintenanceResult> RunOnceAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        var backup = CreateBackup(now);
        var orders = await _queue.GetAllAsync();

        var drafts = 0;
        foreach (var order in orders.Where(order =>
                     order.Status == OrderStatus.Draft &&
                     order.CreatedAt < now.AddDays(-Math.Max(1, _options.DraftRetentionDays))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in order.Files) _files.Delete(file.StoredName);
            await _queue.DeleteAsync(order.Id);
            drafts++;
        }

        var removedFiles = 0;
        if (_options.FileRetentionDays > 0)
        {
            foreach (var order in orders.Where(order =>
                         FinishedStatuses.Contains(order.Status) &&
                         order.CreatedAt < now.AddDays(-_options.FileRetentionDays)))
            {
                foreach (var file in order.Files)
                {
                    if (File.Exists(_files.GetPath(file.StoredName)))
                    {
                        _files.Delete(file.StoredName);
                        removedFiles++;
                    }
                }
            }
        }

        // Arquivos sem pedido (upload interrompido, pedido apagado). Um dia de folga evita
        // apagar um arquivo que acabou de ser salvo e ainda nao foi gravado no banco.
        var referenced = (await _queue.GetAllAsync())
            .SelectMany(order => order.Files)
            .Select(file => file.StoredName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(_paths.UploadsPath))
        {
            if (!referenced.Contains(Path.GetFileName(path)) && File.GetLastWriteTime(path) < now.AddDays(-1))
            {
                File.Delete(path);
                removedFiles++;
            }
        }

        return new MaintenanceResult(drafts, removedFiles, backup);
    }

    // Copia consistente do banco (VACUUM INTO funciona com o site e o agente usando o arquivo)
    // mais as chaves de Data Protection, sem as quais as sessoes e cookies deixam de valer.
    // Os uploads nao entram: sao grandes e somem pela retencao.
    private string CreateBackup(DateTime now)
    {
        Directory.CreateDirectory(BackupPath);
        var stamp = now.ToString("yyyyMMdd-HHmmss");
        var tempDb = Path.Combine(BackupPath, $"printshop-{stamp}.db");
        var zipPath = Path.Combine(BackupPath, $"printshop-{stamp}.zip");

        using (var connection = new SqliteConnection(PrintShopDatabase.ConnectionString(_paths, 30)))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $target;";
            command.Parameters.AddWithValue("$target", tempDb);
            command.ExecuteNonQuery();
        }

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(tempDb, "printshop.db");
            foreach (var key in Directory.EnumerateFiles(_paths.KeysPath))
            {
                zip.CreateEntryFromFile(key, "keys/" + Path.GetFileName(key));
            }
        }
        SqliteConnection.ClearAllPools();
        File.Delete(tempDb);

        if (!string.IsNullOrWhiteSpace(_options.BackupCopyPath))
        {
            try
            {
                Directory.CreateDirectory(_options.BackupCopyPath);
                File.Copy(zipPath, Path.Combine(_options.BackupCopyPath, Path.GetFileName(zipPath)), overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Backup gerado, mas a copia para {Path} falhou.", _options.BackupCopyPath);
            }
        }

        foreach (var old in Directory.EnumerateFiles(BackupPath, "printshop-*.zip")
                     .OrderByDescending(path => path, StringComparer.Ordinal)
                     .Skip(Math.Max(1, _options.BackupKeep)))
        {
            File.Delete(old);
        }

        return zipPath;
    }
}
