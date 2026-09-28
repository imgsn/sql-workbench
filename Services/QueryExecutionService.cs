using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Workbench.Models;

namespace Workbench.Services;

public sealed class QueryExecutionService(ConnectionVault vault, IOptions<WorkbenchOptions> settings)
{
    public async Task<QueryExecutionResult> ExecuteAsync(string owner, QueryInput input, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(input.Query) || input.Query.Length > 50000)
            throw new WorkbenchException("Enter SQL of at most 50,000 characters.");
        // Each execution has its own connection; transaction/session state never carries to another run.
        var builder = new SqlConnectionStringBuilder(vault.Get(owner, input.Connection)) { Pooling = false };
        await using var connection = new SqlConnection(builder.ConnectionString);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.Value.CommandTimeoutSeconds));
        var watch = Stopwatch.StartNew();
        var results = new List<QueryResult>();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => { if (messages.Count < 100) messages.Add(e.Message[..Math.Min(e.Message.Length, 4000)]); };
        await connection.OpenAsync(deadline.Token);
        using var command = new SqlCommand(input.Query, connection) { CommandTimeout = settings.Value.CommandTimeoutSeconds };
        long bytes = 0;
        var totalRows = 0;
        var truncated = false;
        var affected = -1;
        string? error = null;
        try
        {
            await using var reader = await command.ExecuteReaderAsync(deadline.Token);
            do
            {
                if (reader.FieldCount == 0) continue;
                var keep = results.Count < 10;
                var columns = Enumerable.Range(0, reader.FieldCount)
                    .Select(i => new ResultColumn(string.IsNullOrEmpty(reader.GetName(i)) ? $"Column {i + 1}" : reader.GetName(i), reader.GetDataTypeName(i))).ToList();
                var rows = new List<object?[]>();
                if (keep) results.Add(new(columns, rows, DateTimeOffset.UtcNow));
                else truncated = true;
                while (await reader.ReadAsync(deadline.Token))
                {
                    if (!keep || totalRows >= settings.Value.MaxRows || bytes >= 8 * 1024 * 1024) { truncated = true; continue; }
                    var row = new object?[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++)
                    {
                        if (await reader.IsDBNullAsync(i, deadline.Token)) continue;
                        var type = columns[i].Type.ToLowerInvariant();
                        if (type is "nvarchar" or "varchar" or "nchar" or "char" or "text" or "ntext")
                        {
                            var length = reader.GetChars(i, 0, null, 0, 0);
                            var chars = new char[(int)Math.Min(length, 8192)];
                            reader.GetChars(i, 0, chars, 0, chars.Length);
                            row[i] = new string(chars) + (length > chars.Length ? "… [truncated]" : "");
                            truncated |= length > chars.Length;
                        }
                        else if (type is "varbinary" or "binary" or "image" or "timestamp" or "rowversion")
                        {
                            var length = reader.GetBytes(i, 0, null, 0, 0);
                            var buffer = new byte[(int)Math.Min(length, 4096)];
                            reader.GetBytes(i, 0, buffer, 0, buffer.Length);
                            row[i] = "0x" + Convert.ToHexString(buffer) + (length > buffer.Length ? "… [truncated]" : "");
                            truncated |= length > buffer.Length;
                        }
                        else row[i] = type is "decimal" or "numeric" ? reader.GetSqlDecimal(i).ToString() : reader.GetValue(i) switch
                        {
                            byte[] binary => "0x" + Convert.ToHexString(binary),
                            DateTime date => date.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                            DateTimeOffset date => date.ToString("o", CultureInfo.InvariantCulture),
                            bool bit => bit,
                            _ => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)
                        };
                        bytes += (row[i]?.ToString()?.Length ?? 4) * 2L;
                    }
                    if (bytes > 8 * 1024 * 1024) { truncated = true; continue; }
                    rows.Add(row);
                    totalRows++;
                }
            } while (await reader.NextResultAsync(deadline.Token));
            affected = reader.RecordsAffected;
        }
        catch (SqlException e)
        {
            error = e.Number == -2 ? "Query timed out." : $"SQL {e.Number}, line {e.LineNumber}: {e.Message}";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            error = "Query timed out.";
        }
        if (error != null) messages.Add("Execution stopped. Earlier statements may have committed; check database state before running again.");
        return new(results, affected, messages, truncated, watch.ElapsedMilliseconds, error);
    }
}
