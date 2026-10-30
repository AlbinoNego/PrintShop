using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using PrintShop.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.Services.AddControllersWithViews()
    .AddNewtonsoftJson();

var storagePaths = new AppStoragePathService(builder.Environment);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(storagePaths.KeysPath));

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
    options.Preload = false;
});

builder.Services.AddHttpsRedirection(options =>
{
    options.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Todas as politicas sao por IP: uma janela unica para o site inteiro faria o 21o cliente
    // receber 429 e deixaria qualquer pessoa bloquear o login do admin.
    options.AddPolicy("uploads", context => PerIpFixedWindow(context, 20, TimeSpan.FromMinutes(10)));
    options.AddPolicy("pix-webhook", context => PerIpFixedWindow(context, 60, TimeSpan.FromMinutes(1)));
    options.AddPolicy("admin-login", context => PerIpFixedWindow(context, 10, TimeSpan.FromMinutes(15)));
    // O codigo do pedido funciona como senha de acesso; limita tentativas de adivinhacao.
    options.AddPolicy("order-lookup", context => PerIpFixedWindow(context, 15, TimeSpan.FromMinutes(10)));

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        PerIpFixedWindow(context, 120, TimeSpan.FromMinutes(1)));
});

static RateLimitPartition<string> PerIpFixedWindow(HttpContext context, int permitLimit, TimeSpan window) =>
    RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true
        });

builder.Services.AddSingleton(storagePaths);
builder.Services.AddSingleton<AdminSettingsService>();
builder.Services.AddSingleton<OrderQueueService>();
builder.Services.AddSingleton<PrinterRegistryService>();
builder.Services.AddSingleton<PrinterService>();
builder.Services.AddSingleton<PricingService>();
builder.Services.AddHttpClient<MercadoPagoPixService>();
builder.Services.AddSingleton<PageCountService>();
builder.Services.AddSingleton<FileStorageService>();
builder.Services.AddSingleton<AdminUserService>();
builder.Services.AddSingleton<AdminAuthService>();

var app = builder.Build();

app.Services.GetRequiredService<OrderQueueService>();
app.Services.GetRequiredService<PrinterRegistryService>();
app.Services.GetRequiredService<FileStorageService>();
app.Services.GetRequiredService<AdminUserService>();

// Forçar o wwwroot correto
app.Environment.WebRootPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot");

app.UseForwardedHeaders();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    headers["Content-Security-Policy"] = "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        Path.Combine(app.Environment.ContentRootPath, "wwwroot")),
    RequestPath = ""
});
app.UseRouting();
app.UseRateLimiter();
app.UseSession();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
