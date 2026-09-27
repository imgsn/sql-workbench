using Workbench.Models;
using Workbench.Services;
using Microsoft.SqlServer.TransactSql.ScriptDom;

internal static class MigrationChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool valid, string message) { if (!valid) throw new Exception(message); checks++; }
        DatabaseColumn Column(string name, string type, bool nullable = true) => new(name, type, nullable, false, false, false, false, "", "", "");
        var a = new DatabaseObject { Id = 1, Schema = "source", Name = "T]x", Kind = "Table", Columns = [Column("Text", "nvarchar(100)"), Column("New", "int", false) with { Default = "(7)" }, Column("Flag", "int") with { Default = "(1)" }] };
        var b = new DatabaseObject { Id = 2, Schema = "target", Name = "T]x", Kind = "Table", Columns = [Column("Text", "nvarchar(20)", false), Column("Old", "int"), Column("Flag", "int") with { Default = "(0)" }] };
        var source = new DatabaseSnapshot("Source", "16.0", DateTimeOffset.UtcNow, [a], []);
        var target = new DatabaseSnapshot("Target", "16.0", DateTimeOffset.UtcNow, [b], []);
        var settings = new SchemaCompareInput { Source = "a", Target = "b", SourceSchema = "source", TargetSchema = "target" };
        var request = new SchemaMigrationInput { Comparison = settings, SelectedObjects = [0] };
        var plan = SchemaMigrationService.Plan(source, target, request);
        var output = SchemaMigrationService.Export("Target]db", 1, plan);
        Check(output.Sql.Contains("USE [Target]]db]"), "Escapes database names");
        Check(output.Sql.Contains("ALTER TABLE [target].[T]]x] ALTER COLUMN [Text] nvarchar(100) NULL;"), "Direction and schema mapping");
        Check(output.Sql.Contains("ADD [New] int NOT NULL DEFAULT (7)"), "Adds default-backed non-null columns");
        Check(output.Sql.Contains("DROP COLUMN [Old]"), "Scripts removal");
        Check(output.Sql.Contains("sys.default_constraints") && output.Sql.Contains("ADD DEFAULT (1) FOR [Flag]"), "Replaces defaults by discovered target name");
        Check(output.Warnings.Any(w => w.Contains("DATA LOSS")), "Data-loss warning");
        new TSql160Parser(true).Parse(new StringReader(output.Sql), out var errors);
        Check(errors.Count == 0, "SQL parses: " + string.Join(";", errors.Select(e => e.Message)));
        settings.Mode = "types";
        plan = SchemaMigrationService.Plan(source, target, request);
        Check(plan.Count == 1 && plan[0].Sql.EndsWith("nvarchar(100) NOT NULL;"), "Types mode preserves target nullability and skips missing columns");
        settings.Mode = "names";
        plan = SchemaMigrationService.Plan(source, target, request);
        Check(plan.Count == 2 && !plan.Any(s => s.Change.StartsWith("Alter")), "Names mode only changes missing columns");
        settings.Mode = "full";
        a.Columns[0] = a.Columns[0] with { Collation = "SQL_Latin1_General_CP1_CI_AS" };
        var collated = SchemaMigrationService.Export("Target", 1, SchemaMigrationService.Plan(source, target, request));
        new TSql160Parser(true).Parse(new StringReader(collated.Sql), out var collationErrors);
        Check(collationErrors.Count == 0 && collated.Sql.Contains("COLLATE SQL_Latin1_General_CP1_CI_AS"), "Collation syntax uses a validated literal name");
        a.Properties["Index/IX/1"] = "column=Text";
        plan = SchemaMigrationService.Plan(source, target, request);
        Check(plan.Any(s => s.Risk == "manual"), "Unsupported differences are explicit");
        Check(SchemaMigrationService.Export("Target", 1, plan).Warnings.Any(w => w.Contains("INCOMPLETE")), "Partial SQL is marked incomplete");
        var hash = SchemaMigrationService.Fingerprint(source, target, settings);
        Check(hash == SchemaMigrationService.Fingerprint(source with { ReadAt = DateTimeOffset.UtcNow.AddDays(1) }, target, settings), "Read timestamps don't invalidate schemas");
        b.Columns.Add(Column("Drift", "int"));
        Check(hash != SchemaMigrationService.Fingerprint(source, target, settings), "Metadata drift changes fingerprint");
        try { SchemaMigrationService.Plan(source, target, new() { Comparison = settings, SelectedObjects = [500] }); throw new Exception("Invalid selection accepted"); } catch (WorkbenchException) { checks++; }
        a.Columns[0] = a.Columns[0] with { Identity = true };
        Check(SchemaMigrationService.Plan(source, target, request).Any(s => s.Risk == "manual" && s.Change == "Alter column Text"), "Special columns require manual work");
        Console.WriteLine($"PASS: {checks} migration planning checks.");
    }
}
