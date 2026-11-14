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
            ImportProblem problem => (problem.Status, problem.Message),
            RequestFailedException { Status: 409 or 412 } => (409, "The import changed. Reload before continuing."),
            BadHttpRequestException { StatusCode: 413 } => (413, "The request is too large."),
            OperationCanceledException => (504, "The model request timed out. Please try again."),
            HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } =>
                (429, "The model is busy. Please wait before trying again."),
            HttpRequestException or RequestFailedException or InvalidDataException or System.Text.Json.JsonException =>
                (502, "The model service could not complete the request."),
            _ => (500, "The request could not be completed.")
        };
        logger.LogError("Request failed: {ErrorType}; status {Status}", exception.GetType().Name, status);
        context.Response.StatusCode = status;
        var problemDetails = new ProblemDetails { Status = status, Title = title };
        if (exception is ImportProblem importProblem) problemDetails.Detail = importProblem.Message;
        if (exception is ImportDuplicateProblem duplicate) problemDetails.Extensions["duplicates"] = duplicate.Duplicates;
        await context.Response.WriteAsJsonAsync(problemDetails, cancellationToken);
        return true;
    }
}
