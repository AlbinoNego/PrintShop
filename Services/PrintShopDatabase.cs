using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace PrintShop.Services;

// Ponto unico de configuracao do SQLite, compartilhado pelo site e pelo PrintShop.Agent.
public static class PrintShopDatabase
{
    private static readonly ConcurrentDictionary<string, bool> Initialized = new();

    public static string ConnectionString(AppStoragePathService paths, int timeoutSeconds)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(paths.DataPath, "printshop.db"),
            DefaultTimeout = timeoutSeconds,
            // Sem isso o SQLite ignora as FOREIGN KEY e os ON DELETE CASCADE das tabelas.
            ForeignKeys = true
        }.ToString();

        Initialized.GetOrAdd(connectionString, EnableWriteAheadLog);
        return connectionString;
    }

    // WAL deixa o site ler enquanto o agente grava (e vice-versa), reduzindo "database is locked".
    // A opcao fica gravada no arquivo do banco, entao basta aplicar uma vez por processo.
    private static bool EnableWriteAheadLog(string connectionString)
    {
        SQLitePCL.Batteries_V2.Init();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        command.ExecuteNonQuery();
        return true;
    }
}
