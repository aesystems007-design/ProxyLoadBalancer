using System.Net.Http;
using System.Text;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace LoadBalancer.Proxy;

public sealed class DestinationCatalog
{
    private readonly object _gate = new();
    private readonly ILogger<DestinationCatalog> _logger;
    private readonly HealthProbeSignal _probeSignal;
    private readonly IProxyConfigStore _store;
    private readonly Dictionary<string, HealthObservation> _health = new(StringComparer.OrdinalIgnoreCase);
    private readonly ClusterConfig _template;
    private readonly InMemoryConfigProvider _configProvider;
    private readonly List<ProxyApplication> _applications;

    public DestinationCatalog(
        IConfiguration configuration,
        IProxyConfigStore store,
        HealthProbeSignal probeSignal,
        ILogger<DestinationCatalog> logger)
    {
        _logger = logger;
        _probeSignal = probeSignal;
        _store = store;
        var stored = _store.LoadOrCreate(configuration, logger);
        var clusters = ParseClusters(stored.ClustersJson);
        _template = clusters.FirstOrDefault(cluster =>
                stored.Applications.Count > 0
                && string.Equals(cluster.ClusterId, stored.Applications[0].Id, StringComparison.OrdinalIgnoreCase))
            ?? clusters.FirstOrDefault()
            ?? throw new InvalidOperationException("The proxy database has no cluster settings.");
        _applications = stored.Applications.Select(CreateApplication).ToList();
        _configProvider = new InMemoryConfigProvider(BuildRoutes(), BuildClusters());
        _logger.LogInformation("Dashboard is managing {Count} applications", _applications.Count);
    }

    public InMemoryConfigProvider ConfigProvider => _configProvider;

    public OverviewResponse Overview(IProxyStateLookup lookup)
    {
        lock (_gate)
        {
            return BuildOverview(lookup);
        }
    }

    public OverviewResponse AddApplication(string? id, string? listenUrl, IProxyStateLookup lookup)
    {
        var applicationId = RequireId(id, "Application id");
        var listen = ParseListenUrl(listenUrl);

        lock (_gate)
        {
            if (FindApplicationIndex(applicationId) >= 0)
            {
                throw new DestinationUpdateException(StatusCodes.Status409Conflict, $"An application named '{applicationId}' already exists.");
            }

            EnsureUniqueListen(listen, null);
            _applications.Add(new ProxyApplication(applicationId, listen.ListenUrl, listen.Host, listen.PathPrefix, []));
            Commit();
            _logger.LogInformation("Added application {ApplicationId} listening at {ListenUrl}", applicationId, listen.ListenUrl);
            return BuildOverview(lookup);
        }
    }

    public OverviewResponse SetListen(string id, string? listenUrl, IProxyStateLookup lookup)
    {
        var listen = ParseListenUrl(listenUrl);
        lock (_gate)
        {
            var index = RequireApplicationIndex(id);
            var current = _applications[index];
            if (string.Equals(current.ListenUrl, listen.ListenUrl, StringComparison.OrdinalIgnoreCase))
            {
                return BuildOverview(lookup);
            }

            EnsureUniqueListen(listen, current.Id);
            _applications[index] = current with
            {
                ListenUrl = listen.ListenUrl,
                Host = listen.Host,
                PathPrefix = listen.PathPrefix
            };
            Commit();
            _logger.LogInformation("Application {ApplicationId} now listens at {ListenUrl}", current.Id, listen.ListenUrl);
            return BuildOverview(lookup);
        }
    }

    public OverviewResponse RemoveApplication(string id, IProxyStateLookup lookup)
    {
        lock (_gate)
        {
            var index = RequireApplicationIndex(id);
            var current = _applications[index];
            foreach (var destination in current.Destinations)
            {
                _health.Remove(HealthObservation.KeyFor(current.Id, destination.Id));
            }

            _applications.RemoveAt(index);
            Commit();
            _logger.LogInformation("Removed application {ApplicationId}", current.Id);
            return BuildOverview(lookup);
        }
    }

