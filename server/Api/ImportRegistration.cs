using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Examinator.Api;

public static class ImportRegistration
{
    public static void AddImports(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Import:Enabled")) return;
        foreach (var key in new[] { "Storage:BlobEndpoint", "Storage:QueueEndpoint", "Storage:TableEndpoint", "Foundry:Endpoint" })
            if (!Uri.TryCreate(configuration[key], UriKind.Absolute, out var uri) || uri.Scheme != "https")
                throw new InvalidOperationException($"Configure a valid HTTPS {key}.");
        if (string.IsNullOrWhiteSpace(configuration["Foundry:ImportDeployment"]))
            throw new InvalidOperationException("Configure the evaluated Foundry:ImportDeployment.");
        if (configuration.GetValue("Import:MaximumModelCalls", 64) is < 1 or > 256)
            throw new InvalidOperationException("Import:MaximumModelCalls must be between 1 and 256.");
        services.AddSingleton<ImportStorage>(provider => new(new Uri(configuration["Storage:BlobEndpoint"]!),
            new Uri(configuration["Storage:QueueEndpoint"]!), new Uri(configuration["Storage:TableEndpoint"]!),
            provider.GetRequiredService<TokenCredential>()));
        services.AddSingleton<IImportPersistence>(provider => provider.GetRequiredService<ImportStorage>());
        services.AddScoped<IExamImports, ExamImports>();
        services.AddHttpClient<IImportModel, ImportModel>(client => client.Timeout = TimeSpan.FromSeconds(120));
        services.AddHttpClient<IPdfDownloader, PdfDownloader>(client => client.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(PdfDownloader.CreateHandler);
        services.AddSingleton<IPdfRenderer, PdfRenderer>();
        services.AddTransient<ImportProcessor>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new ImportProgressStreamOptions());
        services.TryAddSingleton<ImportProgressStreamGate>();
        services.AddTransient<ImportProgressStream>();
    }

    public static async Task RunWorkerAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args.Where(arg => arg is not ("--import-worker" or "--reconcile")).ToArray());
        builder.Services.AddSingleton<TokenCredential>(_ => builder.Environment.IsDevelopment()
            ? new AzureCliCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(
                builder.Configuration["Azure:ManagedIdentityClientId"] ?? throw new InvalidOperationException("Configure the worker identity."))));
        builder.Services.AddTelemetry(builder.Configuration);
        builder.Services.AddImports(builder.Configuration);
        builder.Services.AddSingleton<ImportProgressReporter>();
        using var host = builder.Build();
        await host.StartAsync();
        try { await RunWorkerOnceAsync(host, args); }
        catch (Exception exception)
        {
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ImportWorker")
                .LogCritical(exception, "The import worker stopped with an unhandled {ErrorType}.", exception.GetType().Name);
            throw;
        }
    }

    private static async Task RunWorkerOnceAsync(IHost host, string[] args)
    {
        var storage = host.Services.GetRequiredService<ImportStorage>();
        var ct = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        if (args.Contains("--reconcile"))
        {
            await storage.ReconcileAsync(ct);
            await host.StopAsync();
            return;
        }
        var message = (await storage.Queue.ReceiveMessageAsync(TimeSpan.FromMinutes(5), ct)).Value;
        if (message is null) { await host.StopAsync(); return; }
        var validId = Guid.TryParseExact(message.MessageText, "N", out _);
        if (!validId || message.DequeueCount > ImportProcessor.MaximumProcessingAttempts)
        {
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ImportWorker")
                .LogError("An invalid or repeatedly interrupted dispatch was moved to the poison queue.");
            await storage.Poison.SendMessageAsync(message.MessageText, ct);
            if (validId)
            {
                var record = await storage.ReadAsync(message.MessageText, ct);
                if (record is not null && record.State is "queued" or "extracting" or "aligning" or "tagging")
                    await storage.WriteAsync(record with
                    {
                        State = "failed", Error = "De achtergrondtaak is herhaaldelijk onderbroken. Controleer de infrastructuur voordat u bewust opnieuw verwerkt."
                    }, record.Revision, ct);
            }
            await storage.Queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, ct);
            Environment.ExitCode = 1;
            await host.StopAsync();
            return;
        }
        if (await storage.ReadAsync(message.MessageText, ct) is null)
        {
            await storage.Queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, ct);
            await host.StopAsync();
            return;
        }
        var blob = storage.Lock(message.MessageText);
        try { await blob.UploadAsync(BinaryData.FromString("lock"), overwrite: false, cancellationToken: ct); }
        catch (Azure.RequestFailedException exception) when (exception.Status == 409) { }
        var lease = blob.GetBlobLeaseClient();
        try { await lease.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: ct); }
        catch (Azure.RequestFailedException exception) when (exception.Status == 409) { await host.StopAsync(); return; }
        using var renewCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receipt = message.PopReceipt;
        var progress = host.Services.GetRequiredService<ImportProgressReporter>();
        var renew = Task.Run(async () =>
        {
            try
            {
                while (!renewCancellation.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(25), renewCancellation.Token);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(renewCancellation.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    await lease.RenewAsync(cancellationToken: deadline.Token);
                    receipt = (await storage.Queue.UpdateMessageAsync(message.MessageId, receipt, message.MessageText,
                        TimeSpan.FromMinutes(5), deadline.Token)).Value.PopReceipt;
                    await progress.HeartbeatAsync(deadline.Token);
                }
            }
            catch (Exception)
            {
                processingCancellation.Cancel();
                throw;
            }
        }, CancellationToken.None);
        try
        {
            await host.Services.GetRequiredService<ImportProcessor>().ProcessAsync(message.MessageText, processingCancellation.Token);
            renewCancellation.Cancel();
            try { await renew; } catch (OperationCanceledException) when (renewCancellation.IsCancellationRequested) { }
            if ((await storage.ReadAsync(message.MessageText, ct))?.State == "failed")
            {
                await storage.Poison.SendMessageAsync(message.MessageText, ct);
                Environment.ExitCode = 1;
            }
            await storage.Queue.DeleteMessageAsync(message.MessageId, receipt, ct);
        }
        finally
        {
            renewCancellation.Cancel();
            try
            {
                try { await renew; } catch (OperationCanceledException) when (renewCancellation.IsCancellationRequested) { }
            }
            finally
            {
                try { await lease.ReleaseAsync(cancellationToken: CancellationToken.None); }
                finally { await host.StopAsync(); }
            }
        }
    }
}
