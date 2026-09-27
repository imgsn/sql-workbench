using System.Globalization;
using System.Text.Json;
using Workbench.Models;

namespace Workbench.Services;

public sealed class ComparisonService(SqlDatabaseService database)
{
    public static List<ObjectDifference> CompareSchemas(DatabaseSnapshot source, DatabaseSnapshot target, SchemaCompareInput input)
    {
        if (input.Mode is not ("full" or "names" or "types" or "both")) throw new WorkbenchException("Unknown comparison mode.");
        var left = source.Objects.Where(o => string.IsNullOrEmpty(input.SourceSchema) || o.Schema == input.SourceSchema).ToList();
        var right = target.Objects.Where(o => string.IsNullOrEmpty(input.TargetSchema) || o.Schema == input.TargetSchema).ToList();
        bool mapSchema = !string.IsNullOrEmpty(input.SourceSchema) && !string.IsNullOrEmpty(input.TargetSchema);
        string Key(DatabaseObject o) => o.Kind + "/" + (mapSchema ? o.Name : o.FullName);
        var a = left.ToDictionary(Key, StringComparer.Ordinal); var b = right.ToDictionary(Key, StringComparer.Ordinal);
        var names = a.Keys.Union(b.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal);
        if (input.ObjectIds.Count > 0) names = names.Where(n => a.TryGetValue(n, out var o) && input.ObjectIds.Contains(o.Id)).Order(StringComparer.Ordinal);
        var results = new List<ObjectDifference>();
        foreach (var name in names)
        {
            a.TryGetValue(name, out var x); b.TryGetValue(name, out var y);
            var differences = new List<Difference>();
            if (x != null && y != null)
            {
                foreach (var column in x.Columns.Select(c => c.Name).Union(y.Columns.Select(c => c.Name), StringComparer.Ordinal))
                {
                    var c = x.Columns.Find(c => c.Name == column); var d = y.Columns.Find(c => c.Name == column);
                    if (c == null || d == null) { if (input.Mode != "types") differences.Add(new(column, "Column", c == null ? "Missing" : "Present", d == null ? "Missing" : "Present")); continue; }
                    void Add(string property, object? l, object? r) { if (!Equals(l, r)) differences.Add(new(column, property, l?.ToString(), r?.ToString())); }
                    if (input.Mode != "names") Add("Data type", c.Type, d.Type);
                    if (input.Mode == "full")
                    {
                        Add("Nullable", c.Nullable, d.Nullable); Add("Identity", c.Identity, d.Identity);
                        Add("Computed", c.ComputedDefinition, d.ComputedDefinition); Add("Generated", c.Generated, d.Generated);
                        Add("Primary key", c.PrimaryKey, d.PrimaryKey); Add("Default", c.Default, d.Default); Add("Collation", c.Collation, d.Collation);
                    }
                }
                if (input.Mode == "full") foreach (var property in x.Properties.Keys.Union(y.Properties.Keys))
                {
                    x.Properties.TryGetValue(property, out var l); y.Properties.TryGetValue(property, out var r);
                    if (l != r) differences.Add(new("—", property, l, r));
                }
            }
            results.Add(new((x ?? y)!.FullName, (x ?? y)!.Kind, x == null ? "target" : y == null ? "source" : differences.Count > 0 ? "changed" : "same", differences));
        }
        return results;
    }
    public async Task<DataComparison> CompareDataAsync(string owner, DataCompareInput input, CancellationToken token)
        => (await ReadDataAsync(owner, input, token)).Result;
    public async Task<DataRead> ReadDataAsync(string owner, DataCompareInput input, CancellationToken token)
    {
        if (input.Keys.Count == 0 || input.Columns.Count == 0) throw new WorkbenchException("Select at least one matching key and comparison column.");
        ValidateMappings(input.Keys); ValidateMappings(input.Columns);
        ValidateMappings(input.Keys.Concat(input.Columns).Distinct().ToList());
        var source = await database.SnapshotAsync(owner, input.Source, token);
        var target = await database.SnapshotAsync(owner, input.Target, token);
        var a = source.Objects.Find(o => o.Id == input.SourceTable && o.Kind == "Table") ?? throw new WorkbenchException("Source table not found.");
        var b = target.Objects.Find(o => o.Id == input.TargetTable && o.Kind == "Table") ?? throw new WorkbenchException("Target table not found.");
        var sourceRows = await database.TableRowsAsync(owner, input.Source, a, input.Keys.Concat(input.Columns).Select(c => c.Source), input.SourceFilter, token);
        var targetRows = await database.TableRowsAsync(owner, input.Target, b, input.Keys.Concat(input.Columns).Select(c => c.Target), input.TargetFilter, token);
        var read = new DataRead(target.Database, a, b, sourceRows, targetRows, CompareRows(sourceRows, targetRows, input));
        return read with { Result = read.Result with { Fingerprint = DataDifferenceScriptService.Fingerprint(read, input) } };
    }
    public static DataComparison CompareRows(QueryResult a, QueryResult b, DataCompareInput input)
    {
        int Index(QueryResult result, string name) => result.Columns.FindIndex(c => c.Name == name) is var n && n >= 0 ? n : throw new WorkbenchException("Mapped column not found: " + name);
        object? Normalize(object? value, string type)
        {
            if (value == null) return null;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture)!;
            if (type is "decimal" or "numeric" or "bigint" or "int" or "smallint" or "tinyint" or "money" or "smallmoney")
            {
                // Canonicalize exact numeric strings without reducing precision to double.
                if (text.Contains('.')) text = text.TrimEnd('0').TrimEnd('.');
                return text == "-0" ? "0" : text;
            }
            if (value is string)
            {
                if (input.TrimWhitespace) text = text.Trim();
                if (input.IgnoreCase) text = text.ToUpperInvariant();
                return text;
            }
            return value;
        }
        Dictionary<string, object?[]> Build(QueryResult result, bool left)
        {
            var map = new Dictionary<string, object?[]>(StringComparer.Ordinal);
            foreach (var row in result.Rows)
            {
                var key = input.Keys.Select(k => { var i = Index(result, left ? k.Source : k.Target); return Normalize(row[i], result.Columns[i].Type); }).ToArray();
                if (key.Any(v => v == null)) throw new WorkbenchException("Matching keys contain NULL. Choose non-null unique columns.", 422);
                if (!map.TryAdd(JsonSerializer.Serialize(key), row)) throw new WorkbenchException("Matching keys contain duplicate values under the selected comparison rules. Choose a unique or composite key.", 422);
            }
            return map;
        }
        var leftRows = Build(a, true); var rightRows = Build(b, false); var rows = new List<RowDifference>();
        foreach (var key in leftRows.Keys.Union(rightRows.Keys).Order(StringComparer.Ordinal))
        {
            leftRows.TryGetValue(key, out var left); rightRows.TryGetValue(key, out var right);
            var changed = new List<string>();
            if (left != null && right != null) foreach (var c in input.Columns)
            {
                var ai = Index(a, c.Source); var bi = Index(b, c.Target);
                if (!Equals(Normalize(left[ai], a.Columns[ai].Type), Normalize(right[bi], b.Columns[bi].Type))) changed.Add(c.Source);
            }
            var values = input.Keys.Select(k => left != null ? left[Index(a, k.Source)] : right![Index(b, k.Target)]).ToArray();
            rows.Add(new(values, left == null ? "target" : right == null ? "source" : changed.Count > 0 ? "changed" : "same",
                left == null ? null : input.Columns.Select(c => left[Index(a, c.Source)]).ToArray(),
                right == null ? null : input.Columns.Select(c => right[Index(b, c.Target)]).ToArray(), changed.ToArray(),
                left == null ? null : input.Keys.Select(k => left[Index(a, k.Source)]).ToArray(),
                right == null ? null : input.Keys.Select(k => right[Index(b, k.Target)]).ToArray()));
        }
        return new(input.Columns, rows, a.ReadAt, b.ReadAt);
    }
    public static void ValidateMappings(List<ColumnMapping> mappings)
    {
        if (mappings.Select(c => c.Source).Distinct(StringComparer.Ordinal).Count() != mappings.Count || mappings.Select(c => c.Target).Distinct(StringComparer.Ordinal).Count() != mappings.Count)
            throw new WorkbenchException("Each source and target column can be mapped only once.");
    }
}
