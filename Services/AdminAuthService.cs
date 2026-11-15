namespace PrintShop.Services;

public class AdminAuthService
{
    private readonly AdminUserService _users;

    public const string SessionKey = "AdminLoggedIn";
    public const string UsernameSessionKey = "AdminUsername";

    public AdminAuthService(AdminUserService users)
    {
        _users = users;
    }

    public PrintShop.Models.AdminUser? Validate(string username, string password) => _users.Authenticate(username, password);

    public static bool IsLoggedIn(HttpContext context) =>
        context.Session.GetString(SessionKey) == "true";

    public static void SignIn(HttpContext context, string username)
    {
        context.Session.Clear();
        context.Session.SetString(SessionKey, "true");
        context.Session.SetString(UsernameSessionKey, username);
    }

    public static string? GetUsername(HttpContext context) =>
        context.Session.GetString(UsernameSessionKey);
}
