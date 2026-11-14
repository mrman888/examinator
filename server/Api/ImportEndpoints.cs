using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Examinator.Api;

public static class ImportEndpoints
{
    public static void MapImports(this WebApplication app, bool localAnonymous)
    {
        var group = app.MapGroup("/api");
        if (!localAnonymous) group.RequireAuthorization("WebsiteGateway");
        group.MapGet("/subjects", () => TypedResults.Ok(new[] { new SubjectResponse("biology", "Biologie") }))
            .WithSummary("List enabled exam subjects");
        if (!app.Configuration.GetValue<bool>("Import:Enabled"))
        {
            foreach (var path in new[] { "/catalog", "/documents/{id}", "/imports", "/imports/events", "/imports/{id}", "/topics", "/exams", "/questions", "/exams/{id}/documents" })
                group.MapGet(path, Unavailable);
            group.MapPost("/imports", Unavailable);
            group.MapDelete("/imports/{id}", Unavailable);
            group.MapPatch("/imports/{id}/draft", Unavailable);
            foreach (var action in new[] { "confirm-definitions", "publish", "cancel", "retry", "reprocess" })
                group.MapPost($"/imports/{{id}}/{action}", Unavailable);
            return;
        }
        group.MapGet("/imports", async (IExamImports service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(ct))).WithSummary("List durable exam imports");
        group.MapGet("/imports/events", StreamProgressAsync)
            .WithSummary("Server-sent live progress (progress, settled, ping) for all processing imports");
        group.MapGet("/imports/{id}", async (string id, IExamImports service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetAsync(id, ct))).WithSummary("Get import progress and review draft");
        group.MapPost("/imports", async Task<IResult> (HttpContext context, IExamImports service, CancellationToken ct) =>
        {
            SetBodyLimit(context, 46L * 1024 * 1024);
            if (!context.Request.HasFormContentType) throw new ImportProblem(415, "Upload de documenten als multipart/form-data.");
            var record = await service.CreateAsync(await context.Request.ReadFormAsync(ct), ct);
            return TypedResults.Accepted($"/api/imports/{record.Id}", record);
        }).WithSummary("Store exam/correction PDFs and enqueue import").DisableAntiforgery();
        group.MapPost("/imports/{id}/reprocess", async Task<IResult> (string id, HttpContext context, IExamImports service, CancellationToken ct) =>
        {
            SetBodyLimit(context, 10L * 1024 * 1024);
            if (!context.Request.HasFormContentType) throw new ImportProblem(415, "Stuur de onderwerpen als multipart/form-data.");
            var record = await service.ReprocessAsync(id, await context.Request.ReadFormAsync(ct), ct);
            return TypedResults.Accepted($"/api/imports/{record.Id}", record);
        }).WithSummary("Start a new import job from the stored originals of an existing import").DisableAntiforgery();
        group.MapPatch("/imports/{id}/draft", async (string id, DraftUpdate update, IExamImports service, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(id, "draft", update, ct))).WithSummary("Save a revision-checked review draft")
            .WithMetadata(new RequestSizeLimitAttribute(4 * 1024 * 1024));
        group.MapPost("/imports/{id}/confirm-definitions", async (string id, DefinitionsUpdate update, IExamImports service, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(id, "confirm-definitions", update, ct))).WithSummary("Approve topic definitions and resume import")
            .WithMetadata(new RequestSizeLimitAttribute(4 * 1024 * 1024));
        foreach (var action in new[] { "publish", "cancel", "retry" })
        {
            var name = action;
            group.MapPost($"/imports/{{id}}/{name}", async (string id, RevisionUpdate update, IExamImports service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateAsync(id, name, update, ct))).WithSummary($"Revision-checked import action: {name}");
        }
        group.MapDelete("/imports/{id}", async Task<IResult> (string id, IExamImports service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return TypedResults.NoContent();
        }).WithSummary("Delete an unpublished, idle import with its data and unshared originals");
        group.MapGet("/documents/{id}", async Task<IResult> (string id, IExamImports service, CancellationToken ct) =>
        {
            var document = await service.DocumentAsync(id, ct);
            if (document is null) return TypedResults.NotFound();
            return TypedResults.Stream(document.Stream, document.ContentType, lastModified: document.LastModified,
                entityTag: new EntityTagHeaderValue(document.ETag.ToString()), enableRangeProcessing: true);
        }).WithSummary("Stream an authorized source document with HTTP Range support");
        group.MapGet("/catalog", async (string? subjectId, IExamImports service, CancellationToken ct) =>
            TypedResults.Ok(await service.CatalogAsync(subjectId ?? "biology", ct))).WithSummary("Get published subject-scoped trainer catalog");
        group.MapGet("/topics", async (string? subjectId, string? search, IExamImports service, CancellationToken ct) =>
        {
            var catalog = await service.CatalogAsync(subjectId ?? "biology", ct);
            return TypedResults.Ok(catalog.Topics.Where(t => string.IsNullOrWhiteSpace(search) ||
                t.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                t.Aliases.Any(a => a.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray());
        }).WithSummary("Find independent topics across chapters");
        group.MapGet("/exams", async (string? subjectId, string? level, int? year, int? timevak, IExamImports service, CancellationToken ct) =>
        {
            var catalog = await service.CatalogAsync(subjectId ?? "biology", ct);
            return TypedResults.Ok(catalog.Exams.Where(e => (level is null || e.Level == level) &&
                (year is null || e.Year == year) && (timevak is null || e.Timevak == timevak)).ToArray());
        }).WithSummary("Find published exam/document pairs by subject and sitting");
        group.MapGet("/questions", async (string? subjectId, string topicId, IExamImports service, CancellationToken ct) =>
            TypedResults.Ok((await service.CatalogAsync(subjectId ?? "biology", ct)).Questions.Where(q => q.Topics.Contains(topicId)).ToArray()))
            .WithSummary("Find tagged questions across published exams");
        group.MapGet("/exams/{id}/documents", async Task<IResult> (string id, IExamImports service, CancellationToken ct) =>
        {
            var catalog = await service.CatalogAsync("biology", ct);
            var exam = catalog.Exams.SingleOrDefault(e => e.Id == id);
            if (exam is null) return TypedResults.NotFound();
            return TypedResults.Ok(exam);
        }).WithSummary("Get a published exam's verified document pair");
    }

    public static void SetBodyLimit(HttpContext context, long limit)
    {
        if (context.Request.ContentLength > limit) throw new ImportProblem(413, "Het verzoek is te groot.");
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;
    }

    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Hand-written SSE: named events only (EventSource cannot observe comments), one flush per event, and a capped
    /// lifetime after which the browser reconnects using the <c>retry</c> delay.
    /// </summary>
    private static async Task StreamProgressAsync(HttpContext context, [FromServices] ImportProgressStream stream,
        [FromServices] ImportProgressStreamGate gate, [FromServices] ILogger<ImportProgressStream> logger)
    {
        if (!gate.TryEnter())
        {
            await TypedResults.Problem(statusCode: 429, title: "Too many progress streams.",
                detail: "Er zijn te veel live voortgangsverbindingen open. Sluit andere tabbladen en probeer het opnieuw.").ExecuteAsync(context);
            return;
        }
        try
        {
            var response = context.Response;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.Headers.CacheControl = "no-cache, no-transform";
            response.Headers["X-Accel-Buffering"] = "no";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            var abort = context.RequestAborted;
            await response.WriteAsync("retry: 3000\n\n", abort);
            await response.Body.FlushAsync(abort);
            await stream.RunAsync(async (name, payload, ct) =>
            {
                await response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(payload, payload.GetType(), EventJson)}\n\n", ct);
                await response.Body.FlushAsync(ct);
            }, abort);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException && context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("A progress stream closed because the client disconnected.");
        }
        finally { gate.Exit(); }
    }

    private static IResult Unavailable() => TypedResults.Problem(statusCode: 503,
        title: "Exam import is unavailable.", detail: "Examenimport is nog niet ingeschakeld of de Azure-opslag is niet geconfigureerd.");
}
