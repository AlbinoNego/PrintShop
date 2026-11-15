using Microsoft.Data.Sqlite;
using PrintShop.Models;
using PrintShop.Services;

namespace PrintShop.Tests;

// Pasta App_Data temporaria e isolada por teste, apagada no fim.
public sealed class TestStorage : IDisposable
{
    public TestStorage()
    {
        Root = Path.Combine(Path.GetTempPath(), "printshop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new AppStoragePathService(Root);
    }

    public string Root { get; }
    public AppStoragePathService Paths { get; }

    public string WriteUpload(string storedName, string content = "x")
    {
        var path = Path.Combine(Paths.UploadsPath, storedName);
        File.WriteAllText(path, content);
        return path;
    }

    public static PrintOrder Order(params (int Pages, int Copies)[] files)
    {
        var order = new PrintOrder { Status = OrderStatus.Draft, CustomerName = "Teste", CustomerPhone = "000" };
        foreach (var (pages, copies) in files)
        {
            order.Files.Add(new UploadedFile
            {
                OriginalName = $"arquivo{order.Files.Count + 1}.pdf",
                StoredName = $"{Guid.NewGuid():N}.pdf",
                ContentType = "application/pdf",
                PageCount = pages,
                Copies = copies
            });
        }
        return order;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
