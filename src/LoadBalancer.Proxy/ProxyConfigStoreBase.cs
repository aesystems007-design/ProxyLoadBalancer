using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LoadBalancer.Proxy;

public interface IProxyConfigStore
{
    StoredProxyConfig LoadOrCreate(IConfiguration configuration, ILogger logger);

    void SaveApplications(IReadOnlyList<StoredApplication> applications);
}

public abstract class ProxyConfigStoreBase : IProxyConfigStore
{
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _legacyDestinationsPath;

    protected ProxyConfigStoreBase(string contentRoot)
    {
        _legacyDestinationsPath = Path.Combine(contentRoot, "App_Data", "destinations.json");
    }

    protected abstract string StoreDescription { get; }

    public StoredProxyConfig LoadOrCreate(IConfiguration configuration, ILogger logger)
    {
        EnsureReady();

        var existing = Read();
        if (existing is not null)
        {
            if (existing.Applications.Count > 0)
            {
                logger.LogInformation("Loaded {Count} applications from {Store}", existing.Applications.Count, StoreDescription);
                return existing;
            }

            var migrated = existing with
            {
                Applications = MigrateApplications(existing.RoutesJson, existing.Destinations)
            };
            UpdateApplications(migrated.Applications);
            logger.LogInformation("Moved the existing endpoints into application {ApplicationId}", migrated.Applications[0].Id);
            return migrated;
        }

        if (SectionToNode(configuration.GetSection("ReverseProxy:Routes")) is not JsonObject routes
            || SectionToNode(configuration.GetSection("ReverseProxy:Clusters")) is not JsonObject clusters
            || routes.Count == 0
            || clusters.Count == 0)
        {
            throw new InvalidOperationException("ReverseProxy:Routes and ReverseProxy:Clusters are required to seed the database.");
        }

        var managedClusterId = FirstClusterId(routes);
        if (clusters[managedClusterId] is JsonObject managedCluster)
        {
            managedCluster.Remove("Destinations");
        }

        var destinations = LoadLegacyDestinations(logger)
            ?? ReadSeedDestinations(configuration, managedClusterId);

        var applications = new List<StoredApplication>
        {
            new(managedClusterId, "http://*/", destinations)
        };
        var stored = new StoredProxyConfig(routes.ToJsonString(JsonOptions), clusters.ToJsonString(JsonOptions), destinations, applications);
        Insert(stored);
        logger.LogInformation("Seeded proxy configuration into {Store}", StoreDescription);
        return stored;
    }

    public void SaveApplications(IReadOnlyList<StoredApplication> applications)
    {
        EnsureReady();
        UpdateApplications(applications);
    }

    protected abstract void EnsureReady();

    protected abstract StoredProxyConfig? Read();

    protected abstract void Insert(StoredProxyConfig config);

    protected abstract void UpdateApplications(IReadOnlyList<StoredApplication> applications);

    protected static StoredProxyConfig ParseStored(
        string routesJson,
        string clustersJson,
        string destinationsJson,
        string? applicationsJson,
        string source)
    {
        var destinations = JsonSerializer.Deserialize<DestinationFile>(destinationsJson, JsonOptions)?.Destinations
            ?? throw new InvalidOperationException("Stored destinations could not be read.");
        ValidateDestinations(destinations, source);
        var applications = applicationsJson is null
            ? []
            : JsonSerializer.Deserialize<ApplicationFile>(applicationsJson, JsonOptions)?.Applications
                ?? throw new InvalidOperationException("Stored applications could not be read.");
        foreach (var application in applications)
        {
            if (string.IsNullOrWhiteSpace(application.Id) || string.IsNullOrWhiteSpace(application.ListenUrl))
            {
                throw new InvalidOperationException($"{source} contains an application without an id or listen address.");
            }

            ValidateDestinations(application.Destinations, application.Id);
        }

        return new StoredProxyConfig(routesJson, clustersJson, destinations, applications);
    }

    protected static string SerializeApplications(IReadOnlyList<StoredApplication> applications)
    {
        return JsonSerializer.Serialize(new ApplicationFile(applications.ToList()), JsonOptions);
    }

    protected static string SerializeDestinations(IReadOnlyList<StoredDestination> destinations)
    {
        return JsonSerializer.Serialize(new DestinationFile(destinations.ToList()), JsonOptions);
    }

