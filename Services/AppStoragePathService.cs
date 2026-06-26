namespace PrintShop.Services;

public class AppStoragePathService
{
    public string RootPath { get; }
    public string DataPath { get; }
    public string UploadsPath { get; }
    public string KeysPath { get; }

    public AppStoragePathService(IWebHostEnvironment env)
    {
        RootPath = FindProjectRoot(env.ContentRootPath);
        DataPath = Path.Combine(RootPath, "App_Data");
        UploadsPath = Path.Combine(DataPath, "uploads");
        KeysPath = Path.Combine(DataPath, "keys");

        Directory.CreateDirectory(DataPath);
        Directory.CreateDirectory(UploadsPath);
        Directory.CreateDirectory(KeysPath);
    }

    private static string FindProjectRoot(string startPath)
    {
        var directory = new DirectoryInfo(startPath);

        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PrintShop.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return startPath;
    }
}
