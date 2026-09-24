using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workbench.Models;
using Workbench.Services;

internal static class ProtectionChecks
{
    public static async Task RunAsync(string root)
    {
        var keyDirectory = Path.Combine(Path.GetTempPath(), "Workbench-KeyTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keyDirectory);
        var logs = new CapturedLogs();
        var cookies = new CookieContainer();
        var address = new Uri("https://localhost");
        // Simulate browser cookies from the old, non-persistent key provider.
        cookies.Add(address, new Cookie("Workbench.Session", "obsolete-session-cookie"));
        cookies.Add(address, new Cookie(".AspNetCore.Antiforgery.Legacy", "obsolete-antiforgery-cookie"));
        int assertions = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; }
        WebApplicationFactory<global::Program> NewHost() => new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder => {
            builder.UseContentRoot(root).UseEnvironment("Testing");
            builder.ConfigureServices(services => services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDirectory)));
            builder.ConfigureLogging(logging => { logging.ClearProviders(); logging.AddProvider(logs); });
        });
        async Task<string> GetPage(HttpClient client)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/?tool=connections");
            request.Headers.Add("Cookie", cookies.GetCookieHeader(address));
            using var response = await client.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.OK, "Page loads with existing cookies");
            if (response.Headers.TryGetValues("Set-Cookie", out var headers))
                foreach (var cookie in headers) cookies.SetCookies(address, cookie);
            return await response.Content.ReadAsStringAsync();
        }
        try
        {
            string protectedText, oldRequestToken, sessionCookie;
            await using (var first = NewHost())
            {
                using var client = first.CreateClient(new() { BaseAddress = address, HandleCookies = false });
                var html = await GetPage(client);
                oldRequestToken = WebUtility.HtmlDecode(Regex.Match(html, "name=\"csrf-token\" content=\"([^\"]+)\"").Groups[1].Value);
                Check(oldRequestToken.Length > 0, "Antiforgery request token generated");
                sessionCookie = cookies.GetCookies(address)["Workbench.Session.v2"]?.Value ?? "";
                Check(sessionCookie.Length > 0, "Versioned session cookie generated");
                Check(cookies.GetCookies(address)["Workbench.Antiforgery.v2"] != null, "Versioned antiforgery cookie generated");
                var provider = first.Services.GetRequiredService<IDataProtectionProvider>();
                protectedText = provider.CreateProtector("RestartTest").Protect("survives-restart");
                first.Services.GetRequiredService<ConnectionVault>().Add("test-owner", new ConnectionInput { Name = "Memory only" },
                    "Server=fake;Database=fake;User Id=fake;Password=memory-only-secret", "test");
            }
            Check(Directory.GetFiles(keyDirectory, "key-*.xml").Length > 0, "Key ring persisted to disk");
            if (OperatingSystem.IsWindows())
                Check(Directory.GetFiles(keyDirectory, "key-*.xml").All(path => File.ReadAllText(path).Contains("encryptedSecret")), "Windows key material protected with DPAPI");

            // Entirely new host/service provider: no memory cache or ephemeral provider reuse.
            await using (var second = NewHost())
            {
                using var client = second.CreateClient(new() { BaseAddress = address, HandleCookies = false });
                await GetPage(client);
                Check(second.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("RestartTest").Unprotect(protectedText) == "survives-restart", "Independent host decrypts persisted data");
                Check(cookies.GetCookies(address)["Workbench.Session.v2"]?.Value == sessionCookie, "Session cookie decrypts without replacement after restart");
                Check(second.Services.GetRequiredService<ConnectionVault>().List("test-owner").Count == 0, "Database credentials are not persisted");
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/connections/nonexistent/test") { Content = JsonContent.Create(new { }) };
                request.Headers.Add("Cookie", cookies.GetCookieHeader(address));
                request.Headers.Add("X-CSRF-TOKEN", oldRequestToken);
                using var response = await client.SendAsync(request);
                Check(response.StatusCode == HttpStatusCode.NotFound, "Pre-restart antiforgery token accepted; request reaches controller");
                using var missingToken = new HttpRequestMessage(HttpMethod.Post, "/api/connections/nonexistent/test") { Content = JsonContent.Create(new { }) };
                missingToken.Headers.Add("Cookie", cookies.GetCookieHeader(address));
                using var rejected = await client.SendAsync(missingToken);
                Check(rejected.StatusCode == HttpStatusCode.BadRequest, "Antiforgery validation remains enabled");
            }
            Check(!logs.Messages.Any(m => m.Contains("unprotecting", StringComparison.OrdinalIgnoreCase) || m.Contains("could not be decrypted", StringComparison.OrdinalIgnoreCase) || m.Contains("deserializing the token", StringComparison.OrdinalIgnoreCase)), "No session/antiforgery decryption errors");
            Console.WriteLine($"PASS: {assertions} restart assertions; persistent encrypted keys, legacy cookies, session-cookie reuse, antiforgery validation, and memory-only database credentials.");
        }
        finally
        {
            var resolved = Path.GetFullPath(keyDirectory);
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("Workbench-KeyTest-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup path");
            Directory.Delete(resolved, recursive: true);
        }
    }
    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);
        public void Dispose() { }
        private sealed class CaptureLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) messages.Enqueue(formatter(state, exception) + exception?.ToString());
            }
        }
    }
}
