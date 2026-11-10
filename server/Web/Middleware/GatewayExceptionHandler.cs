using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Examinator.Web.Middleware;

public sealed class GatewayExceptionHandler(ILogger<GatewayExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            return false;
        var status = exception is OperationCanceledException ? 504 : 502;
        logger.LogError("Gateway failed: {ErrorType}; status {Status}", exception.GetType().Name, status);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = status == 504 ? "The request timed out. Please try again." : "The backend request could not be completed."
        }, cancellationToken);
        return true;
    }
}
