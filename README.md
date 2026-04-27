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
- **Issues and renews certificates from Let's Encrypt** automatically over the ACME HTTP-01 challenge. A daily background worker renews any cert whose expiry is within the configured threshold; the SNI cache hot-swaps with no restart.

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
- `/certificates` — list, upload, delete; manually trigger ACME renewal
- `/certificates/upload` — paste a PEM cert + key
- `/certificates/request` — request a fresh certificate from Let's Encrypt
- `/settings` — read-only view of storage, runtime info, and ACME state

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

1. Get a certificate. Either:
   - **Request one from Let's Encrypt** (recommended for public hosts) — see [Let's Encrypt automation](#lets-encrypt-automation) below.
   - **Upload an existing PEM pair** via the UI (`/certificates/upload`) or:

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

## Let's Encrypt automation

HomeYarp can replace certbot+nginx for issuance and renewal. It speaks ACME v2 (HTTP-01 challenge) directly to Let's Encrypt via [Certes](https://github.com/fszlin/certes), persists the account key under `{DataRoot}/acme/`, and runs a daily background worker that renews any cert whose expiry is within the configured threshold.

### How it works

1. The HTTP listener (`HomeYarp:Listeners:Http`) hosts an endpoint at `GET /.well-known/acme-challenge/{token}`. Inbound port 80 must reach this listener.
2. When you request a cert, `AcmeService` registers an account on first use, places an order, publishes each challenge response to an in-memory token store, calls `validate`, polls until the challenge is verified, generates a fresh ES256 key, downloads the chain, and saves the PEM pair through the same repository the manual upload flow uses.
3. The certificate's `Acme` block records the hostnames + directory URL + key type. A `BackgroundService` wakes up daily, lists every cert with that block populated, and renews anything within `RenewBefore` of expiry. The SNI cache hot-swaps as soon as the new material lands on disk.

### Enable it

Edit `appsettings.json`:

```json
"HomeYarp": {
  "Acme": {
    "Enabled": true,
    "AccountEmail": "you@example.com",
    "AgreeToTermsOfService": true,
    "DirectoryUrl": "https://acme-v02.api.letsencrypt.org/directory",
    "KeyType": "Ec256",
    "RenewBefore": "30.00:00:00",
    "RenewalInterval": "24:00:00",
    "StartupDelay": "00:01:00"
  }
}
```

| Key | Default | Notes |
|---|---|---|
| `Enabled` | `false` | Master switch. The renewal worker exits immediately when false. |
| `AccountEmail` | `""` | Required for issuance. Used by Let's Encrypt for expiry warnings. |
| `AgreeToTermsOfService` | `false` | Required for issuance. By setting this you agree to the [Let's Encrypt Subscriber Agreement](https://letsencrypt.org/repository/). |
| `DirectoryUrl` | LE production | Use `https://acme-staging-v02.api.letsencrypt.org/directory` while testing — staging has effectively no rate limits. |
| `KeyType` | `Ec256` | Or `Rsa2048`. |
| `RenewBefore` | `30.00:00:00` | Renew when `NotAfter - now` drops below this. |
| `RenewalInterval` | `24:00:00` | How often the background worker checks. |
| `StartupDelay` | `00:01:00` | Grace period before the first tick after startup. |

> **Test against staging first.** LE production has tight per-domain rate limits (50 certs/registered domain/week). Run one full issue + renew cycle against staging before flipping `DirectoryUrl` to production.

### Request a certificate

#### Via the UI

`/certificates` → **+ Request via Let's Encrypt** → fill in name + hostnames (one per line) → submit. Issuance typically takes 30–60 seconds. The cert appears in the list with an **ACME** badge.

#### Via the API

```bash
curl -X POST http://localhost:5268/api/certificates/acme \
  -H 'Content-Type: application/json' \
  -d '{
    "name": "cloud-perezfaulks",
    "friendlyName": "Cloud (Let's Encrypt)",
    "hostnames": ["cloud.perezfaulks.com"]
  }'
```

Bind the resulting `id` to an application's `tls.certificateId` (with `tls.mode = 1`) and you're done.

### Manual renewal

Renewal happens automatically. To trigger it on demand:

- UI: `/certificates` → **Renew now** on any ACME-managed cert.
- API: `POST /api/certificates/{id}/renew`.

### Requirements and limits

- **Inbound port 80 must reach HomeYarp's HTTP listener** for HTTP-01 to validate. In production, listen on `:80` (or port-forward `80 → HomeYarp:Listeners:Http`).
- Each requested hostname must publicly resolve to your server.
- **Wildcards (`*.example.com`) are not supported.** They require DNS-01, which needs a per-DNS-provider plugin and is not implemented.
- Manually-uploaded certificates are never renewed by the background worker — only certs whose `Acme` metadata is set.

### Migrating from certbot

You don't need to copy anything. On cutover:

1. Repoint your firewall/router so port 80 → HomeYarp's HTTP listener (and 443 → its HTTPS-offload listener).
2. Configure `HomeYarp:Acme` and start with the staging directory.
3. For each domain, either re-issue through HomeYarp (recommended — the new cert gets ACME metadata so it renews automatically), or upload the existing certbot-issued PEM pair via `/certificates/upload`.
4. Switch `DirectoryUrl` back to production. Stop and remove the certbot+nginx stack.

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
    },
    "Acme": {
      "Enabled": false,
      "AccountEmail": "",
      "AgreeToTermsOfService": false,
      "DirectoryUrl": "https://acme-v02.api.letsencrypt.org/directory",
      "KeyType": "Ec256",
      "RenewBefore": "30.00:00:00",
      "RenewalInterval": "24:00:00",
      "StartupDelay": "00:01:00"
    }
  }
}
```

- `Storage.DataRoot` — relative paths resolve against `AppContext.BaseDirectory` (i.e. `bin/.../net10.0/data` in dev). Absolute paths are honored as-is.
- Any listener can be disabled by setting it to `null` or `0`.
- For production, point the listeners at `80` / `443` / `8443` (or wherever your port forwarding lands) and override the `DataRoot` to a stable absolute path. HTTP-01 challenges always arrive on the `Http` listener.
- ACME settings are read at startup — change them and restart. See [Let's Encrypt automation](#lets-encrypt-automation) above for the full table of knobs.

## Storage layout

```
{DataRoot}/
  applications/
    {guid}.json             # one file per application
  certificates/
    {guid}.json             # cert manifest (parsed metadata + optional ACME block)
    {guid}.cert.pem         # PEM-encoded certificate (chain)
    {guid}.key.pem          # PEM-encoded private key (unencrypted)
  acme/
    account.{sha8}.pem      # ACME account key (one per directory URL)
    account.{sha8}.json     # ACME account manifest (email, registration URL)
