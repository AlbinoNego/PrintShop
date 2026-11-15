using Microsoft.Data.Sqlite;

namespace PrintShop.Services;

public class AdminAuditEntry
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string Username { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Details { get; set; } = "";
    public string IpAddress { get; set; } = "";
}

// Registro de quem fez o que no painel: autorizacoes, cancelamentos, precos, impressoras, logins.
public class AdminAuditService
{
    private readonly string _connectionString;
    private readonly ILogger<AdminAuditService>? _logger;

    public AdminAuditService(AppStoragePathService paths, ILogger<AdminAuditService>? logger = null)
    {
        _connectionString = PrintShopDatabase.ConnectionString(paths, 15);
        _logger = logger;
        EnsureDatabase();
    }

    public void Log(HttpContext context, string action, string target = "", string details = "")
    {
        Log(
            AdminAuthService.GetUsername(context) ?? "",
            action,
            target,
            details,
            context.Connection.RemoteIpAddress?.ToString() ?? "");
    }

    public void Log(string username, string action, string target, string details, string ipAddress)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO AdminAuditLog (At, Username, Action, Target, Details, IpAddress)
                VALUES ($at, $username, $action, $target, $details, $ip);
                """;
            command.Parameters.AddWithValue("$at", DateTime.Now.ToString("O"));
            command.Parameters.AddWithValue("$username", Truncate(username, 100));
            command.Parameters.AddWithValue("$action", Truncate(action, 100));
            command.Parameters.AddWithValue("$target", Truncate(target, 200));
            command.Parameters.AddWithValue("$details", Truncate(details, 1000));
            command.Parameters.AddWithValue("$ip", Truncate(ipAddress, 64));
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            // A auditoria nao pode impedir a acao do atendente; o erro fica no log do servidor.
            _logger?.LogError(ex, "Nao foi possivel registrar a auditoria de {Action} em {Target}.", action, target);
        }
    }

    public List<AdminAuditEntry> GetRecent(int limit = 200, string? search = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, At, Username, Action, Target, Details, IpAddress
            FROM AdminAuditLog
            WHERE $search IS NULL
               OR Username LIKE $search OR Action LIKE $search OR Target LIKE $search OR Details LIKE $search
            ORDER BY Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$search",
            string.IsNullOrWhiteSpace(search) ? DBNull.Value : $"%{search.Trim()}%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

        var entries = new List<AdminAuditEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new AdminAuditEntry
            {
                Id = reader.GetInt64(0),
                At = DateTime.Parse(reader.GetString(1)),
                Username = reader.GetString(2),
                Action = reader.GetString(3),
                Target = reader.GetString(4),
                Details = reader.GetString(5),
                IpAddress = reader.GetString(6)
            });
        }

        return entries;
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];

    private void EnsureDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AdminAuditLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                At TEXT NOT NULL,
                Username TEXT NOT NULL,
                Action TEXT NOT NULL,
                Target TEXT NOT NULL,
                Details TEXT NOT NULL,
                IpAddress TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_AdminAuditLog_At ON AdminAuditLog(At);
            """;
        command.ExecuteNonQuery();
    }
}
