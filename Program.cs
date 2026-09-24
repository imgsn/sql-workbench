using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Workbench.Data;
using Workbench.Models;
using Workbench.Services;

var builder = WebApplication.CreateBuilder(args);
// Cookie keys survive restarts; session contents and database credentials remain memory-only.
var keyPath = builder.Configuration["DataProtection:KeysPath"];
if (string.IsNullOrWhiteSpace(keyPath))
    keyPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtectionKeys");
else if (!Path.IsPathRooted(keyPath))
    keyPath = Path.Combine(builder.Environment.ContentRootPath, keyPath);
var protection = builder.Services.AddDataProtection()
    .SetApplicationName("SQLWorkbench")
    .PersistKeysToFileSystem(new DirectoryInfo(keyPath));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddOptions<WorkbenchOptions>().Bind(builder.Configuration.GetSection("Workbench"))
    .Validate(o => o.MaxRows is >= 1 and <= 50000, "MaxRows must be between 1 and 50000.")
    .Validate(o => o.CommandTimeoutSeconds is >= 1 and <= 300, "Command timeout must be between 1 and 300 seconds.")
    .Validate(o => o.ConnectionLifetimeMinutes is >= 1 and <= 480, "Connection lifetime must be between 1 and 480 minutes.")
    .ValidateOnStart();
builder.Services.AddControllersWithViews();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options => {
    options.IdleTimeout = TimeSpan.FromHours(2);
    // New names avoid trying to decrypt cookies created by the old ephemeral provider.
    options.Cookie.Name = "Workbench.Session.v2";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddAntiforgery(options => {
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "Workbench.Antiforgery.v2";
});
builder.Services.AddSingleton<ConnectionVault>();
builder.Services.AddScoped<SqlDatabaseService>();
builder.Services.AddScoped<ComparisonService>();
builder.Services.AddScoped<ScriptService>();
builder.Services.AddDbContext<WorkbenchDbContext>((services, options) => options.UseSqlServer(
    services.GetRequiredService<IConfiguration>().GetConnectionString("Workbench")
    ?? throw new WorkbenchException("The Workbench application database is not configured.", 409)));
builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = 429;
    options.AddPolicy("database", context => RateLimitPartition.GetConcurrencyLimiter(
        context.Session.GetString("owner") ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new ConcurrencyLimiterOptions { PermitLimit = 2, QueueLimit = 0 }));
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.GetConcurrencyLimiter("server", _ => new ConcurrencyLimiterOptions { PermitLimit = 16, QueueLimit = 0 }));
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024 * 1024);
var app = builder.Build();
if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Home/Error"); app.UseHsts(); }
app.Use(async (context, next) => {
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});
// IIS or a trusted HTTPS reverse proxy terminates TLS. Bind the local preview to loopback.
app.UseRouting();
app.UseSession();
app.UseMiddleware<ApiExceptionMiddleware>();
app.Use(async (context, next) => {
    if (!context.Request.Path.StartsWithSegments("/css") && !context.Request.Path.StartsWithSegments("/js") && !context.Request.Path.StartsWithSegments("/lib")) {
        await context.Session.LoadAsync(context.RequestAborted);
        if (context.Session.GetString("owner") == null) context.Session.SetString("owner", Guid.NewGuid().ToString("N"));
    }
    await next();
});
app.UseRateLimiter();
app.UseAuthorization();
app.MapStaticAssets();
app.MapControllers();
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}").WithStaticAssets();
app.Run();
public partial class Program { }
