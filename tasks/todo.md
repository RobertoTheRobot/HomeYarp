# HY-2 — Management surface on a dedicated port, gated by connection local port

## Context

Hand-off doc: `C:\dev\Agents\Lab manager\tasks\HY-2-lockdown-plan.md`. The management surface
(REST API + Blazor UI + MCP) shares the public HTTP listener with the proxy and is scoped only by
a client-spoofable `Host`-header allowlist — confirmed reachable from the WAN with
`Host: 192.168.1.2`. The fix: give management its own listener port so the deployment can simply
not forward that port at the firewall (WAN unreachability becomes a network fact).

## Critical deviation from the hand-off plan (§5.2.3)

The hand-off plan proposes `RequireHost($"*:{port}")`. **That does not close the hole.**
ASP.NET Core's `HostMatcherPolicy` matches the port in the *client-supplied Host header*, not the
port the connection arrived on — the official docs say so explicitly ("API that relies on the
Host header, such as `HttpRequest.Host` and `RequireHost`, are subject to potential spoofing by
clients. To prevent host and port spoofing, use `ConnectionInfo.LocalPort`."). A WAN attacker
could send `Host: whatever:5269` to the still-forwarded port 80 and route straight into
management — the exact same spoof as today, and the plan's own §6 verification (`Host:
192.168.1.2` → 404) would NOT catch it.

**Instead:** a custom `MatcherPolicy` (`LocalPortMatcherPolicy`, the same mechanism `RequireHost`
itself uses) that invalidates management endpoint candidates when
`HttpContext.Connection.LocalPort != managementPort`. Routing-level candidate invalidation (not a
middleware 404) so YARP's catch-all still wins on the public port — proxied apps that legitimately
serve `/api/*` paths of their own keep working, same semantics as the current `RequireHost` gate.

`HomeYarp:Management:Hosts` stays as an *optional additional* filter (ANDs with the port gate);
the port is the primary boundary.

## Checklist

- [x] `ListenerOptions.cs` — add `int? Management`
- [x] `HomeYarpKestrelConfiguration.cs` — bind the management listener + log line; include Management in the all-disabled warning
- [x] NEW `HomeYarp.WebServer/LocalPortRestriction.cs` — `LocalPortRestrictionMetadata` + `LocalPortMatcherPolicy` (`IEndpointSelectorPolicy`) + `RequireLocalPort<TBuilder>()` extension
- [x] `Program.cs` — register the policy in DI; apply `RequireLocalPort` to controllers/razor/mcp (+ OpenAPI in dev) when `HomeYarp:Listeners:Management` is set; keep the Hosts filter
- [x] `appsettings.json` — `"Management": 5269` under `HomeYarp:Listeners`
- [x] Tests — `HomeYarp.Tests/WebServer/LocalPortRestrictionTests.cs` (policy applies/doesn't, candidate valid on mgmt port, invalidated on public port, unrestricted candidate untouched, **spoofed Host-header port doesn't bypass**, extension adds metadata) + `ListenerOptionsTests.cs` (config binding)
- [x] Docs — CLAUDE.md (listeners + MCP sections), README (ports + MCP snippets + auth note), `HomeYarp.WebServer.http` (management requests → :5269)
- [x] `dotnet build` + `dotnet test --solution HomeYarp.WebServer.slnx` clean

Deploy-side steps (compose port publish 8080:5269, OPNsense verification, lab-manager repo docs,
Pi-hole record) live in the hand-off doc §5.3/§6/§8 and happen outside this repo.

## Review

**Result: 261/261 tests passing (252 baseline + 7 new `LocalPortRestrictionTests` + 2 new
`ListenerOptionsTests`). Live `dotnet run` smoke verified every boundary condition.**

Live smoke evidence (Development, ports 5268 public / 5269 management):

| Probe | Result |
|---|---|
| `GET :5269/api/applications` | **200** (route table JSON) |
| `GET :5269/` (Blazor UI) | **200** |
| `POST :5269/mcp` initialize | **200** (SSE initialize result) |
| `GET :5268/api/applications` | **404** |
| `GET :5268/api/applications` with `Host: localhost:5269` (spoof) | **404** |
| `GET :5268/api/applications` with `Host: 192.168.1.2:5269` (spoof) | **404** |
| `POST :5268/mcp` with `Host: localhost:5269` (spoof) | **404** |
| `GET :5268/.well-known/acme-challenge/probe` | **404 from HomeYarp's handler** — log shows "ACME HTTP-01 challenge requested for unknown token 'probe'", so the endpoint stays public |
| `GET :5268/` | **404** (UI no longer served on the public listener) |

Key decisions:
- **Deviated from the hand-off plan's `RequireHost("*:{port}")`** — it matches the client-supplied
  Host header's port (MS docs warn about exactly this) and is spoofable from the WAN through the
  still-forwarded port 80. Implemented `LocalPortMatcherPolicy` on `Connection.LocalPort` instead;
  the `ApplyAsync_SpoofedHostHeaderPort_DoesNotBypassRestriction` test pins the attack down.
- Candidate-level invalidation (a `MatcherPolicy`, same mechanism `RequireHost` uses) rather than a
  middleware 404, so YARP's catch-all still matches `/api/*` paths on the public listeners —
  proxied apps that serve their own `/api/*` routes keep working.
- `HomeYarp:Management:Hosts` kept as an optional additional filter; both gates AND together. The
  port is the primary boundary.
- Management listener binds AnyIP inside the container by design — LAN-only is enforced by the
  docker port publish + firewall (don't WAN-forward the host port), per hand-off doc §5.3.

Not in this repo (hand-off doc §5.3/§6/§8, owner/lab-manager side): compose `8080:5269` publish +
`HomeYarp__Listeners__Management=5269` env, OPNsense forward check, off-network verification,
lab-manager docs + `.mcp.json` URL change to `http://192.168.1.2:8080/mcp`.

Drive-by observation (not touched): `Controllers/ApplicationController.cs` is dead scaffolding —
namespace `HomeYarp.WebApi.Controllers`, no attribute route, returns a `View()` that doesn't exist;
`MapControllers` never routes it. Candidate for deletion in a cleanup PR.

---

# MCP server — expose the management API as MCP tools

## Goal

Everything the REST API can do (`/api/applications` + `/api/certificates`) must be doable via MCP. Host the MCP server **in the same WebServer process** over Streamable HTTP (`ModelContextProtocol.AspNetCore`, `app.MapMcp("/mcp")`) so tools call the same scoped services (`IApplicationService`, `ICertificateService`, `IAcmeService`, `ISelfSignedCertificateService`) the controllers use — same validation, same auto-managed cert lifecycle, same change-token hot reload.

## Design decisions

- **HTTP transport, same process.** No separate stdio project — the management surface already lives here, DI is free, and any MCP client can point at `http://host:5268/mcp`. Subject to the same `HomeYarp:Management:Hosts` `RequireHost` filter as controllers.
- **Reuse the existing DTOs + mappers** (`ApplicationRequest/Response`, `CertificateResponse`, etc. in `Dtos/`). The MCP surface stays in lockstep with the REST API by construction; the documented Advanced-fields limitation (transforms/healthCheck/httpRequest not in DTOs) applies identically.
- **Tool classes in `HomeYarp.WebServer/Mcp/`** — `ApplicationTools` + `CertificateTools`, `[McpServerToolType]` with constructor injection (non-static), registered via `.WithTools<T>()` (explicit, no assembly scan). Every tool method and every parameter carries `[Description]`.
- **Error mapping:** the controller's catch blocks translate to `McpException` — `ArgumentException` → "Validation failed: …", `KeyNotFoundException`/null → "not found", `InvalidOperationException` → conflict message. MCP clients see `isError` + message instead of HTTP status codes.
- **Tool surface (14 tools, 1:1 with the REST surface):**
  - Applications: `list_applications`, `get_application`, `create_application(request)`, `update_application(id, request)`, `delete_application(id)`
  - Certificates: `list_certificates`, `get_certificate`, `download_certificate_pem` (returns the public PEM chain string), `upload_certificate`, `issue_self_signed_certificate`, `regenerate_certificate`, `issue_acme_certificate`, `renew_certificate`, `delete_certificate`
- **Logging convention:** keep `{AppId}`/`{AppName}` (and `{CertId}`/`{CertName}`) structured properties, mirroring the controllers, prefixed "MCP …" instead of "API …".

## Plan (checklist)

- [x] Add `ModelContextProtocol.AspNetCore` package to `HomeYarp.WebServer.csproj`
- [x] `Mcp/ApplicationTools.cs` — 5 tools wrapping `IApplicationService`
- [x] `Mcp/CertificateTools.cs` — 9 tools wrapping `ICertificateService`/`IAcmeService`/`ISelfSignedCertificateService`
- [x] `Program.cs` — `AddMcpServer().WithHttpTransport().WithTools<…>()`, `MapMcp("/mcp")` before `MapReverseProxy()`, apply management-host filter
- [x] Tests: `HomeYarp.Tests/WebServer/Mcp/{ApplicationToolsTests,CertificateToolsTests}.cs` — NSubstitute mocks, Shouldly, mirroring the controller tests (happy path + error mapping per tool)
- [x] `dotnet build` + `dotnet test` clean (baseline 214)
- [x] Live smoke: `dotnet run` + MCP initialize/tools-list/tools-call against `/mcp`
- [x] Docs: CLAUDE.md "MCP server" section + REST-surface note; README snippet

## Review

**Result: 251/251 tests passing (220 pre-existing + 31 new MCP tool tests). Live smoke against `http://localhost:5268/mcp` verified: initialize handshake (protocol 2025-06-18), tools/list returns all 14 tools, `tools/call list_applications` returns the real on-disk apps, and a not-found `get_application` surfaces as `isError: true` with the McpException message.**

- Package: `ModelContextProtocol.AspNetCore 2.0.0-preview.1`. Its `McpException` in this version has no `McpErrorCode` overload — message-only ctor used throughout (the one anticipated deviation from the spec).
- `HomeYarp.WebServer/Mcp/ApplicationTools.cs` + `CertificateTools.cs` — sealed, ctor-injected, `[McpServerToolType]`; reuse the REST DTOs/mappers unchanged; snake_case tool names; every method + parameter carries `[Description]`; structured logging mirrors the controllers ("MCP …" prefix, same `{AppId}`/`{AppName}`/`{CertId}`/`{CertName}` properties).
- Wiring in `Program.cs`: `AddMcpServer().WithHttpTransport().WithTools<…>()` after `AddReverseProxy()`; `MapMcp("/mcp")` next to the controllers/razor mapping, before `MapReverseProxy()`, with the same `HomeYarp:Management:Hosts` `RequireHost` filter.
- Drive-by fix: `SniCertificateSelectorTests.Select_ParallelReadersDuringReload…` (from commit 93a0757) flaked when the 400 ms stop timer fired before any reader task got scheduled (`hits + misses = 0`); reader loop switched `while` → `do…while` so every reader completes at least one `Select`.
- Follow-up (pre-existing, unrelated): NU1903 warning — transitive `Microsoft.OpenApi 2.0.0` (via `Microsoft.AspNetCore.OpenApi 10.0.6`) has a known high-severity advisory; bump when a patched version ships.

---

# Performance pass — snapshot repos + passthrough route table (and what to do after)

## Scope of this plan

Two intertwined refactors that we'll ship together as **Phase 1**:

1. **Snapshot-backed repositories** (`JsonApplicationRepository`, `JsonCertificateRepository`) — replace per-read `_gate.WaitAsync` + `_cache.Values.ToList()` with an atomically-swapped immutable snapshot (`AllItems` + `ById` + `ByName`). Add a sync `GetSnapshot()` accessor on the interfaces so change-token consumers stop using `.GetAwaiter().GetResult()`.
2. **Passthrough route table** — new singleton in `HomeYarp.Application/Tls/` that subscribes to the app repo's reload token and exposes O(1) host→`Application` lookup. `TlsPassthroughConnectionHandler` consumes it instead of doing a per-connection `GetAllAsync` + nested LINQ scan.

Plus a roadmap for **Phases 2–5**, in the best order, summarised at the bottom.

## Phase 1 — detailed plan

### Why these two together

They share a common shape: the repos already cache in memory but every read pretends they don't, and the passthrough handler is the worst offender — `GetAllAsync().ToList()` + triple-nested `Any` per inbound TCP connection (`TlsPassthroughConnectionHandler.cs:142-149`). Once the repos expose a sync snapshot, the passthrough route table is the natural consumer that proves the new contract works under load.

### Approach

**1. New types in `HomeYarp.Application/Abstractions/`:**

```csharp
public sealed class ApplicationSnapshot
{
    public ImmutableArray<Domain.Application> All { get; init; }
    public ImmutableDictionary<Guid, Domain.Application> ById { get; init; }
    public ImmutableDictionary<string, Domain.Application> ByName { get; init; }   // OrdinalIgnoreCase
    public static ApplicationSnapshot Empty { get; } = ...;
}

public sealed class CertificateSnapshot
{
    public ImmutableArray<Certificate> All { get; init; }
    public ImmutableDictionary<Guid, Certificate> ById { get; init; }
    public ImmutableDictionary<string, Certificate> ByName { get; init; }   // OrdinalIgnoreCase
    public static CertificateSnapshot Empty { get; } = ...;
}
```

**2. Extend the repo interfaces with a sync accessor:**

```csharp
public interface IApplicationRepository
{
    ApplicationSnapshot GetSnapshot();   // NEW — sync, lock-free, returns the current immutable snapshot
    // ...existing async methods unchanged
}
public interface ICertificateRepository
{
    CertificateSnapshot GetSnapshot();   // NEW
    // ...
}
```

**3. Repo implementation changes (`JsonApplicationRepository`, `JsonCertificateRepository`):**

- Drop the lazy-load (`EnsureLoadedAsync`). Load synchronously in the constructor — it's bootstrap, file I/O is fine sync, JSON deserialize is cheap at homelab N. Result populates `_snapshot` before the first read can happen.
- Replace `Dictionary<Guid, T> _cache` + `bool _loaded` with `private volatile ApplicationSnapshot _snapshot = ApplicationSnapshot.Empty;` (and equivalent for cert repo).
- Keep `SemaphoreSlim _gate` — but **only writers acquire it**. Readers never touch it.
- `GetSnapshot()` → `return _snapshot;` (one volatile read).
- Existing async read methods become trivial wrappers: `Task.FromResult<IReadOnlyList<T>>(_snapshot.All)` etc. (or `ValueTask` if we want to be tidy, but `Task` keeps the interface change zero — preferred).
- Writers (`AddAsync`/`UpdateAsync`/`DeleteAsync`/`SaveAsync`) acquire `_gate`, do the file I/O, build the new snapshot from the previous one + the mutation, then `_snapshot = newSnapshot` (atomic reference swap on the volatile field).
- Snapshot rebuild on write: `_snapshot.All.Add/Remove/Replace` + `ById.SetItem/Remove` + `ByName.SetItem/Remove`. All O(log N) on `ImmutableDictionary`, fine at this scale.

**4. New singleton `PassthroughRouteTable` in `HomeYarp.Application/Tls/`:**

```csharp
public sealed class PassthroughRouteTable : IDisposable
{
    private readonly IApplicationRepository _apps;
    private readonly ILogger<PassthroughRouteTable>? _logger;
    private readonly object _writerLock = new();
    private volatile RouteTable _table = RouteTable.Empty;
    private IDisposable? _subscription;

    public PassthroughRouteTable(IApplicationRepository apps, ILogger<PassthroughRouteTable>? logger = null)
    {
        _apps = apps; _logger = logger ?? NullLogger<PassthroughRouteTable>.Instance;
        Rebuild();
        _subscription = ChangeToken.OnChange(apps.GetReloadToken, Rebuild);
    }

    public bool TryResolve(string sni, out Domain.Application app) { /* exact then wildcard */ }

    private void Rebuild()
    {
        var snapshot = _apps.GetSnapshot();   // sync now — no .GetAwaiter().GetResult()
        var byHost = ImmutableDictionary.CreateBuilder<string, Domain.Application>(StringComparer.OrdinalIgnoreCase);
        var wildcards = ImmutableArray.CreateBuilder<(string suffix, Domain.Application app)>();
        foreach (var a in snapshot.All)
        {
            if (!a.Enabled || a.Tls.Mode != TlsMode.Passthrough) continue;
            foreach (var r in a.Routes)
                foreach (var h in r.Hosts)
                    if (h.StartsWith("*.", StringComparison.Ordinal)) wildcards.Add((h[1..], a));
                    else byHost[h] = a;
        }
        _table = new RouteTable(byHost.ToImmutable(), wildcards.ToImmutable());
        _logger.LogInformation("Passthrough route table rebuilt: {ExactCount} exact, {WildcardCount} wildcard", byHost.Count, wildcards.Count);
    }

    public void Dispose() => _subscription?.Dispose();

    private sealed record RouteTable(ImmutableDictionary<string, Domain.Application> Exact, ImmutableArray<(string Suffix, Domain.Application App)> Wildcards) { public static RouteTable Empty { get; } = ...; }
}
```

`TryResolve` looks up `Exact` first, then linear-scans `Wildcards` (small list — wildcards are rare). Same matching semantics as the current `HostMatches`.

**5. `TlsPassthroughConnectionHandler` consumes the table:**

```csharp
public TlsPassthroughConnectionHandler(PassthroughRouteTable routes, ILogger<...> logger)
```

`ResolveAppAsync` becomes `_routes.TryResolve(sni, out app)` — sync, no await. `OnConnectedAsync` keeps its async/Task signature for the SNI-peek + pump.

**6. Hot consumers switch off `.GetAwaiter().GetResult()`:**

- `HomeYarpConfigProvider.BuildConfig`: `_repository.GetSnapshot().All` instead of `_repository.GetAllAsync().GetAwaiter().GetResult()`.
- `SniCertificateSelector.ReloadCore`: `_applications.GetSnapshot().All` and `_certificates.GetSnapshot().All` instead of two `.GetAwaiter().GetResult()`s.
- `SniCertificateSelector.ReloadCore` cert PEM read on line 125: leave as `.GetAwaiter().GetResult()` for now — that's `GetMaterialAsync` which does real file I/O. Phase 3 (cert caching) addresses this.

**7. DI registration (`HomeYarp.Application/DependencyInjection.cs`):**

- Add `services.AddSingleton<PassthroughRouteTable>();` next to the existing `SniCertificateSelector` registration.

### Files touched

- `HomeYarp.Application/Abstractions/IApplicationRepository.cs` — add `GetSnapshot()`.
- `HomeYarp.Application/Abstractions/ICertificateRepository.cs` — add `GetSnapshot()`.
- `HomeYarp.Application/Abstractions/ApplicationSnapshot.cs` — NEW.
- `HomeYarp.Application/Abstractions/CertificateSnapshot.cs` — NEW.
- `HomeYarp.Application/Tls/PassthroughRouteTable.cs` — NEW.
- `HomeYarp.Application/Tls/TlsPassthroughConnectionHandler.cs` — depend on `PassthroughRouteTable`; delete `ResolveAppAsync` + `HostMatches`.
- `HomeYarp.Application/Proxy/HomeYarpConfigProvider.cs` — sync snapshot read in `BuildConfig`.
- `HomeYarp.Application/Tls/SniCertificateSelector.cs` — sync snapshot reads at the top of `ReloadCore` (cert PEM load deferred to Phase 3).
- `HomeYarp.Application/DependencyInjection.cs` — register `PassthroughRouteTable`.
- `HomeYarp.Persistance/Json/JsonApplicationRepository.cs` — snapshot-backed; sync constructor load; writers rebuild snapshot.
- `HomeYarp.Persistance/Json/JsonCertificateRepository.cs` — same pattern.

### Tests (per the "tests-with-features" rule)

- `HomeYarp.Tests/Persistance/Json/JsonApplicationRepositoryTests.cs` — extend:
  - `Constructor_LoadsExistingFiles_PopulatesSnapshot`
  - `GetSnapshot_AfterAdd_ContainsNewItem`
  - `GetSnapshot_AfterDelete_OmitsItem`
  - `GetSnapshot_ReturnsSameInstanceUntilNextWrite` (reference equality)
  - `GetSnapshot_ConcurrentReadsDoNotBlockEachOther` (smoke — N readers in `Parallel.For` while a writer mutates; readers always observe a *consistent* snapshot, never a half-built one).
- `HomeYarp.Tests/Persistance/Json/JsonCertificateRepositoryTests.cs` — same set, adapted.
- `HomeYarp.Tests/Application/Tls/PassthroughRouteTableTests.cs` — NEW:
  - `TryResolve_ExactHost_ReturnsApp`
  - `TryResolve_WildcardHost_MatchesSubdomain`
  - `TryResolve_DisabledApp_NotResolved`
  - `TryResolve_NonPassthroughApp_NotResolved`
  - `Rebuild_AfterRepoSignalReload_PicksUpNewApp`
  - `Rebuild_AfterRepoSignalReload_DropsRemovedApp`
- Existing `TlsPassthroughConnectionHandler` tests (if any) updated to inject the table.
- Existing `HomeYarpConfigProvider` / `SniCertificateSelector` tests should still pass — interface contract preserved.

### Verification

1. `dotnet build HomeYarp.WebServer.slnx` clean.
2. `dotnet test --solution HomeYarp.WebServer.slnx` — all tests pass (177 baseline).
3. `dotnet run` smoke: create a passthrough app via UI, hit it via `curl --resolve foo:5444:127.0.0.1 https://foo/` (or whatever the existing smoke recipe is) — passthrough still works end-to-end.
4. Spot-check logs: `Passthrough route table rebuilt: ...` appears at startup and on every UI reload.

