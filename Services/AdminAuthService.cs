namespace PrintShop.Services;

public class AdminAuthService
{
    private readonly IConfiguration _configuration;

    public const string SessionKey = "AdminLoggedIn";

    public AdminAuthService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool Validate(string username, string password)
    {
        var configuredUser = _configuration["PrintShop:Admin:Username"] ?? "admin";
        var configuredPassword = _configuration["PrintShop:Admin:Password"] ?? "admin123";

        return string.Equals(username, configuredUser, StringComparison.Ordinal) &&
               string.Equals(password, configuredPassword, StringComparison.Ordinal);
    }

    public static bool IsLoggedIn(HttpContext context) =>
        context.Session.GetString(SessionKey) == "true";
}
