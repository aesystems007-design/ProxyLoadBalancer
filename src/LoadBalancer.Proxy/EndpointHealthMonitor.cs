using System.Diagnostics;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;

namespace LoadBalancer.Proxy;

public sealed class DisabledActiveHealthCheckMonitor : IActiveHealthCheckMonitor
{
    public bool InitialProbeCompleted => true;

    public Task CheckHealthAsync(IEnumerable<ClusterState> clusters)
    {
        return Task.CompletedTask;
    }
}

public sealed class HealthProbeSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Request()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return _signal.WaitAsync(timeout, cancellationToken);
    }
}

public sealed class EndpointHealthMonitor : BackgroundService
{
    private readonly DestinationCatalog _catalog;
    private readonly IProxyStateLookup _lookup;
    private readonly IDestinationHealthUpdater _healthUpdater;
    private readonly HealthProbeSignal _signal;
    private readonly ILogger<EndpointHealthMonitor> _logger;
    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _lastStatus = new(StringComparer.OrdinalIgnoreCase);

    public EndpointHealthMonitor(
        DestinationCatalog catalog,
        IProxyStateLookup lookup,
        IDestinationHealthUpdater healthUpdater,
        HealthProbeSignal signal,
        ILogger<EndpointHealthMonitor> logger)
    {
        _catalog = catalog;
        _lookup = lookup;
        _healthUpdater = healthUpdater;
        _signal = signal;
        _logger = logger;
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProbeAll(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Application health probe failed");
                }

                await _signal.WaitAsync(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _http.Dispose();
        }
    }

    private async Task ProbeAll(CancellationToken stoppingToken)
    {
        var targets = _catalog.HealthTargets();
        var observations = new List<HealthObservation>();
        foreach (var target in targets.Where(target => target.ProbeUrl is not null))
        {
            observations.Add(await ProbeAsync(target, stoppingToken));
        }

        foreach (var observation in observations)
        {
            _catalog.RecordHealth(observation);
            var previous = _lastStatus.GetValueOrDefault(observation.Key);
            if (!string.Equals(previous, observation.Status, StringComparison.Ordinal))
            {
                _lastStatus[observation.Key] = observation.Status;
                _logger.LogInformation(
                    "Endpoint {DestinationId} on {ApplicationId} health is {Status} from {ProbeUrl}",
                    observation.DestinationId,
                    observation.ApplicationId,
                    observation.Status,
                    observation.ProbeUrl);
            }
        }

        Apply(targets, observations);
    }

    private async Task<HealthObservation> ProbeAsync(HealthTarget target, CancellationToken stoppingToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var response = await _http.GetAsync(target.ProbeUrl, stoppingToken);
            var latency = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var healthy = (int)response.StatusCode is >= 200 and < 300;
            return new HealthObservation(
                target.ApplicationId,
                target.DestinationId,
                target.ProbeUrl!,
                healthy ? "Healthy" : "Unhealthy",
                (int)response.StatusCode,
                latency,
                healthy ? null : $"Health endpoint returned {(int)response.StatusCode}.",
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return Failed(target, started, "Health endpoint timed out.");
        }
        catch (HttpRequestException exception)
        {
            return Failed(target, started, exception.InnerException?.Message ?? exception.Message);
        }
    }

    private static HealthObservation Failed(HealthTarget target, long started, string error)
    {
        return new HealthObservation(
            target.ApplicationId,
            target.DestinationId,
            target.ProbeUrl!,
            "Unhealthy",
            null,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Trim(error),
            DateTimeOffset.UtcNow);
    }

    private void Apply(IReadOnlyList<HealthTarget> targets, IReadOnlyList<HealthObservation> observations)
    {
        var byKey = observations.ToDictionary(observation => observation.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var group in targets.GroupBy(target => target.ApplicationId, StringComparer.OrdinalIgnoreCase))
        {
            if (!_lookup.TryGetCluster(group.Key, out var cluster) || cluster is null)
            {
                continue;
            }

            var updates = new List<NewActiveDestinationHealth>();
            foreach (var target in group)
            {
                if (!cluster.Destinations.TryGetValue(target.DestinationId, out var destination))
                {
                    continue;
                }

                DestinationHealth health;
                if (target.ProbeUrl is null)
                {
                    health = DestinationHealth.Unknown;
                }
                else if (byKey.TryGetValue(HealthObservation.KeyFor(target.ApplicationId, target.DestinationId), out var observation))
                {
                    health = observation.Status == "Healthy" ? DestinationHealth.Healthy : DestinationHealth.Unhealthy;
                }
                else
                {
                    continue;
                }

                updates.Add(new NewActiveDestinationHealth(destination, health));
            }

            if (updates.Count > 0)
            {
                _healthUpdater.SetActive(cluster, updates);
            }
        }
    }

    private static string Trim(string message)
    {
        var line = message.Split('\r', '\n')[0].Trim();
        return line.Length <= 180 ? line : line[..180];
    }
}