### Risks

- **Constructor file I/O at startup** — `JsonApplicationRepository` and `JsonCertificateRepository` ctors will read every JSON file synchronously before DI returns. At homelab N (≤ a few hundred files) this is sub-100ms; not a concern. If it ever became one, an `IHostedService` startup hook is the migration path.
- **`ImmutableDictionary` rebuild cost on writes** — O(N log N) for a full rebuild per mutation. At homelab N, irrelevant. The win on reads (zero allocations, lock-free) dwarfs it.
- **Existing `EnsureLoadedAsync` removal** — any test that constructs a repo and expects lazy loading will need adjustment. Constructor load is the new contract.
- **Volatile semantics** — `_snapshot = newSnapshot` on a `volatile` reference is atomic on .NET; readers either see the old or the new, never a torn write. The snapshot itself is immutable so partial observation isn't possible.

## Plan (checklist)

- [ ] **Add `ApplicationSnapshot` + `CertificateSnapshot` records** in `HomeYarp.Application/Abstractions/`.
- [ ] **Extend `IApplicationRepository` + `ICertificateRepository`** with `GetSnapshot()`.
- [ ] **Refactor `JsonApplicationRepository`** — snapshot-backed, sync ctor load, writers rebuild + atomic swap.
- [ ] **Refactor `JsonCertificateRepository`** — same pattern.
- [ ] **Add `PassthroughRouteTable`** singleton in `HomeYarp.Application/Tls/`.
- [ ] **Refactor `TlsPassthroughConnectionHandler`** to depend on `PassthroughRouteTable`; delete `ResolveAppAsync` + `HostMatches`.
- [ ] **Update `HomeYarpConfigProvider.BuildConfig`** — drop `.GetAwaiter().GetResult()`, use `GetSnapshot()`.
- [ ] **Update `SniCertificateSelector.ReloadCore`** — drop the two `.GetAwaiter().GetResult()`s on the snapshot reads (cert PEM read stays for Phase 3).
- [ ] **Register `PassthroughRouteTable`** in `HomeYarp.Application/DependencyInjection.cs`.
- [ ] **Tests** — add the test classes listed above; update existing tests that used lazy-load behavior.
- [ ] **Verify** — `dotnet build` + `dotnet test` clean; live `dotnet run` smoke for passthrough + offload paths.
- [ ] **CLAUDE.md** — add a one-paragraph note in the "Change-token reload chain" section about the snapshot pattern + the new `PassthroughRouteTable`. Update the per-connection comment for `TlsPassthroughConnectionHandler`.