```

Files are gitignored under `HomeYarp.WebServer/data/`.

## Architecture

Clean Architecture, four projects:

```
HomeYarp.WebServer  ──►  HomeYarp.Application  ──►  HomeYarp.Domain
                  │
                  └────►  HomeYarp.Persistance  ──►  HomeYarp.Domain
```

- `HomeYarp.Domain` — aggregates and value types: `Application`, `Certificate`, `AcmeMetadata`, `RouteDefinition`, `ClusterDefinition`, `DestinationDefinition`, `TlsConfiguration`, `TlsMode`, `AcmeKeyType`.
- `HomeYarp.Application` — services (`ApplicationService`, `CertificateService`, `AcmeService`), repository abstractions, the YARP bridge (`HomeYarpConfigProvider`), TLS routing (`SniCertificateSelector`, `TlsPassthroughConnectionHandler`, `TlsClientHelloParser`), and ACME automation (`Acme/` namespace: `IAcmeChallengeStore`, `IAcmeAccountStore`, `AcmeRenewalService`).
- `HomeYarp.Persistance` — JSON + PEM file storage with in-memory cache and change-token signalling. Includes `FileAcmeAccountStore`.
- `HomeYarp.WebServer` — composition root: REST controllers, Blazor Server pages, Kestrel listener configuration, and the `/.well-known/acme-challenge/{token}` endpoint.

See [`CLAUDE.md`](CLAUDE.md) for deeper details about the reload chain, DI lifetimes, validation rules, and design decisions.

## Limitations / not yet

- **Private keys are stored unencrypted on disk** (including the ACME account key). Acceptable for a home lab; encryption at rest is a planned follow-up.
- **No authentication on the management API or UI.** Run it on a trusted network only. Auth on top of the management surface is also a planned follow-up.
- **`AuthorizationPolicy` on `Application` is a placeholder field.** Per-route ASP.NET Core authorization policies are not wired yet.
- **ACME supports HTTP-01 only.** No DNS-01, so wildcard certificates can't be issued. ACME options are read once at startup — editing `appsettings.json` requires a restart.
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
