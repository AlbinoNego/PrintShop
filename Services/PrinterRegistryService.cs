using Microsoft.Data.Sqlite;
using PrintShop.Models;

namespace PrintShop.Services;

public class PrinterRegistryService
{
    private readonly string _connectionString;

    public PrinterRegistryService(AppStoragePathService paths)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(paths.DataPath, "printshop.db"),
            DefaultTimeout = 5
        }.ToString();

        EnsureDatabase();
    }

    public List<PrinterDefinition> GetPrinters()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, SystemName, IsActive, IsPaused, SupportsColor, SupportsA3, SupportsDuplex, CreatedAt
            FROM Printers
            ORDER BY Name COLLATE NOCASE;
            """;

        using var reader = command.ExecuteReader();
        var printers = new List<PrinterDefinition>();
        while (reader.Read())
        {
            printers.Add(new PrinterDefinition
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                SystemName = reader.GetString(2),
                IsActive = reader.GetInt32(3) == 1,
                IsPaused = reader.GetInt32(4) == 1,
                SupportsColor = reader.GetInt32(5) == 1,
                SupportsA3 = reader.GetInt32(6) == 1,
                SupportsDuplex = reader.GetInt32(7) == 1,
                CreatedAt = DateTime.Parse(reader.GetString(8))
            });
        }

        return printers;
    }

    public List<PrintJob> GetRecentJobs(int limit = 50)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT j.Id, j.PrintOrderId, j.UploadedFileId, f.OriginalName, j.PrinterId, p.Name,
                   j.Status, j.Attempts, j.LastError, j.CreatedAt, j.UpdatedAt
            FROM PrintJobs j
            LEFT JOIN UploadedFiles f ON f.Id = j.UploadedFileId
            LEFT JOIN Printers p ON p.Id = j.PrinterId
            ORDER BY j.CreatedAt DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        using var reader = command.ExecuteReader();
        var jobs = new List<PrintJob>();
        while (reader.Read())
        {
            jobs.Add(new PrintJob
            {
                Id = reader.GetInt64(0),
                PrintOrderId = reader.GetString(1),
                UploadedFileId = reader.GetInt32(2),
                FileName = reader.IsDBNull(3) ? "Arquivo removido" : reader.GetString(3),
                PrinterId = reader.IsDBNull(4) ? null : reader.GetString(4),
                PrinterName = reader.IsDBNull(5) ? null : reader.GetString(5),
                Status = (PrintJobStatus)reader.GetInt32(6),
                Attempts = reader.GetInt32(7),
                LastError = reader.IsDBNull(8) ? null : reader.GetString(8),
                CreatedAt = DateTime.Parse(reader.GetString(9)),
                UpdatedAt = DateTime.Parse(reader.GetString(10))
            });
        }

        return jobs;
    }

    public void AddPrinter(PrinterDefinition printer)
    {
        if (string.IsNullOrWhiteSpace(printer.Name) || string.IsNullOrWhiteSpace(printer.SystemName))
        {
            throw new ArgumentException("Informe o nome e a impressora instalada no Windows.");
        }

        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Printers
                (Id, Name, SystemName, IsActive, IsPaused, SupportsColor, SupportsA3, SupportsDuplex, CreatedAt)
            VALUES
                ($id, $name, $systemName, $isActive, $isPaused, $supportsColor, $supportsA3, $supportsDuplex, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", printer.Id);
        command.Parameters.AddWithValue("$name", printer.Name.Trim());
        command.Parameters.AddWithValue("$systemName", printer.SystemName.Trim());
        command.Parameters.AddWithValue("$isActive", printer.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$isPaused", printer.IsPaused ? 1 : 0);
        command.Parameters.AddWithValue("$supportsColor", printer.SupportsColor ? 1 : 0);
        command.Parameters.AddWithValue("$supportsA3", printer.SupportsA3 ? 1 : 0);
        command.Parameters.AddWithValue("$supportsDuplex", printer.SupportsDuplex ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", printer.CreatedAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void TogglePause(string printerId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Printers
            SET IsPaused = CASE WHEN IsPaused = 1 THEN 0 ELSE 1 END
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", printerId);
        command.ExecuteNonQuery();
    }

    public async Task EnqueueOrderAsync(PrintOrder order, bool forceNewJobs = false)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        foreach (var file in order.Files.Where(file => file.Id > 0))
        {
            if (!forceNewJobs && await HasOpenJobAsync(connection, (SqliteTransaction)transaction, order.Id, file.Id))
            {
                continue;
            }

            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO PrintJobs
                    (PrintOrderId, UploadedFileId, PrinterId, Status, Attempts, LastError, CreatedAt, UpdatedAt)
                VALUES
                    ($orderId, $fileId, NULL, $status, 0, NULL, $now, $now);
                """;
            command.Parameters.AddWithValue("$orderId", order.Id);
            command.Parameters.AddWithValue("$fileId", file.Id);
            command.Parameters.AddWithValue("$status", (int)PrintJobStatus.Pending);
            command.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<PrintJob?> TryClaimNextJobAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var select = connection.CreateCommand();
        select.Transaction = (SqliteTransaction)transaction;
        select.CommandText = """
            SELECT j.Id, j.PrintOrderId, j.UploadedFileId, f.OriginalName, j.PrinterId, p.Name,
                   j.Status, j.Attempts, j.LastError, j.CreatedAt, j.UpdatedAt
            FROM PrintJobs j
            LEFT JOIN UploadedFiles f ON f.Id = j.UploadedFileId
            LEFT JOIN Printers p ON p.Id = j.PrinterId
            WHERE j.Status = $pending
            ORDER BY j.CreatedAt
            LIMIT 1;
            """;
        select.Parameters.AddWithValue("$pending", (int)PrintJobStatus.Pending);

        PrintJob? job = null;
        await using (var reader = await select.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync()) job = ReadJob(reader);
        }

        if (job == null)
        {
            await transaction.CommitAsync();
            return null;
        }

        var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText = """
            UPDATE PrintJobs
            SET Status = $processing, Attempts = Attempts + 1, UpdatedAt = $now
            WHERE Id = $id AND Status = $pending;
            """;
        update.Parameters.AddWithValue("$processing", (int)PrintJobStatus.Processing);
        update.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        update.Parameters.AddWithValue("$id", job.Id);
        update.Parameters.AddWithValue("$pending", (int)PrintJobStatus.Pending);

        if (await update.ExecuteNonQueryAsync() != 1)
        {
            await transaction.RollbackAsync();
            return null;
        }

        await transaction.CommitAsync();
        job.Status = PrintJobStatus.Processing;
        job.Attempts++;
        job.UpdatedAt = DateTime.Now;
        return job;
    }

    public async Task CompleteJobAsync(long jobId, bool success, string? error = null)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PrintJobs
            SET Status = $status, LastError = $error, UpdatedAt = $now
            WHERE Id = $id AND Status = $processing;
            """;
        command.Parameters.AddWithValue("$status", (int)(success ? PrintJobStatus.Printed : PrintJobStatus.Failed));
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", jobId);
        command.Parameters.AddWithValue("$processing", (int)PrintJobStatus.Processing);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> HasOpenJobsAsync(string orderId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM PrintJobs
                WHERE PrintOrderId = $orderId AND Status IN ($pending, $processing)
            );
            """;
        command.Parameters.AddWithValue("$orderId", orderId);
        command.Parameters.AddWithValue("$pending", (int)PrintJobStatus.Pending);
        command.Parameters.AddWithValue("$processing", (int)PrintJobStatus.Processing);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    public async Task CancelOpenJobsAsync(string orderId, string reason, long? exceptJobId = null)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PrintJobs
            SET Status = $cancelled, LastError = $reason, UpdatedAt = $now
            WHERE PrintOrderId = $orderId
              AND Status IN ($pending, $processing)
              AND ($exceptJobId IS NULL OR Id <> $exceptJobId);
            """;
        command.Parameters.AddWithValue("$cancelled", (int)PrintJobStatus.Cancelled);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        command.Parameters.AddWithValue("$orderId", orderId);
        command.Parameters.AddWithValue("$pending", (int)PrintJobStatus.Pending);
        command.Parameters.AddWithValue("$processing", (int)PrintJobStatus.Processing);
        command.Parameters.AddWithValue("$exceptJobId", (object?)exceptJobId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> HasOpenJobAsync(SqliteConnection connection, SqliteTransaction transaction, string orderId, int fileId)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM PrintJobs
                WHERE PrintOrderId = $orderId AND UploadedFileId = $fileId
                  AND Status IN ($pending, $processing)
            );
            """;
        command.Parameters.AddWithValue("$orderId", orderId);
        command.Parameters.AddWithValue("$fileId", fileId);
        command.Parameters.AddWithValue("$pending", (int)PrintJobStatus.Pending);
        command.Parameters.AddWithValue("$processing", (int)PrintJobStatus.Processing);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static PrintJob ReadJob(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        PrintOrderId = reader.GetString(1),
        UploadedFileId = reader.GetInt32(2),
        FileName = reader.IsDBNull(3) ? "Arquivo removido" : reader.GetString(3),
        PrinterId = reader.IsDBNull(4) ? null : reader.GetString(4),
        PrinterName = reader.IsDBNull(5) ? null : reader.GetString(5),
        Status = (PrintJobStatus)reader.GetInt32(6),
        Attempts = reader.GetInt32(7),
        LastError = reader.IsDBNull(8) ? null : reader.GetString(8),
        CreatedAt = DateTime.Parse(reader.GetString(9)),
        UpdatedAt = DateTime.Parse(reader.GetString(10))
    };

    private void EnsureDatabase()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Printers (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                SystemName TEXT NOT NULL,
                IsActive INTEGER NOT NULL DEFAULT 1,
                IsPaused INTEGER NOT NULL DEFAULT 0,
                SupportsColor INTEGER NOT NULL DEFAULT 0,
                SupportsA3 INTEGER NOT NULL DEFAULT 0,
                SupportsDuplex INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PrintJobs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PrintOrderId TEXT NOT NULL,
                UploadedFileId INTEGER NOT NULL,
                PrinterId TEXT NULL,
                Status INTEGER NOT NULL,
                Attempts INTEGER NOT NULL DEFAULT 0,
                LastError TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                FOREIGN KEY (PrintOrderId) REFERENCES Orders(Id) ON DELETE CASCADE,
                FOREIGN KEY (UploadedFileId) REFERENCES UploadedFiles(Id) ON DELETE CASCADE,
                FOREIGN KEY (PrinterId) REFERENCES Printers(Id) ON DELETE SET NULL
            );

            CREATE INDEX IF NOT EXISTS IX_PrintJobs_Status_CreatedAt
                ON PrintJobs(Status, CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_PrintJobs_PrintOrderId
                ON PrintJobs(PrintOrderId);
            """;
        command.ExecuteNonQuery();
    }
}