## Out of scope for Phase 1

- Cert PEM caching across reloads (Phase 3).
- Reload coalescing / debounce (Phase 4).
- Moving the YARP rebuild off the writer's thread (Phase 4 — but reloads are now user-explicit, not auto, so this is a smaller win than originally framed).
- Lock-free `SniCertificateSelector.Select` (Phase 5 — drive-by).
- `TlsClientHelloParser` `SequenceReader<byte>` rewrite (Phase 6).
- Renewal jitter (Phase 6).
- Service-lifetime audit (Phase 6).

---

# Phase roadmap (after Phase 1)

In order. Each phase is independently mergeable.

## Phase 2 — SNI selector cert caching by thumbprint

**Win:** the user clicks "Reload" → today every PEM is re-read from disk and PEM→PFX-roundtripped, even certs that haven't changed. Cache loaded `X509Certificate2` by `(certId, thumbprint)`; reuse on reload if the manifest's thumbprint matches.

**Approach:** `SniCertificateSelector.ReloadCore` keeps a `Dictionary<Guid, (X509Certificate2 cert, string thumbprint)> _loadedCerts`. New cert in cache + thumbprint match → reuse. Mismatch → reload the PEM, dispose-deferred the old. Brand new id → load. Removed id → defer dispose. Keeps the "don't dispose immediately because Schannel may still hold it" behavior (current comment lines 184-187) — defer disposal to a 60-second timer, then `Dispose()`.

