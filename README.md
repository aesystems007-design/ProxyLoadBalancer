# Proxy Load Balancer

A .NET reverse proxy and load balancer for several applications on one listener. Each application is a host and path you receive traffic on, such as `http://iis.mycomp.com/imaging`, and a set of backend servers that share that traffic.

The proxy is [YARP](https://microsoft.github.io/reverse-proxy/) 2.3 on .NET 10. A Vue 3 dashboard edits the configuration, and changes are saved to SQLite and applied to the running proxy without a restart.

## What a request does

1. The proxy matches the request host and path to one application.
2. A longer path wins when two applications overlap. A host-specific application wins over one that matches any host.
3. The request is forwarded to one of that application's enabled backends. The original path is kept.
4. Backends are chosen round-robin. A backend whose health check is failing is left out of rotation.

The port in a listen URL is kept for display. Matching uses the host name only, so `http://localhost:5080/imaging` matches `localhost` and `/imaging` on whatever port the proxy is bound to.

A path of `/` matches every path. A prefix such as `/imaging` also matches paths under it, including `/imaging/api/orders`. Two applications cannot share the same host and path.

## Applications and backends

An application has:

- a name, which is also its route and cluster id
- a listen URL (`http` or `https`, absolute, no user, query, or fragment)
- one or more backend addresses

`*` as the host matches any host. A blank domain in the dashboard does the same.

Each backend can have its own health URL. Leave it blank and that backend is not probed and stays in rotation. A path such as `/health` is resolved against the backend address. An absolute `http` or `https` URL is used as-is.

The dashboard can pause a backend, remove it, or send traffic to a chosen subset. Paused backends are removed from the YARP cluster until they are resumed.

If every enabled backend is unavailable, traffic still flows to them. That is YARP's `HealthyOrPanic` policy, so a failed health check does not take the whole application offline.

## Health checks

The proxy probes each configured health URL about every 10 seconds, with a 5 second timeout. A response from 200 through 299 is healthy. Any other status, a timeout, or a connection failure marks that backend unhealthy and removes it from rotation. Passive checks also watch transport failures.

YARP's built-in active health monitor is disabled. These probes are the only active checks.

## Configuration

`Proxy:ConfigStore` selects where that document is stored. `Sqlite` is the default. The first start copies `ReverseProxy` from `src/LoadBalancer.Proxy/appsettings.json` into `src/LoadBalancer.Proxy/App_Data/proxy.db`. After that, the database is the configuration that loads and reloads. `App_Data/` is not committed.

`SqlServer` reads `ConnectionStrings:Proxy` and uses `SqlServerProxyConfigStore`. That class is a stub: implement `EnsureReady`, `Read`, `Insert`, and `UpdateApplications` against one `proxy_config` row. The table script is `SqlServerProxyConfigStore.TableSql`. Routing and the dashboard stay the same. Another proxy process using that database does not pick up saves until it restarts.

The seeded cluster is one application named `app`, listening on any host, with destinations `http://localhost:8081/` and `http://localhost:8082/`. Replace those with the IIS sites, or other HTTP servers, you want to balance. Delete `proxy.db` and its `-wal` and `-shm` files to seed again from `appsettings.json`.

Saving in the dashboard writes the database and calls YARP's in-memory config update. Requests already in flight finish on the previous configuration. There is no separate review or publish queue.

`Proxy:ExposeStatus` controls `GET /proxy/status`. `Proxy:LogChosenDestination` logs which backend handled each request.

## Dashboard

The dashboard is at `/_proxy/dashboard/` after the proxy starts.

| Area | What it shows |
| --- | --- |
| Dashboard | Application count, backends in rotation, unhealthy backends |
| Applications | Select an application and edit it |
| Routes | Listen host and path for every application |
| Upstreams | Backend addresses |
| Health Checks | Probe results |
| Certificates | Not stored by this proxy |
| Settings | How configuration is applied |
| Audit Log | Not recorded |

Inside an application, tabs cover the listen route, backends, traffic selection, and health URLs. The application name cannot be renamed after it is created. Load-balancing policy, session affinity, and HTTP client settings come from the cluster template in the database. The dashboard does not edit those, and it does not configure authentication, WebSocket, caching, or an HTTPS-only flag.

`GET /proxy/health` returns the proxy process status. It is not an application health check.

## Run

Install the .NET 10 SDK and Node.js, then build the dashboard and start the proxy:

```powershell
cd src/dashboard
npm install
npm run build
cd ../..
dotnet run --project src/LoadBalancer.Proxy --launch-profile http
```

The proxy listens on `http://localhost:5080`. Open `http://localhost:5080/_proxy/dashboard/`.

`npm run dev` in `src/dashboard` starts the Vite dev server and proxies `/_proxy/api` to `http://localhost:5080`. The proxy still has to be running.

There is an `https` launch profile on `https://localhost:5443` and `http://localhost:5080`. The proxy does not terminate or store TLS certificates for the applications it routes.

## License

MIT. See [LICENSE](LICENSE).
