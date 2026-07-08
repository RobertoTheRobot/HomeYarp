# HomeYarp

A YARP-based reverse proxy with a built-in REST API and Blazor UI for managing applications, certificates, and TLS modes. Designed for home labs.

## What it does

- Reverse proxies HTTP and HTTPS traffic to backend services using [YARP](https://microsoft.github.io/reverse-proxy/).
- Lets you define **applications** (name + frontend routes + backend cluster) via a REST API, a Blazor UI, or an **MCP server** — every management operation is exposed as a [Model Context Protocol](https://modelcontextprotocol.io/) tool so AI agents can drive the proxy (see [MCP server](#mcp-server)).
- Persists everything as JSON files on disk (one file per app, one PEM pair + manifest per certificate).
- Hot-reloads route, cluster, and certificate changes without restarting — `IChangeToken` plumbing rebuilds the YARP snapshot and the SNI certificate cache on every mutation.
- Supports two per-application TLS modes:
  - **Offload** — proxy terminates TLS at its HTTPS port using a per-app cert, then forwards plain HTTP to the backend.
  - **Passthrough** — proxy peeks the TLS ClientHello SNI, opens a raw TCP socket to the backend, and bidirectionally pumps bytes. The proxy never decrypts; the backend terminates TLS itself.
- **Three certificate sources**, all interchangeable from the proxy's point of view:
  - **Manual** — paste a PEM cert + key (or POST one).
  - **Self-signed** — HomeYarp generates a key and self-signs for the requested hostnames. Wildcards and IP SANs supported. Use for internal-only services. Auto-renewed by a daily background worker that regenerates any self-signed cert within the configured expiry threshold.
  - **Let's Encrypt** — issued and auto-renewed via ACME HTTP-01. A daily background worker renews any cert within the configured expiry threshold.
- **One-step TLS via per-app `Internal` / `External` toggle.** Pick `Internal` and HomeYarp self-signs for the route hostnames. Pick `External` and HomeYarp orders from Let's Encrypt. The cert's lifecycle is tied to the app — created on save, regenerated when hostnames change, deleted when the app is deleted.

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

- `http://localhost:5268` — plain HTTP proxy + ACME HTTP-01 challenges (the public listener)
- `https://localhost:5443` — HTTPS offload (TLS terminated by HomeYarp)
- TCP `localhost:5444` — HTTPS passthrough (raw L4 tunnel; speak TLS to it as if it were the backend)
- `http://localhost:5269` — **management**: UI, REST API, MCP endpoint (`/mcp`)

The management surface only routes on connections that physically arrive on the management port (`HomeYarp:Listeners:Management`) — the check uses the connection's local port, not the spoofable `Host` header. In a deployment, publish/forward the first three listeners to the internet as needed and keep the management port LAN-only (don't port-forward it at your firewall); management is then unreachable from the WAN at the network layer. The management endpoints themselves remain unauthenticated — expose the port on a trusted network only.

OpenAPI docs are exposed at `http://localhost:5269/openapi/v1.json` in Development. Sample requests live in `HomeYarp.WebServer/HomeYarp.WebServer.http`.

## Web UI

- `/` — landing page
- `/applications` — list, create, edit, delete proxied apps. Each row that points at a TLS cert exposes a **Download** button (downloads the public PEM, no private key) and — for self-signed certs — a **trust-instructions** dialog with copy-paste snippets for the Windows / Linux / macOS trust stores, Docker daemon, Docker Desktop, and Firefox.
- `/applications/new` and `/applications/{id}` — **Simple** form for the everyday fields (identity, routes, TLS, cluster) plus an **Advanced (JSON)** toggle that opens a VS-Code-style editor for the full domain JSON, including YARP transforms, health checks, and HTTP request options
- `/certificates` — list, upload, delete; trigger ACME renewal or self-signed regeneration. Each row has the same **Download** + **trust-instructions** affordances as the applications page.
- `/certificates/upload` — paste a PEM cert + key
- `/certificates/generate` — generate a self-signed certificate
- `/certificates/request` — request a fresh certificate from Let's Encrypt
- `/settings` — read-only view of storage, runtime info, and ACME state

## Defining an application

### Via the UI

`/applications` → **+ New application**. Fill in the slug, hosts, path, destinations, optionally a TLS mode, then save.

### Via the API

```bash
curl -X POST http://localhost:5269/api/applications \
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

## Advanced configuration (JSON editor)

The simple form covers the everyday cases. For YARP features that aren't on the form — **request/response transforms**, **active and passive health checks**, **per-cluster HTTP request options** (timeout, HTTP version) — flip the **Advanced (JSON)** toggle at the top of the application edit page. You get a Monaco editor (the same component VS Code uses), `vs-dark` theme, JSON syntax highlighting, format-on-paste, and line numbers.

All advanced fields are nullable / optional — leaving them out preserves current behavior.

```jsonc
{
  "name": "api",
  "routes": [
    {
      "hosts": ["api.example.com"],
      "path": "/{**catch-all}",
      "transforms": [
        { "PathSet": "/api/v2" },
        { "RequestHeader": "X-Forwarded-User", "Set": "anonymous" }
      ]
    }
  ],
  "cluster": {
    "destinations": [{ "name": "primary", "address": "http://192.168.1.50:3000" }],
    "healthCheck": {
      "active": {
        "enabled": true,
        "interval": "00:00:30",
        "timeout": "00:00:05",
        "policy": "ConsecutiveFailures",
        "path": "/healthz"
      },
      "passive": {
        "enabled": true,
        "policy": "TransportFailureRate",
        "reactivationPeriod": "00:01:00"
      }
    },
    "httpRequest": {
      "activityTimeout": "00:00:45",
      "version": "2.0",
      "versionPolicy": "RequestVersionExact"
    }
  }
}
```

The editor's Save button parses the JSON, validates via the same service path the simple form uses (so name uniqueness, TLS rules, and auto-managed cert lifecycle still apply), and persists. Switching back to **Simple** is blocked while the JSON is malformed — fix it first.

> The advanced fields (`transforms`, `healthCheck`, `httpRequest`) are not exposed by `POST /api/applications` — the REST DTOs only carry the simple-form fields. The JSON editor goes through the in-process service and is the supported way to set them.

## MCP server

HomeYarp exposes its entire management API as 14 [Model Context Protocol](https://modelcontextprotocol.io/) tools, served over Streamable HTTP at `/mcp` on the HTTP listener — same process as the REST API and Blazor UI. Point an MCP-compatible AI client at it and manage applications and certificates via natural language ("create a passthrough app for Home Assistant at 192.168.1.50", "which certs expire this month?", "renew the cloud cert now").

| Tool | Mirrors |
|---|---|
| `list_applications`, `get_application` | `GET /api/applications[/{id}]` |
| `create_application`, `update_application`, `delete_application` | `POST` / `PUT` / `DELETE /api/applications` |
| `list_certificates`, `get_certificate` | `GET /api/certificates[/{id}]` |
| `download_certificate_pem` | `GET /api/certificates/{id}/download` (public chain, no private key) |
| `upload_certificate` | `POST /api/certificates` |
| `issue_self_signed_certificate`, `regenerate_certificate` | `POST /api/certificates/self-signed`, `POST /api/certificates/{id}/regenerate` |
| `issue_acme_certificate`, `renew_certificate` | `POST /api/certificates/acme`, `POST /api/certificates/{id}/renew` |
| `delete_certificate` | `DELETE /api/certificates/{id}` |

The tools call the same in-process services as the REST controllers, so name uniqueness, TLS validation, ACME gating, the auto-managed cert lifecycle, and hot reload all behave identically. Failures come back as MCP tool errors with the underlying message ("Validation failed: …", "…was not found.", name-conflict text) instead of HTTP status codes. The same advanced-fields limitation as the REST API applies (transforms / healthCheck / httpRequest are not in the DTOs — use the Blazor JSON editor for those).

### Connecting a client

**Claude Code:**

```bash
claude mcp add --transport http homeyarp http://localhost:5269/mcp
```

**Any client that reads an `mcp.json`-style config** (Claude Code project `.mcp.json`, VS Code, Cursor):

```json
{
  "mcpServers": {
    "homeyarp": {
      "type": "http",
      "url": "http://localhost:5269/mcp"
    }
  }
}
```

**Claude Desktop** doesn't take remote HTTP servers in `claude_desktop_config.json` directly — add it as a custom connector (Settings → Connectors → Add custom connector, URL `http://<host>:5269/mcp`), or bridge it through [`mcp-remote`](https://www.npmjs.com/package/mcp-remote):

```json
{
  "mcpServers": {
    "homeyarp": {
      "command": "npx",
      "args": ["mcp-remote", "http://localhost:5269/mcp"]
    }
  }
}
```

> **No authentication.** Like the REST API and UI, the MCP endpoint is unauthenticated — expose it on a trusted network only. `/mcp` lives on the management listener (`HomeYarp:Listeners:Management`) together with the rest of the management surface, so keep that port off any WAN port-forward. If you've additionally configured `HomeYarp:Management:Hosts`, the same host allowlist applies on top.

## Enabling TLS

### Offload (proxy terminates TLS)

The application's `tls` block selects a **mode** (how TLS is handled at the listener) and a **certificate source** (where the cert comes from):

```json
"tls": { "mode": 1, "source": 1 }                                  // Offload, Internal (self-signed)
"tls": { "mode": 1, "source": 2 }                                  // Offload, External (Let's Encrypt)
"tls": { "mode": 1, "source": 0, "certificateId": "<existing>" }   // Offload, Manual (existing cert)
"tls": { "mode": 2 }                                               // Passthrough
```

`source`: `0` = Manual (default), `1` = Internal, `2` = External. For Internal and External, HomeYarp creates the cert on save using the route hostnames as SANs, regenerates it on hostname change, and deletes it when the app is deleted. `certificateId` is populated automatically.

#### One-step path: Internal (self-signed)

For internal-only services that aren't reachable from the public internet:

```bash
curl -X POST http://localhost:5269/api/applications \
  -H 'Content-Type: application/json' \
  -d '{
    "name": "home-assistant",
    "routes": [{ "hosts": ["ha.home.lan"], "path": "/{**catch-all}" }],
    "cluster": { "destinations": [{ "name": "primary", "address": "http://192.168.1.50:8123" }] },
    "tls": { "mode": 1, "source": 1 }
  }'
```

HomeYarp self-signs a cert named `home-assistant-internal` covering `ha.home.lan`, binds it to the app, and serves it on `:5443`. Clients need to trust HomeYarp's cert (or be told to ignore the warning) since it isn't issued by a public CA.

#### One-step path: External (Let's Encrypt)

For publicly-reachable services with port-80 ingress and DNS resolving to your server. ACME must be configured first — see [Let's Encrypt automation](#lets-encrypt-automation).

```json
"tls": { "mode": 1, "source": 2 }
```

Save the application. HomeYarp orders a cert from Let's Encrypt over HTTP-01 using the route hostnames and binds it. The daily renewal worker keeps it fresh.

#### Manual (pick an existing cert)

Useful when one cert (e.g. a wildcard) serves many apps:

1. Get a certificate by uploading PEM material, generating self-signed, or requesting from Let's Encrypt independently. Examples:

   ```bash
   # Upload existing PEM
   curl -X POST http://localhost:5269/api/certificates \
     -H 'Content-Type: application/json' \
     -d "$(jq -n --rawfile cert ./fullchain.pem --rawfile key ./privkey.pem \
            '{name:"home-lan", friendlyName:"*.home.lan", certificatePem:$cert, privateKeyPem:$key}')"

   # Generate self-signed
   curl -X POST http://localhost:5269/api/certificates/self-signed \
     -H 'Content-Type: application/json' \
     -d '{"name":"home-lan-wildcard","hostnames":["*.home.lan"],"keyType":0,"validityDays":365}'
   ```

2. Bind by setting `tls.mode = 1`, `tls.source = 0`, and `tls.certificateId = <cert-id>`.

3. The app is reachable on the offload port (default `5443`). HomeYarp picks the cert by SNI; exact and wildcard hostnames are both supported.

### Passthrough (backend terminates TLS)

Set `tls.mode = 2` (Passthrough). The application is reachable on the passthrough port (default `5444`). HomeYarp peeks the SNI from the TLS ClientHello, opens a TCP socket to the first destination's host:port, replays the buffered bytes, and pipes both directions. The certificate field is unused — the backend presents its own certificate to the client.

This is useful for backends that already have their own valid certificates (e.g. Home Assistant, Synology DSM, anything with an embedded TLS stack you don't want to terminate at the edge).

## Self-signed certificates

For internal-only services where Let's Encrypt won't work (no public DNS, no port-80 ingress, RFC1918 hostnames, IP-only services), HomeYarp can generate a self-signed cert itself. No external dependencies, no NuGet beyond the BCL.

The simplest path is per-application via `tls.source = 1` (see [Enabling TLS](#enabling-tls) above). To generate a cert independently of any application — useful when one cert covers many apps — use:

### Via the UI

`/certificates` → **+ Generate self-signed** → fill in name + hostnames (one per line) + validity + key type → submit. Wildcards (`*.home.lan`) and IP addresses are both accepted.

### Via the API

```bash
curl -X POST http://localhost:5269/api/certificates/self-signed \
  -H 'Content-Type: application/json' \
  -d '{
    "name": "home-assistant-internal",
    "friendlyName": "Home Assistant (internal)",
    "hostnames": ["ha.home.lan", "192.168.1.50", "*.lab.home.lan"],
    "keyType": 0,
    "validityDays": 365
  }'
```

`keyType`: `0` = EC P-256 (default, recommended), `1` = RSA 2048. `validityDays` defaults to `365`. DNS hostnames go into the SAN as `DNS:` entries; entries that parse as `IPAddress` go in as `IP Address:` entries.

### Renewal

A `SelfSignedRenewalService` background worker auto-rotates self-signed certs. It lists every cert with the `SelfSigned` block populated and regenerates anything within `RenewBefore` of expiry (default: 30 days). The SNI cache hot-swaps the new key as soon as the regenerated material lands on disk — clients see a fresh cert on their next handshake.

Configure under `HomeYarp:SelfSigned` in `appsettings.json`:

```json
"SelfSigned": {
  "Enabled": true,
  "RenewBefore": "30.00:00:00",
  "RenewalInterval": "24:00:00",
  "StartupDelay": "00:01:00"
}
```

`Enabled: true` is the default (self-signed renewal is purely local — no rate limits or external deps). Flip to `false` to disable the worker and rotate manually.

To regenerate in place on demand (same id, fresh key, fresh expiry):

- UI: `/certificates` → **Regenerate** on any self-signed cert.
- API: `POST /api/certificates/{id}/regenerate`.

> **Heads up:** clients need to trust the cert (import into the OS/browser trust store, or accept the warning). Self-signed is fine for internal use; it's not a substitute for a publicly-rooted CA.

### Trusting the cert on a client

The certificate page (`/certificates`) and the applications page (`/applications`) each expose a download icon on every row tied to a self-signed cert and a "How to trust this cert" icon next to it. The download serves the public PEM only (no private key) at `GET /api/certificates/{id}/download`; the trust dialog has copy-paste snippets for:

- Windows system store (`Import-Certificate ... -CertStoreLocation Cert:\LocalMachine\Root` from elevated PowerShell)
- Debian/Ubuntu (`/usr/local/share/ca-certificates/*.crt` + `update-ca-certificates`) and RHEL/Fedora (`/etc/pki/ca-trust/source/anchors/` + `update-ca-trust`)
- macOS Keychain (`security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain`)
- Docker daemon (`/etc/docker/certs.d/<host>/ca.crt`) and Docker Desktop on Windows
- Firefox (which keeps its own CA list — manual import via Settings → Privacy & Security → Certificates)

**These stores are independent — trusting one doesn't cover the others.** In particular, if you're
pushing to a registry fronted by HomeYarp using the .NET SDK's built-in container support
(`dotnet publish -t:PublishContainer` / `-p:PublishProfile=DefaultContainer`), that push goes
through the SDK's own HTTP registry client, not the Docker daemon — it relies on `SslStream`/the
**OS CA store**, so the Docker-daemon snippet above won't help it. Trust the OS-store snippet
instead (or in addition, if the same machine also runs plain `docker pull`/`push` against the
same host).

ACME-issued and manually-uploaded certs don't need a trust step (their CA is already in the OS store), so the trust dialog only appears for the self-signed source.

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
curl -X POST http://localhost:5269/api/certificates/acme \
  -H 'Content-Type: application/json' \
  -d '{
    "name": "cloud-example",
    "friendlyName": "Cloud (Let's Encrypt)",
    "hostnames": ["cloud.example.com"]
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
      "HttpsPassthrough": 5444,
      "Management": 5269
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
- Any listener can be disabled by setting it to `null` or `0`. Disabling `Management` also disables the port gate — the UI/API/MCP then answer on every listener (the pre-`Management` behavior); only do that on a fully trusted network.
- For production, point the listeners at `80` / `443` / `8443` (or wherever your port forwarding lands) and override the `DataRoot` to a stable absolute path. HTTP-01 challenges always arrive on the `Http` listener. Publish the `Management` port only to the LAN — never add it to the WAN port-forward.
- ACME settings are read at startup — change them and restart. See [Let's Encrypt automation](#lets-encrypt-automation) above for the full table of knobs.

## Logging

HomeYarp uses **[Serilog](https://serilog.net/)** as its logging provider, plumbed under the standard `ILogger<T>` interface — every service in the four production projects emits structured logs. Routing-related events (YARP config rebuilds, SNI cert binding, TLS passthrough connections, application CRUD, auto-managed cert lifecycle) **always carry the `{AppId}` and `{AppName}`** properties so you can grep by either.

Sample console output on startup:

```
[21:23:30 INF] HomeYarp.Application.Proxy.HomeYarpConfigProvider Built YARP cluster 'echo-cluster' for app 'echo' (05315e43-319c-421f-9d3f-e6e5152cc03a) with 1 destination(s) and 1 route(s)
[21:23:30 INF] HomeYarp.Application.Proxy.HomeYarpConfigProvider YARP config built: 2 route(s), 2 cluster(s), 2 application(s) (0 disabled, 0 skipped without destinations)
[21:23:30 INF] HomeYarp.Application.Acme.AcmeRenewalService ACME renewal worker is disabled (HomeYarp:Acme:Enabled = false).
```

Plus one structured line per HTTP request via `UseSerilogRequestLogging` (Blazor heartbeat and static-asset paths are demoted to Debug).

### Configuring sinks

`appsettings.json` ships with **Console** and **Seq** declared in `Serilog:WriteTo`. The Seq entry is shape-only — its `serverUrl` and `apiKey` are externalized to user secrets (dev) and environment variables / compose (production), so nothing sensitive is committed. The File sink package is referenced but unused until you add it to `WriteTo`.

#### File (rolling)

```json
"Serilog": {
  "Using": [ "Serilog.Sinks.Console", "Serilog.Sinks.File" ],
  "WriteTo": [
    { "Name": "Console" },
    {
      "Name": "File",
      "Args": {
        "path": "logs/homeyarp-.log",
        "rollingInterval": "Day",
        "retainedFileCountLimit": 14,
        "outputTemplate": "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}"
      }
    }
  ]
}
```

The `-` in the path name is where Serilog injects the date suffix (`logs/homeyarp-20260427.log`). Adjust `rollingInterval`, `retainedFileCountLimit`, or `fileSizeLimitBytes` per your retention policy.

#### Seq

[Seq](https://datalust.co/seq) is a self-hostable structured-log server with a queryable web UI — popular for home labs because the free tier covers a single user and runs in a single Docker container.

```bash
docker run --name seq -d -e ACCEPT_EULA=Y -p 5341:80 datalust/seq:latest
```

The shape lives in `appsettings.json`:

```json
"Serilog": {
  "Using": [ "Serilog.Sinks.Console", "Serilog.Sinks.Seq" ],
  "WriteTo": [
    { "Name": "Console", "Args": { "outputTemplate": "..." } },
    { "Name": "Seq", "Args": {} }
  ]
}
```

The values (`serverUrl`, `apiKey`) are supplied per environment — never committed.

##### Development — `dotnet user-secrets`

The `UserSecretsId` is already set on `HomeYarp.WebServer.csproj`. Set the values once on each developer machine:

```bash
dotnet user-secrets set "Serilog:WriteTo:1:Args:serverUrl" "http://192.168.1.2:5341" \
  --project HomeYarp.WebServer/HomeYarp.WebServer.csproj
dotnet user-secrets set "Serilog:WriteTo:1:Args:apiKey" "<your-seq-api-key>" \
  --project HomeYarp.WebServer/HomeYarp.WebServer.csproj
```

Verify with `dotnet user-secrets list --project HomeYarp.WebServer/HomeYarp.WebServer.csproj`. Secrets are stored at `%APPDATA%\Microsoft\UserSecrets\<id>\secrets.json` (Windows) or `~/.microsoft/usersecrets/<id>/secrets.json` (Linux/macOS) and are loaded automatically when `ASPNETCORE_ENVIRONMENT=Development`.

##### Production — environment variables

ASP.NET Core's environment-variable provider maps `__` (double underscore) to the config-key separator `:`. The index `1` matches `WriteTo[1]` (Console is `[0]`, Seq is `[1]`). If you reorder `WriteTo` in `appsettings.json`, bump the index everywhere.

```bash
# bash / Linux
export Serilog__WriteTo__1__Args__serverUrl="http://seq.internal:5341"
export Serilog__WriteTo__1__Args__apiKey="<your-seq-api-key>"

# PowerShell
$env:Serilog__WriteTo__1__Args__serverUrl = "http://seq.internal:5341"
$env:Serilog__WriteTo__1__Args__apiKey    = "<your-seq-api-key>"
```

##### Production — Docker Compose

Keep the API key out of `compose.yaml` by referencing a `.env` file (gitignored) next to it:

```yaml
# compose.yaml
services:
  homeyarp:
    image: homeyarp:latest
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      Serilog__WriteTo__1__Args__serverUrl: http://seq:5341
      Serilog__WriteTo__1__Args__apiKey: ${SEQ_API_KEY}   # interpolated from .env
    depends_on: [ seq ]
    # ...
  seq:
    image: datalust/seq:latest
    environment:
      ACCEPT_EULA: "Y"
    volumes:
      - seq-data:/data
    ports:
      - "5341:80"

volumes:
  seq-data:
```

```bash
# .env (gitignored)
SEQ_API_KEY=...
```

##### Querying

Open Seq and filter on `AppId = '05315e43-...'` or `AppName = 'echo'` to drill into routing events for one app. If the Seq sink fails to construct (missing `serverUrl`), startup fails fast — that's intentional, so misconfiguration surfaces immediately instead of silently dropping logs.

### Log levels

Defaults in `appsettings.json`:

| Source | Level |
|---|---|
| Default | `Information` |
| `Microsoft.AspNetCore` | `Warning` |
| `Microsoft.Hosting.Lifetime` | `Information` |
| `Yarp` | `Information` |
| `HomeYarp` | `Information` |

`appsettings.Development.json` overrides `HomeYarp` → `Debug` so per-route mapping, SNI matches, and reload-cascade events are visible while developing.

To bump a single namespace at runtime, edit `appsettings.json` (or set `Serilog__MinimumLevel__Override__HomeYarp.Application.Proxy=Debug` as an env var) — `Serilog.Settings.Configuration` rereads on file change.

### Adding more sinks

Any sink package in [the Serilog ecosystem](https://github.com/serilog/serilog/wiki/Provided-Sinks) works. Add the NuGet (`<PackageReference Include="Serilog.Sinks.Whatever" />`), append the sink's name to `Serilog:Using`, and add an entry to `Serilog:WriteTo`. No code changes needed — `ReadFrom.Configuration` discovers them at host build time.

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

- `HomeYarp.Domain` — aggregates and value types: `Application`, `Certificate`, `AcmeMetadata`, `SelfSignedMetadata`, `RouteDefinition` (with optional `Transforms`), `ClusterDefinition` (with optional `HealthCheck` + `HttpRequest`), `DestinationDefinition`, `TlsConfiguration`, `TlsMode`, `TlsCertificateSource`, `AcmeKeyType`, `CertificateKeyType`, plus advanced types (`RouteTransform`, `HealthCheckConfiguration`, `HttpRequestConfiguration`).
- `HomeYarp.Application` — services (`ApplicationService`, `CertificateService`, `AcmeService`, `SelfSignedCertificateService`), repository abstractions, the YARP bridge (`HomeYarpConfigProvider`), TLS routing (`SniCertificateSelector`, `TlsPassthroughConnectionHandler`, `TlsClientHelloParser`), ACME automation (`Acme/` namespace: `IAcmeChallengeStore`, `IAcmeAccountStore`, `AcmeRenewalService`, `AcmeOptionsValidator`), and self-signed issuance (`SelfSigned/` namespace).
- `HomeYarp.Persistance` — JSON + PEM file storage with in-memory cache and change-token signalling. Includes `FileAcmeAccountStore`.
- `HomeYarp.WebServer` — composition root: REST controllers, MCP tool classes (`Mcp/`) with the Streamable HTTP endpoint at `/mcp`, Blazor Server pages, Kestrel listener configuration, the `/.well-known/acme-challenge/{token}` endpoint, and the **Serilog** logging pipeline (Console default; File and Seq sinks available on demand).
- `HomeYarp.Tests` — xUnit v3 unit tests covering all four production projects. See [Testing](#testing).

See [`CLAUDE.md`](CLAUDE.md) for deeper details about the reload chain, DI lifetimes, validation rules, and design decisions.

## Limitations / not yet

- **Private keys are stored unencrypted on disk** (including the ACME account key). Acceptable for a home lab; encryption at rest is a planned follow-up.
- **No authentication on the management API, UI, or MCP endpoint.** Run it on a trusted network only. Auth on top of the management surface is also a planned follow-up.
- **`AuthorizationPolicy` on `Application` is a placeholder field.** Per-route ASP.NET Core authorization policies are not wired yet.
- **ACME supports HTTP-01 only.** No DNS-01, so wildcards can't be issued via ACME (use `Source = Internal` for self-signed wildcards). ACME options are read once at startup — editing `appsettings.json` requires a restart.
- **Hostname changes on `External`-managed apps are rejected.** v1 only re-issues with the original hostnames; to change them, switch to `Manual` (or `Internal`), or delete and recreate. Self-signed (`Internal`) regenerates in place on hostname change.
- **Offload and passthrough must live on different ports.** Kestrel cannot host both an HTTPS-terminating endpoint and a raw-TCP `ConnectionHandler` on the same listener. If single-port unified TLS routing matters to you, that's a future design topic.

## Testing

Unit tests live in `HomeYarp.Tests` and cover all four production projects. The test layout mirrors the source tree — to find or add a test for `HomeYarp.Application/Services/ApplicationService.cs`, look in `HomeYarp.Tests/Application/Services/`.

```bash
dotnet test --solution HomeYarp.WebServer.slnx
```

> The `--solution` flag is required because `global.json` configures Microsoft.Testing.Platform as the runner — `dotnet test HomeYarp.WebServer.slnx` (positional) is rejected by the SDK.

**Stack:** xUnit v3 on Microsoft.Testing.Platform · NSubstitute (mocks) · Shouldly (assertions) · `Microsoft.Extensions.TimeProvider.Testing` (FakeTimeProvider for time-dependent code).

**Conventions:**

- Test names read as a sentence: `Method_Scenario_Expectation` (e.g. `CreateAsync_WhenNameAlreadyExists_ThrowsInvalidOperationException`).
- Mock the abstractions (`IApplicationRepository`, `ICertificateRepository`, `IAcmeService`, etc.) with NSubstitute; use real concrete types when the implementation is small and side-effect-free (`AcmeOptionsValidator`, `InMemoryAcmeChallengeStore`, JSON repos against a temp directory).
- Inject `FakeTimeProvider` wherever the SUT takes a `TimeProvider`.
- Assertions: prefer Shouldly (`.ShouldBe`, `.ShouldThrow<T>`, `.ShouldContain`) over xUnit's `Assert.*`.

**The "tests-with-features" rule:** anything you add to `HomeYarp.{Domain,Application,Persistance,WebServer}/` ships with corresponding tests in `HomeYarp.Tests/` in the same PR. Bug fixes ship with a regression test that fails before and passes after the fix.

**Out of scope for unit tests** (need integration tests, tracked separately):

- Live ACME orchestration in `AcmeService.OrchestrateOrderAsync` — Certes drives real Let's Encrypt traffic. Unit tests cover the gating paths only (input validation, name uniqueness, "renew on a non-ACME cert", `EnsureConfigured` wiring).
- `TlsPassthroughConnectionHandler.OnConnectedAsync` end-to-end TCP pumping.
- Full Kestrel-in-process tests (`WebApplicationFactory`) of the proxy pipeline.

## Development

```bash
# Hot reload
dotnet watch --project HomeYarp.WebServer/HomeYarp.WebServer.csproj

# Build the whole solution
dotnet build HomeYarp.WebServer.slnx

# Run the unit tests
dotnet test --solution HomeYarp.WebServer.slnx
```

Visual Studio: open `HomeYarp.WebServer.slnx`.

## License

HomeYarp is licensed under the [MIT License](LICENSE). Third-party components and their license texts are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
