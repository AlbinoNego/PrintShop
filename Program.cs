using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using PrintShop.Services;

var builder = WebApplication.CreateBuilder(args);

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

    options.AddFixedWindowLimiter("uploads", limiter =>
    {
        limiter.PermitLimit = 20;
        limiter.Window = TimeSpan.FromMinutes(10);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });

    options.AddFixedWindowLimiter("pix-webhook", limiter =>
    {
        limiter.PermitLimit = 60;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

builder.Services.AddSingleton(storagePaths);
builder.Services.AddSingleton<AdminSettingsService>();
builder.Services.AddSingleton<OrderQueueService>();
builder.Services.AddSingleton<PrinterService>();
builder.Services.AddSingleton<PricingService>();
builder.Services.AddSingleton<PixService>();
builder.Services.AddSingleton<PageCountService>();
builder.Services.AddSingleton<FileStorageService>();
builder.Services.AddSingleton<AdminAuthService>();

var app = builder.Build();

app.Services.GetRequiredService<OrderQueueService>();
app.Services.GetRequiredService<FileStorageService>();

// Forçar o wwwroot correto
app.Environment.WebRootPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot");

app.UseForwardedHeaders();

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
