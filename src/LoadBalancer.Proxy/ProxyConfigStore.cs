using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace LoadBalancer.Proxy;

public sealed class ProxyConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly string _legacyDestinationsPath;

    public ProxyConfigStore(string contentRoot)
    {
        var directory = System.IO.Path.Combine(contentRoot, "App_Data");
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, "proxy.db");
        _legacyDestinationsPath = System.IO.Path.Combine(directory, "destinations.json");
    }

    public StoredProxyConfig LoadOrCreate(IConfiguration configuration, ILogger logger)
    {
        using var connection = Open();
        EnsureSchema(connection);

        var existing = Read(connection);
        if (existing is not null)
        {
            if (existing.Applications.Count > 0)
            {
                logger.LogInformation("Loaded {Count} applications from {Path}", existing.Applications.Count, _path);
                return existing;
            }

            var migrated = existing with
            {
                Applications = MigrateApplications(existing.RoutesJson, existing.Destinations)
            };
            SaveApplications(connection, migrated.Applications);
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
        Insert(connection, stored);
        logger.LogInformation("Seeded proxy configuration into {Path}", _path);
        return stored;
    }

    public void SaveApplications(IReadOnlyList<StoredApplication> applications)
    {
        using var connection = Open();
        SaveApplications(connection, applications);
    }

    private static void SaveApplications(SqliteConnection connection, IReadOnlyList<StoredApplication> applications)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE proxy_config SET applications_json = $json WHERE id = 1;";
        command.Parameters.AddWithValue("$json", SerializeApplications(applications));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Proxy configuration is missing from the database.");
        }
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

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS proxy_config (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                routes_json TEXT NOT NULL,
                clusters_json TEXT NOT NULL,
                destinations_json TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        EnsureApplicationsColumn(connection);
    }

    private static void EnsureApplicationsColumn(SqliteConnection connection)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(proxy_config);";
        using var reader = info.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
        }

        if (names.Contains("applications_json"))
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE proxy_config ADD COLUMN applications_json TEXT;";
        alter.ExecuteNonQuery();
    }

    private static StoredProxyConfig? Read(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT routes_json, clusters_json, destinations_json, applications_json FROM proxy_config WHERE id = 1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var destinations = JsonSerializer.Deserialize<DestinationFile>(reader.GetString(2), JsonOptions)?.Destinations
            ?? throw new InvalidOperationException("Stored destinations could not be read.");
        ValidateDestinations(destinations, "proxy.db");
        var applications = reader.IsDBNull(3)
            ? []
            : JsonSerializer.Deserialize<ApplicationFile>(reader.GetString(3), JsonOptions)?.Applications
                ?? throw new InvalidOperationException("Stored applications could not be read.");
        foreach (var application in applications)
        {
            if (string.IsNullOrWhiteSpace(application.Id) || string.IsNullOrWhiteSpace(application.ListenUrl))
            {
                throw new InvalidOperationException("proxy.db contains an application without an id or listen address.");
            }

            ValidateDestinations(application.Destinations, application.Id);
        }

        return new StoredProxyConfig(reader.GetString(0), reader.GetString(1), destinations, applications);
    }

    private static void Insert(SqliteConnection connection, StoredProxyConfig config)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO proxy_config (id, routes_json, clusters_json, destinations_json, applications_json)
            VALUES (1, $routes, $clusters, $destinations, $applications);
            """;
        command.Parameters.AddWithValue("$routes", config.RoutesJson);
        command.Parameters.AddWithValue("$clusters", config.ClustersJson);
        command.Parameters.AddWithValue("$destinations", SerializeDestinations(config.Destinations));
        command.Parameters.AddWithValue("$applications", SerializeApplications(config.Applications));
        command.ExecuteNonQuery();
    }

    private static string SerializeApplications(IReadOnlyList<StoredApplication> applications)
    {
        return JsonSerializer.Serialize(new ApplicationFile(applications.ToList()), JsonOptions);
    }

    private static string SerializeDestinations(IReadOnlyList<StoredDestination> destinations)
    {
        return JsonSerializer.Serialize(new DestinationFile(destinations.ToList()), JsonOptions);
    }

    private static void ValidateDestinations(IEnumerable<StoredDestination> destinations, string source)
    {
        if (destinations.Any(destination => string.IsNullOrWhiteSpace(destination.Id) || string.IsNullOrWhiteSpace(destination.Address)))
        {
            throw new InvalidOperationException($"{source} contains an endpoint without an id or address.");
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
        return connection;
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

public sealed record StoredProxyConfig(
    string RoutesJson,
    string ClustersJson,
    List<StoredDestination> Destinations,
    List<StoredApplication> Applications);

public sealed record StoredApplication(string Id, string ListenUrl, List<StoredDestination> Destinations);

public sealed record ApplicationFile(List<StoredApplication> Applications);
