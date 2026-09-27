using Microsoft.SqlServer.TransactSql.ScriptDom;
using Workbench.Models;
using Workbench.Services;

internal static class DataExportChecks
{
    public static void Run()
    {
        int count = 0;
        void Check(bool value, string text) { if (!value) throw new Exception(text); count++; }
        void Reject(Action action, string message) { try { action(); } catch (WorkbenchException) { count++; return; } throw new Exception(message); }
        DatabaseColumn Column(string n, string t, bool nullable = true) => new(n, t, nullable, false, false, false, false, "", "", "");
        var table = new DatabaseObject { Schema = "odd]schema", Name = "Target", Kind = "Table", Columns = [Column("Id", "int", false) with { Identity = true }, Column("Tenant", "nvarchar(20)", false), Column("Label", "nvarchar(100)"), Column("Amount", "decimal(38,8)", false)] };
        var input = new DataCompareInput { Keys = [new("Id", "Id"), new("Tenant", "Tenant")], Columns = [new("Name", "Label"), new("Amount", "Amount")], IgnoreCase = true, TrimWhitespace = true };
        var now = DateTimeOffset.UtcNow;
        var a = new QueryResult([new("Id", "int"), new("Tenant", "nvarchar"), new("Name", "nvarchar"), new("Amount", "decimal")], [[1, " acme ", "O'Brien", "123456789012345678901234567890.12345678"], [3, "acme", null, "9.00"]], now);
        var b = new QueryResult([new("Id", "int"), new("Tenant", "nvarchar"), new("Label", "nvarchar"), new("Amount", "decimal")], [[1, "ACME", "old", "1.00"], [4, "ACME", null, "2.00"]], now);
        var result = ComparisonService.CompareRows(a, b, input);
        var read = new DataRead("Test]db", table, table, a, b, result);
        var request = new DataDifferenceScriptInput { Comparison = input, SelectedRows = [0, 1, 2], IncludeIdentity = true };
        var output = DataDifferenceScriptService.Generate(read, request);
        Check(output.Steps.Select(s => s.Change).Order().SequenceEqual(new[] { "DELETE", "INSERT", "UPDATE" }), "All three operations");
        Check(output.Sql.Contains("[odd]]schema].[Target]"), "Escaped target identifiers");
        Check(output.Sql.Contains("N'O''Brien'") && output.Sql.Contains("123456789012345678901234567890.12345678"), "Escaped strings and exact decimals");
        Check(output.Steps[0].Sql.Contains("[Tenant] = N'ACME'") && !output.Steps[0].Sql.Contains("[Tenant] = N' acme '"), "Predicates use actual target keys, not normalized source keys");
        Check(output.Sql.Contains("[Label] IS NULL"), "NULL-safe target guards");
        Check(output.Sql.Contains("CONVERT(varbinary(max), CONVERT(nvarchar(max), [Label]))"), "Exact text guards");
        Check(output.Sql.Contains("WITH (UPDLOCK, HOLDLOCK)") && output.Sql.Contains("IF @@ROWCOUNT <> 1"), "Unfiltered unique key and affected-row guards");
        Check(output.Sql.Contains("SET IDENTITY_INSERT") && output.Sql.Contains("ROLLBACK TRANSACTION"), "Identity and transactional cleanup");
        new TSql100Parser(true).Parse(new StringReader(output.Sql), out var errors);
        Check(errors.Count == 0, "SQL Server 2008-compatible syntax: " + string.Join(";", errors.Select(e => e.Message)));
        var subset = DataDifferenceScriptService.Generate(read, withSelection([0]));
        Check(subset.Steps.Count == 1 && !subset.Sql.Contains("IDENTITY_INSERT"), "Only selected operations; no identity toggle for UPDATE");
        var hash = DataDifferenceScriptService.Fingerprint(read, input);
        Check(hash == DataDifferenceScriptService.Fingerprint(read with { SourceRows = a with { Rows = a.Rows.AsEnumerable().Reverse().ToList(), ReadAt = now.AddDays(1) } }, input), "Fingerprint independent of read order and time");
        Reject(() => DataDifferenceScriptService.Generate(read, new() { Comparison = input, SelectedRows = [1] }), "Identity inserts need explicit option");
        Reject(() => DataDifferenceScriptService.Generate(read, withSelection([])), "Empty selection rejected");
        var unsupportedKeys = read with { TargetRows = b with { Columns = [new("Id", "int"), new("Tenant", "xml"), new("Label", "nvarchar"), new("Amount", "decimal")] } };
        Reject(() => DataDifferenceScriptService.Generate(unsupportedKeys, request), "Non-comparable SQL key types rejected");
        table.Columns.Add(Column("Required", "int", false));
        Reject(() => DataDifferenceScriptService.Generate(read, request), "Missing required destination mapping rejected");
        table.Columns.RemoveAt(table.Columns.Count - 1);
        table.Columns[2] = table.Columns[2] with { Computed = true };
        Reject(() => DataDifferenceScriptService.Generate(read, withSelection([0])), "Generated column updates rejected");
        Console.WriteLine($"PASS: {count} data export planning checks.");
        DataDifferenceScriptInput withSelection(List<int> selected) => new() { Comparison = input, SelectedRows = selected, IncludeIdentity = true };
    }
}
