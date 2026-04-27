# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

HomeYarp is a YARP-based reverse proxy for a home lab. The `HomeYarp.WebServer` project hosts the proxy (via `MapReverseProxy()`), a REST management API at `/api/applications` + `/api/certificates`, and a Blazor Server UI for managing apps, certificates, and settings — all in one process. Application definitions and certificates are persisted as JSON + PEM files on disk; a custom `IProxyConfigProvider` translates them to YARP `RouteConfig`/`ClusterConfig` and hot-reloads via `IChangeToken` whenever state changes. TLS is supported per-application: offload (proxy terminates) or true L4 passthrough (raw TCP tunnel via SNI peek).

## Commands

```bash
# Build
dotnet build HomeYarp.WebServer.slnx

# Run the API + proxy (http://localhost:5268)
dotnet run --project HomeYarp.WebServer/HomeYarp.WebServer.csproj

# Run with watch (hot reload)
dotnet watch --project HomeYarp.WebServer/HomeYarp.WebServer.csproj

# Run tests (when test projects exist)
dotnet test HomeYarp.WebServer.slnx
```

OpenAPI docs are available at `/openapi` in Development mode. Sample HTTP requests live in `HomeYarp.WebServer/HomeYarp.WebServer.http`.

## Architecture

Clean Architecture targeting .NET 10, four projects:

- **HomeYarp.Domain** — Aggregates: `Application` (with `Routes`, `Cluster`, `Tls`) and `Certificate`. Plus `RouteDefinition`, `ClusterDefinition`, `DestinationDefinition`, `TlsConfiguration`, and the `TlsMode` enum (`None | Offload | Passthrough`). No dependencies.
- **HomeYarp.Application** — Use cases, YARP bridge, and TLS routing. Holds `IApplicationRepository` + `ICertificateRepository` (in `Abstractions/`), `ApplicationService` and `CertificateService`, `HomeYarpConfigProvider` (the YARP `IProxyConfigProvider`), and the `Tls/` namespace: `SniCertificateSelector` (the cert callback Kestrel invokes during HTTPS handshake), `TlsPassthroughConnectionHandler` (a Kestrel `ConnectionHandler` that peeks the TLS ClientHello SNI and tunnels raw bytes to a backend), and `TlsClientHelloParser`. References `Yarp.ReverseProxy`. Depends on Domain only.
- **HomeYarp.Persistance** — `JsonApplicationRepository` stores one file per app at `{DataRoot}/applications/{id}.json` with atomic temp-file rename writes and an in-memory cache. `JsonCertificateRepository` mirrors that pattern with `{DataRoot}/certificates/{id}.json` (manifest) + `{id}.cert.pem` + `{id}.key.pem`. `HomeYarpDbContext` is a thin façade exposing both repos. `JsonStoreOptions` is bound from `HomeYarp:Storage`. Depends on Application + Domain.
- **HomeYarp.WebServer** — Composition root. `Program.cs` calls `ConfigureHomeYarpKestrel()` (binds the three listeners from `HomeYarp:Listeners`) then wires `AddHomeYarpPersistance` → `AddHomeYarpApplication` → `AddReverseProxy()` + Razor Components, and pipes `MapControllers()` + `MapRazorComponents<App>()` + `MapReverseProxy()`. Controllers: `ApplicationsController`, `CertificatesController`. DTOs in `Dtos/`.

Blazor surface (interactive server, no separate WASM project):
- `Components/App.razor` — root document, references `app.css` and `_framework/blazor.web.js`.
- `Components/Routes.razor` — router with `MainLayout` as default.
- `Components/Layout/{MainLayout,NavMenu}.razor` — shell + sidebar.
- `Components/Pages/Home.razor` (`/`), `Applications.razor` (`/applications`), `ApplicationEdit.razor` (`/applications/new` and `/applications/{Id:guid}`), `Certificates.razor` (`/certificates`), `CertificateUpload.razor` (`/certificates/upload`), `Settings.razor` (`/settings`).
- Pages inject `IApplicationService` / `ICertificateService` directly — UI mutations flow through the same path as the controllers, so the repos' change tokens fire and YARP/the SNI selector rebuild without restart.
- Static assets live under `wwwroot/` (currently `app.css`).

