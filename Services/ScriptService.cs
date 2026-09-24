using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using Workbench.Models;

namespace Workbench.Services;

public sealed class ScriptService(ConnectionVault vault, SqlDatabaseService database, IOptions<WorkbenchOptions> settings)
{
    public async Task<ScriptResult> SchemaAsync(string owner, SchemaScriptInput input, CancellationToken token)
    {
        if (input.ObjectIds.Count == 0) throw new WorkbenchException("Select at least one database object.");
        var snapshot = await database.SnapshotAsync(owner, input.Connection, token);
        var selected = snapshot.Objects.Where(o => input.ObjectIds.Contains(o.Id)).ToList();
        if (selected.Count != input.ObjectIds.Distinct().Count()) throw new WorkbenchException("An object no longer exists. Refresh the object list.");
        var secret = vault.Get(owner, input.Connection);
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            // SMO dependency discovery opens a transaction across batches, which SQL Server rejects under MARS.
            using var sqlConnection = new SqlConnection(new SqlConnectionStringBuilder(secret) { MultipleActiveResultSets = false }.ConnectionString);
            var connection = new ServerConnection(sqlConnection) { StatementTimeout = settings.Value.CommandTimeoutSeconds };
            try
            {
                var server = new Server(connection);
                var db = server.Databases[new SqlConnectionStringBuilder(secret).InitialCatalog];
                if (db == null) throw new WorkbenchException("Database could not be opened.");
                SqlSmoObject Resolve(DatabaseObject obj) => obj.Kind switch {
                    "Table" => db.Tables[obj.Name, obj.Schema], "View" => db.Views[obj.Name, obj.Schema],
                    "Procedure" => db.StoredProcedures[obj.Name, obj.Schema], "Function" => db.UserDefinedFunctions[obj.Name, obj.Schema],
                    "Trigger" => db.Tables.Cast<Table>().SelectMany(t => t.Triggers.Cast<Trigger>()).First(t => t.ID == obj.Id),
                    _ => throw new WorkbenchException("Unsupported object type: " + obj.Kind)
                };
                var objects = selected.Select(Resolve).ToArray();
                var scripter = new Scripter(server) { Options = new ScriptingOptions {
                    ScriptSchema = true, ScriptData = false, ScriptDrops = false,
                    DriAll = input.IncludeKeys, Indexes = input.IncludeIndexes, Triggers = input.IncludeTriggers,
                    WithDependencies = input.IncludeDependencies, SchemaQualify = true,
                    IncludeDatabaseContext = false, ContinueScriptingOnError = false,
                    NoCollation = false, ExtendedProperties = true
                } };
                using var registration = token.Register(() => { try { connection.Cancel(); } catch { /* Connection may already be closed. */ } });
                var statements = scripter.Script(objects).Cast<string>().ToList();
                token.ThrowIfCancellationRequested();
                var sql = new StringBuilder(Header(snapshot.Database));
                foreach (var schema in selected.Select(o => o.Schema).Distinct().Where(s => s != "dbo"))
                    sql.AppendLine($"IF SCHEMA_ID({TextLiteral(schema)}) IS NULL EXEC({TextLiteral("CREATE SCHEMA " + SelectValidator.Identifier(schema))});");
                if (input.AddGo) sql.AppendLine("GO");
                foreach (var statement in statements)
                {
                    // CREATE VIEW/PROCEDURE must start a batch. EXEC preserves boundaries without GO.
                    sql.AppendLine(input.AddGo ? statement : $"EXEC({TextLiteral(statement)});");
                    if (input.AddGo) sql.AppendLine("GO");
                }
                if (sql.Length > 8 * 1024 * 1024) throw new WorkbenchException("The script exceeds 8 million characters. Select fewer objects.", 422);
                return new ScriptResult(sql.ToString(), "database-schema.sql", selected.Count,
                    ["Generated for the source server version. Review dependencies and existing destination objects before using the file.",
                     input.IncludeDependencies ? "Referenced dependencies may be included beyond the selected objects." : "Dependencies are not included; select required referenced objects separately."]);
            }
            finally { connection.Disconnect(); }
        }, token);
    }
    public async Task<ScriptResult> InsertsAsync(string owner, InsertInput input, CancellationToken token)
    {
        if (input.Columns.Count == 0) throw new WorkbenchException("Map at least one query result column to a destination column.");
        ComparisonService.ValidateMappings(input.Columns);
        var snapshot = await database.SnapshotAsync(owner, input.Connection, token);
        var table = snapshot.Objects.Find(o => o.Id == input.DestinationTable && o.Kind == "Table") ?? throw new WorkbenchException("Select an existing destination table for column metadata.");
        var result = await database.QueryAsync(owner, input.Connection, input.Query, token);
        return GenerateInserts(table, result, input);
    }
    public static ScriptResult GenerateInserts(DatabaseObject table, QueryResult result, InsertInput input)
    {
        ComparisonService.ValidateMappings(input.Columns);
        var mapped = new List<(DatabaseColumn Column, int Index)>();
        foreach (var mapping in input.Columns)
        {
            var column = table.Columns.Find(c => c.Name == mapping.Target) ?? throw new WorkbenchException("Unknown destination column: " + mapping.Target);
            var index = result.Columns.FindIndex(c => c.Name == mapping.Source);
            if (index < 0) throw new WorkbenchException("Unknown result column: " + mapping.Source);
            if (column.Computed || column.Generated) throw new WorkbenchException($"{column.Name} is generated by SQL Server and cannot be inserted.");
            if (column.Identity && !input.IncludeIdentity) throw new WorkbenchException($"Exclude {column.Name}, or enable identity values.");
            if (!column.Nullable && result.Rows.Any(r => r[index] == null)) throw new WorkbenchException($"{column.Name} is NOT NULL but the query returned NULL values.");
            mapped.Add((column, index));
        }
        if (mapped.Count == 0) throw new WorkbenchException("Select columns to export.");
        var missing = table.Columns.Where(c => !c.Nullable && !c.Identity && !c.Computed && !c.Generated && string.IsNullOrEmpty(c.Default) && mapped.All(m => m.Column.Name != c.Name)).ToList();
        if (missing.Count > 0) throw new WorkbenchException("Required destination columns are not mapped: " + string.Join(", ", missing.Select(c => c.Name)));
        bool identity = mapped.Any(c => c.Column.Identity);
        var target = SelectValidator.Qualified(table);
        var sql = new StringBuilder(Header(table.FullName));
        if (input.Transaction) sql.AppendLine("SET XACT_ABORT ON;\nBEGIN TRANSACTION;\n");
        if (identity) sql.AppendLine($"SET IDENTITY_INSERT {target} ON;\n");
        foreach (var batch in result.Rows.Chunk(Math.Clamp(input.BatchSize, 1, 1000)))
        {
            sql.AppendLine($"INSERT INTO {target} ({string.Join(", ", mapped.Select(c => SelectValidator.Identifier(c.Column.Name)))})");
            sql.AppendLine("VALUES");
            sql.AppendLine(string.Join(",\n", batch.Select(row => "(" + string.Join(", ", mapped.Select(c => Literal(row[c.Index], result.Columns[c.Index].Type))) + ")")) + ";\n");
            if (sql.Length > 8 * 1024 * 1024) throw new WorkbenchException("The generated script exceeds 8 million characters. Export fewer rows.", 422);
        }
        if (identity) sql.AppendLine($"SET IDENTITY_INSERT {target} OFF;\n");
        if (input.Transaction) sql.AppendLine("COMMIT TRANSACTION;");
        return new(sql.ToString(), "table-inserts.sql", result.Rows.Count,
            ["The query was read again during generation. Values may differ from an earlier preview if the source changed.", "Destination constraints, conversions, and existing keys are checked only when you execute the file outside this application."]);
    }
    public static string Literal(object? value, string type)
    {
        if (value == null) return "NULL";
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)!;
        type = type.ToLowerInvariant();
        if (type == "bit") return value is true || text == "1" ? "1" : "0";
        if (type is "int" or "bigint" or "smallint" or "tinyint" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real")
        {
            if (!Regex.IsMatch(text, @"^-?\d+(\.\d+)?([Ee][+-]?\d+)?$", RegexOptions.CultureInvariant)) throw new WorkbenchException("A numeric value cannot be scripted exactly.");
            return text;
        }
        if (type is "binary" or "varbinary" or "image" or "timestamp" or "rowversion")
        {
            if (!Regex.IsMatch(text, "^0x[0-9A-Fa-f]*$")) throw new WorkbenchException("Invalid binary value.");
            return text;
        }
        if (type is "nvarchar" or "varchar" or "nchar" or "char" or "text" or "ntext" or "xml" or "uniqueidentifier" or "date" or "datetime" or "smalldatetime" or "datetime2" or "datetimeoffset" or "time") return TextLiteral(text);
        throw new WorkbenchException($"Type {type} cannot be exported directly. CAST the query column to a supported type.", 422);
    }
    private static string TextLiteral(string text) => "N'" + text.Replace("'", "''") + "'";
    private static string Header(string name) => $"-- SQL Workbench export\n-- Object: {name.Replace("\r", " ").Replace("\n", " ")}\n-- Generated UTC: {DateTimeOffset.UtcNow:O}\n-- Review before executing. This application never applies generated scripts.\n\n";
}
