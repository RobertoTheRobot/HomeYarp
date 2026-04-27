# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

HomeYarp is a YARP-based reverse proxy for a home lab. The `HomeYarp.WebServer` project hosts the proxy (via `MapReverseProxy()`), a REST management API at `/api/applications` + `/api/certificates`, and a Blazor Server UI for managing apps, certificates, and settings — all in one process. Application definitions and certificates are persisted as JSON + PEM files on disk; a custom `IProxyConfigProvider` translates them to YARP `RouteConfig`/`ClusterConfig` and hot-reloads via `IChangeToken` whenever state changes. TLS is supported per-application: offload (proxy terminates) or true L4 passthrough (raw TCP tunnel via SNI peek). Certificates have three sources — manual upload, self-signed (HomeYarp generates), and ACME (Let's Encrypt) — and the application's `Tls.Source` toggle (`Manual | Internal | External`) lets the app service own the cert lifecycle automatically.

## Commands

```bash
# Build
dotnet build HomeYarp.WebServer.slnx

# Run the API + proxy (http://localhost:5268)
dotnet run --project HomeYarp.WebServer/HomeYarp.WebServer.csproj

# Run with watch (hot reload)
dotnet watch --project HomeYarp.WebServer/HomeYarp.WebServer.csproj

# Run tests (HomeYarp.Tests, xUnit v3 + MTP)
dotnet test --solution HomeYarp.WebServer.slnx
```

OpenAPI docs are available at `/openapi` in Development mode. Sample HTTP requests live in `HomeYarp.WebServer/HomeYarp.WebServer.http`.

## Architecture

Clean Architecture targeting .NET 10, four production projects plus a test project:

- **HomeYarp.Domain** — Aggregates: `Application` (with `Routes`, `Cluster`, `Tls`) and `Certificate` (with optional `Acme` or `SelfSigned` metadata block — at most one is set; both null ⇒ manually uploaded). Plus `RouteDefinition` (with optional `Transforms`), `ClusterDefinition` (with optional `HealthCheck` + `HttpRequest`), `DestinationDefinition`, `TlsConfiguration` (`Mode`, `CertificateId`, `Source`), `AcmeMetadata`, `SelfSignedMetadata`, advanced types (`RouteTransform`, `HealthCheckConfiguration` + `Active`/`Passive`, `HttpRequestConfiguration`), and the enums `TlsMode` (`None | Offload | Passthrough`), `TlsCertificateSource` (`Manual | Internal | External`), `AcmeKeyType`, `CertificateKeyType` (parallel `Ec256/Rsa2048` enums for the two cert sources). No dependencies.
- **HomeYarp.Application** — Use cases, YARP bridge, and TLS routing. Holds `IApplicationRepository` + `ICertificateRepository` (in `Abstractions/`), `ApplicationService` (now also owns auto-managed cert lifecycle for Internal/External sources) and `CertificateService`, `HomeYarpConfigProvider` (the YARP `IProxyConfigProvider`), the `Tls/` namespace (`SniCertificateSelector` — cert callback Kestrel invokes during HTTPS handshake; `TlsPassthroughConnectionHandler` — Kestrel `ConnectionHandler` that peeks the TLS ClientHello SNI and tunnels raw bytes; `TlsClientHelloParser`), the `Acme/` namespace (`AcmeService`, `AcmeRenewalService`, `IAcmeChallengeStore`, `IAcmeAccountStore`, `AcmeOptionsValidator`), and the `SelfSigned/` namespace (`ISelfSignedCertificateService`, `SelfSignedCertificateService`). References `Yarp.ReverseProxy`. Depends on Domain only.
- **HomeYarp.Persistance** — `JsonApplicationRepository` stores one file per app at `{DataRoot}/applications/{id}.json` with atomic temp-file rename writes and an in-memory cache. `JsonCertificateRepository` mirrors that pattern with `{DataRoot}/certificates/{id}.json` (manifest) + `{id}.cert.pem` + `{id}.key.pem`. `HomeYarpDbContext` is a thin façade exposing both repos. `JsonStoreOptions` is bound from `HomeYarp:Storage`. Depends on Application + Domain.
- **HomeYarp.WebServer** — Composition root. `Program.cs` boots **Serilog** (two-stage: bootstrap logger → `UseSerilog(ReadFrom.Configuration)` → `UseSerilogRequestLogging`), calls `ConfigureHomeYarpKestrel()` (binds the three listeners from `HomeYarp:Listeners`) then wires `AddHomeYarpPersistance` → `AddHomeYarpApplication` → `AddReverseProxy()` + Razor Components, and pipes `MapControllers()` + `MapRazorComponents<App>()` + `MapReverseProxy()`. Controllers: `ApplicationsController`, `CertificatesController`. DTOs in `Dtos/`. See the **Logging** section for the sink configuration and the `{AppId}`/`{AppName}` convention.
- **HomeYarp.Tests** — xUnit v3 + Microsoft.Testing.Platform unit-test project covering all four production projects. Folder layout mirrors the source tree. References all four production projects; `HomeYarp.Application` declares `<InternalsVisibleTo Include="HomeYarp.Tests" />` so internals like `TlsClientHelloParser` are reachable. See the **Testing** section below.

Blazor surface (interactive server, no separate WASM project):
- `Components/App.razor` — root document, references `app.css` and `_framework/blazor.web.js`.
- `Components/Routes.razor` — router with `MainLayout` as default.
- `Components/Layout/{MainLayout,NavMenu}.razor` — shell + sidebar.
- `Components/Pages/Home.razor` (`/`), `Applications.razor` (`/applications`), `ApplicationEdit.razor` (`/applications/new` and `/applications/{Id:guid}`), `Certificates.razor` (`/certificates`), `CertificateUpload.razor` (`/certificates/upload`), `CertificateGenerate.razor` (`/certificates/generate`), `CertificateRequest.razor` (`/certificates/request`), `Settings.razor` (`/settings`).
- `Components/Shared/JsonEditor.razor` — reusable BlazorMonaco wrapper (vs-dark, JSON language, format-on-paste, line numbers, no minimap). Two-way bound via `Value` / `ValueChanged`. Used by `ApplicationEdit.razor`'s Advanced view; reusable for future "edit the JSON directly" surfaces.
- Pages inject `IApplicationService` / `ICertificateService` directly — UI mutations flow through the same path as the controllers, so the repos' change tokens fire and YARP/the SNI selector rebuild without restart.
- Static assets live under `wwwroot/` (currently `app.css`).

Dependency flow: `WebServer → Application/Persistance → Domain`. `HomeYarp.Tests` references all four. The solution file (`HomeYarp.WebServer.slnx`) declares all five projects.

## REST API surface

```
GET    /api/applications                       → ApplicationResponse[]
GET    /api/applications/{id}                  → ApplicationResponse | 404
POST   /api/applications                       → 201 + ApplicationResponse | 400 | 409
PUT    /api/applications/{id}                  → 200 + ApplicationResponse | 404 | 400 | 409
DELETE /api/applications/{id}                  → 204 | 404

GET    /api/certificates                       → CertificateResponse[]
GET    /api/certificates/{id}                  → CertificateResponse | 404
POST   /api/certificates                       → 201 + CertificateResponse | 400 | 409   (manual PEM upload)
POST   /api/certificates/self-signed           → 201 + CertificateResponse | 400 | 409   (HomeYarp-generated)
POST   /api/certificates/{id}/regenerate       → 200 + CertificateResponse | 404 | 409   (self-signed only)
POST   /api/certificates/acme                  → 201 + CertificateResponse | 400 | 409   (Let's Encrypt issue)
POST   /api/certificates/{id}/renew            → 200 + CertificateResponse | 404 | 409   (ACME-managed only)
DELETE /api/certificates/{id}                  → 204 | 404
```

`409` from the application endpoints can mean either name conflict, or — when `tls.source = External` — that ACME isn't configured (`InvalidOperationException` thrown by `AcmeOptionsValidator.EnsureConfigured` is mapped to `Conflict` by the controller). The request body is well-formed; the *server state* blocks it.

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
  "tls": { "mode": 1, "source": 0, "certificateId": "..." },
  "authorizationPolicy": null
}
```

`tls.mode`: `0` = None, `1` = Offload, `2` = Passthrough. `tls.source`: `0` = Manual (default), `1` = Internal (HomeYarp self-signs), `2` = External (Let's Encrypt). Routes default to `[]`, the `tls` block is optional and defaults to `{ mode: 0, source: 0, certificateId: null }` when omitted. For `source = Internal | External`, `certificateId` is populated *by the service* during create/update — clients shouldn't set it.

Certificate POST bodies:

```jsonc
// Upload (manual)
POST /api/certificates
{
  "name": "home-lan-wildcard",
  "friendlyName": "*.home.lan",
  "certificatePem": "-----BEGIN CERTIFICATE-----\n...",
  "privateKeyPem":  "-----BEGIN PRIVATE KEY-----\n..."
}