Dependency flow: `WebServer → Application/Persistance → Domain`. The solution file (`HomeYarp.WebServer.slnx`) declares all four projects.

## REST API surface

```
GET    /api/applications           → ApplicationResponse[]
GET    /api/applications/{id}      → ApplicationResponse | 404
POST   /api/applications           → 201 + ApplicationResponse | 400 | 409
PUT    /api/applications/{id}      → 200 + ApplicationResponse | 404 | 400 | 409
DELETE /api/applications/{id}      → 204 | 404

GET    /api/certificates           → CertificateResponse[]
GET    /api/certificates/{id}      → CertificateResponse | 404
POST   /api/certificates           → 201 + CertificateResponse | 400 | 409
DELETE /api/certificates/{id}      → 204 | 404
```

Application POST/PUT body:

```json
{
  "name": "grafana",
  "displayName": "Grafana",
  "description": "...",
  "enabled": true,
  "routes": [
    { "routeId": "...", "hosts": ["grafana.home.lan"], "path": "/{**catch-all}", "methods": ["GET","POST"], "order": 0 }
  ],
  "cluster": {
    "loadBalancingPolicy": "RoundRobin",
    "destinations": [{ "name": "primary", "address": "http://192.168.1.50:3000", "host": null }]
  },
  "tls": { "mode": 1, "certificateId": "..." },
  "authorizationPolicy": null
}
```

`tls.mode`: `0` = None, `1` = Offload, `2` = Passthrough. Routes default to `[]`, the `tls` block is optional and defaults to `{ mode: 0, certificateId: null }` when omitted.

Certificate POST body:

```json
{
  "name": "home-lan-wildcard",
  "friendlyName": "*.home.lan",
  "certificatePem": "-----BEGIN CERTIFICATE-----\n...",
  "privateKeyPem":  "-----BEGIN PRIVATE KEY-----\n..."
}
```

Conflict (`409`) is returned when the `name` collides. Validation problems (`400`) come from `ApplicationService.Validate` (see below) or from `X509Certificate2.CreateFromPem` failures during cert upload.

## Validation rules

`ApplicationService.Validate` is invoked on Create and Update:

- `Name` is required (non-empty after trim) and must be unique across applications (case-insensitive).
- `Cluster.Destinations` must have at least one entry.
- Each destination requires a non-empty `Name` and an absolute URI for `Address` (`Uri.TryCreate(_, Absolute, out _)`).

`Update` additionally allows the current row to keep its own name (it only fails uniqueness if a *different* row owns the name).

## Change-token reload chain

The hot-reload story is built entirely on `Microsoft.Extensions.Primitives.ChangeToken`:

1. `JsonApplicationRepository` and `JsonCertificateRepository` each hold a `CancellationTokenSource`. Add/Update/Delete swap in a fresh CTS via `Interlocked.Exchange`, then `Cancel()` the old one (`SignalReload`). `GetReloadToken()` returns a `CancellationChangeToken` wrapping the current CTS.
2. `HomeYarpConfigProvider` subscribes to the app repo's reload token via `ChangeToken.OnChange(repo.GetReloadToken, Rebuild)`. Each rebuild produces a new `IProxyConfig` snapshot; YARP picks it up and switches over without dropping in-flight requests.
3. `SniCertificateSelector` subscribes to *both* repos' reload tokens (apps decide which hostnames need a cert; certs may be uploaded/deleted). Its `Reload` re-projects the host→`X509Certificate2` map and disposes the prior generation of certs.
4. `TlsPassthroughConnectionHandler` does not subscribe — it just queries `IApplicationRepository.GetAllAsync()` per connection. The in-memory cache + small N for homelab use makes this fine.

## DI lifetimes

