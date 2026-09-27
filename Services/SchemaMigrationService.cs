using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Workbench.Models;

namespace Workbench.Services;

// Produces review files only. No generated statement is executed by this service.
public sealed class SchemaMigrationService(SqlDatabaseService database, ScriptService scripts)
{
    public static string Fingerprint(DatabaseSnapshot source, DatabaseSnapshot target, SchemaCompareInput input) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            Source = new { source.Database, source.Version, source.Objects },
            Target = new { target.Database, target.Version, target.Objects }, Input = input
        })));

    public async Task<MigrationResult> GenerateAsync(string owner, SchemaMigrationInput input, CancellationToken token)
    {
        var source = await database.SnapshotAsync(owner, input.Comparison.Source, token);
        var target = await database.SnapshotAsync(owner, input.Comparison.Target, token);
        if (input.Fingerprint != Fingerprint(source, target, input.Comparison))
            throw new WorkbenchException("The schema or comparison settings changed. Run comparison again before generating SQL.", 409);
        var steps = Plan(source, target, input);
        // Whole missing objects use SMO rather than reconstructing incomplete catalog definitions.
        foreach (var step in steps.Where(s => s.Risk == "create").ToArray())
        {
            var obj = source.Objects.Single(o => o.FullName == step.Object && o.Kind == step.Change);
            var result = await scripts.SchemaAsync(owner, new SchemaScriptInput {
                Connection = input.Comparison.Source, ObjectIds = [obj.Id],
                IncludeDependencies = false, IncludeTriggers = false
            }, token);
            steps[steps.IndexOf(step)] = step with { Risk = "review", Change = "Create " + obj.Kind.ToLowerInvariant(), Sql = result.Sql,
                Note = "Check referenced types, schemas, keys and object order. Related triggers and unselected dependencies are not included." };
        }
        // SMO reads metadata separately: reject a script if either schema changed during generation.
        var sourceAfter = await database.SnapshotAsync(owner, input.Comparison.Source, token);
        var targetAfter = await database.SnapshotAsync(owner, input.Comparison.Target, token);
        if (input.Fingerprint != Fingerprint(sourceAfter, targetAfter, input.Comparison))
            throw new WorkbenchException("Schema changed during generation. Run comparison again.", 409);
        return Export(target.Database, input.SelectedObjects.Distinct().Count(), steps);
    }

    public static List<MigrationStep> Plan(DatabaseSnapshot source, DatabaseSnapshot target, SchemaMigrationInput input)
    {
        var comparison = input.Comparison;
        var differences = ComparisonService.CompareSchemas(source, target, comparison);
        if (input.SelectedObjects.Count == 0 || input.SelectedObjects.Any(i => i < 0 || i >= differences.Count || differences[i].Status == "same"))
            throw new WorkbenchException("Select at least one changed object from the current comparison.");
        var steps = new List<MigrationStep>();
        bool mapped = !string.IsNullOrEmpty(comparison.SourceSchema) && !string.IsNullOrEmpty(comparison.TargetSchema);
        foreach (var index in input.SelectedObjects.Distinct().Order())
        {
            var diff = differences[index];
            var a = source.Objects.Find(o => o.FullName == diff.Name && o.Kind == diff.Kind);
            var b = target.Objects.Find(o => o.Kind == diff.Kind && (mapped && a != null
                ? o.Schema == comparison.TargetSchema && o.Name == a.Name : o.FullName == diff.Name));
            // A target-only object can share its full name with an excluded source object.
            if (diff.Status == "target") a = null;
            if (diff.Status == "source") b = null;
            string name = (b ?? a)!.FullName;
            void Manual(string change, string note) => steps.Add(new(name, change, "manual", "", note));
            if (a == null)
            {
                var keyword = b!.Kind switch { "Table" => "TABLE", "View" => "VIEW", "Procedure" => "PROCEDURE", "Function" => "FUNCTION", "Trigger" => "TRIGGER", _ => null };
                if (keyword == null) Manual("Remove object", "Unsupported object kind.");
                else steps.Add(new(name, "Drop " + b.Kind.ToLowerInvariant(), "destructive", $"DROP {keyword} {SelectValidator.Qualified(b)};", "Removes the target object. Dropping a table permanently deletes its data. Review dependencies and permissions."));
                continue;
            }
            if (b == null)
            {
                if (mapped && comparison.SourceSchema != comparison.TargetSchema)
                    Manual("Create object in mapped schema", "Script the source object and review schema-qualified references before changing its schema. Automatic text replacement is not safe.");
                else steps.Add(new(name, a.Kind, "create", "", "Source-only object."));
                continue;
            }
            if (a.Kind != "Table")
            {
                Manual("Update " + a.Kind.ToLowerInvariant(), "Export the source definition using Generate schema, then review an ALTER statement and its dependencies. Definition changes are not automatically rewritten.");
                continue;
            }
            foreach (var group in diff.Differences.GroupBy(d => d.Column))
            {
                var c = a.Columns.Find(c => c.Name == group.Key);
                var d = b.Columns.Find(c => c.Name == group.Key);
                var properties = group.Select(v => v.Property).ToHashSet();
                var qualified = SelectValidator.Qualified(b);
                var column = SelectValidator.Identifier(group.Key);
                if (group.Key == "—" && c == null && d == null)
                {
                    foreach (var property in group) Manual(property.Property, "Keys, indexes, constraints, and identity settings require dependency-aware manual SQL. See the source/target definitions in comparison details.");
                    continue;
                }
                if (c == null)
                {
                    steps.Add(new(name, "Drop column " + group.Key, "destructive", DropDefault(b, group.Key) + $"\nALTER TABLE {qualified} DROP COLUMN {column};", "Permanently deletes column values. Remove dependent keys, indexes, computed columns and constraints first."));
                    continue;
                }
                if (d == null)
                {
                    if (c.Identity || c.Computed || c.Generated || c.PrimaryKey)
                        Manual("Add column " + c.Name, "Identity, computed, generated or primary-key columns need a complete definition and dependency review.");
                    else steps.Add(new(name, "Add column " + c.Name, "review", $"ALTER TABLE {qualified} ADD {column} {Definition(c)}{(string.IsNullOrWhiteSpace(c.Default) ? "" : " DEFAULT " + c.Default)};",
                        c.Nullable ? "Existing rows receive NULL unless a default is required by SQL Server. Review data backfill requirements." : "A populated table needs a default or a separate backfill before adding a NOT NULL column."));
                    continue;
                }
                if (properties.Overlaps(["Data type", "Nullable", "Collation"]))
                {
                    if (c.Identity || d.Identity || c.Computed || d.Computed || c.Generated || d.Generated)
                        Manual("Alter column " + c.Name, "This special column requires a rebuild or an explicit migration strategy.");
                    else
                    {
                        // Narrow comparison modes must preserve target properties outside their scope.
                        var desired = d with {
                            Type = properties.Contains("Data type") ? c.Type : d.Type,
                            Nullable = properties.Contains("Nullable") ? c.Nullable : d.Nullable,
                            Collation = properties.Contains("Collation") || (properties.Contains("Data type") && string.IsNullOrWhiteSpace(c.Collation)) ? c.Collation : d.Collation
                        };
                        steps.Add(new(name, "Alter column " + c.Name, "destructive", $"ALTER TABLE {qualified} ALTER COLUMN {column} {Definition(desired)};",
                            "Conversion, truncation, collation changes or NOT NULL may lose data or fail. Check existing values and dependent indexes/constraints first."));
                    }
                }
                if (properties.Contains("Default"))
                    steps.Add(new(name, "Change default for " + c.Name, "review", DropDefault(b, c.Name) + (string.IsNullOrWhiteSpace(c.Default) ? "" : $"\nALTER TABLE {qualified} ADD DEFAULT {c.Default} FOR {column};"), "Changes the default for future inserts; existing values are not backfilled."));
                foreach (var property in properties.Except(["Data type", "Nullable", "Collation", "Default"]))
                    Manual(property + " for " + c.Name, "This property requires manual SQL; it is not changed by the generated statements.");
            }
        }
        // Drop dependents before tables; create tables before modules. Cross-table cycles still need review.
        return steps.OrderBy(s => s.Change == "Drop table" ? 3 : s.Risk == "destructive" && s.Change.StartsWith("Drop ") ? 0 : s.Risk == "create" && s.Change == "Table" ? 1 : 2).ToList();
    }
    private static string Definition(DatabaseColumn column) => column.Type
        + (string.IsNullOrWhiteSpace(column.Collation) ? "" : " COLLATE " + Collation(column.Collation))
        + (column.Nullable ? " NULL" : " NOT NULL");
    private static string Collation(string name) => Regex.IsMatch(name, @"\A[A-Za-z0-9_]+\z")
        ? name : throw new WorkbenchException("This collation name requires manual scripting.", 422);
    private static string Literal(string text) => "N'" + text.Replace("'", "''") + "'";
    private static string DropDefault(DatabaseObject table, string column) => $"""
        DECLARE @defaultName sysname;
        SELECT @defaultName = dc.name FROM sys.default_constraints dc
        JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
        WHERE dc.parent_object_id = OBJECT_ID({Literal(SelectValidator.Qualified(table))}) AND c.name = {Literal(column)};
        IF @defaultName IS NOT NULL
        BEGIN
            DECLARE @dropDefaultSql nvarchar(max) = {Literal("ALTER TABLE " + SelectValidator.Qualified(table) + " DROP CONSTRAINT ")} + QUOTENAME(@defaultName);
            EXEC(@dropDefaultSql);
        END;
        """;
    public static MigrationResult Export(string targetDatabase, int count, List<MigrationStep> steps)
    {
        string Comment(string text) => text.Replace("\r", " ").Replace("\n", " ");
        var warnings = new List<string> { "Direction: target is changed to match source. Review only; Workbench never executes this file.", "This is a migration draft, not a validated deployment. Review dependencies, server-version compatibility, backups and permissions before use." };
        if (steps.Any(s => s.Risk == "destructive")) warnings.Add("POTENTIAL DATA LOSS: selected changes include drops or column alterations.");
        if (steps.Any(s => s.Risk is "manual" or "create")) warnings.Add("INCOMPLETE: manual steps remain. The generated SQL does not fully synchronize the selected objects.");
        var sql = new StringBuilder("-- SQL Workbench schema differences\n");
        foreach (var warning in warnings) sql.AppendLine("-- " + warning);
        sql.AppendLine($"USE {SelectValidator.Identifier(targetDatabase)};\nGO");
        foreach (var step in steps)
        {
            sql.AppendLine($"-- {step.Risk.ToUpperInvariant()}: {Comment(step.Object)} / {Comment(step.Change)}");
            sql.AppendLine("-- " + Comment(step.Note));
            if (!string.IsNullOrWhiteSpace(step.Sql)) sql.AppendLine(step.Sql).AppendLine("GO");
            sql.AppendLine();
        }
        if (sql.Length > 8 * 1024 * 1024) throw new WorkbenchException("The migration draft exceeds 8 million characters. Select fewer objects.", 422);
        return new(sql.ToString(), "schema-differences.sql", count, warnings.ToArray(), steps);
    }
}