    public OverviewResponse Add(string applicationId, string? id, string? address, string? healthAddress, IProxyStateLookup lookup)
    {
        var destinationId = RequireId(id, "Endpoint id");
        var normalized = NormalizeAddress(address);
        var health = NormalizeHealthInput(healthAddress);
        if (health is not null)
        {
            ResolveHealthUrl(normalized, health);
        }

        lock (_gate)
        {
            var index = RequireApplicationIndex(applicationId);
            var application = _applications[index];
            if (application.Destinations.Any(destination => string.Equals(destination.Id, destinationId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DestinationUpdateException(StatusCodes.Status409Conflict, $"An endpoint named '{destinationId}' already exists on {application.Id}.");
            }

            var next = application.Destinations.ToList();
            next.Add(new StoredDestination(destinationId, normalized, true, health));
            _applications[index] = application with { Destinations = next };
            Commit();
            _logger.LogInformation("Added endpoint {DestinationId} at {Address} for {ApplicationId}", destinationId, normalized, application.Id);
            return BuildOverview(lookup);
        }
    }

    public OverviewResponse SetHealth(string applicationId, string id, string? healthAddress, IProxyStateLookup lookup)
    {
        lock (_gate)
        {
            var applicationIndex = RequireApplicationIndex(applicationId);
            var application = _applications[applicationIndex];
            var index = FindDestinationIndex(application, id);
            var current = application.Destinations[index];
            var health = NormalizeHealthInput(healthAddress);
            if (health is not null)
            {
                ResolveHealthUrl(current.Address, health);
            }

            if (string.Equals(current.HealthAddress, health, StringComparison.Ordinal))
            {
                _probeSignal.Request();
                return BuildOverview(lookup);
            }

            var next = application.Destinations.ToList();
            next[index] = current with { HealthAddress = health };
            _health.Remove(HealthObservation.KeyFor(application.Id, current.Id));
            _applications[applicationIndex] = application with { Destinations = next };
            Commit();
            _logger.LogInformation(
                "Endpoint {DestinationId} on {ApplicationId} health endpoint is {HealthAddress}",
                current.Id,
                application.Id,
                health ?? "not set");
            return BuildOverview(lookup);
        }
    }

    public OverviewResponse SetEnabled(string applicationId, string id, bool enabled, IProxyStateLookup lookup)
    {
        lock (_gate)
        {
            var applicationIndex = RequireApplicationIndex(applicationId);
            var application = _applications[applicationIndex];
            var index = FindDestinationIndex(application, id);
            var current = application.Destinations[index];
            if (current.Enabled == enabled)
            {
                return BuildOverview(lookup);
            }

            var next = application.Destinations.ToList();
            next[index] = current with { Enabled = enabled };
            _applications[applicationIndex] = application with { Destinations = next };
            Commit();
            _logger.LogInformation("Endpoint {DestinationId} on {ApplicationId} is now {State}", current.Id, application.Id, enabled ? "receiving traffic" : "paused");
            return BuildOverview(lookup);
        }
    }

    public OverviewResponse Remove(string applicationId, string id, IProxyStateLookup lookup)
    {
        lock (_gate)
        {
            var applicationIndex = RequireApplicationIndex(applicationId);
            var application = _applications[applicationIndex];
            var index = FindDestinationIndex(application, id);
            var current = application.Destinations[index];
            var next = application.Destinations.ToList();
            next.RemoveAt(index);
            _health.Remove(HealthObservation.KeyFor(application.Id, current.Id));
            _applications[applicationIndex] = application with { Destinations = next };
            Commit();
            _logger.LogInformation("Removed endpoint {DestinationId} from {ApplicationId}", current.Id, application.Id);
            return BuildOverview(lookup);
        }
    }

    public OverviewResponse SetTraffic(string applicationId, IReadOnlyList<string>? destinationIds, IProxyStateLookup lookup)
    {
        if (destinationIds is null || destinationIds.Count == 0)
        {
            throw new DestinationUpdateException(StatusCodes.Status400BadRequest, "Select at least one endpoint to receive traffic.");
        }

        var requested = destinationIds
            .Select(id => (id ?? "").Trim())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requested.Count == 0)
        {
            throw new DestinationUpdateException(StatusCodes.Status400BadRequest, "Select at least one endpoint to receive traffic.");
        }

        lock (_gate)
        {
            var applicationIndex = RequireApplicationIndex(applicationId);
            var application = _applications[applicationIndex];
            var unknown = requested
                .Where(id => application.Destinations.All(destination => !string.Equals(destination.Id, id, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (unknown.Count > 0)
            {
                throw new DestinationUpdateException(StatusCodes.Status404NotFound, $"Unknown endpoint: {string.Join(", ", unknown)}.");
            }

            var selected = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var next = application.Destinations
                .Select(destination => destination with { Enabled = selected.Contains(destination.Id) })
                .ToList();
            _applications[applicationIndex] = application with { Destinations = next };
            Commit();
            _logger.LogInformation("Traffic for {ApplicationId} is limited to {Destinations}", application.Id, string.Join(", ", requested));
            return BuildOverview(lookup);
        }
    }

    private void Commit()
    {
        _store.SaveApplications(_applications
            .Select(application => new StoredApplication(application.Id, application.ListenUrl, application.Destinations))
            .ToList());
        _configProvider.Update(BuildRoutes(), BuildClusters());
        _probeSignal.Request();
    }

    public IReadOnlyList<HealthTarget> HealthTargets()
    {
        lock (_gate)
        {
            return _applications
                .SelectMany(application => application.Destinations.Select(destination => new HealthTarget(
                    application.Id,
                    destination.Id,
                    TryResolveHealthUrl(destination.Address, destination.HealthAddress))))
                .ToList();
        }
    }

    public void RecordHealth(HealthObservation observation)
    {
        lock (_gate)
        {
            _health[observation.Key] = observation;
        }
    }

    private List<RouteConfig> BuildRoutes()
    {
        var ordered = _applications
            .OrderByDescending(application => application.PathPrefix.Length)
            .ThenBy(application => application.Host is null ? 1 : 0)
            .ToList();
        var routes = new List<RouteConfig>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            var application = ordered[index];
            routes.Add(new RouteConfig
            {
                RouteId = application.Id,
                ClusterId = application.Id,
                Order = index,
                Match = new RouteMatch
                {
                    Hosts = application.Host is null ? null : [application.Host],
                    Path = application.PathPrefix == "/"
                        ? "/{**catch-all}"
                        : application.PathPrefix + "/{**catch-all}"
                }
            });
        }

        return routes;
    }

    private List<ClusterConfig> BuildClusters()
    {
        var health = _template.HealthCheck ?? new HealthCheckConfig();
        var active = health.Active ?? new ActiveHealthCheckConfig();
        var healthCheck = health with
        {
            Active = active with
            {
                Enabled = true,
                Path = null
            }
        };

        return _applications.Select(application => _template with
        {
            ClusterId = application.Id,
            Destinations = application.Destinations
                .Where(destination => destination.Enabled)
                .ToDictionary(
                    destination => destination.Id,
                    destination => new DestinationConfig
                    {
                        Address = destination.Address,
                        Health = TryResolveHealthUrl(destination.Address, destination.HealthAddress)
                    },
                    StringComparer.OrdinalIgnoreCase),
            HealthCheck = healthCheck
        }).ToList();
    }

    private OverviewResponse BuildOverview(IProxyStateLookup lookup)
    {
        var policy = string.IsNullOrWhiteSpace(_template.LoadBalancingPolicy) ? "PowerOfTwoChoices" : _template.LoadBalancingPolicy;
        return new OverviewResponse(_applications.Select(application => BuildApplication(application, policy, lookup)).ToList());
    }

    private ApplicationView BuildApplication(ProxyApplication application, string policy, IProxyStateLookup lookup)
    {
        lookup.TryGetCluster(application.Id, out var cluster);
        var views = new List<DestinationView>(application.Destinations.Count);
        foreach (var destination in application.Destinations)
        {
            DestinationState? state = null;
            cluster?.Destinations.TryGetValue(destination.Id, out state);
            var probeUrl = TryResolveHealthUrl(destination.Address, destination.HealthAddress);
            _health.TryGetValue(HealthObservation.KeyFor(application.Id, destination.Id), out var observation);
            var currentObservation = observation is not null && observation.ProbeUrl == probeUrl ? observation : null;
            var appHealth = probeUrl is null
                ? "NotConfigured"
                : currentObservation is null ? "Pending" : currentObservation.Status;
            var passiveDown = state?.Health.Passive == DestinationHealth.Unhealthy;
            var activeDown = state?.Health.Active == DestinationHealth.Unhealthy;
            var available = appHealth switch
            {
                "Unhealthy" => false,
                "NotConfigured" => !passiveDown,
                "Pending" => !activeDown && !passiveDown,
                _ => !passiveDown
            };

            views.Add(new DestinationView(
                destination.Id,
                destination.Address,
                destination.HealthAddress,
                probeUrl,
                appHealth,
                currentObservation?.StatusCode,
                currentObservation?.LatencyMs,
                currentObservation?.Error,
                currentObservation?.CheckedAt,
                destination.Enabled,
                false,
                destination.Enabled ? state?.Health.Active.ToString() ?? "Unknown" : "Off",
                destination.Enabled ? state?.Health.Passive.ToString() ?? "Unknown" : "Off",
                state?.ConcurrentRequestCount ?? 0,
                available));
        }

        var enabled = views.Where(view => view.Enabled).ToList();
        var availableCount = enabled.Count(view => view.Available);
        var panic = enabled.Count > 0 && availableCount == 0;
        views = views
            .Select(view => view with { ReceivingTraffic = view.Enabled && (panic || view.Available) })
            .ToList();

        return new ApplicationView(application.Id, application.ListenUrl, application.Host, application.PathPrefix, policy, panic, views);
    }

    private int RequireApplicationIndex(string id)
    {
        var index = FindApplicationIndex(id);
        if (index < 0)
        {
            throw new DestinationUpdateException(StatusCodes.Status404NotFound, $"Unknown application '{id}'.");
        }

        return index;
    }

    private int FindApplicationIndex(string id)
    {
        return _applications.FindIndex(application => string.Equals(application.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private static int FindDestinationIndex(ProxyApplication application, string id)
    {
        var index = application.Destinations.FindIndex(destination => string.Equals(destination.Id, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new DestinationUpdateException(StatusCodes.Status404NotFound, $"Unknown endpoint '{id}'.");
        }

        return index;
    }

    private void EnsureUniqueListen(ListenAddress listen, string? exceptApplicationId)
    {
        var conflict = _applications.FirstOrDefault(application =>
            !string.Equals(application.Id, exceptApplicationId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(application.Host, listen.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(application.PathPrefix, listen.PathPrefix, StringComparison.OrdinalIgnoreCase));
        if (conflict is not null)
        {
            throw new DestinationUpdateException(StatusCodes.Status409Conflict, $"{conflict.Id} is already listening on that host and path.");
        }
    }

    private static ProxyApplication CreateApplication(StoredApplication stored)
    {
        var listen = ParseListenUrl(stored.ListenUrl);
        return new ProxyApplication(stored.Id, listen.ListenUrl, listen.Host, listen.PathPrefix, stored.Destinations);
    }

    private static ListenAddress ParseListenUrl(string? listenUrl)
    {
        var value = (listenUrl ?? "").Trim();
        if (value.Contains(' ') || value.Contains('\\'))
        {
            throw new DestinationUpdateException(StatusCodes.Status400BadRequest, "Listen address cannot contain spaces.");
        }

        var wildcard = value.Contains("://*", StringComparison.OrdinalIgnoreCase);
        var parseable = wildcard ? value.Replace("://*", "://wildcard.local", StringComparison.OrdinalIgnoreCase) : value;
        if (!Uri.TryCreate(parseable, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new DestinationUpdateException(StatusCodes.Status400BadRequest, "Listen address must be an absolute http or https URL, such as http://iis.mycomp.com/imaging.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new DestinationUpdateException(StatusCodes.Status400BadRequest, "Listen address cannot include a user, query string, or fragment.");
        }

        var path = uri.AbsolutePath;
        if (path.Length > 1)
        {
            path = path.TrimEnd('/');
        }

        if (path.Length == 0)
        {
            path = "/";
        }

        var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        var hostLabel = wildcard ? "*" : uri.Host;
        return new ListenAddress($"{uri.Scheme}://{hostLabel}{port}{path}", wildcard ? null : uri.Host, path);
    }

    private static string RequireId(string? id, string label)
    {
        var value = (id ?? "").Trim();
        if (!IsValidId(value))
        {
            throw new DestinationUpdateException(StatusCodes.Status400BadRequest, $"{label} must start with a letter or number and use only letters, numbers, dots, underscores, or hyphens.");
        }

        return value;
    }

    private static List<ClusterConfig> ParseClusters(string clustersJson)
    {
        var payload = "{\"Clusters\":" + clustersJson + "}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var clusters = ReadClusters(configuration.GetSection("Clusters"));
        if (clusters.Count == 0)
        {
            throw new InvalidOperationException("Stored cluster settings could not be read.");
        }

        return clusters;
    }

    private static string NormalizeAddress(string? address)
    {
        var value = (address ?? "").Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new DestinationUpdateException(StatusCodes.Status400BadRequest, "Address must be an absolute http or https URL.");
        }

        var builder = new UriBuilder(uri);
        if (string.IsNullOrEmpty(builder.Path))
        {
            builder.Path = "/";
        }
        else if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri.AbsoluteUri;
    }

    private static string? NormalizeHealthInput(string? healthAddress)
    {
        var value = (healthAddress ?? "").Trim();
        return value.Length == 0 ? null : value;
    }

    private static string? TryResolveHealthUrl(string appAddress, string? healthAddress)
    {
        if (string.IsNullOrWhiteSpace(healthAddress))
        {
            return null;
        }

        try
        {
            return ResolveHealthUrl(appAddress, healthAddress);
        }
        catch (DestinationUpdateException)
        {
            return null;
        }
    }

    private static string ResolveHealthUrl(string appAddress, string healthAddress)
    {
        if (healthAddress.Contains(' ') || healthAddress.Contains('\\'))
        {
            throw new DestinationUpdateException(StatusCodes.Status400BadRequest, "Health endpoint cannot contain spaces.");
        }

        if (Uri.TryCreate(healthAddress, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps)
            {
                throw new DestinationUpdateException(StatusCodes.Status400BadRequest, "Health endpoint must be an http or https URL, or a path starting with /.");
            }

            return absolute.AbsoluteUri;
        }

        if (!healthAddress.StartsWith('/'))
        {
            throw new DestinationUpdateException(StatusCodes.Status400BadRequest, "Health endpoint must be a full URL or a path starting with /, such as /health.");
        }

        if (!Uri.TryCreate(appAddress, UriKind.Absolute, out var app))
        {
            throw new DestinationUpdateException(StatusCodes.Status400BadRequest, "The app address is not a valid URL.");
        }

        return new Uri(app, healthAddress).AbsoluteUri;
    }

    private static bool IsValidId(string id)
    {
        if (id.Length is 0 or > 64 || !char.IsAsciiLetterOrDigit(id[0]))
        {
            return false;
        }

        foreach (var character in id)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static List<ClusterConfig> ReadClusters(IConfigurationSection section)
    {
        return section.GetChildren().Select(ReadCluster).ToList();
    }

    private static ClusterConfig ReadCluster(IConfigurationSection section)
    {
        var destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var destination in section.GetSection("Destinations").GetChildren())
        {
            var address = destination["Address"];
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new InvalidOperationException($"Destination '{destination.Key}' needs an Address.");
            }

            destinations.Add(destination.Key, new DestinationConfig { Address = address });
        }

        var health = section.GetSection("HealthCheck");
        var active = health.GetSection("Active");
        var passive = health.GetSection("Passive");
        var affinity = section.GetSection("SessionAffinity");
        var httpClient = section.GetSection("HttpClient");
        var httpRequest = section.GetSection("HttpRequest");

        return new ClusterConfig
        {
            ClusterId = section.Key,
            LoadBalancingPolicy = section["LoadBalancingPolicy"],
            SessionAffinity = affinity.Exists()
                ? new SessionAffinityConfig
                {
                    Enabled = ReadBool(affinity, "Enabled"),
                    Policy = affinity["Policy"],
                    FailurePolicy = affinity["FailurePolicy"],
                    AffinityKeyName = affinity["AffinityKeyName"] ?? ""
                }
                : null,
            HealthCheck = health.Exists()
                ? new HealthCheckConfig
                {
                    AvailableDestinationsPolicy = health["AvailableDestinationsPolicy"],
                    Active = active.Exists()
                        ? new ActiveHealthCheckConfig
                        {
                            Enabled = ReadBool(active, "Enabled"),
                            Interval = ReadTimeSpan(active, "Interval"),
                            Timeout = ReadTimeSpan(active, "Timeout"),
                            Policy = active["Policy"],
                            Path = active["Path"]
                        }
                        : null,
                    Passive = passive.Exists()
                        ? new PassiveHealthCheckConfig
                        {
                            Enabled = ReadBool(passive, "Enabled"),
                            Policy = passive["Policy"],
                            ReactivationPeriod = ReadTimeSpan(passive, "ReactivationPeriod")
                        }
                        : null
                }
                : null,
            HttpClient = httpClient.Exists()
                ? new HttpClientConfig
                {
                    EnableMultipleHttp2Connections = ReadBool(httpClient, "EnableMultipleHttp2Connections")
                }
                : null,
            HttpRequest = httpRequest.Exists()
                ? new ForwarderRequestConfig
                {
                    ActivityTimeout = ReadTimeSpan(httpRequest, "ActivityTimeout"),
                    Version = ReadVersion(httpRequest, "Version"),
                    VersionPolicy = ReadEnum<HttpVersionPolicy>(httpRequest, "VersionPolicy")
                }
                : null,
            Metadata = ReadDictionary(section.GetSection("Metadata")),
            Destinations = destinations
        };
    }

    private static Dictionary<string, string>? ReadDictionary(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            return null;
        }

        return children.ToDictionary(child => child.Key, child => child.Value ?? "", StringComparer.OrdinalIgnoreCase);
    }

    private static bool? ReadBool(IConfigurationSection section, string key)
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value) ? null : bool.Parse(value);
    }

    private static TimeSpan? ReadTimeSpan(IConfigurationSection section, string key)
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value) ? null : TimeSpan.Parse(value);
    }

    private static Version? ReadVersion(IConfigurationSection section, string key)
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value) ? null : Version.Parse(value);
    }

    private static TEnum? ReadEnum<TEnum>(IConfigurationSection section, string key) where TEnum : struct, Enum
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value) ? null : Enum.Parse<TEnum>(value, ignoreCase: true);
    }
}

