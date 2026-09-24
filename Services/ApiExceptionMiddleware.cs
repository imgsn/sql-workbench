using Microsoft.Data.SqlClient;
using Workbench.Models;

namespace Workbench.Services;

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (Exception error) when (context.Request.Path.StartsWithSegments("/api"))
        {
            if (context.RequestAborted.IsCancellationRequested) return;
            var (status, message) = error switch {
                WorkbenchException known => (known.Status, known.Message),
                SqlException sql when sql.Number == -2 => (408, "SQL Server timed out. Use a smaller scope or filter and try again."),
                SqlException sql when sql.Number == 18456 => (400, "SQL Server rejected the login. Check the username and password."),
                SqlException sql when sql.Number is 229 or 230 or 916 => (403, "The SQL login does not have permission for this operation. It needs SELECT and VIEW DEFINITION on the relevant objects."),
                SqlException => (422, "SQL Server could not complete the request. Check server reachability, credentials, certificate trust, object names, and query syntax."),
                OperationCanceledException => (408, "The operation was cancelled or timed out."),
                _ => (500, "The operation could not be completed. The server version, object type, or application database configuration may be unsupported. See the request ID for troubleshooting.")
            };
            // Do not log exception bodies, query text, or connection-string values.
            logger.LogWarning("Request {RequestId} failed with {ErrorType}; status {Status}", context.TraceIdentifier, error.GetType().Name, status);
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new { title = message, status, requestId = context.TraceIdentifier });
        }
    }
}
