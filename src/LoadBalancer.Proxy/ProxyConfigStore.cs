using Microsoft.Data.Sqlite;

namespace LoadBalancer.Proxy;

public sealed class ProxyConfigStore : ProxyConfigStoreBase
{
    private readonly string _path;

    public ProxyConfigStore(string contentRoot)
        : base(contentRoot)
    {
        var directory = Path.Combine(contentRoot, "App_Data");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "proxy.db");
    }

    protected override string StoreDescription => _path;

    protected override void EnsureReady()
    {
        using var connection = Open();
        EnsureSchema(connection);
    }

    protected override StoredProxyConfig? Read()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT routes_json, clusters_json, destinations_json, applications_json FROM proxy_config WHERE id = 1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return ParseStored(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            "proxy.db");
    }

    protected override void Insert(StoredProxyConfig config)
    {
        using var connection = Open();
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

    protected override void UpdateApplications(IReadOnlyList<StoredApplication> applications)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE proxy_config SET applications_json = $json WHERE id = 1;";
        command.Parameters.AddWithValue("$json", SerializeApplications(applications));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Proxy configuration is missing from the database.");
        }
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

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
        return connection;
    }
}
