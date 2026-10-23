using LoadBalancer.Proxy;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<HealthProbeSignal>();
builder.Services.AddSingleton<IActiveHealthCheckMonitor, DisabledActiveHealthCheckMonitor>();
builder.Services.AddSingleton<DestinationCatalog>();
builder.Services.AddSingleton<IProxyConfigProvider>(services => services.GetRequiredService<DestinationCatalog>().ConfigProvider);
builder.Services.AddReverseProxy();
builder.Services.AddHostedService<EndpointHealthMonitor>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    await next(context);

    if (!context.RequestServices.GetRequiredService<IConfiguration>().GetValue("Proxy:LogChosenDestination", false))
    {
        return;
    }

    var destination = context.Features.Get<IReverseProxyFeature>()?.ProxiedDestination;
    if (destination is null)
    {
        return;
    }

    app.Logger.LogInformation(
        "{Method} {Path} forwarded to {DestinationId} at {Address} with status {StatusCode}",
        context.Request.Method,
        context.Request.Path,
        destination.DestinationId,
        destination.Model.Config.Address,
        context.Response.StatusCode);
});

var dashboardRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "wwwroot", "_proxy", "dashboard"));
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (path.Equals("/_proxy", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/_proxy/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/_proxy/dashboard", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/_proxy/dashboard/", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect("/_proxy/dashboard/index.html");
        return;
    }

    if (path.StartsWithSegments("/_proxy/dashboard", out var remainder))
    {
        var relative = (remainder.Value ?? "").TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var file = Path.GetFullPath(Path.Combine(dashboardRoot, relative));
        var rootPrefix = dashboardRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!file.StartsWith(rootPrefix, comparison) || !File.Exists(file))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Dashboard file was not found. From src/dashboard, run npm install and npm run build.");
            return;
        }

        context.Response.ContentType = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".json" => "application/json",
            _ => "application/octet-stream"
        };
        await context.Response.SendFileAsync(file);
        return;
    }

    await next(context);
});

app.MapGet("/proxy/health", () => Results.Ok(new { status = "Healthy" }));

app.MapGet("/proxy/status", (IProxyStateLookup lookup, IConfiguration configuration) =>
{
    if (!configuration.GetValue("Proxy:ExposeStatus", true))
    {
        return Results.NotFound();
    }

    var clusters = lookup.GetClusters()
        .OrderBy(cluster => cluster.ClusterId, StringComparer.OrdinalIgnoreCase)
        .Select(cluster => new
        {
            cluster.ClusterId,
            LoadBalancingPolicy = cluster.Model.Config.LoadBalancingPolicy ?? "PowerOfTwoChoices",
            Destinations = cluster.Destinations.Values
                .OrderBy(destination => destination.DestinationId, StringComparer.OrdinalIgnoreCase)
                .Select(destination => new
                {
                    destination.DestinationId,
                    Address = destination.Model.Config.Address,
                    ActiveHealth = destination.Health.Active.ToString(),
                    PassiveHealth = destination.Health.Passive.ToString()
                })
        });

    return Results.Ok(new { clusters });
});

app.MapDestinationApi();
app.MapReverseProxy();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var lookup = app.Services.GetRequiredService<IProxyStateLookup>();
    foreach (var cluster in lookup.GetClusters())
    {
        var policy = cluster.Model.Config.LoadBalancingPolicy ?? "PowerOfTwoChoices";
        var destinations = string.Join(
            ", ",
            cluster.Destinations.Values.Select(destination => $"{destination.DestinationId}={destination.Model.Config.Address}"));

        app.Logger.LogInformation(
            "Proxy cluster {ClusterId} is using {LoadBalancingPolicy}. Destinations: {Destinations}",
            cluster.ClusterId,
            policy,
            destinations);
    }

    app.Logger.LogInformation("Dashboard available at /_proxy/dashboard/");
});

app.Run();
