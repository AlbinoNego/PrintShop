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
            SELECT p.Id, p.Name, p.SystemName, p.IsActive, p.IsPaused, p.SupportsColor, p.SupportsA3, p.SupportsDuplex, p.CreatedAt,
                   SUM(CASE WHEN j.Status = $pending THEN 1 ELSE 0 END) AS PendingJobs,
                   SUM(CASE WHEN j.Status = $processing THEN 1 ELSE 0 END) AS ProcessingJobs,
                   SUM(CASE WHEN j.Status = $failed THEN 1 ELSE 0 END) AS FailedJobs
            FROM Printers p
            LEFT JOIN PrintJobs j ON j.PrinterId = p.Id
            GROUP BY p.Id, p.Name, p.SystemName, p.IsActive, p.IsPaused, p.SupportsColor, p.SupportsA3, p.SupportsDuplex, p.CreatedAt
            ORDER BY p.Name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$pending", (int)PrintJobStatus.Pending);
        command.Parameters.AddWithValue("$processing", (int)PrintJobStatus.Processing);
        command.Parameters.AddWithValue("$failed", (int)PrintJobStatus.Failed);

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
                CreatedAt = DateTime.Parse(reader.GetString(8)),
                PendingJobs = reader.GetInt32(9),
                ProcessingJobs = reader.GetInt32(10),
                FailedJobs = reader.GetInt32(11)
            });
        }

        return printers;
    }

    public List<PrintJob> GetRecentJobs(PrintJobStatus? status = null, int limit = 50)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT j.Id, j.PrintOrderId, j.UploadedFileId, f.OriginalName, j.PrinterId, p.Name, p.SystemName,
                   j.Status, j.Attempts, j.LastError, j.CreatedAt, j.UpdatedAt
            FROM PrintJobs j
            LEFT JOIN UploadedFiles f ON f.Id = j.UploadedFileId
            LEFT JOIN Printers p ON p.Id = j.PrinterId
            WHERE ($status IS NULL OR j.Status = $status)
            ORDER BY j.CreatedAt DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        command.Parameters.AddWithValue("$status", status is null ? DBNull.Value : (int)status.Value);

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

    public PrintJobSummary GetJobSummary()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN Status = $pending THEN 1 ELSE 0 END),
                SUM(CASE WHEN Status = $processing THEN 1 ELSE 0 END),
                SUM(CASE WHEN Status = $printed THEN 1 ELSE 0 END),
                SUM(CASE WHEN Status = $failed THEN 1 ELSE 0 END),
                SUM(CASE WHEN Status = $cancelled THEN 1 ELSE 0 END)
            FROM PrintJobs;
            """;
        command.Parameters.AddWithValue("$pending", (int)PrintJobStatus.Pending);
        command.Parameters.AddWithValue("$processing", (int)PrintJobStatus.Processing);
        command.Parameters.AddWithValue("$printed", (int)PrintJobStatus.Printed);
        command.Parameters.AddWithValue("$failed", (int)PrintJobStatus.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)PrintJobStatus.Cancelled);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new PrintJobSummary();

        return new PrintJobSummary
        {
            Pending = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            Processing = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            Printed = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            Failed = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            Cancelled = reader.IsDBNull(4) ? 0 : reader.GetInt32(4)
        };
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
            SELECT j.Id, j.PrintOrderId, j.UploadedFileId, f.OriginalName, p.Id, p.Name, p.SystemName,
                   j.Status, j.Attempts, j.LastError, j.CreatedAt, j.UpdatedAt
            FROM PrintJobs j
            JOIN Orders o ON o.Id = j.PrintOrderId
            JOIN Printers p ON p.IsActive = 1 AND p.IsPaused = 0
                AND (o.Color <> $color OR p.SupportsColor = 1)
                AND (o.PaperType <> $a3 OR p.SupportsA3 = 1)
                AND (o.Sides <> $duplex OR p.SupportsDuplex = 1)
            LEFT JOIN UploadedFiles f ON f.Id = j.UploadedFileId
            WHERE j.Status = $pending
            ORDER BY j.CreatedAt,
                (SELECT COUNT(1) FROM PrintJobs load
                 WHERE load.PrinterId = p.Id AND load.Status IN ($pending, $processing)),
                p.Name COLLATE NOCASE
            LIMIT 1;
            """;
        select.Parameters.AddWithValue("$pending", (int)PrintJobStatus.Pending);
        select.Parameters.AddWithValue("$processing", (int)PrintJobStatus.Processing);
        select.Parameters.AddWithValue("$color", (int)PrintColor.Color);
        select.Parameters.AddWithValue("$a3", (int)PaperType.A3_75g);
        select.Parameters.AddWithValue("$duplex", (int)PrintSides.BothSides);

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
            SET PrinterId = $printerId, Status = $processing, Attempts = Attempts + 1, UpdatedAt = $now
            WHERE Id = $id AND Status = $pending;
            """;
        update.Parameters.AddWithValue("$processing", (int)PrintJobStatus.Processing);
        update.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        update.Parameters.AddWithValue("$id", job.Id);
        update.Parameters.AddWithValue("$printerId", job.PrinterId!);
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

    public async Task<string?> RetryJobAsync(long jobId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var read = connection.CreateCommand();
        read.Transaction = (SqliteTransaction)transaction;
        read.CommandText = "SELECT PrintOrderId FROM PrintJobs WHERE Id = $id AND Status IN ($failed, $cancelled);";
        read.Parameters.AddWithValue("$id", jobId);
        read.Parameters.AddWithValue("$failed", (int)PrintJobStatus.Failed);
        read.Parameters.AddWithValue("$cancelled", (int)PrintJobStatus.Cancelled);
        var orderId = (string?)await read.ExecuteScalarAsync();
        if (orderId == null)
        {
            await transaction.CommitAsync();
            return null;
        }

        var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText = """
            UPDATE PrintJobs
            SET PrinterId = NULL, Status = $pending, LastError = NULL, UpdatedAt = $now
            WHERE PrintOrderId = $orderId AND Status IN ($failed, $cancelled);
            """;
        update.Parameters.AddWithValue("$pending", (int)PrintJobStatus.Pending);
        update.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        update.Parameters.AddWithValue("$orderId", orderId);
        update.Parameters.AddWithValue("$failed", (int)PrintJobStatus.Failed);
        update.Parameters.AddWithValue("$cancelled", (int)PrintJobStatus.Cancelled);
        await update.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return orderId;
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
        PrinterSystemName = reader.IsDBNull(6) ? null : reader.GetString(6),
        Status = (PrintJobStatus)reader.GetInt32(7),
        Attempts = reader.GetInt32(8),
        LastError = reader.IsDBNull(9) ? null : reader.GetString(9),
        CreatedAt = DateTime.Parse(reader.GetString(10)),
        UpdatedAt = DateTime.Parse(reader.GetString(11))
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