**Files:** `SniCertificateSelector.cs` only. Tests cover (a) reuse on no-change reload, (b) replace on thumbprint change, (c) deferred disposal of replaced cert.

## Phase 3 — Lock-free `SniCertificateSelector.Select`

**Win:** drop the `lock` per TLS handshake (`SniCertificateSelector.cs:35-38`). Tiny but free.

**Approach:** make `_byHost` a `volatile Dictionary<string, X509Certificate2>` (or wrap in a small immutable record). Writers in `Reload` build a fresh dictionary, then `_byHost = newDict`. Reader does one volatile load, no lock.

**Files:** `SniCertificateSelector.cs` only. One stress test (parallel `Select` while reload runs).

## Phase 4 — Reload coordination (debounce + off-writer-thread rebuild)

**Win:** lower-priority now that reloads are user-explicit (not fired automatically by writes), but renewal workers still trigger bursts. A renewal cycle that rotates 10 self-signed certs fires `SignalReload` 10 times → 10 selector rebuilds + 10 YARP rebuilds back-to-back. Coalesce to one.

**Approach:** new `ReloadCoordinator` singleton with a `Channel<ReloadKind>` worker. Repos publish to the channel from `SignalReload` (instead of cancelling the CTS directly). Worker drains, debounces (~50ms), then fires consumer rebuilds in dependency order: `PassthroughRouteTable` → `HomeYarpConfigProvider` → `SniCertificateSelector`. Consumers expose sync `RebuildSync()` methods; they no longer subscribe to the repo's change token directly. The repo's `IChangeToken` stays available for any external subscriber but the in-process consumers all go through the coordinator.

