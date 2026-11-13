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

public static class ImportGatewayEndpoints
{
    private const long MaxUploadBytes = 46L * 1024 * 1024;
    private const long MaxJsonBytes = 4L * 1024 * 1024;

    public static void MapImportGateway(this WebApplication app, GatewaySettings gateway)
    {
        var group = app.MapGroup("/api");
        foreach (var path in new[] { "/subjects", "/catalog", "/documents/{id}", "/imports", "/imports/{id}",
            "/topics", "/exams", "/questions", "/exams/{id}/documents" })
            group.MapGet(path, Forward);
        group.MapPost("/imports", (HttpContext context, IImportGateway proxy) => Mutation(context, proxy, gateway, MaxUploadBytes));
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