public sealed record StoredDestination(string Id, string Address, bool Enabled, string? HealthAddress = null);

public sealed record DestinationFile(List<StoredDestination> Destinations);

public sealed record ProxyApplication(
    string Id,
    string ListenUrl,
    string? Host,
    string PathPrefix,
    List<StoredDestination> Destinations);

public sealed record ListenAddress(string ListenUrl, string? Host, string PathPrefix);

public sealed record HealthTarget(string ApplicationId, string DestinationId, string? ProbeUrl);

public sealed record HealthObservation(
    string ApplicationId,
    string DestinationId,
    string ProbeUrl,
    string Status,
    int? StatusCode,
    long? LatencyMs,
    string? Error,
    DateTimeOffset CheckedAt)
{
    public string Key => KeyFor(ApplicationId, DestinationId);

    public static string KeyFor(string applicationId, string destinationId) => applicationId + "\n" + destinationId;
}

public sealed record DestinationView(
    string Id,
    string Address,
    string? HealthAddress,
    string? HealthProbeUrl,
    string AppHealth,
    int? HealthStatusCode,
    long? HealthLatencyMs,
    string? HealthError,
    DateTimeOffset? HealthCheckedAt,
    bool Enabled,
    bool ReceivingTraffic,
    string ActiveHealth,
    string PassiveHealth,
    int ActiveRequests,
    bool Available);

public sealed record ApplicationView(
    string Id,
    string ListenUrl,
    string? Host,
    string Path,
    string LoadBalancingPolicy,
    bool Panic,
    IReadOnlyList<DestinationView> Destinations);

public sealed record OverviewResponse(IReadOnlyList<ApplicationView> Applications);

public sealed class DestinationUpdateException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