**Files:** new `ReloadCoordinator`; tweak `JsonApplicationRepository.SignalReload` + `JsonCertificateRepository.SignalReload`; tweak the three consumers' subscription wiring.

## Phase 5 — Lesser items (single PR, drive-by)

- **`TlsClientHelloParser.cs:52`** — replace `handshake.ToArray()` fallback with `SequenceReader<byte>` so multi-segment ClientHellos don't allocate. Edge case, but free.
- **`AcmeRenewalService` + `SelfSignedRenewalService`** — add per-cert random jitter (0-10% of `RenewalInterval`) to the next-tick delay so N certs at the same threshold don't spike together.
- **Service-lifetime audit** — `ApplicationService` and `CertificateService` are scoped but stateless — promote to singleton. Verify no scoped dependencies.

---

## Why this order

| Phase | Ships | Hot-path? | Depends on |
|---|---|---|---|
| 1 | Snapshot repos + passthrough route table | Yes — every passthrough connection + every snapshot read | Nothing |
| 2 | Cert PEM caching | Reload-time (user-initiated) | Phase 1 sync snapshot accessor |
| 3 | Lock-free Select | Yes — every TLS handshake | Nothing (independent) |
| 4 | Reload coordination | Reload-time | Phase 1 + 2 done so consumers have stable rebuild contracts |
| 5 | Misc drive-by | Mixed | Nothing |

