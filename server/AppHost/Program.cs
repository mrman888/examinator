using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// This AppHost only orchestrates local development. It switches on Development__AllowAnonymous, so it
// must never produce deployment output; Azure deployment stays with deployment/deploy.ps1.
if (!builder.ExecutionContext.IsRunMode || !builder.Environment.IsDevelopment())
    throw new InvalidOperationException(
        "The Examinator AppHost only runs locally in the Development environment. Deploy with deployment/deploy.ps1.");

var settings = builder.Configuration.GetSection("Examinator");
var importsEnabled = settings.GetValue("Imports", false);
var frontendEnabled = settings.GetValue("Frontend", true);
var frontendPort = settings.GetValue("FrontendPort", 4200);

var foundryEndpoint = builder.AddParameter("foundry-endpoint");
var foundryDeployment = builder.AddParameter("foundry-deployment");

var api = AddApi("api")
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/healthz");

var web = builder.AddProject<Projects.Web>("web", options => options.ExcludeLaunchProfile = true)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithEnvironment("Development__AllowAnonymous", "true")
    // The gateway accepts plain HTTP only for a loopback address, so it gets the concrete endpoint.
    .WithEnvironment("Gateway__ApiBaseUrl", api.GetEndpoint("http"))
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/healthz")
    .WaitFor(api);

if (importsEnabled)
{
    if (!OnPath("pdftoppm"))
        throw new InvalidOperationException("Imports need Poppler: put pdftoppm on PATH, or set Examinator:Imports to false.");
    // Real uploads and Foundry calls cost money. The worker polls until it is stopped and ends at the
    // first failed import; reconciliation runs once when it is started from the dashboard.
    AddApi("import-worker").WithArgs("--import-worker", "--poll");
    AddApi("import-reconcile").WithArgs("--reconcile").WithExplicitStart();
}

if (frontendEnabled)
{
    // ng serve ignores PORT, so the port is an argument ("--" makes npm pass the options on). The fixed port is not proxied: Angular itself
    // listens on it, on IPv4 loopback only.
    builder.AddJavaScriptApp("frontend", Path.Combine(builder.AppHostDirectory, "..", ".."), "start")
        .WithNpm(install: false)
        .WithRunScript("start", ["--", "--host", "127.0.0.1", "--port", $"{frontendPort}"])
        .WithEnvironment("EXAMINATOR_WEB_URL", web.GetEndpoint("http"))
        .WithHttpEndpoint(port: frontendPort, isProxied: false)
        .WithEndpoint("http", endpoint => endpoint.TargetHost = "127.0.0.1")
        .WithHttpHealthCheck("/")
        .WaitFor(web);
}

builder.Build().Run();

// The Api project runs as the HTTP service and, with other arguments, as the import worker and reconciler.
IResourceBuilder<ProjectResource> AddApi(string name)
{
    var resource = builder.AddProject<Projects.Api>(name, options => options.ExcludeLaunchProfile = true)
        .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
        .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
        .WithEnvironment("Development__AllowAnonymous", "true")
        .WithEnvironment("Foundry__Endpoint", foundryEndpoint)
        .WithEnvironment("Foundry__Deployment", foundryDeployment);
    if (!importsEnabled) return resource;

    var importDeployment = Parameter("foundry-import-deployment");
    var storageAccount = Parameter("storage-account");
    // Fail at startup when the configured account is refused; a value entered in the dashboard is
    // checked again when the resource starts.
    if (builder.Configuration["Parameters:storage-account"] is { Length: > 0 } configured) StorageEndpoint(configured, "blob");
    return resource
        .WithEnvironment("Import__Enabled", "true")
        .WithEnvironment("Foundry__ImportDeployment", importDeployment)
        .WithEnvironment(async context =>
        {
            var account = await storageAccount.Resource.GetValueAsync(context.CancellationToken) ?? "";
            context.EnvironmentVariables["Storage__BlobEndpoint"] = StorageEndpoint(account, "blob");
            context.EnvironmentVariables["Storage__QueueEndpoint"] = StorageEndpoint(account, "queue");
            context.EnvironmentVariables["Storage__TableEndpoint"] = StorageEndpoint(account, "table");
        });
}

IResourceBuilder<ParameterResource> Parameter(string name) =>
    builder.Resources.OfType<ParameterResource>().FirstOrDefault(parameter => parameter.Name == name) is { } existing
        ? builder.CreateResourceBuilder(existing)
        : builder.AddParameter(name);

// Local runs use the dedicated test storage account. Production accounts are named "exam" plus 13
// generated characters and are always refused.
static string StorageEndpoint(string account, string service)
{
    if (!Regex.IsMatch(account, "^[a-z0-9]{3,24}$"))
        throw new InvalidOperationException("The storage-account parameter must be a storage account name: 3 to 24 lowercase letters and digits.");
    if (Regex.IsMatch(account, "^exam[a-z0-9]{13}$"))
        throw new InvalidOperationException("The storage-account parameter matches the production storage naming pattern. Use the dedicated test storage account.");
    return $"https://{account}.{service}.core.windows.net/";
}

static bool OnPath(string command) =>
    (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .SelectMany(directory => new[] { command, command + ".exe" }.Select(file => Path.Combine(directory, file)))
        .Any(File.Exists);
