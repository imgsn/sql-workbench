using System.ComponentModel.DataAnnotations;

namespace Workbench.Models;

public sealed class ConnectionInput
{
    [Required, StringLength(80)] public string Name { get; set; } = "";
    [StringLength(8192)] public string? ConnectionString { get; set; }
    [StringLength(256)] public string? Server { get; set; }
    [StringLength(128)] public string? Database { get; set; }
    [StringLength(128)] public string? Username { get; set; }
    [StringLength(256)] public string? Password { get; set; }
    public bool IntegratedSecurity { get; set; }
    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; }
    [StringLength(30)] public string Environment { get; set; } = "Development";
    public bool Remember { get; set; }
}
public record ConnectionInfo(string Id, string Name, string Server, string Database, string Environment, string Version, DateTimeOffset ExpiresAt,
    string? RememberToken = null, DateTimeOffset? RememberExpiresAt = null);
public sealed class RestoreConnectionInput
{
    [Required, StringLength(16384)] public string Token { get; set; } = "";
}
public record RememberedConnection(string Name, string Environment, string ConnectionString);
public record DatabaseColumn(string Name, string Type, bool Nullable, bool Identity, bool Computed, bool Generated, bool PrimaryKey, string? Default, string? Collation, string? ComputedDefinition);
public sealed class DatabaseObject
{
    public int Id { get; set; }
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string FullName => Schema + "." + Name;
    public List<DatabaseColumn> Columns { get; set; } = [];
    public SortedDictionary<string, string> Properties { get; set; } = new(StringComparer.Ordinal);
}
public record DatabaseSnapshot(string Database, string Version, DateTimeOffset ReadAt, List<DatabaseObject> Objects, string[] Warnings);
public record Difference(string Column, string Property, string? Source, string? Target);
public record ObjectDifference(string Name, string Kind, string Status, List<Difference> Differences);
public sealed class SchemaCompareInput
{
    [Required] public string Source { get; set; } = "";
    [Required] public string Target { get; set; } = "";
    public string Mode { get; set; } = "full";
    public string? SourceSchema { get; set; }
    public string? TargetSchema { get; set; }
    public List<int> ObjectIds { get; set; } = [];
}
public record ColumnMapping(string Source, string Target);
public sealed class DataCompareInput
{
    [Required] public string Source { get; set; } = "";
    [Required] public string Target { get; set; } = "";
    public int SourceTable { get; set; }
    public int TargetTable { get; set; }
    public List<ColumnMapping> Keys { get; set; } = [];
    public List<ColumnMapping> Columns { get; set; } = [];
    public string? SourceFilter { get; set; }
    public string? TargetFilter { get; set; }
    public bool IgnoreCase { get; set; }
    public bool TrimWhitespace { get; set; }
}
public record ResultColumn(string Name, string Type);
public record QueryResult(List<ResultColumn> Columns, List<object?[]> Rows, DateTimeOffset ReadAt);
public record RowDifference(object?[] Key, string Status, object?[]? Source, object?[]? Target, string[] ChangedColumns, object?[]? SourceKey = null, object?[]? TargetKey = null);
public record DataComparison(List<ColumnMapping> Columns, List<RowDifference> Rows, DateTimeOffset SourceReadAt, DateTimeOffset TargetReadAt, string? Fingerprint = null);
public record DataRead(string TargetDatabase, DatabaseObject SourceTable, DatabaseObject TargetTable, QueryResult SourceRows, QueryResult TargetRows, DataComparison Result);
public sealed class DataDifferenceScriptInput
{
    [Required] public DataCompareInput Comparison { get; set; } = new();
    [Required] public string Fingerprint { get; set; } = "";
    public List<int> SelectedRows { get; set; } = [];
    public bool IncludeIdentity { get; set; }
}
public sealed class SchemaScriptInput
{
    [Required] public string Connection { get; set; } = "";
    public List<int> ObjectIds { get; set; } = [];
    public bool IncludeKeys { get; set; } = true;
    public bool IncludeIndexes { get; set; } = true;
    public bool IncludeTriggers { get; set; } = true;
    public bool IncludeDependencies { get; set; } = true;
    public bool AddGo { get; set; } = true;
}
public sealed class InsertInput
{
    [Required] public string Connection { get; set; } = "";
    [Required, StringLength(50000)] public string Query { get; set; } = "";
    // Destination metadata is resolved in the selected connection, never guessed.
    public int DestinationTable { get; set; }
    public List<ColumnMapping> Columns { get; set; } = [];
    public bool IncludeIdentity { get; set; }
    public bool Transaction { get; set; } = true;
    [Range(1, 1000)] public int BatchSize { get; set; } = 100;
}
public record ScriptResult(string Sql, string Filename, int ObjectCount, string[] Warnings);
public sealed class SchemaMigrationInput
{
    [Required] public SchemaCompareInput Comparison { get; set; } = new();
    [Required] public string Fingerprint { get; set; } = "";
    public List<int> SelectedObjects { get; set; } = [];
}
public record MigrationStep(string Object, string Change, string Risk, string Sql, string Note);
public record MigrationResult(string Sql, string Filename, int ObjectCount, string[] Warnings, List<MigrationStep> Steps);
public sealed class QueryInput
{
    [Required] public string Connection { get; set; } = "";
    [Required, StringLength(50000)] public string Query { get; set; } = "";
}
public sealed class WorkbenchException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}
public sealed class WorkbenchOptions
{
    public int MaxRows { get; set; } = 5000;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int ConnectionLifetimeMinutes { get; set; } = 120;
    public int RememberedConnectionDays { get; set; } = 30;
    public string[] AllowedServers { get; set; } = [];
    public bool AllowWindowsAuthentication { get; set; } = true;
}