Phase 1 + 2 cover the user-visible wins (passthrough latency, reload responsiveness). Phase 3 is a single-line change you can ship anytime. Phase 4 is the architectural cleanup that makes future consumers easier to wire. Phase 5 is debt cleanup.

---

## Phase 1 — Review

**Result: 209/209 tests passing (177 baseline + 12 new snapshot tests + 9 new PassthroughRouteTable tests + the pre-existing 11 changed-stub tests still passing). Live `dotnet run` smoke confirms eager constructor load + route table rebuild on startup.**

### What got built

- **`ApplicationSnapshot` + `CertificateSnapshot`** in `HomeYarp.Application/Abstractions/`. Each holds `ImmutableArray<T> All` + `ImmutableDictionary<Guid, T> ById` + case-insensitive `ImmutableDictionary<string, T> ByName`. Static `Empty` and `FromItems(IEnumerable<T>)` factories.
- **`IApplicationRepository.GetSnapshot()` + `ICertificateRepository.GetSnapshot()`** — sync, lock-free accessor for the current snapshot. Existing async API methods (`GetAllAsync`, `GetByIdAsync`, `GetByNameAsync`) preserved as `Task.FromResult` wrappers so controllers + Blazor pages don't change.
- **`JsonApplicationRepository` + `JsonCertificateRepository`** — replaced `Dictionary<Guid, T> _cache` + `bool _loaded` + per-read `_gate.WaitAsync` with `volatile ApplicationSnapshot/CertificateSnapshot _snapshot`. Constructor now loads synchronously (small N, JSON deserialize is cheap, removes "consumer started before first read" race). Writers acquire `_writeGate` (single-writer file-I/O serialization), do the I/O, then rebuild the snapshot from the post-mutation `All` and `volatile`-store. Rebuilding from `All` (instead of diffing against the prior snapshot) keeps `ByName` correct when callers mutate the same `Application` instance in place — that bit me on the rename test until I switched away from the diff approach.
- **`PassthroughRouteTable`** singleton in `HomeYarp.Application/Tls/`. Subscribes to the app repo's reload token, exposes O(1) `TryResolve(sni, out app)` with exact-host `ImmutableDictionary` + wildcard suffix `ImmutableArray`. Replaces the per-connection `GetAllAsync().FirstOrDefault(... Routes.Any(r => r.Hosts.Any(...)))` in `TlsPassthroughConnectionHandler.ResolveAppAsync` (deleted along with the now-unused `HostMatches`).
- **Hot consumers** (`HomeYarpConfigProvider.BuildConfig`, `SniCertificateSelector.ReloadCore`) — dropped `.GetAwaiter().GetResult()` over `GetAllAsync`, now read `_repo.GetSnapshot().All` (or `.ById` for the cert lookup) directly. Cert PEM read in the selector still uses `GetMaterialAsync` (real file I/O — Phase 2 addresses this).
- **DI** — `services.AddSingleton<PassthroughRouteTable>()` next to `SniCertificateSelector` in `HomeYarp.Application/DependencyInjection.cs`.
- **Tests** — extended `JsonApplicationRepositoryTests` + `JsonCertificateRepositoryTests` with snapshot semantics (after-add / after-update / after-rename / after-delete / reference-equality between writes / concurrent-reads-with-writer / constructor-loads). New `PassthroughRouteTableTests` (exact + wildcard match, case-insensitive, disabled-app skip, non-passthrough skip, reload pickup/drop, exact-wins-over-wildcard). Added `RepositoryMockExtensions.WithApps` / `WithCerts` test helper to keep mock setups terse — applied to `HomeYarpConfigProviderTests` and `SniCertificateSelectorTests` whose mocks now need to stub `GetSnapshot` consistently with `GetAllAsync`.
- **CLAUDE.md** — added a "Snapshot-backed repositories" subsection under "Change-token reload chain" and a 4th bullet covering `PassthroughRouteTable`. The old line "TlsPassthroughConnectionHandler does not subscribe — it just queries `GetAllAsync()` per connection. The in-memory cache + small N for homelab use makes this fine." is gone — the route table is now the canonical pattern.

