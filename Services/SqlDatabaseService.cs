using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Workbench.Models;

namespace Workbench.Services;

public sealed class SqlDatabaseService(ConnectionVault vault, IOptions<WorkbenchOptions> settings)
{
    private readonly WorkbenchOptions options = settings.Value;
    public async Task<string> TestAsync(string connectionString, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        using var command = new SqlCommand("SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')", connection);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) != 1)
            throw new WorkbenchException("The login needs VIEW DEFINITION on this database to explore and script its schema. Grant SELECT on the tables you want to compare or export.", 403);
        return connection.ServerVersion;
    }
    public async Task<DatabaseSnapshot> SnapshotAsync(string owner, string id, CancellationToken token)
    {
        await using var connection = new SqlConnection(vault.Get(owner, id));
        await connection.OpenAsync(token);
        var major = int.Parse(connection.ServerVersion.Split('.')[0], CultureInfo.InvariantCulture);
        if (major < 10) throw new WorkbenchException("This SQL Server version is older than the supported metadata catalog baseline (SQL Server 2008).", 422);
        using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.CommandText = """
            SELECT o.object_id, s.name, o.name, o.type
            FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
            WHERE o.is_ms_shipped=0 AND o.type IN ('U','V','P','FN','IF','TF','TR')
            ORDER BY s.name,o.name;
            """;
        var objects = new Dictionary<int, DatabaseObject>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) objects[reader.GetInt32(0)] = new() {
                Id = reader.GetInt32(0), Schema = reader.GetString(1), Name = reader.GetString(2),
                Kind = reader.GetString(3).Trim() switch { "U" => "Table", "V" => "View", "P" => "Procedure", "TR" => "Trigger", _ => "Function" }
            };
        command.CommandText = """
            SELECT c.object_id,c.name,t.name,c.max_length,c.precision,c.scale,c.is_nullable,c.is_identity,c.is_computed,
            ISNULL(d.definition,''),ISNULL(c.collation_name,''),ISNULL(cc.definition,''),
            CASE WHEN EXISTS(SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id=ic.object_id AND i.index_id=ic.index_id WHERE ic.object_id=c.object_id AND ic.column_id=c.column_id AND i.is_primary_key=1) THEN 1 ELSE 0 END,
            t.is_user_defined,SCHEMA_NAME(t.schema_id),
            """ + (major >= 13 ? "c.generated_always_type" : "0") + """
             FROM sys.columns c JOIN sys.types t ON c.user_type_id=t.user_type_id
            LEFT JOIN sys.default_constraints d ON c.default_object_id=d.object_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
            ORDER BY c.object_id,c.column_id;
            """;
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                if (!objects.TryGetValue(reader.GetInt32(0), out var obj)) continue;
                var type = reader.GetString(2);
                if (reader.GetBoolean(13)) type = SelectValidator.Identifier(reader.GetString(14)) + "." + SelectValidator.Identifier(type);
                else type = FormatType(type, reader.GetInt16(3), reader.GetByte(4), reader.GetByte(5));
                obj.Columns.Add(new(reader.GetString(1), type, reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8),
                    Convert.ToInt32(reader.GetValue(15)) != 0 || type is "timestamp" or "rowversion", reader.GetInt32(12) == 1,
                    reader.GetString(9), reader.GetString(10), reader.GetString(11)));
            }
        command.CommandText = """
            SELECT i.object_id,'Index/'+i.name COLLATE DATABASE_DEFAULT+'/'+CONVERT(nvarchar(10),ic.index_column_id),
            i.type_desc COLLATE DATABASE_DEFAULT+'; unique='+CONVERT(nvarchar(1),i.is_unique)+'; primary='+CONVERT(nvarchar(1),i.is_primary_key)+'; column='+c.name+'; descending='+CONVERT(nvarchar(1),ic.is_descending_key)+'; included='+CONVERT(nvarchar(1),ic.is_included_column)+'; filter='+ISNULL(i.filter_definition,'')
            FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
            JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE i.name IS NOT NULL
            UNION ALL
            SELECT f.parent_object_id,'Foreign key/'+f.name+'/'+CONVERT(nvarchar(10),fc.constraint_column_id),
            COL_NAME(fc.parent_object_id,fc.parent_column_id) COLLATE DATABASE_DEFAULT+' -> '+OBJECT_SCHEMA_NAME(fc.referenced_object_id)+'.'+OBJECT_NAME(fc.referenced_object_id)+'.'+COL_NAME(fc.referenced_object_id,fc.referenced_column_id)+'; delete='+f.delete_referential_action_desc+'; update='+f.update_referential_action_desc+'; disabled='+CONVERT(nvarchar(1),f.is_disabled)+'; untrusted='+CONVERT(nvarchar(1),f.is_not_trusted)
            FROM sys.foreign_keys f JOIN sys.foreign_key_columns fc ON fc.constraint_object_id=f.object_id
            UNION ALL
            SELECT parent_object_id,'Check/'+name,definition+'; disabled='+CONVERT(nvarchar(1),is_disabled)+'; untrusted='+CONVERT(nvarchar(1),is_not_trusted) FROM sys.check_constraints
            UNION ALL
            SELECT object_id,'Identity/'+name,CONVERT(nvarchar(100),seed_value)+'; increment='+CONVERT(nvarchar(100),increment_value) FROM sys.identity_columns
            UNION ALL
            SELECT object_id,'Definition',ISNULL(definition,'[Encrypted or inaccessible]') FROM sys.sql_modules;
            """;
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                if (objects.TryGetValue(reader.GetInt32(0), out var obj)) obj.Properties[reader.GetString(1)] = reader.IsDBNull(2) ? "[Unavailable to this login]" : reader.GetString(2);
        return new(connection.Database, connection.ServerVersion, DateTimeOffset.UtcNow, objects.Values.ToList(),
            ["Results reflect metadata visible to this login. Unavailable definitions cannot be verified. Logins, jobs, permissions, and server-level objects are outside the comparison scope."]);
    }
    public async Task<QueryResult> QueryAsync(string owner, string id, string query, CancellationToken token)
    {
        SelectValidator.Validate(query);
        await using var connection = new SqlConnection(vault.Get(owner, id));
        await connection.OpenAsync(token);
        using var command = new SqlCommand(query, connection) { CommandTimeout = options.CommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, token);
        var columns = Enumerable.Range(0, reader.FieldCount).Select(i => new ResultColumn(reader.GetName(i), reader.GetDataTypeName(i))).ToList();
        if (columns.Any(c => string.IsNullOrWhiteSpace(c.Name)) || columns.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != columns.Count)
            throw new WorkbenchException("Every result column needs a unique name. Add aliases to expressions or duplicate column names.");
        var rows = new List<object?[]>();
        long bytes = 0;
        while (await reader.ReadAsync(token))
        {
            if (rows.Count >= options.MaxRows) throw new WorkbenchException($"The result exceeds {options.MaxRows:N0} rows. Add a WHERE filter or TOP limit; no partial result was returned.", 422);
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
            {
                var type = columns[i].Type.ToLowerInvariant();
                if (await reader.IsDBNullAsync(i, token)) values[i] = null;
                else if (type is "decimal" or "numeric") values[i] = reader.GetSqlDecimal(i).ToString();
                else if (type is "bigint") values[i] = reader.GetInt64(i).ToString(CultureInfo.InvariantCulture);
                else values[i] = reader.GetValue(i) switch {
                    byte[] binary => "0x" + Convert.ToHexString(binary),
                    DateTime date => date.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                    DateTimeOffset date => date.ToString("o", CultureInfo.InvariantCulture),
                    TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
                    Guid guid => guid.ToString(),
                    string text => text,
                    bool bit => bit,
                    short or int or byte or float or double or decimal => reader.GetValue(i),
                    _ => throw new WorkbenchException($"Column '{columns[i].Name}' uses an unsupported result type ({type}). CAST it to a supported SQL type.", 422)
                };
                bytes += (values[i]?.ToString()?.Length ?? 4) * 2L;
                if (bytes > 8 * 1024 * 1024) throw new WorkbenchException("The result exceeds the 8 MB preview limit. Select fewer columns or rows.", 422);
            }
            rows.Add(values);
        }
        return new(columns, rows, DateTimeOffset.UtcNow);
    }
    public async Task<QueryResult> TableRowsAsync(string owner, string connection, DatabaseObject table, IEnumerable<string> names, string? filter, CancellationToken token)
    {
        var selected = names.Distinct(StringComparer.Ordinal).ToList();
        if (selected.Count == 0 || selected.Any(n => table.Columns.All(c => c.Name != n))) throw new WorkbenchException("Select valid table columns.");
        var query = $"SELECT {string.Join(", ", selected.Select(SelectValidator.Identifier))} FROM {SelectValidator.Qualified(table)}";
        if (!string.IsNullOrWhiteSpace(filter)) query += " WHERE " + filter;
        return await QueryAsync(owner, connection, query, token);
    }
    private static string FormatType(string type, short length, byte precision, byte scale) => type switch {
        "nvarchar" or "nchar" => $"{type}({(length == -1 ? "max" : (length / 2).ToString())})",
        "varchar" or "char" or "binary" or "varbinary" => $"{type}({(length == -1 ? "max" : length.ToString())})",
        "decimal" or "numeric" => $"{type}({precision},{scale})",
        "datetime2" or "datetimeoffset" or "time" => $"{type}({scale})", _ => type
    };
}
