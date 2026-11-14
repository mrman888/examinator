using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Examinator.ServiceDefaults;

public static class Extensions
{
    public const string ApplicationInsightsKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    // Observability only. Hosted apps export to Application Insights; under the Aspire AppHost, which
    // sets the OTLP endpoint, they export to the dashboard. Without either setting nothing is registered.
    // HTTP resilience and service discovery are deliberately absent: retries would repeat paid Foundry
    // calls and the default timeouts would cut off the long-running and streaming clients.
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        if (!string.IsNullOrWhiteSpace(builder.Configuration[ApplicationInsightsKey]))
        {
            builder.Services.AddOpenTelemetry().UseAzureMonitor();
            return builder;
        }
        if (string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpointKey])) return builder;

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddAspNetCoreInstrumentation(options => options.Filter = context => context.Request.Path != "/healthz")
                .AddHttpClientInstrumentation())
            .UseOtlpExporter();
        return builder;
    }
}