### Live smoke

`timeout 8 dotnet run` (existing 2-app dataset, 1 passthrough):
```
JsonApplicationRepository initialized at .../data/applications with 2 application(s)
Passthrough route table rebuilt: 1 exact host(s), 0 wildcard(s)
YARP config built: 2 route(s), 2 cluster(s), 2 application(s)
Listening for HTTPS-passthrough (raw L4 + SNI peek) on port 5444
Now listening on: http://[::]:5268 / https://[::]:5443 / http://[::]:5444
```
Eager constructor load fires before DI returns. Route table rebuilds during selector startup. All three listeners bind cleanly.

### Surprises / corrections during the work

- The plan said "existing HomeYarpConfigProvider / SniCertificateSelector tests should still pass — interface contract preserved." Wrong: adding `GetSnapshot()` to the interface meant NSubstitute's auto-null return broke 19 tests on first run. Fixed by adding a `RepositoryMockExtensions.WithApps`/`WithCerts` helper that stubs both `GetSnapshot` and `GetAllAsync` together, so tests stay terse.
- Initial `ReplaceItem` tried to diff incoming-vs-previous to detect renames. That fails when callers mutate the same instance in place (the Blazor JSON-edit path does this, and the `GetSnapshot_AfterRename_DropsOldNameBinding` test caught it). Switched to "rebuild ById/ByName from the post-mutation `All` array via `FromItems`" — robust and trivially cheap at homelab N.
- `ImmutableArray<T>.Remove(T)` uses default equality (reference for class types), so `RemoveItem` taking the `existing` reference would have worked in practice — but I switched to `RemoveAll(a => a.Id == id)` for clarity. Same cost.

### Notes for future contributors

- `_writeGate` is now write-only — readers must never touch it. Adding a new read API? Use `_snapshot` directly.
- The snapshot is **immutable**, so it's safe to capture into local variables and iterate without re-reading. This matters for hot paths: capture `var s = _snapshot;` once at the top, not per-loop-iteration.
- New consumers that need to react to repo changes should mirror `PassthroughRouteTable`'s pattern: subscribe to `repo.GetReloadToken()` via `ChangeToken.OnChange`, read via `GetSnapshot()` (sync), build an immutable index, `volatile`-store. Don't go through the async API on the change-token callback path.
- Test mocks for `IApplicationRepository` / `ICertificateRepository` must stub `GetSnapshot` — use `RepositoryMockExtensions.WithApps` / `WithCerts` in `HomeYarp.Tests/TestHelpers/` for the right combined setup.

---

## Phase 2 — Review

**Result: 213/213 tests passing (Phase 1 baseline 209 + 4 new SNI selector cache/disposal tests). Live `dotnet run` boots cleanly; selector is lazy-constructed by Kestrel on first TLS handshake so no startup log entry appears in the 8s smoke window.**

### What got built

- **`SniCertificateSelector` cert cache** — added persistent `Dictionary<Guid, LoadedCert> _loadedCerts` (a `record(X509Certificate2 Cert, string Thumbprint)`) that survives across reloads. On each `ReloadCore`, for every cert id referenced by an enabled offload-mode app, the selector first checks the cache: if `cached.Thumbprint == certMeta.Thumbprint` (case-insensitive), it reuses the existing `X509Certificate2` and skips `GetMaterialAsync` + `LoadX509` (the disk read + PEM→PFX roundtrip). The reload-summary log now reports `{Reused}/{Fresh}/{Orphaned}` so it's visible whether the cache is hot.
- **Reload serialization** — added `_reloadLock` separate from `_stateLock`. The whole `ReloadCore` body runs under `_reloadLock` so two concurrent reloads (apps token + certs token firing on different threads) can't double-load the same cert or overwrite each other's `_loadedCerts`. `_stateLock` still guards the brief `_loadedCerts`/`_byHost` reference swap and the `Select` snapshot read.
- **Deferred disposal** — replaced the "leak X509Certificate2 instances to the finalizer" approach with explicit deferred dispose. When a cert is replaced (thumbprint changed) or orphaned (no app references it anymore), `ScheduleDeferredDispose` schedules `cert.Dispose()` after `DeferredDisposeDelay = 60s`. Uses `Task.Delay(delay, _timeProvider, _disposeCts.Token)` so (a) tests with `FakeTimeProvider` can advance virtual time, (b) selector `Dispose()` cancels all pending disposals and runs them eagerly.
- **`TimeProvider` parameter** — selector ctor now takes an optional `TimeProvider? timeProvider = null` (defaults to `TimeProvider.System`). DI already registers `TryAddSingleton(TimeProvider.System)` from `AddHomeYarpApplication`, so production wiring needs no change. Existing tests pass because the parameter is optional.
- **Tests** — added `Reload_WhenCertThumbprintUnchanged_DoesNotReadMaterialAgain` (verifies `GetMaterialAsync` call count stays at 1 after a same-thumbprint reload), `Reload_WhenCertThumbprintChanges_ReadsMaterialAgain` (call count goes to 2 when the metadata thumbprint changes), `Reload_WhenAppRemoved_OrphanedCertNotImmediatelyDisposed` (cert handle still alive at t=0), `Reload_WhenAppRemoved_OrphanedCertDisposedAfterDelay` (`time.Advance(61s)` triggers disposal — verified via `cert.Handle == IntPtr.Zero`).
- **Test helper `NewIsolatedSubstitutesWithSingleCert`** — bypasses the test class's default constructor stubs (which use `Returns(_ => Substitute.For<...>())` lambdas and break NSubstitute's call tracking when overridden). Mirrors the approach already used by `Reload_WhenCertMaterialThrows`.
- **CLAUDE.md** — updated bullet 3 of "Change-token reload chain" to describe the cache-by-thumbprint behavior, deferred-dispose semantics, and the `TimeProvider` injection point.

