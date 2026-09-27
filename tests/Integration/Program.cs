using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Workbench.Models;
using Workbench.Services;

internal static class TestRunner
{
    private static int assertions;
    private static void Check(bool condition, string description) { if (!condition) throw new Exception(description); assertions++; }
    private static void Reject(string query)
    {
        try { SelectValidator.Validate(query); } catch (WorkbenchException) { assertions++; return; }
        throw new Exception("Unsafe query accepted: " + query);
    }
    public static async Task Main(string[] args)
    {
        MigrationChecks.Run();
        DataExportChecks.Run();
        if (args.Contains("--migration-only")) return;
        if (args.Contains("--protection-only"))
        {
            await ProtectionChecks.RunAsync(FindRoot());
            return;
        }
        var windowsExample = "data source=.;initial catalog=Taiqz_backup;MultipleActiveResultSets=True;App=EntityFramework;Encrypt=False;Integrated Security=true;pooling=true; Max Pool Size=500";
        var authVault = new ConnectionVault(new EphemeralDataProtectionProvider(), Options.Create(new WorkbenchOptions()));
        var normalized = new SqlConnectionStringBuilder(authVault.Normalize(new ConnectionInput { Name = "Windows", ConnectionString = windowsExample }));
        Check(normalized.IntegratedSecurity && normalized.UserID == "" && normalized.Password == "", "Windows authentication needs no SQL credentials");
        Check(normalized.Encrypt == SqlConnectionEncryptOption.Optional, "Explicit Encrypt=False preserved");
        Check(normalized.MultipleActiveResultSets && normalized.ApplicationName == "EntityFramework", "MARS and App preserved");
        Check(normalized.Pooling && normalized.MaxPoolSize == 500, "Pooling options preserved");
        Check(new SqlConnectionStringBuilder(authVault.Normalize(new ConnectionInput { Name = "Fields", Server = ".", Database = "Example", IntegratedSecurity = true, Encrypt = false })).IntegratedSecurity, "Windows authentication from form fields");
        Check(new SqlConnectionStringBuilder(authVault.Normalize(new ConnectionInput { Name = "Alias", ConnectionString = "Server=.;Database=Example;Trusted_Connection=True" })).IntegratedSecurity, "Trusted_Connection alias accepted");
        Check(new SqlConnectionStringBuilder(authVault.Normalize(new ConnectionInput { Name = "Default TLS", ConnectionString = "Server=.;Database=Example;Integrated Security=SSPI" })).Encrypt == SqlConnectionEncryptOption.Mandatory, "Encryption defaults to enabled");
        var disabledVault = new ConnectionVault(new EphemeralDataProtectionProvider(), Options.Create(new WorkbenchOptions { AllowWindowsAuthentication = false }));
        try { disabledVault.Normalize(new ConnectionInput { Name = "Disabled", ConnectionString = windowsExample }); throw new Exception("Disabled Windows auth accepted"); }
        catch (WorkbenchException) { assertions++; }
        SelectValidator.Validate("WITH c AS (SELECT 1 AS Id) SELECT Id FROM c;");
        SelectValidator.Validate("SELECT N'DROP TABLE x' AS Text;");
        foreach (var query in new[] { "DELETE FROM dbo.T", "SELECT 1; DELETE FROM dbo.T", "SELECT * INTO dbo.Copy FROM dbo.T", "SELECT NEXT VALUE FOR dbo.Seq AS Id", "SELECT * FROM OPENQUERY(Remote, 'SELECT 1')", "SELECT * FROM master.sys.tables", "SELECT * FROM dbo.T WITH (UPDLOCK)", "SELECT @x = 1", "EXEC('SELECT 1')", "SELECT 1 AS Id OPTION (MAXDOP 0)" }) Reject(query);
        Check(ScriptService.Literal("O'Brien", "nvarchar") == "N'O''Brien'", "Quote escaping");
        Check(ScriptService.Literal("123456789012345678901234567890.12345678", "decimal") == "123456789012345678901234567890.12345678", "Exact decimal literal");
        Check(ScriptService.Literal("0x00FF", "varbinary") == "0x00FF", "Binary literal");

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var prefix = "Workbench_Test_" + suffix;
        var names = new[] { prefix + "_Source", prefix + "_Target", prefix + "_Output", prefix + "_App", prefix + "_NoGo" };
        var login = prefix + "_reader";
        var password = "Wb!" + Guid.NewGuid().ToString("N") + "7x";
        var admin = Environment.GetEnvironmentVariable("WORKBENCH_TEST_ADMIN") ?? "Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True";
        var created = new List<string>(); bool loginCreated = false;
        string InDatabase(string db) { var builder = new SqlConnectionStringBuilder(admin) { InitialCatalog = db }; return builder.ConnectionString; }
        async Task Execute(string connectionString, string sql)
        {
            await using var connection = new SqlConnection(connectionString); await connection.OpenAsync();
            foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                using var command = new SqlCommand(batch, connection) { CommandTimeout = 30 }; await command.ExecuteNonQueryAsync();
            }
        }
        try
        {
            foreach (var name in names) { await Execute(admin, $"CREATE DATABASE [{name}]"); created.Add(name); }
            await Execute(admin, $"CREATE LOGIN [{login}] WITH PASSWORD='{password}', CHECK_POLICY=OFF"); loginCreated = true;
            const string schema = """
                CREATE TABLE dbo.Customers (
                    CustomerId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
                    Name nvarchar(100) NOT NULL,
                    City nvarchar(80) NULL,
                    Amount decimal(38,8) NOT NULL,
                    IsActive bit NOT NULL CONSTRAINT DF_Customers_Active DEFAULT(1),
                    Payload varbinary(100) NULL,
                    CreatedAt datetime2(7) NOT NULL,
                    DisplayName AS (Name + N'!'),
                    Version rowversion
                );
                CREATE INDEX IX_Customers_City ON dbo.Customers(City);
                CREATE TABLE dbo.Orders (OrderId int NOT NULL PRIMARY KEY, CustomerId int NOT NULL,
                    CONSTRAINT FK_Orders_Customers FOREIGN KEY(CustomerId) REFERENCES dbo.Customers(CustomerId));
                GO
                CREATE SCHEMA audit;
                GO
                CREATE TABLE audit.Events (EventId int NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL);
                GO
                CREATE VIEW dbo.CustomerNames AS SELECT CustomerId, Name FROM dbo.Customers;
                GO
                CREATE PROCEDURE dbo.GetCustomers AS SELECT CustomerId, Name FROM dbo.Customers;
                GO
                INSERT dbo.Customers(Name,City,Amount,Payload,CreatedAt) VALUES
                    (N'O''Brien',N'Riyadh',123456789012345678901234567890.12345678,0x00FF,'2026-09-01T10:20:30.1234567'),
                    (N'سارة أحمد',NULL,12.50,NULL,'2026-09-02T10:20:30.1234567'),
                    (N'Only source',N'Jeddah',1,NULL,'2026-09-03T10:20:30.1234567');
                """;
            foreach (var db in names.Take(2))
            {
                await Execute(InDatabase(db), schema);
                await Execute(InDatabase(db), $"CREATE USER [{login}] FOR LOGIN [{login}]; GRANT SELECT, VIEW DEFINITION TO [{login}]; DENY INSERT, UPDATE, DELETE TO [{login}];");
            }
            await Execute(InDatabase(names[1]), "ALTER TABLE dbo.Customers DROP COLUMN DisplayName; ALTER TABLE dbo.Customers ALTER COLUMN Name nvarchar(80) NOT NULL; ALTER TABLE dbo.Customers ADD DisplayName AS (Name + N'!'); UPDATE dbo.Customers SET City=N'Dammam' WHERE CustomerId=1; DELETE dbo.Customers WHERE CustomerId=3; INSERT dbo.Customers(Name,City,Amount,CreatedAt) VALUES(N'Only target',N'Abha',1,'2026-09-04'); DROP TABLE audit.Events;");
            await Execute(InDatabase(names[3]), await File.ReadAllTextAsync(Path.Combine(FindRoot(), "Data", "setup.sql")));

            await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder => {
                builder.UseContentRoot(FindRoot());
                builder.UseEnvironment("Testing");
                builder.ConfigureLogging(logging => logging.ClearProviders());
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["ConnectionStrings:Workbench"] = InDatabase(names[3]), ["Workbench:MaxRows"] = "20"
                }));
            });
            using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
            var html = await client.GetStringAsync("/?tool=connections");
            var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name=\"csrf-token\" content=\"([^\"]+)\"").Groups[1].Value);
            Check(csrf.Length > 0, "Antiforgery token rendered");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf);
            async Task<JsonElement> Post(string path, object body, int expected = 200)
            {
                var response = await client.PostAsJsonAsync("/api/" + path, body);
                var text = await response.Content.ReadAsStringAsync();
                Check((int)response.StatusCode == expected, $"{path}: expected {expected}, got {(int)response.StatusCode}: {text}");
                return JsonDocument.Parse(text).RootElement.Clone();
            }
            string ReaderString(string db) => new SqlConnectionStringBuilder { DataSource = new SqlConnectionStringBuilder(admin).DataSource, InitialCatalog = db, UserID = login, Password = password, Encrypt = true, TrustServerCertificate = true }.ConnectionString;
            var a = await Post("connections", new { name = "Source", connectionString = ReaderString(names[0]) });
            if (OperatingSystem.IsWindows() && new SqlConnectionStringBuilder(admin).IntegratedSecurity)
            {
                var windowsString = new SqlConnectionStringBuilder(authVault.Normalize(new ConnectionInput { Name = "Windows", ConnectionString = windowsExample })) {
                    DataSource = new SqlConnectionStringBuilder(admin).DataSource, InitialCatalog = names[0]
                }.ConnectionString;
                var windows = await Post("connections", new { name = "Windows integrated", connectionString = windowsString });
                var windowsId = windows.GetProperty("id").GetString();
                var windowsRows = await Post("query/preview", new { connection = windowsId, query = "SELECT CustomerId FROM dbo.Customers" });
                Check(windowsRows.GetProperty("rows").GetArrayLength() == 3, "Real integrated Windows SELECT succeeds");
                Check((await client.DeleteAsync("/api/connections/" + windowsId)).IsSuccessStatusCode, "Windows pooled connection disconnects");
            }
            // Direct metadata check gives actionable SQL diagnostics if the catalog query is invalid.
            using (var scope = factory.Services.CreateScope())
            {
                var vault = scope.ServiceProvider.GetRequiredService<ConnectionVault>();
                var temporary = vault.Add("integration", new ConnectionInput { Name = "Metadata test" }, ReaderString(names[0]), "16");
                await scope.ServiceProvider.GetRequiredService<SqlDatabaseService>().SnapshotAsync("integration", temporary.Id, CancellationToken.None);
                vault.Remove("integration", temporary.Id);
            }
            var b = await Post("connections", new { name = "Target", connectionString = ReaderString(names[1]) });
            var aid = a.GetProperty("id").GetString()!; var bid = b.GetProperty("id").GetString()!;
            Check(!a.ToString().Contains(password), "Connection response omits secret");
            var remembered = await Post("connections", new { name = "Remembered", connectionString = ReaderString(names[0]), remember = true });
            var rememberToken = remembered.GetProperty("rememberToken").GetString()!;
            Check(!remembered.ToString().Contains(password), "Remember token does not expose the password");
            var restored = await Post("connections/restore", new { token = rememberToken });
            Check(restored.GetProperty("database").GetString() == names[0] && restored.GetProperty("name").GetString() == "Remembered", "Remembered connection restores");
            await Post("connections/restore", new { token = rememberToken[..^8] + "AAAAAAAA" }, 410);
            foreach (var entry in new[] { remembered, restored }) await client.DeleteAsync("/api/connections/" + entry.GetProperty("id").GetString());
            var snapshot = await client.GetFromJsonAsync<DatabaseSnapshot>("/api/connections/" + aid + "/schema") ?? throw new Exception("No snapshot");
            var customers = snapshot.Objects.Single(o => o.Name == "Customers");
            Check(customers.Columns.Single(c => c.Name == "Amount").Type == "decimal(38,8)", "Precise schema type");
            Check(customers.Columns.Single(c => c.Name == "Version").Generated, "Rowversion generated metadata");
            Check(customers.Columns.Single(c => c.Name == "CustomerId").Identity, "Identity metadata");
            var targetSnapshot = await client.GetFromJsonAsync<DatabaseSnapshot>("/api/connections/" + bid + "/schema") ?? throw new Exception("No target snapshot");
            var targetCustomers = targetSnapshot.Objects.Single(o => o.Name == "Customers");
            var comparison = await Post("compare/schema", new { source = aid, target = bid, mode = "full" });
            Check(comparison.GetProperty("objects").EnumerateArray().Any(o => o.GetProperty("name").GetString() == "dbo.Customers" && o.GetProperty("status").GetString() == "changed"), "Live schema difference");
            var selectedChanges = comparison.GetProperty("objects").EnumerateArray().Select((o, i) => (o, i)).Where(x => x.o.GetProperty("status").GetString() != "same").Select(x => x.i).ToArray();
            var migrationRequest = new { comparison = new { source = aid, target = bid, mode = "full" }, fingerprint = comparison.GetProperty("fingerprint").GetString(), selectedObjects = selectedChanges };
            var migration = await Post("scripts/differences", migrationRequest);
            Check(migration.GetProperty("sql").GetString()!.Contains("ALTER COLUMN [Name] nvarchar(100)"), "Live migration direction");
            Check(migration.GetProperty("sql").GetString()!.Contains("CREATE TABLE [audit].[Events]"), "SMO scripts missing source table");
            var untouched = await client.GetFromJsonAsync<DatabaseSnapshot>("/api/connections/" + bid + "/schema");
            Check(untouched!.Objects.Single(o => o.Name == "Customers").Columns.Single(c => c.Name == "Name").Type == "nvarchar(80)", "Generation does not apply changes");
            await Post("scripts/differences", new { migrationRequest.comparison, fingerprint = "outdated", selectedObjects = selectedChanges }, 409);
            await Post("scripts/differences", new { migrationRequest.comparison, migrationRequest.fingerprint, selectedObjects = Array.Empty<int>() }, 400);
            var mapping = new[] { new ColumnMapping("Name", "Name"), new ColumnMapping("City", "City"), new ColumnMapping("Amount", "Amount") };
            var data = await Post("compare/data", new { source = aid, target = bid, sourceTable = customers.Id, targetTable = targetCustomers.Id, keys = new[] { new ColumnMapping("CustomerId", "CustomerId") }, columns = mapping });
            var statuses = data.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("status").GetString()).ToList();
            Check(statuses.Count(s => s == "changed") == 1 && statuses.Count(s => s == "source") == 1 && statuses.Count(s => s == "target") == 1 && statuses.Count(s => s == "same") == 1, "Live row statuses");
            var query = "SELECT CustomerId,Name,City,Amount,IsActive,Payload,CreatedAt FROM dbo.Customers ORDER BY CustomerId";
            var preview = await Post("query/preview", new { connection = aid, query });
            Check(preview.GetProperty("rows")[0][3].GetString() == "123456789012345678901234567890.12345678", "Large decimal survives JSON without precision loss");
            foreach (var bad in new[] { "DELETE FROM dbo.Customers", "SELECT * INTO dbo.Copy FROM dbo.Customers", "SELECT 1 AS Id; DROP TABLE dbo.Customers", "SELECT NEXT VALUE FOR dbo.Sequence AS Id" }) await Post("query/preview", new { connection = aid, query = bad }, 400);
            await Post("query/preview", new { connection = aid, query = "SELECT TOP (21) a.object_id AS Id FROM sys.objects a CROSS JOIN sys.objects b" }, 422);
            await Post("query/preview", new { connection = aid, query = "SELECT 1 AS Id, 2 AS Id" }, 400);
            await Post("compare/data", new { source = aid, target = bid, sourceTable = customers.Id, targetTable = targetCustomers.Id, keys = new[] { new ColumnMapping("IsActive", "IsActive") }, columns = mapping }, 422);

            var schemaResult = await Post("scripts/schema", new { connection = aid, objectIds = snapshot.Objects.Where(o => o.Kind != "Trigger").Select(o => o.Id).ToArray(), includeDependencies = true });
            var schemaSql = schemaResult.GetProperty("sql").GetString()!;
            Check(schemaSql.Contains("CREATE TABLE"), "SMO table output");
            Check(!Regex.IsMatch(schemaSql, @"\bUSE\s+\[", RegexOptions.IgnoreCase), "No source database context in generated script");
            // Executing generated files here is confined to the disposable output database.
            await Execute(InDatabase(names[2]), schemaSql);
            var noGoResult = await Post("scripts/schema", new { connection = aid, objectIds = snapshot.Objects.Where(o => o.Kind != "Trigger").Select(o => o.Id).ToArray(), includeDependencies = true, addGo = false });
            await Execute(InDatabase(names[4]), noGoResult.GetProperty("sql").GetString()!);
            Check(!Regex.IsMatch(noGoResult.GetProperty("sql").GetString()!, @"^\s*GO\s*$", RegexOptions.Multiline), "Schema without GO preserves valid object batches");
            var resultColumns = preview.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray();
            var inserts = await Post("scripts/inserts", new { connection = aid, query, destinationTable = customers.Id, columns = resultColumns.Select(n => new ColumnMapping(n, n)), includeIdentity = true, transaction = true, batchSize = 2 });
            var insertSql = inserts.GetProperty("sql").GetString()!;
            Check(insertSql.Contains("N'O''Brien'") && insertSql.Contains("سارة أحمد") && insertSql.Contains("0x00FF"), "INSERT escaping, Unicode, binary");
            await Execute(InDatabase(names[2]), insertSql);
            await using (var verify = new SqlConnection(InDatabase(names[2])))
            {
                await verify.OpenAsync(); using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.Customers", verify);
                Check(Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 3, "Generated schema and INSERT files execute successfully");
                cmd.CommandText = "SELECT CONVERT(nvarchar(100),Amount) FROM dbo.Customers WHERE CustomerId=1";
                Check((string)(await cmd.ExecuteScalarAsync())! == "123456789012345678901234567890.12345678", "Decimal roundtrip");
            }
            // Exercise the draft in disposable fixtures only, outside the application API.
            await Execute(InDatabase(names[0]), "CREATE TABLE dbo.MigrationProbe (Label nvarchar(100) NULL, NewValue int NOT NULL DEFAULT(7), Flag int NULL DEFAULT(1));");
            await Execute(InDatabase(names[1]), "CREATE TABLE dbo.MigrationProbe (Label nvarchar(20) NOT NULL, OldValue int NULL DEFAULT(0), Flag int NULL DEFAULT(0)); INSERT dbo.MigrationProbe(Label) VALUES(N'kept');");
            var probeSource = await client.GetFromJsonAsync<DatabaseSnapshot>("/api/connections/" + aid + "/schema");
            var probeInput = new SchemaCompareInput { Source = aid, Target = bid, ObjectIds = [probeSource!.Objects.Single(o => o.Name == "MigrationProbe").Id] };
            var probeComparison = await Post("compare/schema", probeInput);
            var probeRequest = new { comparison = probeInput, fingerprint = probeComparison.GetProperty("fingerprint").GetString(), selectedObjects = new[] { 0 } };
            var probeDraft = await Post("scripts/differences", probeRequest);
            await Execute(InDatabase(names[1]), probeDraft.GetProperty("sql").GetString()!);
            var probeAfter = await Post("compare/schema", probeInput);
            Check(probeAfter.GetProperty("objects")[0].GetProperty("status").GetString() == "same", "Migration SQL roundtrip matches source columns/defaults");
            await using (var verify = new SqlConnection(InDatabase(names[1])))
            {
                await verify.OpenAsync(); using var cmd = new SqlCommand("SELECT NewValue FROM dbo.MigrationProbe WHERE Label=N'kept'", verify);
                Check(Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 7, "Migration preserves existing rows and backfills NOT NULL default");
            }
            await Post("scripts/differences", probeRequest, 409);
            await Execute(InDatabase(names[0]), "CREATE TABLE dbo.DataExportProbe(Id int IDENTITY NOT NULL, Tenant nvarchar(20) NOT NULL, Name nvarchar(100) NULL, Amount decimal(38,8) NOT NULL, PRIMARY KEY(Id,Tenant)); SET IDENTITY_INSERT dbo.DataExportProbe ON; INSERT dbo.DataExportProbe(Id,Tenant,Name,Amount) VALUES(1,N' acme ',N'O''Brien',123456789012345678901234567890.12345678),(2,N'acme',N'Same',2),(3,N'acme',NULL,3); SET IDENTITY_INSERT dbo.DataExportProbe OFF;");
            await Execute(InDatabase(names[1]), "CREATE TABLE dbo.DataExportProbe(Id int IDENTITY NOT NULL, Tenant nvarchar(20) NOT NULL, Label nvarchar(100) NULL, Amount decimal(38,8) NOT NULL, PRIMARY KEY(Id,Tenant)); SET IDENTITY_INSERT dbo.DataExportProbe ON; INSERT dbo.DataExportProbe(Id,Tenant,Label,Amount) VALUES(1,N'ACME',N'Old',1),(2,N'acme',N'Same',2),(4,N'acme',N'Delete',4); SET IDENTITY_INSERT dbo.DataExportProbe OFF;");
            var exportA = await client.GetFromJsonAsync<DatabaseSnapshot>("/api/connections/" + aid + "/schema");
            var exportB = await client.GetFromJsonAsync<DatabaseSnapshot>("/api/connections/" + bid + "/schema");
            var dataInput = new DataCompareInput { Source = aid, Target = bid, SourceTable = exportA!.Objects.Single(o => o.Name == "DataExportProbe").Id, TargetTable = exportB!.Objects.Single(o => o.Name == "DataExportProbe").Id, Keys = [new("Id", "Id"), new("Tenant", "Tenant")], Columns = [new("Name", "Label"), new("Amount", "Amount")], IgnoreCase = true, TrimWhitespace = true };
            var dataDiff = await Post("compare/data", dataInput);
            var selectedRows = dataDiff.GetProperty("rows").EnumerateArray().Select((r,i)=>(r,i)).Where(x=>x.r.GetProperty("status").GetString()!="same").Select(x=>x.i).ToList();
            var exportRequest = new DataDifferenceScriptInput { Comparison = dataInput, Fingerprint = dataDiff.GetProperty("fingerprint").GetString()!, SelectedRows = selectedRows, IncludeIdentity = true };
            var dataSql = (await Post("scripts/data-differences", exportRequest)).GetProperty("sql").GetString()!;
            Check(dataSql.Contains("INSERT INTO") && dataSql.Contains("UPDATE ") && dataSql.Contains("DELETE FROM"), "Live export includes selected INSERT/UPDATE/DELETE");
            Check((await Post("compare/data", dataInput)).GetProperty("fingerprint").GetString() == exportRequest.Fingerprint, "Export does not execute generated SQL");
            exportRequest.IncludeIdentity = false;
            await Post("scripts/data-differences", exportRequest, 422);
            exportRequest.IncludeIdentity = true;
            await Execute(InDatabase(names[1]), "UPDATE dbo.DataExportProbe SET Label=N'Changed after export' WHERE Id=4;");
            await Post("scripts/data-differences", exportRequest, 409);
            try { await Execute(InDatabase(names[1]), dataSql); throw new Exception("Conflicting exported script succeeded"); }
            catch (SqlException ex) when (ex.Number == 50000) { Check(true, "Exported SQL detects target drift"); }
            await using (var verify = new SqlConnection(InDatabase(names[1])))
            {
                await verify.OpenAsync(); using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.DataExportProbe WHERE (Id=1 AND Label=N'Old') OR Id=3", verify);
                Check(Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1, "Conflict rolls back earlier UPDATE and INSERT");
            }
            await Execute(InDatabase(names[1]), "UPDATE dbo.DataExportProbe SET Label=N'Delete' WHERE Id=4;");
            await Execute(InDatabase(names[1]), dataSql);
            var synchronized = await Post("compare/data", dataInput);
            Check(synchronized.GetProperty("rows").EnumerateArray().All(r => r.GetProperty("status").GetString() == "same"), "Data export roundtrip: composite normalized keys, renamed columns, exact decimals, identity INSERT and DELETE");
            var profile = await Post("profiles", new { name = "Integration profile", mode = "types", sourceSchema = "dbo", targetSchema = "dbo" });
            var profiles = await client.GetStringAsync("/api/profiles"); Check(profiles.Contains("Integration profile"), "EF Core persisted shared profile");
            Check((await client.DeleteAsync("/api/profiles/" + profile.GetProperty("id").GetString())).IsSuccessStatusCode, "EF profile removal");
            using var stranger = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
            await stranger.GetAsync("/");
            Check((await stranger.GetAsync("/api/connections/" + aid + "/schema")).StatusCode == HttpStatusCode.NotFound, "Connection isolated between sessions");
            Check((await stranger.PostAsJsonAsync("/api/connections", new { name = "Blocked" })).StatusCode == HttpStatusCode.BadRequest, "Missing antiforgery token rejected");
            Check((await client.DeleteAsync("/api/connections/" + aid)).IsSuccessStatusCode, "Disconnect succeeds");
            Check((await client.GetAsync("/api/connections/" + aid + "/schema")).StatusCode == HttpStatusCode.NotFound, "Disconnected credential removed");
            Console.WriteLine($"PASS: {assertions} assertions; SQL Server metadata, comparisons, query guards, precision, SMO and INSERT roundtrips, EF profiles, session isolation, and CSRF.");
        }
        finally
        {
            SqlConnection.ClearAllPools();
            foreach (var name in created.AsEnumerable().Reverse())
            {
                // Only exact database names created by this run are eligible for cleanup.
                if (!name.StartsWith(prefix + "_", StringComparison.Ordinal)) throw new Exception("Unsafe test cleanup name");
                await Execute(admin, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]");
            }
            if (loginCreated) await Execute(admin, $"DROP LOGIN [{login}]");
        }
    }
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Workbench.csproj"))) directory = directory.Parent;
        return directory?.FullName ?? throw new Exception("Run tests from the repository root.");
    }
}
