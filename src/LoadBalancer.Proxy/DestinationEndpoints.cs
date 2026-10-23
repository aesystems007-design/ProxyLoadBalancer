using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Model;

namespace LoadBalancer.Proxy;

public static class DestinationEndpoints
{
    public static void MapDestinationApi(this WebApplication app)
    {
        var api = app.MapGroup("/_proxy/api");

        api.MapGet("/overview", (DestinationCatalog catalog, IProxyStateLookup lookup) =>
            Results.Ok(catalog.Overview(lookup)));

        api.MapPost("/applications", (AddApplicationRequest? request, DestinationCatalog catalog, IProxyStateLookup lookup) =>
            Update(() => catalog.AddApplication(request?.Id, request?.ListenUrl, lookup)));

        api.MapPut("/applications/{id}/listen", (string id, SetListenRequest? request, DestinationCatalog catalog, IProxyStateLookup lookup) =>
            Update(() => catalog.SetListen(id, request?.ListenUrl, lookup)));

        api.MapDelete("/applications/{id}", (string id, DestinationCatalog catalog, IProxyStateLookup lookup) =>
            Update(() => catalog.RemoveApplication(id, lookup)));

        api.MapPost("/applications/{applicationId}/destinations", (string applicationId, AddDestinationRequest? request, DestinationCatalog catalog, IProxyStateLookup lookup) =>
            Update(() => catalog.Add(applicationId, request?.Id, request?.Address, request?.HealthAddress, lookup)));

        api.MapPatch("/applications/{applicationId}/destinations/{id}", (string applicationId, string id, SetEnabledRequest? request, DestinationCatalog catalog, IProxyStateLookup lookup) =>
        {
            if (request is null)
            {
                return Results.BadRequest(new { error = "Request body is required." });
            }

            return Update(() => catalog.SetEnabled(applicationId, id, request.Enabled, lookup));
        });

        api.MapDelete("/applications/{applicationId}/destinations/{id}", (string applicationId, string id, DestinationCatalog catalog, IProxyStateLookup lookup) =>
            Update(() => catalog.Remove(applicationId, id, lookup)));

        api.MapPut("/applications/{applicationId}/destinations/{id}/health", (string applicationId, string id, SetHealthRequest? request, DestinationCatalog catalog, IProxyStateLookup lookup) =>
            Update(() => catalog.SetHealth(applicationId, id, request?.Address, lookup)));

        api.MapPut("/applications/{applicationId}/traffic", (string applicationId, TrafficRequest? request, DestinationCatalog catalog, IProxyStateLookup lookup) =>
            Update(() => catalog.SetTraffic(applicationId, request?.DestinationIds, lookup)));
    }

    private static IResult Update(Func<OverviewResponse> action)
    {
        try
        {
            return Results.Ok(action());
        }
        catch (DestinationUpdateException exception)
        {
            return Results.Json(new { error = exception.Message }, statusCode: exception.StatusCode);
        }
    }
}

public sealed record AddApplicationRequest(string? Id, string? ListenUrl);

public sealed record SetListenRequest(string? ListenUrl);

public sealed record AddDestinationRequest(string? Id, string? Address, string? HealthAddress);

public sealed record SetHealthRequest(string? Address);

public sealed record SetEnabledRequest(bool Enabled);

public sealed record TrafficRequest(IReadOnlyList<string>? DestinationIds);
