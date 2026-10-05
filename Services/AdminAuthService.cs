namespace PrintShop.Services;

public class AdminAuthService
{
    private readonly AdminUserService _users;

    public const string SessionKey = "AdminLoggedIn";

    public AdminAuthService(AdminUserService users)
    {
        _users = users;
    }

    public PrintShop.Models.AdminUser? Validate(string username, string password) => _users.Authenticate(username, password);

    public static bool IsLoggedIn(HttpContext context) =>
        context.Session.GetString(SessionKey) == "true";
}