// Self-signed
POST /api/certificates/self-signed
{
  "name": "homeassistant-internal",
  "friendlyName": "Home Assistant (internal)",
  "hostnames": ["ha.home.lan", "192.168.1.50", "*.lab.home.lan"],
  "keyType": 0,         // 0 = Ec256 (default), 1 = Rsa2048
  "validityDays": 365   // default 365
}

// ACME (Let's Encrypt)
POST /api/certificates/acme
{
  "name": "cloud-perezfaulks",
  "friendlyName": "Cloud (Let's Encrypt)",
  "hostnames": ["cloud.perezfaulks.com"]
}
```

Conflict (`409`) is returned when the `name` collides. Validation problems (`400`) come from `ApplicationService.Validate` (see below), `SelfSignedCertificateService` (empty hostnames, non-positive validity), `AcmeService.IssueAsync` (empty hostnames, wildcards), or `X509Certificate2.CreateFromPem` failures during manual upload.

## Validation rules

`ApplicationService.Validate` is invoked on Create and Update:

- `Name` is required (non-empty after trim) and must be unique across applications (case-insensitive).
- `Cluster.Destinations` must have at least one entry.
- Each destination requires a non-empty `Name` and an absolute URI for `Address` (`Uri.TryCreate(_, Absolute, out _)`).
- TLS rules (`ValidateTls`):
  - `Tls.Source != Manual` requires `Tls.Mode == Offload` — auto-managed sources only make sense when the proxy terminates TLS.
  - `Tls.Source == Manual && Tls.Mode == Offload` requires a non-null `CertificateId` (was previously silently dropped — now an explicit 400).
  - `Tls.Source != Manual` requires at least one non-empty hostname collected from `Routes[*].Hosts`.
  - `Tls.Source == External` rejects wildcard hostnames (HTTP-01 limitation).

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
| `IApplicationService`, `ICertificateService`, `IAcmeService`, `ISelfSignedCertificateService`, `HomeYarpDbContext` | Scoped | Cheap, stateless; created per request/component activation. `AcmeService` and `SelfSignedCertificateService` each hold a static `ConcurrentDictionary<Guid, SemaphoreSlim>` to serialize concurrent issue/renew/regenerate calls for the same cert id. |

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

`AcmeOptionsValidator` (in `Acme/`) exposes `EnsureConfigured(options)` (throws) + `IsConfigured(options)` (bool). `AcmeService` uses the former at issue/renew time; `ApplicationService` uses it before kicking off `Source = External` provisioning so the user gets a fast failure at app-save time. The Razor `ApplicationEdit.razor` page uses `IsConfigured` to gray out the External radio button when ACME isn't configured.

## Self-signed certificates

HomeYarp can generate self-signed certificates itself for internal-only services — `HomeYarp.Application/SelfSigned/`:

- **`SelfSignedCertificateService`** (scoped) drives `IssueAsync(name, friendlyName, hostnames, keyType, validityDays)` and two `RegenerateAsync` overloads — one no-arg (used by the cert page's "Regenerate" button) and one taking new hostnames (used by `ApplicationService` when route hosts change on an `Internal`-source app). Both eventually go through `RegenerateInternalAsync`. Generation uses BCL `CertificateRequest`: build a `SubjectAlternativeNameBuilder` adding entries as DNS names by default, IP addresses when `IPAddress.TryParse` succeeds; configure key (`ECDsa.Create(NistP256)` or `RSA.Create(2048)`); set `BasicConstraints(false)`, `KeyUsage(DigitalSignature | KeyEncipherment, critical)`, `EnhancedKeyUsage(serverAuth)`, `SubjectKeyIdentifier`; call `CreateSelfSigned(notBefore, notAfter)`; export cert via `ExportCertificatePem()` and key via `ExportPkcs8PrivateKeyPem()`. The PEM material has the same shape as ACME or upload — `SniCertificateSelector.LoadX509` handles it identically.
- **`Certificate.SelfSigned`** is the marker for HomeYarp-generated certs. `{ Hostnames, KeyType, ValidityDays, IssuedAt, RegeneratedAt? }`. Mutually exclusive with `Certificate.Acme`.
- **No background renewal** — self-signed certs aren't auto-rotated. The user clicks "Regenerate" or `POST /api/certificates/{id}/regenerate` when they want fresh material. Wildcards and IP SANs are accepted (since we control issuance).

REST surface: `POST /api/certificates/self-signed` issues, `POST /api/certificates/{id}/regenerate` regenerates. Blazor surface: `/certificates/generate` (issuance form), `/certificates` shows "Self-signed" badge + per-row "Regenerate" button.

## Auto-managed certificate lifecycle (Internal / External sources)

`ApplicationService` owns the cert when `Tls.Source != Manual`. Logic lives in `EnsureAutoManagedCertificateAsync(incoming, previous)`, called from both `CreateAsync` and `UpdateAsync` *before* the app row is persisted:

- **Source switch** (Internal↔External, or auto→Manual): the previously auto-managed cert (1:1 with the app) is deleted. This is silent — there's no confirmation in the UI; the user explicitly changed sources.
- **Reuse-if-unchanged**: if `newSource == prevSource` and the existing cert's `Hostnames` (`SelfSigned.Hostnames` or `Acme.Hostnames`) match the route hosts, leave it alone.
- **Hostname change on `Internal`**: regenerate via `ISelfSignedCertificateService.RegenerateAsync(id, newHostnames)`. Same cert id, fresh key, fresh expiry, updated SANs.
- **Hostname change on `External`**: throws `InvalidOperationException` ("Delete and recreate, or switch to Manual"). v1 doesn't support ACME re-issue with new hostnames — it'd require a fresh order across the network and a new account-key signature; the cleanest follow-up is `IAcmeService.ReissueAsync(id, hostnames)`.
- **First-time `Internal` provisioning**: issue via `ISelfSignedCertificateService.IssueAsync` with `keyType = Ec256`, `validityDays = 365`, name = `{appName}-internal`, friendly name = `{displayName ?? appName} (internal)`.
- **First-time `External` provisioning**: `AcmeOptionsValidator.EnsureConfigured` first, then `IAcmeService.IssueAsync`, name = `{appName}-external`, friendly name = `{displayName ?? appName} (Let's Encrypt)`.
- **App delete**: `DeleteAsync` reads the app first; if `Tls.Source != Manual && CertificateId is { }`, the cert is deleted immediately after the app row.

The auto-managed cert's `Name` is stable at creation — renaming the app doesn't rename the cert. Linkage is by `Tls.CertificateId`, so nothing breaks; the cert just keeps its original `{oldName}-{source}` slug.

`ApplicationService` therefore depends on `IApplicationRepository`, `ICertificateRepository` (for direct delete), `ISelfSignedCertificateService`, `IAcmeService`, and `IOptionsMonitor<AcmeOptions>`. All scoped, all in the same project except the options monitor.

## Application edit: Simple vs Advanced view

`ApplicationEdit.razor` exposes two views, toggled by a control at the top:

- **Simple** (default) — the existing form: Identity, Routes (Hosts/Path/Order, multiple), TLS (Mode/Source/Certificate), Cluster (LoadBalancingPolicy + multiple Destinations).
- **Advanced (JSON)** — a Monaco editor (BlazorMonaco, `vs-dark`) bound to the full `HomeYarp.Domain.Application` JSON. Use this to set advanced fields the simple form doesn't expose: `routes[].transforms`, `cluster.healthCheck.active`, `cluster.healthCheck.passive`, `cluster.httpRequest`.

State machine:
- `Simple → Advanced`: serializes `_model` (web-defaults JSON, indented, `JsonStringEnumConverter`) into the editor buffer.
- `Advanced → Simple`: parses the buffer back into `Application`. If the JSON is malformed, the toggle is blocked and an error message is shown — fix the JSON or stay in Advanced.
- Save in either mode goes through `IApplicationService.{Create,Update}Async` so all validation rules still apply (name uniqueness, TLS rules, ACME gating, auto-managed cert lifecycle).

**REST API limitation:** the Advanced fields (`transforms`, `healthCheck`, `httpRequest`) are NOT in the `ApplicationRequest` / `ApplicationResponse` DTOs — `ApplicationDtoMapper.ToDomain/ToResponse` doesn't carry them. Setting them via `POST/PUT /api/applications` silently drops them. The Blazor JSON editor bypasses the controllers (it calls the scoped service directly), so it's the intended path for advanced fields. If we ever want full parity, add the new fields to the DTOs + mapper.

## Listeners

Configured under `HomeYarp:Listeners` in `appsettings.json`. Each port is optional — set to `null` or `0` to disable. Defaults in dev:

- `Http: 5268` — plain HTTP. Hosts the management API, Blazor UI, and any HTTP-mode proxy routes.
- `HttpsOffload: 5443` — Kestrel HTTPS endpoint. `SniCertificateSelector` picks a cert by SNI. Decrypted requests flow through YARP to the backend (typically as plain HTTP). Apps with `Tls.Mode == Offload` and a `CertificateId` are reachable here.
- `HttpsPassthrough: 5444` — raw TCP listener with `TlsPassthroughConnectionHandler`. The handler peeks the ClientHello, parses SNI, finds the matching app whose `Tls.Mode == Passthrough`, opens a TCP socket to the backend (host:port from the first destination's address), replays the buffered bytes, then bidirectionally pumps data. The proxy never decrypts — the backend terminates TLS itself.

Production deployments would typically map these to 80/443/8443 (or whatever the user's port-forwarding scheme requires).

## Logging

HomeYarp uses **Serilog** as the provider behind `Microsoft.Extensions.Logging.ILogger<T>` — call sites use the standard MEL API everywhere, Serilog is just the sink dispatcher. Wired in `HomeYarp.WebServer/Program.cs`:

1. **Two-stage bootstrap.** `Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger()` *before* `WebApplication.CreateBuilder` — captures any exception thrown while the host is still being built (Kestrel binding errors, malformed config, etc.). Then `builder.Host.UseSerilog((ctx, services, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).ReadFrom.Services(services).Enrich.FromLogContext().Enrich.WithProperty("Application", "HomeYarp"))` swaps in the real logger. The whole `app.Run()` is wrapped in `try/catch/finally` with `Log.CloseAndFlush()`.
2. **`UseSerilogRequestLogging`** emits one structured line per HTTP request (method, path, status, elapsed). The `GetLevel` callback demotes `/_blazor/*`, `/_framework/*`, `/_content/*` to Debug so the Blazor server-side render heartbeat doesn't drown the console.
3. **Default sink: Console.** Declared in `appsettings.json` `Serilog:WriteTo`. `appsettings.Development.json` overrides `MinimumLevel:Override:HomeYarp` → `Debug` so the YARP rebuild + SNI binding traces are visible during dev.
4. **Optional sinks: File and Seq.** Both NuGet packages (`Serilog.Sinks.File`, `Serilog.Sinks.Seq`) are referenced by `HomeYarp.WebServer.csproj` but **silent unless the user adds them to `Serilog:WriteTo`**. README has copy-paste-ready snippets. Adding any other Serilog ecosystem sink is `<PackageReference />` + entry in `Serilog:Using` + `Serilog:WriteTo` — no code changes needed thanks to `ReadFrom.Configuration`.

**Convention: routing-related logs carry `{AppId}` and `{AppName}`.** This applies everywhere the app's identity is in scope — `HomeYarpConfigProvider.BuildConfig` (per-app `BeginScope`), `SniCertificateSelector.Reload` (per-app `BeginScope`), `TlsPassthroughConnectionHandler.OnConnectedAsync` (direct templates after the SNI→app resolve), and every `ApplicationService` write path (Create/Update/Delete + the `EnsureAutoManagedCertificateAsync` decision branches). When adding new routing-adjacent code, keep the convention so users can filter by `AppId = '...'` in Seq / by grep on the console.

Services that gained an `ILogger<T>` constructor parameter use the **nullable-logger pattern** (`ILogger<T>? logger = null` → fallback to `NullLogger<T>.Instance`) so existing tests don't have to plumb a logger through; controllers take a non-nullable one (DI guarantees it). Tests pass `NullLogger<T>.Instance` for the controllers (see `WebServer/Controllers/*ControllerTests.cs`).

## Key design decisions

- **`Application.Name` is the user-facing slug** (unique) and surfaces in YARP IDs as `{Name}-cluster` / `{Name}-route-{i}`. `Application.Id` (Guid) is the stable identity used by the management API.
- **One JSON file per Application** — atomic writes, easy to diff/hand-edit.
- **`IApplicationRepository` is registered as a singleton** because YARP requires the `IProxyConfigProvider` to be a singleton, and the provider needs the repo's in-memory cache + change-token to drive hot reload.
- **DTOs live in WebServer**, not Domain. The controller maps in/out at the API boundary; Blazor pages bind to domain types directly since they're in-process.
- **Blazor Server (interactive) over WASM** for simplicity — single project, no separate hosting story, direct access to `IApplicationService`. Antiforgery is enabled (`UseAntiforgery()`) as required by interactive components.
- **TLS offload and passthrough live on separate ports**, not unified on :443. Kestrel's HTTPS pipeline can't coexist with a raw-TCP `ConnectionHandler` on the same listener, so we expose two endpoints. Each app's `Tls.Mode` determines which port it's reachable on.
- **PEM, not PFX, for cert uploads.** Stored as `{id}.cert.pem` + `{id}.key.pem` next to a JSON manifest holding parsed metadata (subject, issuer, SANs, expiry, thumbprint). The selector roundtrips PEM → in-memory PFX before handing the cert to Kestrel — `X509Certificate2.CreateFromPem` alone produces a cert whose private key Schannel can't see during the handshake.
- **Private keys are stored unencrypted on disk for v1.** Acceptable for a homelab; encrypting at rest (DPAPI / Data Protection) is a known follow-up.
- **All code in `HomeYarp.{Domain,Application,Persistance,WebServer}` ships with unit-test coverage.** New features and bug fixes ship with tests in the *same* PR — see the Testing section below for stack and conventions.
- **`AuthorizationPolicy` on `Application` is a string placeholder** — not yet wired to ASP.NET Core auth. Adding policy registration + binding to YARP routes is the next iteration.
- **Three certificate sources, one shape on disk.** Manual upload, self-signed, and ACME all produce the same `Certificate` record + `{cert,key}.pem` pair. The `SelfSigned` and `Acme` metadata blocks are mutually exclusive — at most one set; both null means manual upload. The selector and proxy don't care which source produced a cert.
- **Auto-managed certs are 1:1 with their owning application.** Naming convention `{appName}-{internal|external}` makes them discoverable; ownership is enforced by `ApplicationService` cleaning up on source switch + app delete.
- **Tests live in `HomeYarp.Tests` and run via `dotnet test --solution HomeYarp.WebServer.slnx`.** See the Testing section below.

## Naming gotcha

`HomeYarp.Domain.Application` (the domain aggregate) collides with `HomeYarp.Application` (the project namespace) and with `Microsoft.AspNetCore.Builder.WebApplication` in the WebServer host. Razor components and DI registrations almost always need the fully-qualified `HomeYarp.Domain.Application` to disambiguate — see `Components/Pages/Applications.razor` and `ApplicationEdit.razor` for the pattern. New code in `HomeYarp.Application` can use the unqualified `Application` since the namespace `using HomeYarp.Domain;` makes it accessible.

## Testing

`HomeYarp.Tests` (net10.0) covers all four source projects. Tests mirror the source folder layout — to find or add a test for `HomeYarp.Application/Services/ApplicationService.cs`, look in `HomeYarp.Tests/Application/Services/`.

**Stack:** xUnit v3 on Microsoft.Testing.Platform · NSubstitute (mocks) · Shouldly (assertions) · `Microsoft.Extensions.TimeProvider.Testing` (FakeTimeProvider) for time-dependent code.

**Run:**
```bash
dotnet test --solution HomeYarp.WebServer.slnx
```
(`dotnet test HomeYarp.WebServer.slnx` is rejected by the SDK; pass `--solution` explicitly.)

**Conventions:**
- Test names read as a sentence: `Method_Scenario_Expectation`, e.g. `CreateAsync_WhenNameAlreadyExists_ThrowsInvalidOperationException`.
- Mock the abstractions (`IApplicationRepository`, `ICertificateRepository`, `ISelfSignedCertificateService`, `IAcmeService`, `IAcmeAccountStore`, `IAcmeChallengeStore`, etc.) via NSubstitute.
- Use real concrete types where the implementation is simple and side-effect-free (`AcmeOptionsValidator`, `InMemoryAcmeChallengeStore`, JSON repos against a `TempDirectory`).
- Inject `FakeTimeProvider` wherever the SUT takes a `TimeProvider` (`SelfSignedCertificateService`, `AcmeService`, `AcmeRenewalService`).
- Assertions: prefer Shouldly (`.ShouldBe`, `.ShouldThrow<T>`, `.ShouldNotBeNull`, `.ShouldContain`) over xUnit's `Assert.*`.
- The `HomeYarp.Domain.Application` ↔ `HomeYarp.Application` namespace collision is avoided in tests by the global alias `DomainApplication = HomeYarp.Domain.Application` in `HomeYarp.Tests/GlobalUsings.cs`. Use `DomainApplication` or fully-qualify; bare `Application` will fail with CS0118.
- `HomeYarp.Application.csproj` declares `<InternalsVisibleTo Include="HomeYarp.Tests" />` so internal types like `TlsClientHelloParser` can be tested directly.

**The "tests-with-features" rule:** anything you add to `HomeYarp.{Domain,Application,Persistance,WebServer}/` ships with corresponding tests in `HomeYarp.Tests/` in the same PR. Bug fixes ship with a regression test that fails before and passes after the fix.

**Out of scope for unit tests** (need integration tests, tracked separately):
- Live ACME orchestration in `AcmeService.OrchestrateOrderAsync` — Certes drives real Let's Encrypt traffic. Unit tests cover the gating paths only (input validation, name uniqueness, "renew on a non-ACME cert", `EnsureConfigured` wiring).
- `TlsPassthroughConnectionHandler.OnConnectedAsync` end-to-end TCP pumping. The static `HostMatches` and `ParseTcpTarget` helpers are private — they would need promotion to internal to be unit-testable.
- Full Kestrel-in-process tests (`WebApplicationFactory`) of the proxy pipeline.

## Local data

Applications are written to `HomeYarp.WebServer/bin/Debug/net{TFM}/data/applications/` when running via `dotnet run` (relative to `AppContext.BaseDirectory`). The path is configurable via the `HomeYarp:Storage:DataRoot` setting; absolute paths are honored as-is. The `data/` folder is gitignored.
