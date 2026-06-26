namespace PrintShop.Services;

public class FileStorageService
{
    private readonly AppStoragePathService _paths;

    public FileStorageService(AppStoragePathService paths)
    {
        _paths = paths;
    }

    public string GetPath(string storedName) => Path.Combine(_paths.UploadsPath, storedName);

    public async Task<string> SaveAsync(IFormFile file, string extension)
    {
        var storedName = $"{Guid.NewGuid():N}{extension}";
        var filePath = GetPath(storedName);

        await using (var stream = new FileStream(filePath, FileMode.CreateNew))
        {
            await file.CopyToAsync(stream);
        }

        return storedName;
    }

    public void Delete(string storedName)
    {
        var filePath = GetPath(storedName);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}
