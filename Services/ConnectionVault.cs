using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Workbench.Models;
using ConnectionInfo = Workbench.Models.ConnectionInfo;

namespace Workbench.Services;

// Credentials are protected in process memory only, bound to an unguessable session.
public sealed class ConnectionVault(IDataProtectionProvider protection, IOptions<WorkbenchOptions> settings)
{
    private sealed record Entry(string Owner, string Secret, ConnectionInfo Info);
    private readonly ConcurrentDictionary<string, Entry> entries = new();
    private readonly IDataProtector protector = protection.CreateProtector("Workbench.Connections.v1");
    // Remembered connections are held by the browser as opaque tokens; the server stores nothing.
    private readonly ITimeLimitedDataProtector remembered = protection.CreateProtector("Workbench.RecentConnections.v1").ToTimeLimitedDataProtector();
    private readonly WorkbenchOptions options = settings.Value;

    public ConnectionInfo Remember(ConnectionInfo info, string normalized)
    {
        var expires = DateTimeOffset.UtcNow.AddDays(options.RememberedConnectionDays);
        var payload = JsonSerializer.Serialize(new RememberedConnection(info.Name, info.Environment, normalized));
        return info with { RememberToken = remembered.Protect(payload, expires), RememberExpiresAt = expires };
    }
    public ConnectionInput Recall(string token)
    {
        RememberedConnection? saved;
        try { saved = JsonSerializer.Deserialize<RememberedConnection>(remembered.Unprotect(token)); }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException) { saved = null; }
        if (saved == null) throw new WorkbenchException("This saved connection has expired or can no longer be read. Add it again.", 410);
        return new ConnectionInput { Name = saved.Name, Environment = saved.Environment, ConnectionString = saved.ConnectionString };
    }

    public string Normalize(ConnectionInput input)
    {
        SqlConnectionStringBuilder parsed;
        try
        {
            parsed = string.IsNullOrWhiteSpace(input.ConnectionString)
                ? new SqlConnectionStringBuilder { DataSource = input.Server ?? "", InitialCatalog = input.Database ?? "", UserID = input.Username ?? "", Password = input.Password ?? "", IntegratedSecurity = input.IntegratedSecurity, Encrypt = input.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional, TrustServerCertificate = input.TrustServerCertificate }
                : new SqlConnectionStringBuilder(input.ConnectionString);
        }
        catch (ArgumentException) { throw new WorkbenchException("Invalid SQL Server connection string. Check the field names and quoting."); }
        if (string.IsNullOrWhiteSpace(parsed.DataSource) || string.IsNullOrWhiteSpace(parsed.InitialCatalog))
            throw new WorkbenchException("Server and Database are required.");
        if (options.AllowedServers.Length > 0 && !options.AllowedServers.Contains(parsed.DataSource, StringComparer.OrdinalIgnoreCase))
            throw new WorkbenchException("This server is not in the administrator's allowed server list.", 403);
        if (parsed.IntegratedSecurity && !options.AllowWindowsAuthentication)
            throw new WorkbenchException("Windows authentication is disabled by the administrator. Enable Workbench:AllowWindowsAuthentication or use a SQL login.");
        if (!parsed.IntegratedSecurity && (string.IsNullOrWhiteSpace(parsed.UserID) || string.IsNullOrEmpty(parsed.Password)))
            throw new WorkbenchException("Username and password are required.");
        // Rebuild an allowlisted string: disallow attach files, failover endpoints, and arbitrary settings.
        return new SqlConnectionStringBuilder {
            DataSource = parsed.DataSource, InitialCatalog = parsed.InitialCatalog,
            UserID = parsed.IntegratedSecurity ? "" : parsed.UserID,
            Password = parsed.IntegratedSecurity ? "" : parsed.Password,
            IntegratedSecurity = parsed.IntegratedSecurity,
            Encrypt = parsed.Encrypt,
            TrustServerCertificate = parsed.TrustServerCertificate,
            ConnectTimeout = 10, CommandTimeout = options.CommandTimeoutSeconds,
            ApplicationName = parsed.ContainsKey("Application Name") ? parsed.ApplicationName : "SQL Workbench",
            MultipleActiveResultSets = parsed.MultipleActiveResultSets,
            Pooling = parsed.Pooling, MaxPoolSize = parsed.MaxPoolSize,
            PersistSecurityInfo = false,
            Enlist = false, ConnectRetryCount = 0
        }.ConnectionString;
    }
    public ConnectionInfo Add(string owner, ConnectionInput input, string normalized, string version)
    {
        Prune();
        if (List(owner).Count >= 10) throw new WorkbenchException("A session can have up to 10 connections. Remove one first.");
        var builder = new SqlConnectionStringBuilder(normalized);
        var id = Guid.NewGuid().ToString("N");
        var info = new ConnectionInfo(id, input.Name.Trim(), builder.DataSource, builder.InitialCatalog,
            input.Environment, version, DateTimeOffset.UtcNow.AddMinutes(options.ConnectionLifetimeMinutes));
        entries[id] = new(owner, protector.Protect(normalized), info);
        return info;
    }
    public List<ConnectionInfo> List(string owner) { Prune(); return entries.Values.Where(e => e.Owner == owner).Select(e => e.Info).OrderBy(e => e.Name).ToList(); }
    public string Get(string owner, string id)
    {
        Prune();
        if (!entries.TryGetValue(id, out var entry) || entry.Owner != owner)
            throw new WorkbenchException("Connection not found or expired. Add the connection again.", 404);
        return protector.Unprotect(entry.Secret);
    }
    public void Remove(string owner, string id)
    {
        _ = Get(owner, id);
        if (entries.TryRemove(id, out var entry)) ClearPool(entry);
    }
    private void Prune()
    {
        foreach (var pair in entries)
            if (pair.Value.Info.ExpiresAt <= DateTimeOffset.UtcNow && entries.TryRemove(pair.Key, out var entry)) ClearPool(entry);
    }
    private void ClearPool(Entry entry)
    {
        using var connection = new SqlConnection(protector.Unprotect(entry.Secret));
        SqlConnection.ClearPool(connection);
    }
}