| Service | Lifetime | Why |
|---|---|---|
| `IApplicationRepository`, `ICertificateRepository` | Singleton | Shared in-memory cache and change tokens. |
| `IProxyConfigProvider` (`HomeYarpConfigProvider`) | Singleton | Required by YARP. |
| `SniCertificateSelector`, `TlsPassthroughConnectionHandler` | Singleton | Plugged into Kestrel callbacks; cert cache is shared. |
| `IAcmeChallengeStore`, `IAcmeAccountStore` | Singleton | Challenge store is a process-wide token map; account store wraps shared on-disk state. |
| `AcmeRenewalService` | Hosted (Singleton) | Background loop. |
| `IApplicationService`, `ICertificateService`, `IAcmeService`, `HomeYarpDbContext` | Scoped | Cheap, stateless; created per request/component activation. `AcmeService` uses static per-id semaphores to serialize concurrent calls. |

## Let's Encrypt / ACME

HomeYarp can issue and renew certificates from Let's Encrypt over the ACME HTTP-01 challenge — `HomeYarp.Application/Acme/`:

- **`AcmeService`** (scoped) drives both `IssueAsync` and `RenewAsync`. It loads or registers an account via `IAcmeAccountStore`, places an order with Certes, publishes each `KeyAuthz` to the `IAcmeChallengeStore`, calls `Validate()`, polls until the challenge resource turns valid, generates the cert with a fresh ES256 key, downloads the PEM chain, parses metadata exactly the way `CertificateService.UploadAsync` does, and persists via `ICertificateRepository.SaveAsync`. A `static ConcurrentDictionary<Guid, SemaphoreSlim>` serializes concurrent renewals for the same cert id.
- **`AcmeRenewalService` : `BackgroundService`** waits `Acme:StartupDelay` (60s default) then loops every `Acme:RenewalInterval` (24h default). Each tick it lists certs, filters those with `Acme is not null && NotAfter - now < RenewBefore`, and calls `RenewAsync` per cert. Failures log and continue — the next tick retries. The loop exits immediately if `Acme:Enabled == false`, so flipping the flag requires a restart.
- **`InMemoryAcmeChallengeStore`** is a singleton `ConcurrentDictionary<token, keyAuthz>`. `Program.cs` maps `GET /.well-known/acme-challenge/{token}` immediately before `MapReverseProxy()` so the literal endpoint wins over YARP's catch-all routing.
- **`FileAcmeAccountStore`** (in `HomeYarp.Persistance.Json`) persists the ACME account key + registration metadata at `{DataRoot}/acme/account.{sha8(directoryUrl)}.{pem|json}`. Account is scoped per directory URL, so flipping between staging and production creates separate accounts.
- **`Certificate.Acme`** is the marker. Manually-uploaded certs leave it null (renewal worker skips them); ACME-issued/renewed certs hold `{ Hostnames, AccountEmail, DirectoryUrl, KeyType, IssuedAt, RenewedAt? }`.

Configuration lives under `HomeYarp:Acme` in `appsettings.json`:

| Key | Default | Notes |
|---|---|---|
| `Enabled` | `false` | Master switch. Renewal worker exits immediately when false. |
| `AccountEmail` | `""` | Required when issuing. |
| `AgreeToTermsOfService` | `false` | Required when issuing. |
| `DirectoryUrl` | LE production | Set to `https://acme-staging-v02.api.letsencrypt.org/directory` for testing. |
| `KeyType` | `Ec256` | Or `Rsa2048`. |
| `RenewBefore` | `30.00:00:00` | Renew when `NotAfter - now` is below this. |
| `RenewalInterval` | `24:00:00` | How often the background worker checks. |
| `StartupDelay` | `00:01:00` | Grace period before the first tick. |

HTTP-01 requires inbound port 80 to reach the `Http` listener, so port-forward 80 → HomeYarp's Http port. Wildcards (`*.example.com`) are not supported — they require DNS-01.

