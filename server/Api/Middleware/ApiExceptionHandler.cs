using Azure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Examinator.Api.Middleware;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            return false;
        var (status, title) = exception switch
        {
            OperationCanceledException => (504, "The model request timed out. Please try again."),
            HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } =>
                (429, "The model is busy. Please wait before trying again."),
            HttpRequestException or RequestFailedException or InvalidDataException or System.Text.Json.JsonException =>
                (502, "The model service could not complete the request."),
            _ => (500, "The request could not be completed.")
        };
        logger.LogError("Request failed: {ErrorType}; status {Status}", exception.GetType().Name, status);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title },
            cancellationToken);
        return true;
    }
}
