using Microsoft.Data.Sqlite;
using PrintShop.Models;
using System.Globalization;

namespace PrintShop.Services;

public class AdminSettingsService
{
    private readonly string _connectionString;

    public AdminSettingsService(AppStoragePathService paths)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(paths.DataPath, "printshop.db")
        }.ToString();

        EnsureDatabase();
    }

    public AdminSettings Get()
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM AdminSettings;";

        var values = new Dictionary<string, string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        var defaults = new AdminSettings();
        return new AdminSettings
        {
            BlackAndWhitePrice = GetDecimal(values, nameof(AdminSettings.BlackAndWhitePrice), defaults.BlackAndWhitePrice),
            ColorPrice = GetDecimal(values, nameof(AdminSettings.ColorPrice), defaults.ColorPrice),
            A4_90gExtra = GetDecimal(values, nameof(AdminSettings.A4_90gExtra), defaults.A4_90gExtra),
            A3_75gExtra = GetDecimal(values, nameof(AdminSettings.A3_75gExtra), defaults.A3_75gExtra),
            GlossyExtra = GetDecimal(values, nameof(AdminSettings.GlossyExtra), defaults.GlossyExtra),
            LaminatePrice = GetDecimal(values, nameof(AdminSettings.LaminatePrice), defaults.LaminatePrice),
            DeliveryFee = GetDecimal(values, nameof(AdminSettings.DeliveryFee), defaults.DeliveryFee),
            DefaultPrinter = GetString(values, nameof(AdminSettings.DefaultPrinter), defaults.DefaultPrinter),
            PdfPrinter = GetString(values, nameof(AdminSettings.PdfPrinter), defaults.PdfPrinter),
            WordPrinter = GetString(values, nameof(AdminSettings.WordPrinter), defaults.WordPrinter),
            PowerPointPrinter = GetString(values, nameof(AdminSettings.PowerPointPrinter), defaults.PowerPointPrinter),
            ImagePrinter = GetString(values, nameof(AdminSettings.ImagePrinter), defaults.ImagePrinter),
            AutomaticPrintingEnabled = GetBool(values, nameof(AdminSettings.AutomaticPrintingEnabled), defaults.AutomaticPrintingEnabled)
        };
    }

    public void Save(AdminSettings settings)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        Set(connection, transaction, nameof(AdminSettings.BlackAndWhitePrice), settings.BlackAndWhitePrice);
        Set(connection, transaction, nameof(AdminSettings.ColorPrice), settings.ColorPrice);
        Set(connection, transaction, nameof(AdminSettings.A4_90gExtra), settings.A4_90gExtra);
        Set(connection, transaction, nameof(AdminSettings.A3_75gExtra), settings.A3_75gExtra);
        Set(connection, transaction, nameof(AdminSettings.GlossyExtra), settings.GlossyExtra);
        Set(connection, transaction, nameof(AdminSettings.LaminatePrice), settings.LaminatePrice);
        Set(connection, transaction, nameof(AdminSettings.DeliveryFee), settings.DeliveryFee);
        Set(connection, transaction, nameof(AdminSettings.DefaultPrinter), settings.DefaultPrinter);
        Set(connection, transaction, nameof(AdminSettings.PdfPrinter), settings.PdfPrinter);
        Set(connection, transaction, nameof(AdminSettings.WordPrinter), settings.WordPrinter);
        Set(connection, transaction, nameof(AdminSettings.PowerPointPrinter), settings.PowerPointPrinter);
        Set(connection, transaction, nameof(AdminSettings.ImagePrinter), settings.ImagePrinter);
        Set(connection, transaction, nameof(AdminSettings.AutomaticPrintingEnabled), settings.AutomaticPrintingEnabled);

        transaction.Commit();
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private void EnsureDatabase()
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AdminSettings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void Set(SqliteConnection connection, SqliteTransaction transaction, string key, decimal value) =>
        Set(connection, transaction, key, value.ToString(CultureInfo.InvariantCulture));

    private static void Set(SqliteConnection connection, SqliteTransaction transaction, string key, bool value) =>
        Set(connection, transaction, key, value ? "true" : "false");

    private static void Set(SqliteConnection connection, SqliteTransaction transaction, string key, string? value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AdminSettings (Key, Value)
            VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value ?? "");
        command.ExecuteNonQuery();
    }

    private static decimal GetDecimal(Dictionary<string, string> values, string key, decimal fallback) =>
        values.TryGetValue(key, out var value) && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
            ? result
            : fallback;

    private static string GetString(Dictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) ? value : fallback;

    private static bool GetBool(Dictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var value) && bool.TryParse(value, out var result) ? result : fallback;
}