REST surface: `POST /api/certificates/acme` issues, `POST /api/certificates/{id}/renew` renews. Blazor surface: `/certificates/request` (issuance form), `/certificates` shows ACME badge + per-row "Renew now" button, `/settings` shows a read-only ACME section.

## Listeners

Configured under `HomeYarp:Listeners` in `appsettings.json`. Each port is optional — set to `null` or `0` to disable. Defaults in dev:

- `Http: 5268` — plain HTTP. Hosts the management API, Blazor UI, and any HTTP-mode proxy routes.
- `HttpsOffload: 5443` — Kestrel HTTPS endpoint. `SniCertificateSelector` picks a cert by SNI. Decrypted requests flow through YARP to the backend (typically as plain HTTP). Apps with `Tls.Mode == Offload` and a `CertificateId` are reachable here.
- `HttpsPassthrough: 5444` — raw TCP listener with `TlsPassthroughConnectionHandler`. The handler peeks the ClientHello, parses SNI, finds the matching app whose `Tls.Mode == Passthrough`, opens a TCP socket to the backend (host:port from the first destination's address), replays the buffered bytes, then bidirectionally pumps data. The proxy never decrypts — the backend terminates TLS itself.

Production deployments would typically map these to 80/443/8443 (or whatever the user's port-forwarding scheme requires).

## Key design decisions

- **`Application.Name` is the user-facing slug** (unique) and surfaces in YARP IDs as `{Name}-cluster` / `{Name}-route-{i}`. `Application.Id` (Guid) is the stable identity used by the management API.
- **One JSON file per Application** — atomic writes, easy to diff/hand-edit.
- **`IApplicationRepository` is registered as a singleton** because YARP requires the `IProxyConfigProvider` to be a singleton, and the provider needs the repo's in-memory cache + change-token to drive hot reload.
- **DTOs live in WebServer**, not Domain. The controller maps in/out at the API boundary; Blazor pages bind to domain types directly since they're in-process.
- **Blazor Server (interactive) over WASM** for simplicity — single project, no separate hosting story, direct access to `IApplicationService`. Antiforgery is enabled (`UseAntiforgery()`) as required by interactive components.
- **TLS offload and passthrough live on separate ports**, not unified on :443. Kestrel's HTTPS pipeline can't coexist with a raw-TCP `ConnectionHandler` on the same listener, so we expose two endpoints. Each app's `Tls.Mode` determines which port it's reachable on.
- **PEM, not PFX, for cert uploads.** Stored as `{id}.cert.pem` + `{id}.key.pem` next to a JSON manifest holding parsed metadata (subject, issuer, SANs, expiry, thumbprint). The selector roundtrips PEM → in-memory PFX before handing the cert to Kestrel — `X509Certificate2.CreateFromPem` alone produces a cert whose private key Schannel can't see during the handshake.
- **Private keys are stored unencrypted on disk for v1.** Acceptable for a homelab; encrypting at rest (DPAPI / Data Protection) is a known follow-up.
- **`AuthorizationPolicy` on `Application` is a string placeholder** — not yet wired to ASP.NET Core auth. Adding policy registration + binding to YARP routes is the next iteration.
- **No tests yet.** Verification is currently manual via the `.http` file.

## Naming gotcha

`HomeYarp.Domain.Application` (the domain aggregate) collides with `HomeYarp.Application` (the project namespace) and with `Microsoft.AspNetCore.Builder.WebApplication` in the WebServer host. Razor components and DI registrations almost always need the fully-qualified `HomeYarp.Domain.Application` to disambiguate — see `Components/Pages/Applications.razor` and `ApplicationEdit.razor` for the pattern. New code in `HomeYarp.Application` can use the unqualified `Application` since the namespace `using HomeYarp.Domain;` makes it accessible.

## Local data

Applications are written to `HomeYarp.WebServer/bin/Debug/net{TFM}/data/applications/` when running via `dotnet run` (relative to `AppContext.BaseDirectory`). The path is configurable via the `HomeYarp:Storage:DataRoot` setting; absolute paths are honored as-is. The `data/` folder is gitignored.
