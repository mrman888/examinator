using Azure.Monitor.OpenTelemetry.AspNetCore;

namespace Examinator.Api;

public static class Telemetry
{
    public const string ConnectionStringKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    // Exports logs, traces and metrics to Application Insights only when a connection string is configured.
    public static void AddTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration[ConnectionStringKey])) return;
        services.AddOpenTelemetry().UseAzureMonitor();
    }
}
