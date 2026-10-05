using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PrintShop.Models;

namespace PrintShop.Services;

public class AdminUserService
{
    private const int HashIterations = 210_000;
    private readonly string _connectionString;
    private readonly IConfiguration _configuration;

    public AdminUserService(AppStoragePathService paths, IConfiguration configuration)
    {
        _configuration = configuration;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(paths.DataPath, "printshop.db"),
            DefaultTimeout = 15
        }.ToString();

        EnsureDatabase();
        CreateBootstrapUser();
    }

    public AdminUser? Authenticate(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return null;

        AdminUser? user;
        using (var connection = CreateConnection())
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, Username, PasswordHash, CreatedAt, LastLoginAt
                FROM AdminUsers WHERE Username = $username COLLATE NOCASE LIMIT 1;
                """;
            command.Parameters.AddWithValue("$username", username.Trim());
            using var reader = command.ExecuteReader();
            user = reader.Read() ? ReadUser(reader) : null;
        }

        if (user == null) return null;
        if (!VerifyPassword(password, user.PasswordHash)) return null;

        TryRecordLogin(user.Id);
        user.LastLoginAt = DateTime.Now;
        return user;
    }

    public AdminUser? GetById(string id)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Username, PasswordHash, CreatedAt, LastLoginAt
            FROM AdminUsers WHERE Id = $id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    public bool UpdateCredentials(string userId, string currentPassword, string username, string newPassword, out string error)
    {
        error = "";
        username = username.Trim();
        if (username.Length < 3 || username.Length > 50)
        {
            error = "O usuário deve ter entre 3 e 50 caracteres.";
            return false;
        }

        if (newPassword.Length < 10)
        {
            error = "A nova senha deve ter ao menos 10 caracteres.";
            return false;
        }

        var user = GetById(userId);
        if (user == null || !VerifyPassword(currentPassword, user.PasswordHash))
        {
            error = "A senha atual não confere.";
            return false;
        }

        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AdminUsers
            SET Username = $username, PasswordHash = $passwordHash
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$passwordHash", HashPassword(newPassword));
        command.Parameters.AddWithValue("$id", userId);

        try
        {
            return command.ExecuteNonQuery() == 1;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            error = "Este nome de usuário já está em uso.";
            return false;
        }
    }

    private void CreateBootstrapUser()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(1) FROM AdminUsers;";
        if (Convert.ToInt32(count.ExecuteScalar()) > 0) return;

        var username = _configuration["PrintShop:Admin:Username"];
        var password = _configuration["PrintShop:Admin:Password"];
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return;

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO AdminUsers (Id, Username, PasswordHash, CreatedAt, LastLoginAt)
            VALUES ($id, $username, $passwordHash, $createdAt, NULL);
            """;
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        insert.Parameters.AddWithValue("$username", username.Trim());
        insert.Parameters.AddWithValue("$passwordHash", HashPassword(password));
        insert.Parameters.AddWithValue("$createdAt", DateTime.Now.ToString("O"));
        insert.ExecuteNonQuery();
    }

    private void TryRecordLogin(string userId)
    {
        try
        {
            using var connection = CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE AdminUsers SET LastLoginAt = $now WHERE Id = $id;";
            command.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
            command.Parameters.AddWithValue("$id", userId);
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            // Last-login is an audit detail; a temporary SQLite lock must not deny valid access.
        }
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, HashIterations, HashAlgorithmName.SHA256, 32);
        return $"PBKDF2${HashIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string storedValue)
    {
        var parts = storedValue.Split('$');
        if (parts.Length != 4 || parts[0] != "PBKDF2" || !int.TryParse(parts[1], out var iterations)) return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static AdminUser ReadUser(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Username = reader.GetString(1),
        PasswordHash = reader.GetString(2),
        CreatedAt = DateTime.Parse(reader.GetString(3)),
        LastLoginAt = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4))
    };

    private SqliteConnection CreateConnection() => new(_connectionString);

    private void EnsureDatabase()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AdminUsers (
                Id TEXT PRIMARY KEY,
                Username TEXT NOT NULL COLLATE NOCASE UNIQUE,
                PasswordHash TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                LastLoginAt TEXT NULL
            );
            """;
        command.ExecuteNonQuery();
    }
}
