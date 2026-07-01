using Microsoft.Data.Sqlite;
using PrintShop.Models;

namespace PrintShop.Services;

public class OrderQueueService
{
    private readonly string _connectionString;

    public OrderQueueService(AppStoragePathService paths)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(paths.DataPath, "printshop.db")
        }.ToString();

        SQLitePCL.Batteries_V2.Init();
        EnsureDatabase();
    }

    public async Task AddAsync(PrintOrder order)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO Orders
                (Id, CreatedAt, Status, Color, Copies, PaperType, Laminate, Sides,
                 PaymentMethod, TotalPrice, PixCode, PaymentConfirmed, CustomerName, CustomerPhone,
                 FulfillmentMethod, DeliveryAddress, DeliveryNumber, DeliveryNeighborhood, DeliveryComplement, DeliveryFee,
                 Orientation)
            VALUES
                ($id, $createdAt, $status, $color, $copies, $paperType, $laminate, $sides,
                 $paymentMethod, $totalPrice, $pixCode, $paymentConfirmed, $customerName, $customerPhone,
                 $fulfillmentMethod, $deliveryAddress, $deliveryNumber, $deliveryNeighborhood, $deliveryComplement, $deliveryFee,
                 $orientation);
            """;
        AddOrderParameters(command, order);
        await command.ExecuteNonQueryAsync();

        foreach (var file in order.Files)
        {
            var fileCommand = connection.CreateCommand();
            fileCommand.Transaction = (SqliteTransaction)transaction;
            fileCommand.CommandText = """
                INSERT INTO UploadedFiles
                    (PrintOrderId, OriginalName, StoredName, ContentType, SizeBytes, PageCount, Copies)
                VALUES
                    ($printOrderId, $originalName, $storedName, $contentType, $sizeBytes, $pageCount, $copies);
                """;
            AddFileParameters(fileCommand, order.Id, file);
            await fileCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<PrintOrder?> GetAsync(string id)
    {
        var orders = await QueryOrdersAsync("WHERE o.Id = $id", command =>
        {
            command.Parameters.AddWithValue("$id", id);
        });

        return orders.FirstOrDefault();
    }

    public async Task UpdateAsync(PrintOrder order)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE Orders SET
                Status = $status,
                Color = $color,
                Copies = $copies,
                PaperType = $paperType,
                Laminate = $laminate,
                Sides = $sides,
                PaymentMethod = $paymentMethod,
                TotalPrice = $totalPrice,
                PixCode = $pixCode,
                PaymentConfirmed = $paymentConfirmed,
                CustomerName = $customerName,
                CustomerPhone = $customerPhone,
                FulfillmentMethod = $fulfillmentMethod,
                DeliveryAddress = $deliveryAddress,
                DeliveryNumber = $deliveryNumber,
                DeliveryNeighborhood = $deliveryNeighborhood,
                DeliveryComplement = $deliveryComplement,
                DeliveryFee = $deliveryFee,
                Orientation = $orientation
            WHERE Id = $id;
            """;
        AddOrderParameters(command, order);
        await command.ExecuteNonQueryAsync();

        var retainedFileIds = order.Files
            .Where(file => file.Id > 0)
            .Select(file => file.Id)
            .ToList();

        var deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = (SqliteTransaction)transaction;
        deleteCommand.Parameters.AddWithValue("$orderId", order.Id);
        if (retainedFileIds.Count == 0)
        {
            deleteCommand.CommandText = "DELETE FROM UploadedFiles WHERE PrintOrderId = $orderId;";
        }
        else
        {
            var parameterNames = retainedFileIds.Select((_, index) => $"$fileId{index}").ToList();
            deleteCommand.CommandText =
                $"DELETE FROM UploadedFiles WHERE PrintOrderId = $orderId AND Id NOT IN ({string.Join(", ", parameterNames)});";

            for (var index = 0; index < retainedFileIds.Count; index++)
            {
                deleteCommand.Parameters.AddWithValue(parameterNames[index], retainedFileIds[index]);
            }
        }
        await deleteCommand.ExecuteNonQueryAsync();

        foreach (var file in order.Files.Where(file => file.Id > 0))
        {
            var updateFileCommand = connection.CreateCommand();
            updateFileCommand.Transaction = (SqliteTransaction)transaction;
            updateFileCommand.CommandText = """
                UPDATE UploadedFiles SET
                    Copies = $copies
                WHERE Id = $fileId AND PrintOrderId = $printOrderId;
                """;
            updateFileCommand.Parameters.AddWithValue("$copies", Math.Max(1, Math.Min(file.Copies, 100)));
            updateFileCommand.Parameters.AddWithValue("$fileId", file.Id);
            updateFileCommand.Parameters.AddWithValue("$printOrderId", order.Id);
            await updateFileCommand.ExecuteNonQueryAsync();
        }

        foreach (var file in order.Files.Where(file => file.Id == 0))
        {
            var fileCommand = connection.CreateCommand();
            fileCommand.Transaction = (SqliteTransaction)transaction;
            fileCommand.CommandText = """
                INSERT INTO UploadedFiles
                    (PrintOrderId, OriginalName, StoredName, ContentType, SizeBytes, PageCount, Copies)
                VALUES
                    ($printOrderId, $originalName, $storedName, $contentType, $sizeBytes, $pageCount, $copies);
                """;
            AddFileParameters(fileCommand, order.Id, file);
            await fileCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public Task<List<PrintOrder>> GetAllAsync() =>
        QueryOrdersAsync("", command => { });

    public Task<List<PrintOrder>> GetPendingAsync() =>
        QueryOrdersAsync("WHERE o.Status IN ($paid, $printing)", command =>
        {
            command.Parameters.AddWithValue("$paid", (int)OrderStatus.PaymentConfirmed);
            command.Parameters.AddWithValue("$printing", (int)OrderStatus.Printing);
        });

    private SqliteConnection CreateConnection() => new(_connectionString);

    private void EnsureDatabase()
    {
        using var connection = CreateConnection();
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Orders (
                Id TEXT PRIMARY KEY,
                CreatedAt TEXT NOT NULL,
                Status INTEGER NOT NULL,
                Color INTEGER NOT NULL,
                Copies INTEGER NOT NULL,
                PaperType INTEGER NOT NULL,
                Laminate INTEGER NOT NULL,
                Sides INTEGER NOT NULL,
                PaymentMethod INTEGER NOT NULL,
                TotalPrice REAL NOT NULL,
                PixCode TEXT NULL,
                PaymentConfirmed INTEGER NOT NULL,
                CustomerName TEXT NOT NULL,
                CustomerPhone TEXT NOT NULL,
                FulfillmentMethod INTEGER NOT NULL DEFAULT 0,
                DeliveryAddress TEXT NOT NULL DEFAULT '',
                DeliveryNumber TEXT NOT NULL DEFAULT '',
                DeliveryNeighborhood TEXT NOT NULL DEFAULT '',
                DeliveryComplement TEXT NOT NULL DEFAULT '',
                DeliveryFee REAL NOT NULL DEFAULT 0,
                Orientation INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS UploadedFiles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PrintOrderId TEXT NOT NULL,
                OriginalName TEXT NOT NULL,
                StoredName TEXT NOT NULL,
                ContentType TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                PageCount INTEGER NOT NULL,
                Copies INTEGER NOT NULL DEFAULT 1,
                FOREIGN KEY (PrintOrderId) REFERENCES Orders(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_UploadedFiles_PrintOrderId
                ON UploadedFiles(PrintOrderId);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "Orders", "FulfillmentMethod", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Orders", "DeliveryAddress", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Orders", "DeliveryNumber", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Orders", "DeliveryNeighborhood", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Orders", "DeliveryComplement", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Orders", "DeliveryFee", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Orders", "Orientation", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "UploadedFiles", "Copies", "INTEGER NOT NULL DEFAULT 1");
    }

    private async Task<List<PrintOrder>> QueryOrdersAsync(
        string whereClause,
        Action<SqliteCommand> configure)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                o.Id, o.CreatedAt, o.Status, o.Color, o.Copies, o.PaperType, o.Laminate, o.Sides,
                o.PaymentMethod, o.TotalPrice, o.PixCode, o.PaymentConfirmed, o.CustomerName, o.CustomerPhone,
                o.FulfillmentMethod, o.DeliveryAddress, o.DeliveryNumber, o.DeliveryNeighborhood, o.DeliveryComplement, o.DeliveryFee,
                o.Orientation,
                f.Id, f.PrintOrderId, f.OriginalName, f.StoredName, f.ContentType, f.SizeBytes, f.PageCount, f.Copies
            FROM Orders o
            LEFT JOIN UploadedFiles f ON f.PrintOrderId = o.Id
            {whereClause}
            ORDER BY o.CreatedAt DESC;
            """;
        configure(command);

        var byId = new Dictionary<string, PrintOrder>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var orderId = reader.GetString(0);
            if (!byId.TryGetValue(orderId, out var order))
            {
                order = new PrintOrder
                {
                    Id = orderId,
                    CreatedAt = DateTime.Parse(reader.GetString(1)),
                    Status = (OrderStatus)reader.GetInt32(2),
                    Color = (PrintColor)reader.GetInt32(3),
                    Copies = reader.GetInt32(4),
                    PaperType = (PaperType)reader.GetInt32(5),
                    Laminate = reader.GetInt32(6) == 1,
                    Sides = (PrintSides)reader.GetInt32(7),
                    PaymentMethod = (PaymentMethod)reader.GetInt32(8),
                    TotalPrice = Convert.ToDecimal(reader.GetDouble(9)),
                    PixCode = reader.IsDBNull(10) ? null : reader.GetString(10),
                    PaymentConfirmed = reader.GetInt32(11) == 1,
                    CustomerName = reader.GetString(12),
                    CustomerPhone = reader.GetString(13),
                    FulfillmentMethod = (FulfillmentMethod)reader.GetInt32(14),
                    DeliveryAddress = reader.GetString(15),
                    DeliveryNumber = reader.GetString(16),
                    DeliveryNeighborhood = reader.GetString(17),
                    DeliveryComplement = reader.GetString(18),
                    DeliveryFee = Convert.ToDecimal(reader.GetDouble(19)),
                    Orientation = (PrintOrientation)reader.GetInt32(20)
                };
                byId[orderId] = order;
            }

            if (!reader.IsDBNull(21))
            {
                order.Files.Add(new UploadedFile
                {
                    Id = reader.GetInt32(21),
                    PrintOrderId = reader.GetString(22),
                    OriginalName = reader.GetString(23),
                    StoredName = reader.GetString(24),
                    ContentType = reader.GetString(25),
                    SizeBytes = reader.GetInt64(26),
                    PageCount = reader.GetInt32(27),
                    Copies = reader.GetInt32(28)
                });
            }
        }

        return byId.Values.ToList();
    }

    private static void AddOrderParameters(SqliteCommand command, PrintOrder order)
    {
        command.Parameters.AddWithValue("$id", order.Id);
        command.Parameters.AddWithValue("$createdAt", order.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$status", (int)order.Status);
        command.Parameters.AddWithValue("$color", (int)order.Color);
        command.Parameters.AddWithValue("$copies", order.Copies);
        command.Parameters.AddWithValue("$paperType", (int)order.PaperType);
        command.Parameters.AddWithValue("$laminate", order.Laminate ? 1 : 0);
        command.Parameters.AddWithValue("$sides", (int)order.Sides);
        command.Parameters.AddWithValue("$paymentMethod", (int)order.PaymentMethod);
        command.Parameters.AddWithValue("$totalPrice", Convert.ToDouble(order.TotalPrice));
        command.Parameters.AddWithValue("$pixCode", (object?)order.PixCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$paymentConfirmed", order.PaymentConfirmed ? 1 : 0);
        command.Parameters.AddWithValue("$customerName", order.CustomerName ?? "");
        command.Parameters.AddWithValue("$customerPhone", order.CustomerPhone ?? "");
        command.Parameters.AddWithValue("$fulfillmentMethod", (int)order.FulfillmentMethod);
        command.Parameters.AddWithValue("$deliveryAddress", order.DeliveryAddress ?? "");
        command.Parameters.AddWithValue("$deliveryNumber", order.DeliveryNumber ?? "");
        command.Parameters.AddWithValue("$deliveryNeighborhood", order.DeliveryNeighborhood ?? "");
        command.Parameters.AddWithValue("$deliveryComplement", order.DeliveryComplement ?? "");
        command.Parameters.AddWithValue("$deliveryFee", Convert.ToDouble(order.DeliveryFee));
        command.Parameters.AddWithValue("$orientation", (int)order.Orientation);
    }

    private static void AddFileParameters(SqliteCommand command, string orderId, UploadedFile file)
    {
        command.Parameters.AddWithValue("$printOrderId", orderId);
        command.Parameters.AddWithValue("$originalName", file.OriginalName);
        command.Parameters.AddWithValue("$storedName", file.StoredName);
        command.Parameters.AddWithValue("$contentType", file.ContentType);
        command.Parameters.AddWithValue("$sizeBytes", file.SizeBytes);
        command.Parameters.AddWithValue("$pageCount", file.PageCount);
        command.Parameters.AddWithValue("$copies", Math.Max(1, Math.Min(file.Copies, 100)));
    }

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        alter.ExecuteNonQuery();
    }
}
