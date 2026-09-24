using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Workbench.Data;
using Workbench.Models;
using Workbench.Services;

namespace Workbench.Controllers;

[ApiController, Route("api"), AutoValidateAntiforgeryToken, EnableRateLimiting("database")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DatabaseController(ConnectionVault vault, SqlDatabaseService database, ComparisonService comparison,
    ScriptService scripts, IConfiguration configuration, IServiceProvider services) : ControllerBase
{
    private string Owner => HttpContext.Session.GetString("owner") ?? throw new WorkbenchException("Reload the page to initialize a session.", 401);

    [HttpGet("connections")]
    public IActionResult Connections() => Ok(vault.List(Owner));

    [HttpPost("connections")]
    public async Task<IActionResult> AddConnection(ConnectionInput input, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new WorkbenchException("Connection name is required.");
        var normalized = vault.Normalize(input);
        var version = await database.TestAsync(normalized, token);
        return Ok(vault.Add(Owner, input, normalized, version));
    }
    [HttpDelete("connections/{id}")]
    public IActionResult RemoveConnection(string id) { vault.Remove(Owner, id); return NoContent(); }

    [HttpPost("connections/{id}/test")]
    public async Task<IActionResult> TestConnection(string id, CancellationToken token) => Ok(new { version = await database.TestAsync(vault.Get(Owner, id), token) });

    [HttpGet("connections/{id}/schema")]
    public async Task<IActionResult> Schema(string id, CancellationToken token) => Ok(await database.SnapshotAsync(Owner, id, token));

    [HttpPost("compare/schema")]
    public async Task<IActionResult> CompareSchema(SchemaCompareInput input, CancellationToken token)
    {
        var source = await database.SnapshotAsync(Owner, input.Source, token);
        var target = await database.SnapshotAsync(Owner, input.Target, token);
        return Ok(new { objects = ComparisonService.CompareSchemas(source, target, input), sourceReadAt = source.ReadAt, targetReadAt = target.ReadAt, warnings = source.Warnings });
    }
    [HttpPost("compare/data")]
    public async Task<IActionResult> CompareData(DataCompareInput input, CancellationToken token) => Ok(await comparison.CompareDataAsync(Owner, input, token));

    [HttpPost("query/preview")]
    public async Task<IActionResult> Query(QueryInput input, CancellationToken token) => Ok(await database.QueryAsync(Owner, input.Connection, input.Query, token));

    [HttpPost("scripts/schema")]
    public async Task<IActionResult> SchemaScript(SchemaScriptInput input, CancellationToken token) => Ok(await scripts.SchemaAsync(Owner, input, token));

    [HttpPost("scripts/inserts")]
    public async Task<IActionResult> InsertScript(InsertInput input, CancellationToken token) => Ok(await scripts.InsertsAsync(Owner, input, token));

    [HttpGet("profiles")]
    public async Task<IActionResult> Profiles(CancellationToken token)
    {
        if (string.IsNullOrEmpty(configuration.GetConnectionString("Workbench"))) return Ok(new { enabled = false, profiles = Array.Empty<object>() });
        var db = services.GetRequiredService<WorkbenchDbContext>();
        return Ok(new { enabled = true, profiles = await db.ComparisonProfiles.AsNoTracking().OrderBy(p => p.Name).Take(100).ToListAsync(token) });
    }
    [HttpPost("profiles")]
    public async Task<IActionResult> SaveProfile(ComparisonProfile input, CancellationToken token)
    {
        if (string.IsNullOrEmpty(configuration.GetConnectionString("Workbench"))) throw new WorkbenchException("Configure the dedicated Workbench application database to save shared profiles. You can still download a profile as JSON.", 409);
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100 || input.Mode is not ("full" or "names" or "types" or "both") || input.SourceSchema?.Length > 128 || input.TargetSchema?.Length > 128)
            throw new WorkbenchException("Enter a name of up to 100 characters and valid comparison settings.");
        var db = services.GetRequiredService<WorkbenchDbContext>();
        if (await db.ComparisonProfiles.CountAsync(token) >= 100) throw new WorkbenchException("The shared profile limit is 100.");
        if (await db.ComparisonProfiles.AnyAsync(p => p.Name == input.Name.Trim(), token)) throw new WorkbenchException("A shared profile already has that name.", 409);
        var profile = new ComparisonProfile { Name = input.Name.Trim(), Mode = input.Mode, SourceSchema = input.SourceSchema, TargetSchema = input.TargetSchema };
        db.ComparisonProfiles.Add(profile);
        await db.SaveChangesAsync(token);
        return Ok(profile);
    }
    [HttpDelete("profiles/{id:guid}")]
    public async Task<IActionResult> RemoveProfile(Guid id, CancellationToken token)
    {
        if (string.IsNullOrEmpty(configuration.GetConnectionString("Workbench"))) throw new WorkbenchException("Shared profiles are not configured.", 409);
        var db = services.GetRequiredService<WorkbenchDbContext>();
        var profile = await db.ComparisonProfiles.FindAsync([id], token);
        if (profile == null) return NotFound();
        db.ComparisonProfiles.Remove(profile);
        await db.SaveChangesAsync(token);
        return NoContent();
    }
}
