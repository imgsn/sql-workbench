namespace Workbench.Models;

public record QueryExecutionResult(List<QueryResult> Results, int AffectedRows, List<string> Messages,
    bool Truncated, long ElapsedMilliseconds, string? Error);