### Surprises / corrections during the work

- First test run failed because my test data used a placeholder `Thumbprint = "ABCDEF1234567890"` in the manifest, but the cache stores `x509.Thumbprint` (the actual thumbprint computed from the PEM). Cache miss every reload → no win. Fixed by computing the real thumbprint via `X509Certificate2.CreateFromPem(certPem, keyPem).Thumbprint` in the test helper. **Production code already does this** (e.g. `CertificateService.UploadAsync` reads the thumbprint from the parsed cert before persisting), so the SUT contract is correct — the test data was wrong.
- NSubstitute's `Returns(_ => SomethingThatCallsAnotherSubstitute())` pattern in the test class constructor (`NeverFiringToken()` calls `Substitute.For<IChangeToken>()`) breaks call tracking when downstream tests try to override the same method. Solved by giving the new tests their own substitutes via `NewIsolatedSubstitutesWithSingleCert` — same workaround the existing `Reload_WhenCertMaterialThrows_DoesNotPropagateAndKeepsPreviousBindings` already uses.
- `cert.Handle == IntPtr.Zero` is the reliable cross-platform signal for "X509Certificate2 has been disposed" — `cert.Thumbprint` caches the value internally and keeps returning it post-disposal on .NET 10.

### Notes for future contributors

- The cache is keyed on the manifest's `Thumbprint` field. **Whenever code persists or rotates a certificate, it MUST set `Thumbprint` to match the actual cert's thumbprint** (uppercase hex). All current writers do — `CertificateService.UploadAsync`, `SelfSignedCertificateService.IssueAsync`/`RegenerateAsync`, `AcmeService.IssueAsync`/`RenewAsync`. If you add a new writer, mirror that behavior or the SNI selector will reload from disk every time even when nothing changed.
- `DeferredDisposeDelay = 60s` is conservative for typical handshake lifetimes. If you ever observe "Cannot find the requested object" Schannel errors after a reload, increase it. If you ever want eager reclaim during testing, inject a `FakeTimeProvider` and advance it.
- New tests that need to interact with the selector's reload subscription should use the `NewIsolatedSubstitutesWithSingleCert` + `FireApps` helpers, not the class's default `_apps`/`_certs` fields. The class-level fields are pre-stubbed in a way that conflicts with downstream `.Returns(...)` overrides.

---

## Phase 3 — Review

**Result: 214/214 tests passing (Phase 2 baseline 213 + 1 new parallel-readers stress test). Build clean, smoke clean.**

### What got built

- **`_byHost` is now `volatile Dictionary<string, X509Certificate2>`** — the publication field for the host-binding map. Writers in `ReloadCore` build a fresh dict, populate it fully, then assign (`_byHost = newByHost;`). Volatile semantics give release-on-write / acquire-on-read so readers either see the previous fully-published dict or the new one — never a half-built one.
- **`Select(sni)` is lock-free** — replaced the `lock (_stateLock) { snapshot = _byHost; }` guard with a single volatile read (`var snapshot = _byHost;`). No lock per TLS handshake, no allocation.
- **`_stateLock` removed entirely** — its only other use guarded the `_loadedCerts` swap, but `_loadedCerts` is exclusively touched inside `_reloadLock` (in `ReloadCore` and `Dispose`), so no extra lock is needed. Disposing now takes only `_reloadLock`.
- **`Dispose` rewires the `_byHost` swap** — instead of `_byHost.Clear()` (which would mutate a dict a concurrent `Select` might still be reading), Dispose publishes a fresh empty dict via the volatile field. Concurrent readers either see the populated pre-Dispose dict or the new empty one — both safe.
- **Stress test** — `Select_ParallelReadersDuringReload_NeverThrowAndAlwaysReturnConsistentSnapshot` spins up 8 reader threads calling `Select` in tight loops while a writer thread fires reload events for ~400ms. Every non-null result has its `.Thumbprint` touched to verify the cert is alive (would throw if torn). All readers complete without exception.

### Surprises / corrections during the work

- The stress test failed to compile on first try because I used `Enumerable.Range(0, n).Select(_ => ...)` and then `_ = result.Thumbprint` inside the inner lambda. The lambda parameter `_` is `int` from `Range`; the outer `_ = ...` discard tried to assign a `string` to that captured `int`. Renamed the lambda parameter to `i` and the discard to `var probe = ...`. C# discards (`_`) are normally context-free, but they collide with explicitly-named `_` parameters in enclosing scopes.

### Notes for future contributors

- `_byHost` must remain `volatile` — `Volatile.Read`/`Volatile.Write` calls would be equivalent but `volatile` field is the cleanest expression of intent. Don't drop the modifier.
- Treat the published dict as **immutable** — never mutate `_byHost` in place. Build a new one and atomic-swap the reference. The current `Dispose` violation (`_byHost.Clear()`) was the bug to avoid.
- Phase 3 is independent of Phase 4 (reload coordination). You can ship them in either order or together.
