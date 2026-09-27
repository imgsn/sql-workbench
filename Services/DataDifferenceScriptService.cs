using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Workbench.Models;

namespace Workbench.Services;

public sealed class DataDifferenceScriptService(ComparisonService comparison)
{
    public static string Fingerprint(DataRead read, DataCompareInput input) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new {
            input, read.TargetDatabase, read.SourceTable, read.TargetTable,
            SourceColumns = read.SourceRows.Columns, TargetColumns = read.TargetRows.Columns,
            Source = read.SourceRows.Rows.Select(r => JsonSerializer.Serialize(r)).Order(StringComparer.Ordinal),
            Target = read.TargetRows.Rows.Select(r => JsonSerializer.Serialize(r)).Order(StringComparer.Ordinal)
        })));

    public async Task<MigrationResult> GenerateAsync(string owner, DataDifferenceScriptInput input, CancellationToken token)
    {
        var read = await comparison.ReadDataAsync(owner, input.Comparison, token);
        if (read.Result.Fingerprint != input.Fingerprint)
            throw new WorkbenchException("Compared data, schema or settings changed. Run data comparison again before exporting SQL.", 409);
        return Generate(read, input);
    }

    public static MigrationResult Generate(DataRead read, DataDifferenceScriptInput input)
    {
        var settings = input.Comparison;
        if (settings.Keys.Count == 0) throw new WorkbenchException("Choose a non-null unique matching key.");
        var mappings = settings.Keys.Concat(settings.Columns).Distinct().ToList();
        ComparisonService.ValidateMappings(mappings);
        if (input.SelectedRows.Count == 0 || input.SelectedRows.Any(i => i < 0 || i >= read.Result.Rows.Count || read.Result.Rows[i].Status == "same"))
            throw new WorkbenchException("Select at least one differing row from the current comparison.");
        var table = read.TargetTable;
        var qualified = SelectValidator.Qualified(table);
        DatabaseColumn Column(string name) => table.Columns.Find(c => c.Name == name) ?? throw new WorkbenchException("Target column no longer exists: " + name);
        string Type(string name, bool source) => (source ? read.SourceRows : read.TargetRows).Columns.Find(c => c.Name == name)?.Type
            ?? throw new WorkbenchException("Mapped column no longer exists: " + name);
        if (settings.Keys.Any(k => Type(k.Target, false).ToLowerInvariant() is "text" or "ntext" or "image" or "xml"))
            throw new WorkbenchException("Text, ntext, image and XML columns cannot be export matching keys. Choose columns that support SQL equality.", 422);
        object? Value(RowDifference row, ColumnMapping map, bool source)
        {
            var ki = settings.Keys.FindIndex(k => k == map);
            if (ki >= 0) return (source ? row.SourceKey : row.TargetKey)![ki];
            return (source ? row.Source : row.Target)![settings.Columns.IndexOf(map)];
        }
        string Literal(RowDifference row, ColumnMapping map, bool source) => ScriptService.Literal(Value(row, map, source), Type(source ? map.Source : map.Target, source));
        string Predicate(RowDifference row, ColumnMapping map, bool exact)
        {
            var col = SelectValidator.Identifier(map.Target);
            if (Value(row, map, false) == null) return col + " IS NULL";
            var literal = Literal(row, map, false);
            var type = Type(map.Target, false).ToLowerInvariant();
            // Exact string guards also detect case and trailing-space changes on CI/padded collations.
            if (exact && type is "nvarchar" or "varchar" or "nchar" or "char" or "text" or "ntext" or "xml")
                return $"CONVERT(varbinary(max), CONVERT(nvarchar(max), {col})) = CONVERT(varbinary(max), CONVERT(nvarchar(max), {literal}))";
            if (type == "image") return $"CONVERT(varbinary(max), {col}) = {literal}";
            return col + " = " + literal;
        }
        var steps = new List<MigrationStep>();
        bool identity = false;
        foreach (var index in input.SelectedRows.Distinct().Order())
        {
            var row = read.Result.Rows[index];
            var sourceOnly = row.Status == "source";
            var keyPredicate = string.Join(" AND ", settings.Keys.Select(k => sourceOnly
                ? SelectValidator.Identifier(k.Target) + " = " + Literal(row, k, true) : Predicate(row, k, false)));
            var sql = new StringBuilder();
            sql.AppendLine($"IF (SELECT COUNT_BIG(*) FROM {qualified} WITH (UPDLOCK, HOLDLOCK) WHERE {keyPredicate}) <> {(sourceOnly ? 0 : 1)}");
            sql.AppendLine("    RAISERROR(N'Target key is missing, already exists or is not unique. Recompare before applying.', 16, 1);");
            string action;
            if (sourceOnly)
            {
                action = "INSERT";
                if (settings.Keys.Any(k => Column(k.Target).Computed || Column(k.Target).Generated))
                    throw new WorkbenchException("A generated matching key cannot be reproduced by INSERT. Choose another matching key.", 422);
                var insert = mappings.Where(m => !Column(m.Target).Computed && !Column(m.Target).Generated).ToList();
                if (insert.Any(m => Column(m.Target).Identity))
                {
                    if (!input.IncludeIdentity) throw new WorkbenchException("Selected inserts contain identity values. Enable Preserve identity values to retain their keys.", 422);
                    identity = true;
                }
                var missing = table.Columns.Where(c => !c.Nullable && !c.Identity && !c.Computed && !c.Generated && string.IsNullOrWhiteSpace(c.Default) && insert.All(m => m.Target != c.Name));
                if (missing.Any()) throw new WorkbenchException("INSERT requires additional mapped columns: " + string.Join(", ", missing.Select(c => c.Name)), 422);
                foreach (var m in insert)
                    if (!Column(m.Target).Nullable && Value(row, m, true) == null) throw new WorkbenchException("NULL cannot be inserted into " + m.Target, 422);
                sql.AppendLine($"INSERT INTO {qualified} ({string.Join(", ", insert.Select(m => SelectValidator.Identifier(m.Target)))})");
                sql.AppendLine($"VALUES ({string.Join(", ", insert.Select(m => Literal(row, m, true)))});");
            }
            else
            {
                var expected = string.Join(" AND ", mappings.Select(m => Predicate(row, m, true)));
                if (row.Status == "target")
                {
                    action = "DELETE";
                    sql.AppendLine($"DELETE FROM {qualified} WHERE {keyPredicate} AND {expected};");
                }
                else
                {
                    action = "UPDATE";
                    var changed = settings.Columns.Where(m => row.ChangedColumns.Contains(m.Source)).ToList();
                    if (changed.Count == 0) throw new WorkbenchException("No changed columns selected.");
                    foreach (var m in changed)
                    {
                        var c = Column(m.Target);
                        if (c.Identity || c.Computed || c.Generated || settings.Keys.Any(k => k.Target == m.Target))
                            throw new WorkbenchException($"{m.Target} is a key, identity or generated column. Exclude it from compared columns before exporting updates.", 422);
                        if (!c.Nullable && Value(row, m, true) == null) throw new WorkbenchException("NULL cannot be assigned to " + c.Name, 422);
                    }
                    sql.AppendLine($"UPDATE {qualified} SET {string.Join(", ", changed.Select(m => SelectValidator.Identifier(m.Target) + " = " + Literal(row, m, true)))} WHERE {keyPredicate} AND {expected};");
                }
            }
            sql.AppendLine("IF @@ROWCOUNT <> 1 RAISERROR(N'Target values changed. Recompare before applying.', 16, 1);");
            steps.Add(new("Row " + (index + 1) + " · " + JsonSerializer.Serialize(row.Key), action, sourceOnly ? "review" : "destructive", sql.ToString(),
                sourceOnly ? "Insert mapped values; omitted columns use target defaults or NULL." : "Checks the original compared values; changes exactly one target row."));
        }
        var warnings = new[] {
            "Source → target. Only selected differences and mapped columns are exported. Workbench never executes this SQL.",
            "Review DELETE/UPDATE data loss, filters, conversions, triggers, cascading actions and foreign keys before use. Unmapped columns are not compared.",
            "Reads are separate, not a shared snapshot. The file checks target keys and compared values inside a transaction and rolls back on a detected conflict. Run it in a fresh SQL session.",
            "Text matching options identify pairs only; existing target keys are preserved. INSERT uses original source keys. Review target collation and uniqueness rules."
        };
        var output = new StringBuilder("-- SQL Workbench data differences\n");
        foreach (var warning in warnings) output.AppendLine("-- " + warning);
        output.AppendLine($"USE {SelectValidator.Identifier(read.TargetDatabase)};\nGO\nSET XACT_ABORT ON;\nSET NOCOUNT ON;");
        output.AppendLine("IF @@TRANCOUNT <> 0\nBEGIN\n    RAISERROR(N'Run this file outside any existing transaction.', 16, 1);\n    RETURN;\nEND;");
        if (identity) output.AppendLine("DECLARE @workbenchIdentityEnabled bit = 0;");
        output.AppendLine("BEGIN TRY\n    BEGIN TRANSACTION;");
        if (identity) output.AppendLine($"    SET IDENTITY_INSERT {qualified} ON;\n    SET @workbenchIdentityEnabled = 1;");
        foreach (var step in steps)
        {
            output.AppendLine("-- " + step.Change + " / " + step.Object.Replace("\r", " ").Replace("\n", " ")).AppendLine(step.Sql);
            if (output.Length > 8 * 1024 * 1024) throw new WorkbenchException("The script exceeds 8 million characters. Select fewer rows.", 422);
        }
        if (identity) output.AppendLine($"    SET IDENTITY_INSERT {qualified} OFF;\n    SET @workbenchIdentityEnabled = 0;");
        output.AppendLine("    COMMIT TRANSACTION;\nEND TRY\nBEGIN CATCH\n    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
        if (identity) output.AppendLine($"    IF @workbenchIdentityEnabled = 1 SET IDENTITY_INSERT {qualified} OFF;");
        output.AppendLine("    DECLARE @workbenchError nvarchar(4000) = ERROR_MESSAGE();\n    RAISERROR(N'%s', 16, 1, @workbenchError);\nEND CATCH;\nGO");
        return new(output.ToString(), "data-differences.sql", steps.Count, warnings, steps);
    }
}