    private static List<StoredApplication> MigrateApplications(string routesJson, List<StoredDestination> destinations)
    {
        var routes = JsonNode.Parse(routesJson) as JsonObject
            ?? throw new InvalidOperationException("Stored routes could not be read.");
        return [new StoredApplication(FirstClusterId(routes), "http://*/", destinations)];
    }

    private List<StoredDestination>? LoadLegacyDestinations(ILogger logger)
    {
        if (!File.Exists(_legacyDestinationsPath))
        {
            return null;
        }

        var stored = JsonSerializer.Deserialize<DestinationFile>(File.ReadAllText(_legacyDestinationsPath), JsonOptions)
            ?? throw new InvalidOperationException($"Could not read {_legacyDestinationsPath}.");
        ValidateDestinations(stored.Destinations, _legacyDestinationsPath);
        logger.LogInformation("Imported {Count} endpoints from {Path}", stored.Destinations.Count, _legacyDestinationsPath);
        return stored.Destinations;
    }

    private static List<StoredDestination> ReadSeedDestinations(IConfiguration configuration, string clusterId)
    {
        var destinations = new List<StoredDestination>();
        foreach (var destination in configuration.GetSection($"ReverseProxy:Clusters:{clusterId}:Destinations").GetChildren())
        {
            var address = destination["Address"];
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new InvalidOperationException($"Destination '{destination.Key}' needs an Address.");
            }

            destinations.Add(new StoredDestination(destination.Key, address, true, destination["Health"]));
        }

        return destinations;
    }

    private static string FirstClusterId(JsonObject routes)
    {
        foreach (var route in routes)
        {
            if (route.Value is JsonObject body)
            {
                var clusterId = body["ClusterId"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(clusterId))
                {
                    return clusterId;
                }
            }
        }

        throw new InvalidOperationException("The proxy route needs a ClusterId.");
    }

    private static void ValidateDestinations(IEnumerable<StoredDestination> destinations, string source)
    {
        if (destinations.Any(destination => string.IsNullOrWhiteSpace(destination.Id) || string.IsNullOrWhiteSpace(destination.Address)))
        {
            throw new InvalidOperationException($"{source} contains an endpoint without an id or address.");
        }
    }

    private static JsonNode SectionToNode(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            return JsonValue.Create(section.Value ?? "")!;
        }

        var indexes = new List<int>(children.Count);
        var isArray = true;
        foreach (var child in children)
        {
            if (!int.TryParse(child.Key, out var index))
            {
                isArray = false;
                break;
            }

            indexes.Add(index);
        }

        if (isArray && indexes.OrderBy(index => index).SequenceEqual(Enumerable.Range(0, indexes.Count)))
        {
            var array = new JsonArray();
            foreach (var child in children.OrderBy(child => int.Parse(child.Key)))
            {
                array.Add(SectionToNode(child));
            }

            return array;
        }

        var obj = new JsonObject();
        foreach (var child in children)
        {
            obj[child.Key] = SectionToNode(child);
        }

        return obj;
    }
}

public static class ProxyConfigServiceCollectionExtensions
{
    public static IServiceCollection AddProxyConfigStore(this IServiceCollection services)
    {
        services.AddSingleton<IProxyConfigStore>(static services =>
        {
            var configuration = services.GetRequiredService<IConfiguration>();
            var contentRoot = services.GetRequiredService<IHostEnvironment>().ContentRootPath;
            var provider = configuration["Proxy:ConfigStore"];
            if (string.IsNullOrWhiteSpace(provider) || provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                return new ProxyConfigStore(contentRoot);
            }

            if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                return new SqlServerProxyConfigStore(configuration, contentRoot);
            }

            throw new InvalidOperationException($"Proxy:ConfigStore '{provider}' is unknown. Use Sqlite or SqlServer.");
        });
        return services;
    }
}

public sealed record StoredProxyConfig(
    string RoutesJson,
    string ClustersJson,
    List<StoredDestination> Destinations,
    List<StoredApplication> Applications);

public sealed record StoredApplication(string Id, string ListenUrl, List<StoredDestination> Destinations);

public sealed record ApplicationFile(List<StoredApplication> Applications);
