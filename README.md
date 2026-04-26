# HomeYarp

A YARP-based reverse proxy with a built-in REST API and Blazor UI for managing applications, certificates, and TLS modes. Designed for home labs.

## What it does

- Reverse proxies HTTP and HTTPS traffic to backend services using [YARP](https://microsoft.github.io/reverse-proxy/).
- Lets you define **applications** (name + frontend routes + backend cluster) via a REST API or a Blazor UI.
- Persists everything as JSON files on disk (one file per app, one PEM pair + manifest per certificate).
- Hot-reloads route, cluster, and certificate changes without restarting — `IChangeToken` plumbing rebuilds the YARP snapshot and the SNI certificate cache on every mutation.
- Supports two per-application TLS modes:
  - **Offload** — proxy terminates TLS at its HTTPS port using a per-app cert, then forwards plain HTTP to the backend.
  - **Passthrough** — proxy peeks the TLS ClientHello SNI, opens a raw TCP socket to the backend, and bidirectionally pumps bytes. The proxy never decrypts; the backend terminates TLS itself.

## Requirements

- .NET 10 SDK

## Quick start

```bash
git clone <this-repo>
cd HomeYarp
dotnet build HomeYarp.WebServer.slnx
dotnet run --project HomeYarp.WebServer/HomeYarp.WebServer.csproj
```

Defaults in development:

- `http://localhost:5268` — UI, management API, plain HTTP proxy
- `https://localhost:5443` — HTTPS offload (TLS terminated by HomeYarp)
- TCP `localhost:5444` — HTTPS passthrough (raw L4 tunnel; speak TLS to it as if it were the backend)

OpenAPI docs are exposed at `http://localhost:5268/openapi/v1.json` in Development. Sample requests live in `HomeYarp.WebServer/HomeYarp.WebServer.http`.

## Web UI

- `/` — landing page
- `/applications` — list, create, edit, delete proxied apps
- `/certificates` — list, upload, delete certificates
- `/settings` — read-only view of storage and runtime info

## Defining an application

### Via the UI

`/applications` → **+ New application**. Fill in the slug, hosts, path, destinations, optionally a TLS mode, then save.

### Via the API

```bash
curl -X POST http://localhost:5268/api/applications \
  -H 'Content-Type: application/json' \
  -d '{
    "name": "grafana",
    "displayName": "Grafana",
    "routes": [{ "hosts": ["grafana.home.lan"], "path": "/{**catch-all}" }],
    "cluster": {
      "destinations": [{ "name": "primary", "address": "http://192.168.1.50:3000" }]
    }
  }'
```

The proxy is now reachable at the HTTP listener. Setting your local DNS or a `Host:` header to `grafana.home.lan` forwards the request to `http://192.168.1.50:3000`.

## Enabling TLS

### Offload (proxy terminates TLS)

1. Upload a PEM certificate. Either via the UI (`/certificates/upload`) or:

   ```bash
   curl -X POST http://localhost:5268/api/certificates \
     -H 'Content-Type: application/json' \
     -d "$(jq -n --rawfile cert ./fullchain.pem --rawfile key ./privkey.pem \
            '{name:"home-lan", friendlyName:"*.home.lan", certificatePem:$cert, privateKeyPem:$key}')"
   ```

2. Bind the certificate to an application by setting `tls.mode = 1` (Offload) and `tls.certificateId = <cert-id>`.

3. The application is now reachable on the offload port (default `5443`). HomeYarp uses the certificate matching the SNI hostname; both exact (`grafana.home.lan`) and wildcard (`*.home.lan`) entries are supported.

### Passthrough (backend terminates TLS)

Set `tls.mode = 2` (Passthrough). The application is reachable on the passthrough port (default `5444`). HomeYarp peeks the SNI from the TLS ClientHello, opens a TCP socket to the first destination's host:port, replays the buffered bytes, and pipes both directions. The certificate field is unused — the backend presents its own certificate to the client.

This is useful for backends that already have their own valid certificates (e.g. Home Assistant, Synology DSM, anything with an embedded TLS stack you don't want to terminate at the edge).

## Configuration

`HomeYarp.WebServer/appsettings.json`:

```json
{
  "HomeYarp": {
    "Storage": {
      "DataRoot": "data"
    },
    "Listeners": {
      "Http": 5268,
      "HttpsOffload": 5443,
      "HttpsPassthrough": 5444
    }
  }
}
```

- `Storage.DataRoot` — relative paths resolve against `AppContext.BaseDirectory` (i.e. `bin/.../net10.0/data` in dev). Absolute paths are honored as-is.
- Any listener can be disabled by setting it to `null` or `0`.
- For production, point the listeners at `80` / `443` / `8443` (or wherever your port forwarding lands) and override the `DataRoot` to a stable absolute path.

## Storage layout

```
{DataRoot}/
  applications/
    {guid}.json             # one file per application
  certificates/
    {guid}.json             # cert manifest (parsed metadata)
    {guid}.cert.pem         # PEM-encoded certificate (chain)
    {guid}.key.pem          # PEM-encoded private key (unencrypted)
```

Files are gitignored under `HomeYarp.WebServer/data/`.

## Architecture

Clean Architecture, four projects:

```
HomeYarp.WebServer  ──►  HomeYarp.Application  ──►  HomeYarp.Domain
                  │
                  └────►  HomeYarp.Persistance  ──►  HomeYarp.Domain
```

- `HomeYarp.Domain` — aggregates and value types: `Application`, `Certificate`, `RouteDefinition`, `ClusterDefinition`, `DestinationDefinition`, `TlsConfiguration`, `TlsMode`.
- `HomeYarp.Application` — services (`ApplicationService`, `CertificateService`), repository abstractions, the YARP bridge (`HomeYarpConfigProvider`), and TLS routing (`SniCertificateSelector`, `TlsPassthroughConnectionHandler`, `TlsClientHelloParser`).
- `HomeYarp.Persistance` — JSON + PEM file storage with in-memory cache and change-token signalling.
- `HomeYarp.WebServer` — composition root: REST controllers, Blazor Server pages, Kestrel listener configuration.

See [`CLAUDE.md`](CLAUDE.md) for deeper details about the reload chain, DI lifetimes, validation rules, and design decisions.

## Limitations / not yet

- **Private keys are stored unencrypted on disk.** Acceptable for a home lab; encryption at rest is a planned follow-up.
- **No authentication on the management API or UI.** Run it on a trusted network only. Auth on top of the management surface is also a planned follow-up.
- **`AuthorizationPolicy` on `Application` is a placeholder field.** Per-route ASP.NET Core authorization policies are not wired yet.
- **No automated tests.** Verification is currently manual via the `.http` file and the smoke flow described above.
- **Offload and passthrough must live on different ports.** Kestrel cannot host both an HTTPS-terminating endpoint and a raw-TCP `ConnectionHandler` on the same listener. If single-port unified TLS routing matters to you, that's a future design topic.

## Development

```bash
# Hot reload
dotnet watch --project HomeYarp.WebServer/HomeYarp.WebServer.csproj

# Build the whole solution
dotnet build HomeYarp.WebServer.slnx
```

Visual Studio: open `HomeYarp.WebServer.slnx`.
