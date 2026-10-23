namespace LoadBalancer.Proxy;

/// <summary>
/// Extension point for storing the same proxy document in SQL Server.
/// Replace the four persistence methods, or subclass this type and register the subclass from <see cref="ProxyConfigServiceCollectionExtensions.AddProxyConfigStore"/>.
/// A second proxy process pointed at the same database does not reload until it restarts.
/// </summary>
public class SqlServerProxyConfigStore : ProxyConfigStoreBase
{
    public const string TableSql = """
        CREATE TABLE proxy_config (
            id int NOT NULL CONSTRAINT PK_proxy_config PRIMARY KEY,
            routes_json nvarchar(max) NOT NULL,
            clusters_json nvarchar(max) NOT NULL,
            destinations_json nvarchar(max) NOT NULL,
            applications_json nvarchar(max) NULL,
            CONSTRAINT CK_proxy_config_id CHECK (id = 1)
        );
        """;

    public SqlServerProxyConfigStore(IConfiguration configuration, string contentRoot)
        : base(contentRoot)
    {
        var connectionString = configuration.GetConnectionString("Proxy");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Proxy:ConfigStore is SqlServer. Set ConnectionStrings:Proxy to the SQL Server connection string.");
        }

        ConnectionString = connectionString;
    }

    protected string ConnectionString { get; }

    protected override string StoreDescription => "SQL Server";

    protected override void EnsureReady() => throw Stub();

    protected override StoredProxyConfig? Read() => throw Stub();

    protected override void Insert(StoredProxyConfig config) => throw Stub();

    protected override void UpdateApplications(IReadOnlyList<StoredApplication> applications) => throw Stub();

    private static NotImplementedException Stub()
    {
        return new NotImplementedException(
            "SQL Server configuration storage is a stub. Implement EnsureReady, Read, Insert, and UpdateApplications in SqlServerProxyConfigStore with Microsoft.Data.SqlClient. "
            + "Add the Microsoft.Data.SqlClient package, open ConnectionString, and create one row in this table: "
            + TableSql
            + " Read returns null when that row is missing. Insert writes all four JSON columns. UpdateApplications updates only applications_json. "
            + "Use ParseStored, SerializeApplications, and SerializeDestinations for the JSON.");
    }
}
