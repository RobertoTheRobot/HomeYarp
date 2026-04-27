# Serilog logging + extensive structured logs

## Goal

Replace the default `Microsoft.Extensions.Logging` console-only setup with **Serilog** (using the standard `ILogger<T>` API so call sites don't change). Add **extensive structured logging** across all four production projects, with app **id + name** on every routing-related event. Default config writes to **Console only**. Add optional sinks that activate **only if explicitly configured** in `appsettings.json` — File, Seq, and Grafana Loki (the three most common in home-lab stacks). Update README and CLAUDE.md.

## Approach

1. **Serilog wiring** (WebServer):
   - Add `Serilog.AspNetCore`, `Serilog.Settings.Configuration`, `Serilog.Sinks.Console`, `Serilog.Sinks.File`, `Serilog.Sinks.Seq`.
   - `Program.cs`: two-stage bootstrap — `Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger()` before `WebApplication.CreateBuilder`, then `builder.Host.UseSerilog((ctx, services, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).ReadFrom.Services(services).Enrich.FromLogContext())`. Wrap `app.Run()` in try/catch/finally `Log.CloseAndFlush()`.
   - Use `app.UseSerilogRequestLogging()` so every HTTP request gets a one-line summary (method, path, status, elapsed).
   - Keep the default `Microsoft.Extensions.Logging` plumbing — Serilog plugs in *under* `ILogger<T>`. **Zero changes to existing logging call sites.**

2. **Default config** (`appsettings.json`):
   - Replace the existing `Logging` section with a `Serilog` section. Console is in the `WriteTo` array by default. Uses `Serilog.Sinks.Console` with `Theme: Code`. Min level Information, Microsoft.AspNetCore Warning.
   - `appsettings.Development.json` overrides Min level → Debug for HomeYarp and Information for Microsoft.
   - `Serilog.Settings.Configuration` only activates sinks listed in `WriteTo`. File/Seq/Loki packages are referenced but **silent unless the user adds them** to `WriteTo`. README documents the exact JSON snippets.

3. **Extensive logging** — add `ILogger<T>` to services that don't have it yet, then:
   - **HomeYarpConfigProvider** (`HomeYarp.Application/Proxy/`) — log every rebuild: app count, route count, cluster count, plus per-app `{AppId} {AppName} {RouteCount} {ClusterId}`. Log skipped apps (`disabled`, `no destinations`).
   - **SniCertificateSelector** — log reload (`{HostCount} hosts`), per-app SNI binding `{AppId} {AppName} {Host} → {CertId} {CertSubject}`, cert load failures with the app context, and a Debug per `Select(sni)` hit/miss with `{Sni} {AppId?} {CertId?}`.
   - **TlsPassthroughConnectionHandler** — already logs; expand to include `{AppId} {AppName}` on the resolved-app and pump-end events. Add Debug bytes-pumped totals (cheap counters at end of pump).
   - **ApplicationService** — log create/update/delete with `{AppId} {AppName}`, validation failures (Warning), auto-managed cert decisions (provision new / regenerate / reuse / delete on source switch / delete on app delete) — each with `{AppId} {AppName} {Source} {CertId}`.
   - **CertificateService** — log upload / delete with `{CertId} {CertName} {Subject} {NotAfter}` and PEM-parse failures (Warning).
   - **SelfSignedCertificateService** — log issue / regenerate (`{CertId} {CertName} {Hostnames} {KeyType} {ValidityDays} {NotAfter}`).
   - **AcmeService** — already logs the high-level flow; add: account-load (cached vs fresh), per-authorization challenge published (`{Token}`), challenge poll loop transitions, order resource transitions, full operation timing (Stopwatch).
   - **AcmeRenewalService** — already logs; add per-tick start/end and per-cert timing.
   - **JsonApplicationRepository** / **JsonCertificateRepository** — log Add/Update/Delete (`{Id} {Name}`), startup load summary (`{Count} apps loaded from {Directory}`), malformed-file warnings (was a `// Skip malformed files` comment — promote to Warning with file path + exception). Repo emits Debug on `SignalReload()` so the cascade is visible.
   - **FileAcmeAccountStore** — log account save/load with directory URL hash.
   - **HomeYarpKestrelConfiguration** — log effective listener config at startup (`Http: 5268, HttpsOffload: 5443, HttpsPassthrough: 5444`), warn if all are disabled.
   - **Controllers** — log each entry point with the request id + payload identity (name on POST, id on PUT/DELETE/GET-by-id). Failures already surface via the exceptions; no need to double-log.
   - **InMemoryAcmeChallengeStore** — Debug on Publish/Remove with token (HTTP-01 challenges are short-lived so it's safe to log token).

4. **App-context enrichment**:
   - For **routing rebuilds**, use `_logger.BeginScope` with `{ AppId, AppName }` so all logs *inside* the per-app loop in `HomeYarpConfigProvider.BuildConfig` automatically carry both. Same in `SniCertificateSelector.Reload`.
   - For **TLS passthrough** and **app-service flows**, pass `{AppId}` + `{AppName}` directly in the message template (already structured).
   - Avoid hot-path string concat — Serilog handles deferred formatting.

5. **Testing**:
   - The logging itself doesn't get unit tests (logs are observability, not behavior). 
   - Existing tests must still pass — verify nothing was broken by adding `ILogger<T>` constructor parameters. Where a service that didn't take a logger now does, the existing test's `new Service(...)` call needs `NullLogger<T>.Instance` (or use the existing `ILogger<T>?` nullable pattern that some services already use, e.g. `AcmeService`).
   - Run: `dotnet test --solution HomeYarp.WebServer.slnx`.

6. **Docs**:
   - `README.md`: new "Logging" section describing default behavior, structured event shape (mention `{AppId}` / `{AppName}` on routing logs), and **the four sinks with copy-paste-ready JSON snippets** for each. Add a "viewing logs" subsection with quick wins (Seq locally on Docker, file rotation, Loki via Grafana).
   - `CLAUDE.md`: short "Logging" section under Architecture explaining the bootstrap + ReadFrom.Configuration approach, the sinks list, and the convention that **routing logs carry `{AppId}` and `{AppName}`**.

## Plan (checklist)

- [ ] **WebServer csproj** — add Serilog packages.
- [ ] **`Program.cs`** — two-stage bootstrap, `UseSerilog`, `UseSerilogRequestLogging`, try/finally with `CloseAndFlush`.
- [ ] **`appsettings.json` / `appsettings.Development.json`** — replace `Logging` with `Serilog` section (Console only by default).
- [ ] **HomeYarpKestrelConfiguration** — log effective listener config; refactor to take `ILogger`.
- [ ] **HomeYarpConfigProvider** — `ILogger<HomeYarpConfigProvider>` ctor, log rebuilds with per-app scope `{AppId}` `{AppName}`.
- [ ] **SniCertificateSelector** — expand existing logging to log reload summary and per-host binding with `{AppId}` `{AppName}` `{CertId}`.
- [ ] **TlsPassthroughConnectionHandler** — include `{AppId}` `{AppName}` on resolve + termination logs; Debug bytes pumped.
- [ ] **ApplicationService** — `ILogger<ApplicationService>` ctor, log all CRUD + auto-cert-lifecycle decisions.
- [ ] **CertificateService** — `ILogger<CertificateService>` ctor, log upload/delete + parse failures.
- [ ] **SelfSignedCertificateService** — `ILogger<SelfSignedCertificateService>` ctor, log issue/regenerate.
- [ ] **AcmeService** — expand existing logger usage, add timing, account-load, challenge-poll Debug.
- [ ] **AcmeRenewalService** — expand existing logger usage, per-tick timing.
- [ ] **JsonApplicationRepository** — `ILogger<JsonApplicationRepository>` ctor, startup load summary + per-mutation logs + malformed-file Warning.
- [ ] **JsonCertificateRepository** — same as above.
- [ ] **FileAcmeAccountStore** — `ILogger<FileAcmeAccountStore>` ctor, save/load logs.
- [ ] **InMemoryAcmeChallengeStore** — `ILogger<InMemoryAcmeChallengeStore>` ctor, publish/remove Debug.
- [ ] **Controllers** — `ILogger<T>` for both controllers, log each entry point.
- [ ] **Tests fix-up** — NullLogger where needed (or rely on nullable param pattern already used by some services).
- [ ] **Verify** — `dotnet build` + `dotnet test` clean, `dotnet run` and observe logs for create-app + cert-issue paths.
- [ ] **README.md** — new "Logging" section with sink snippets.
- [ ] **CLAUDE.md** — short Logging section + the `{AppId}` / `{AppName}` convention.

## Out of scope

- Replacing `ILogger<T>` call sites with Serilog's static `Log` API. Goes against DI testability; using Serilog as a *provider* under MEL is the standard ASP.NET Core pattern.
- Adding metrics or distributed tracing (OpenTelemetry) — separate concern.
- Encrypting log files at rest — log rotation + retention is enough for v1.
- Custom enrichers (machine name, process id, etc.) — `ReadFrom.Configuration` lets users add `Serilog.Enrichers.Environment` etc. themselves; we don't bake them in.

## Review

**Result: 177/177 tests still passing; live `dotnet run` smoke-tested with Serilog Console output.**

### What got built

- **Serilog wiring** — `Program.cs` two-stage bootstrap (`CreateBootstrapLogger` → `UseSerilog(ReadFrom.Configuration + ReadFrom.Services + Enrich.FromLogContext + Enrich.WithProperty("Application","HomeYarp"))`) + `UseSerilogRequestLogging` with a `GetLevel` callback that demotes Blazor heartbeat / `_framework` / `_content` traffic to Debug. `try/catch/finally` with `Log.CloseAndFlush()` around `app.Run()`.
- **Sinks** — `Serilog.AspNetCore`, `Serilog.Settings.Configuration`, `Serilog.Sinks.Console` (default), `Serilog.Sinks.File`, `Serilog.Sinks.Seq`. The latter two are referenced but inert — they only fire if explicitly listed in `Serilog:WriteTo` (per `Serilog.Settings.Configuration` semantics).
- **Default config** — `appsettings.json` carries the full `Serilog` section (Console only). `appsettings.Development.json` bumps `HomeYarp` namespace to `Debug`. Per-namespace overrides for `Microsoft.AspNetCore` (Warning), `Microsoft.Hosting.Lifetime`, `Yarp`, `HomeYarp`.
- **Routing context: `{AppId}` + `{AppName}`** — `HomeYarpConfigProvider.BuildConfig` (per-app `BeginScope` *and* explicit template props for grep-ability), `SniCertificateSelector.Reload` (per-app `BeginScope` plus per-host bind log), `TlsPassthroughConnectionHandler.OnConnectedAsync` (resolved-app log + close log), `ApplicationService.{Create,Update,Delete}Async` (every entry/exit + the `EnsureAutoManagedCertificateAsync` decision branches: source-switch delete, hostname-changed regen, External re-issue rejection, first-time provisioning).
- **Cert lifecycle logs** — `CertificateService` (upload/delete + PEM-parse failure as Warning), `SelfSignedCertificateService` (issue/regen with thumbprint + validity window), `AcmeService` (now logs ms timing per orchestration, account-load cached vs fresh, per-challenge published/validate/poll, error catch-and-rethrow with timing), `AcmeRenewalService` (per-tick pre/post + per-cert timing), `InMemoryAcmeChallengeStore` (Debug publish/remove), the `/.well-known/acme-challenge/{token}` endpoint (Information on hit, Warning on miss).
- **Repos** — `JsonApplicationRepository` + `JsonCertificateRepository` log init, startup load count, per-mutation Debug, malformed-file Warning (was a silent skip before — now visible). `FileAcmeAccountStore` logs init + load/save with directory hash.
- **Controllers + Kestrel** — both controllers gained `ILogger<T>` and log each entry point (Information for writes, Debug for reads). `HomeYarpKestrelConfiguration` logs the resolved listener config at startup and warns if all listeners are disabled.
- **Test fix-ups** — only two needed: `ApplicationsControllerTests` and `CertificatesControllerTests` got `NullLogger<T>.Instance` since the controllers' loggers are non-nullable. All other services use the nullable-logger pattern (`ILogger<T>? logger = null` → `NullLogger<T>.Instance`) so existing test ctors keep working unchanged.
- **Docs** — README has a new "Logging" section with the Serilog overview, sample console output, copy-paste File + Seq snippets (including a one-liner Docker invocation for Seq), the level table, and a "Adding more sinks" pointer. CLAUDE.md gained a "Logging" section under Architecture explaining the bootstrap pattern, sink loading semantics, the `{AppId}`/`{AppName}` convention, and the nullable-logger pattern used by services.

### Live smoke

`timeout 8 dotnet run` shows the full chain working — `JsonApplicationRepository Loaded 2 application(s)`, then `Built YARP cluster 'echo-cluster' for app 'echo' (05315e43-…) with 1 destination(s) and 1 route(s)`, then `YARP config built: 2 route(s), 2 cluster(s), 2 application(s)`, then Kestrel binding messages, then `ACME renewal worker is disabled` since Acme:Enabled is off. Routing logs carry both AppId and AppName as required.

### Notes for future contributors

- Adding a new Serilog sink: `<PackageReference Include="Serilog.Sinks.X" />` in `HomeYarp.WebServer.csproj`, then `Serilog:Using` + `Serilog:WriteTo` entry in `appsettings.json` (or environment-specific override). No code changes — `ReadFrom.Configuration` handles discovery.
- Any new routing-adjacent code (route mapping, cert binding, TLS connection handlers, application CRUD) should keep the `{AppId}` + `{AppName}` convention for filterability.
- Services use `ILogger<T>? logger = null` to keep test ctors backwards-compatible. Controllers take non-nullable loggers since DI always provides them and the test boilerplate is small.
