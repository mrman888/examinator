using System.Net;
using System.Net.Http.Headers;
using Azure.Core;
using Microsoft.AspNetCore.Http.Features;

namespace Examinator.Web;

public interface IImportGateway
{
    Task ForwardAsync(HttpContext context, CancellationToken cancellationToken);
}

public sealed class ImportGateway(HttpClient client, TokenCredential credential, GatewaySettings settings) : IImportGateway
{
    public async Task ForwardAsync(HttpContext context, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        var uri = new Uri(settings.ApiBaseUrl, context.Request.Path + context.Request.QueryString);
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), uri);
        if (!settings.LocalAnonymous)
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext([$"api://{settings.ApiClientId}/.default"]), cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        if (context.Request.Headers.TryGetValue("Range", out var range))
            request.Headers.TryAddWithoutValidation("Range", (IEnumerable<string>)range!);
        if (context.Request.Method is "POST" or "PATCH")
        {
            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is { } contentType)
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            if (context.Request.ContentLength is { } contentLength)
                request.Content.Headers.ContentLength = contentLength;
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException("Unexpected API redirect.");
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in new[] { "Accept-Ranges", "Content-Range", "Content-Type", "Content-Length", "ETag", "Last-Modified", "Location" })
        {
            if (response.Headers.TryGetValues(header, out var values) ||
                response.Content.Headers.TryGetValues(header, out values))
                context.Response.Headers[header] = values.ToArray();
        }
        await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }

    public static HttpMessageHandler CreateHandler() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None
    };
}

public interface IImportEventsGateway
{
    Task ForwardAsync(HttpContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Streams <c>GET /api/imports/events</c> (server-sent events) from the private Api. It uses its own HttpClient
/// without a timeout, flushes every read immediately, and treats a browser disconnect as a normal end.
/// </summary>
public sealed class ImportEventsGateway(HttpClient client, TokenCredential credential, GatewaySettings settings,
    ILogger<ImportEventsGateway> logger) : IImportEventsGateway
{
    public const string Path = "/api/imports/events";

    public async Task ForwardAsync(HttpContext context, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(settings.ApiBaseUrl, Path));
        if (!settings.LocalAnonymous)
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext([$"api://{settings.ApiClientId}/.default"]), cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (context.Request.Headers.TryGetValue("Last-Event-ID", out var lastEventId))
            request.Headers.TryAddWithoutValidation("Last-Event-ID", (IEnumerable<string>)lastEventId!);
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        using (response)
        {
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new HttpRequestException("Unexpected API redirect.");
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            {
                var status = (int)response.StatusCode is >= 400 and < 600 ? (int)response.StatusCode : 502;
                await Results.Problem(statusCode: status, title: "Live import progress is unavailable.").ExecuteAsync(context);
                return;
            }
            context.Response.StatusCode = StatusCodes.Status200OK;
            foreach (var header in new[] { "Content-Type", "Cache-Control", "X-Accel-Buffering" })
            {
                if (response.Headers.TryGetValues(header, out var values) ||
                    response.Content.Headers.TryGetValues(header, out values))
                    context.Response.Headers[header] = values.ToArray();
            }
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            try
            {
                await context.Response.StartAsync(cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                await using var upstream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[4096];
                int read;
                while ((read = await upstream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException &&
                cancellationToken.IsCancellationRequested) { }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                // Headers are already sent, so the stream just ends; EventSource reconnects.
                logger.LogInformation("The upstream progress stream ended with {ErrorType}.", exception.GetType().Name);
            }
        }
    }
}

public static class ImportGatewayEndpoints
{
    private const long MaxUploadBytes = 46L * 1024 * 1024;
    private const long MaxReprocessBytes = 10L * 1024 * 1024;
    private const long MaxJsonBytes = 4L * 1024 * 1024;

    public static void MapImportGateway(this WebApplication app, GatewaySettings gateway)
    {
        var group = app.MapGroup("/api");
        foreach (var path in new[] { "/subjects", "/catalog", "/documents/{id}", "/imports", "/imports/{id}",
            "/topics", "/exams", "/questions", "/exams/{id}/documents" })
            group.MapGet(path, Forward);
        // EventSource cannot send custom headers, so this read-only stream has no CSRF header check.
        group.MapGet("/imports/events", (HttpContext context, IImportEventsGateway proxy) =>
            proxy.ForwardAsync(context, context.RequestAborted));
        group.MapPost("/imports", (HttpContext context, IImportGateway proxy) => Mutation(context, proxy, gateway, MaxUploadBytes));
        group.MapPost("/imports/{id}/reprocess", (HttpContext context, IImportGateway proxy) => Mutation(context, proxy, gateway, MaxReprocessBytes));
        group.MapDelete("/imports/{id}", (HttpContext context, IImportGateway proxy) => Mutation(context, proxy, gateway, 1024));
        group.MapPatch("/imports/{id}/draft", (HttpContext context, IImportGateway proxy) => Mutation(context, proxy, gateway, MaxJsonBytes));
        foreach (var action in new[] { "confirm-definitions", "publish", "cancel", "retry" })
            group.MapPost($"/imports/{{id}}/{action}", (HttpContext context, IImportGateway proxy) => Mutation(context, proxy, gateway, MaxJsonBytes));
    }

    private static Task Forward(HttpContext context, IImportGateway proxy) =>
        proxy.ForwardAsync(context, context.RequestAborted);

    private static async Task Mutation(HttpContext context, IImportGateway proxy, GatewaySettings settings, long limit)
    {
        if (context.Request.Headers["X-Examinator-Request"] != "1" ||
            (!settings.LocalAnonymous && context.Request.Headers.Origin != settings.PublicOrigin))
        {
            await Results.Problem(statusCode: 403, title: "A same-origin website request is required.").ExecuteAsync(context);
            return;
        }
        if (context.Request.ContentLength > limit)
        {
            await Results.Problem(statusCode: 413, title: "The request is too large.").ExecuteAsync(context);
            return;
        }
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;
        await proxy.ForwardAsync(context, context.RequestAborted);
    }
}
