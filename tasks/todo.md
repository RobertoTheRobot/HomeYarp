# Application edit — Advanced JSON view

## Goal

Keep the existing Simple form intact. Add an **Advanced (JSON)** toggle that swaps the form for a VS-Code-style JSON editor (BlazorMonaco) so power users can configure YARP features that aren't on the simple form — transforms, health checks, HTTP request options. Extend the domain to carry those new fields.

Plan file: `C:\Users\rober\.claude\plans\dazzling-churning-yeti.md`

## Plan

- [x] **Domain extension** — `RouteTransform`, `HealthCheckConfiguration` (+ `Active`/`Passive`), `HttpRequestConfiguration`. Wire into `RouteDefinition.Transforms`, `ClusterDefinition.{HealthCheck,HttpRequest}` as nullables.
- [x] **ConfigProvider mapping** — propagate the new fields to YARP `RouteConfig.Transforms`, `ClusterConfig.HealthCheck` (`HealthCheckConfig` with active/passive), `ClusterConfig.HttpRequest` (`ForwarderRequestConfig`). HTTP version parsing handles `1.1` / `2` / `2.0` / `3` / `3.0`.
- [x] **BlazorMonaco package + JsonEditor component** — add `BlazorMonaco` 3.4.0 to WebServer; wire Monaco loader scripts in `App.razor`; new reusable `Components/Shared/JsonEditor.razor` (vs-dark, JSON language, format-on-paste, line numbers, no minimap, two-way bound).
- [x] **ApplicationEdit toggle** — Simple/Advanced view buttons, Advanced view renders `<JsonEditor>` bound to a serialized `_model`, parse-and-save path that goes through `IApplicationService.{Create,Update}Async`. Switching back to Simple is blocked when JSON is malformed.
- [x] **Tests** —
  - `Domain/HealthCheckConfigurationDefaultsTests.cs` (defaults, all-null)
  - `Domain/RouteTransformTests.cs` (JSON round-trip)
  - `Application/Proxy/HomeYarpConfigProviderTests.cs` (Transforms / HealthCheck / HttpRequest mapping; HTTP version parser table)
  - `Persistance/JsonApplicationRepositoryTests.cs` (advanced fields round-trip across instances)
- [x] **Docs** — `CLAUDE.md` (domain bullet, Shared/JsonEditor, new "Application edit: Simple vs Advanced view" section noting the REST DTO limitation), `README.md` (Web UI, new "Advanced configuration (JSON editor)" section, Architecture).
- [x] **Verify** — `dotnet build` clean, `dotnet test` 177/177 (was 159, +18 new), live UI smoke (`/applications/new` returns 200, all four Monaco assets load 200).

## Out of scope

- Adding the advanced fields to `ApplicationRequest` / `ApplicationResponse` DTOs. The REST API still ignores them on POST/PUT. The JSON editor goes through the in-process service and is the supported way to set them. Adding DTO parity is a small follow-up.
- JSON schema autocomplete in Monaco. Generating a schema from `Application` is doable but separate.

## Review

**Result: 177/177 tests passing (159 prior + 18 new).**

```
Test run summary: Passed!
  total: 177
  failed: 0
  succeeded: 177
  skipped: 0
  duration: ~1.7s
```

### What got built

- **Domain** — three new types (`RouteTransform`, `HealthCheckConfiguration`, `HttpRequestConfiguration`) plus three new optional properties (`RouteDefinition.Transforms`, `ClusterDefinition.HealthCheck`, `ClusterDefinition.HttpRequest`). Existing JSON files round-trip unchanged.
- **ConfigProvider** — `BuildConfig` now sets `Transforms` on `RouteConfig`, plus `HealthCheck` and `HttpRequest` on `ClusterConfig`. Two private `ToYarp` helpers map our domain types to YARP's `HealthCheckConfig` / `ForwarderRequestConfig`. `ParseHttpVersion` accepts the common forms (`1.1`, `2`, `2.0`, `3`, `3.0`) and returns `null` for garbage.
- **JsonEditor component** — reusable `<JsonEditor @bind-Value="..." Height="600px" />` wrapping BlazorMonaco. Theme `vs-dark`, language `json`, format-on-paste/-type, tabsize 2, word wrap on, minimap off, bracket-pair colorization on. Pushes external `Value` updates back into the editor (so toggling Simple → Advanced re-serializes correctly).
- **ApplicationEdit toggle** — `view-toggle` button group above the form. Advanced renders the editor + a help paragraph + Save. State machine: Simple → Advanced serializes `_model`; Advanced → Simple parses or stays put with an error. Save in either mode uses the same service path so all validation rules still apply.
- **Tests** — defaults (all new types nullable, no NRE), JSON round-trip for `RouteTransform`, mapping tests for each YARP field, repo round-trip across two repository instances on the same temp dir.
- **Docs** — README has a new "Advanced configuration (JSON editor)" section with a worked example. CLAUDE.md captures the view toggle's state machine + the REST DTO caveat.

### Live smoke

- `dotnet build` clean.
- `dotnet test --solution HomeYarp.WebServer.slnx` → 177/177.
- `dotnet run` then `curl /applications/new` → 200. Monaco asset URLs (`/_content/BlazorMonaco/jsInterop.js`, `loader.js`, `editor.main.js`, `lib/monaco-editor/min/vs/loader.js`) all 200.
- POSTed an app with transforms/healthCheck/httpRequest in the body via `/api/applications`. Verified the persisted JSON file: the simple-form fields landed correctly; the advanced fields were dropped — confirms the documented DTO limitation. Setting them via the JSON editor (which goes through the service directly) does persist them — covered by the repo round-trip test.

### Notes for future contributors

- `JsonEditor` lives at `Components/Shared/JsonEditor.razor` and is registered via `_Imports.razor`'s `@using HomeYarp.WebServer.Components.Shared` — drop it into any page where users should hand-edit JSON.
- BlazorMonaco 3.4.0 ships its assets under `_content/BlazorMonaco/lib/monaco-editor/min/vs/...`. Earlier 3.x versions used different paths; if you bump the package, double-check `App.razor` script tags.
- The DTO/domain mismatch (advanced fields aren't in `ApplicationRequest`/`ApplicationResponse`) is intentional v1 scope. To close the gap, extend the DTOs + `ApplicationDtoMapper.{ToDomain,ToResponse}` and add round-trip tests in `WebServer/Controllers/ApplicationsControllerTests`.
