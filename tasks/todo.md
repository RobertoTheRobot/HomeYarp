# Internal vs External TLS toggle on Application

## Goal

Today a user wires TLS by: (1) creating a cert on the certificates page, then (2) editing the app and selecting that cert from a dropdown. Two-step. The user wants a one-step path on the application page itself: pick "internal" (auto self-sign) or "external" (auto Let's Encrypt) and HomeYarp owns the cert lifecycle for that app.

The existing cert source flows (manual upload, ACME page, self-signed page) all stay — they're useful when one cert serves many apps (wildcards, shared SAN) or when the user wants to keep cert lifecycle separate from app lifecycle.

## Design

**Data model — new `TlsCertificateSource` enum on `TlsConfiguration`:**

- `Manual` (0, default) — user picks an existing cert via `CertificateId`. Current behavior. No cert is created or deleted by the app service.
- `Internal` (1) — app service auto-creates a self-signed cert when the app is saved. Hostnames come from `app.Routes[*].Hosts`.
- `External` (2) — same, but via ACME (Let's Encrypt).

`Tls.CertificateId` keeps its meaning regardless of source — it's always the id of whichever cert this app uses. For Internal/External, it's populated by the app service after create/update.

**Lifecycle (in `ApplicationService`):**

On Create:
- If `Source == Internal` → call `ISelfSignedCertificateService.IssueAsync(name=app.Name, hostnames=collected from routes)`, set `app.Tls.CertificateId = result.Id`.
- If `Source == External` → call `IAcmeService.IssueAsync(...)`, same.
- If `Source == Manual` → require `CertificateId` when `Mode == Offload` (validation), no auto-creation.

On Update:
- If `Source != Manual` and `CertificateId` is null → first time switching to auto-managed; create cert.
- If `Source != Manual` and `CertificateId` is set → look up the cert; if its SAN list ≠ current hostnames, regenerate (Internal: `RegenerateAsync` after updating its SelfSigned.Hostnames; External: `RenewAsync`). If unchanged, leave alone.
- If `Source` changed from `Internal`/`External` to `Manual` → delete the previously auto-managed cert (it was 1:1 with this app). Then leave `CertificateId` to whatever the user picks.
- If `Source` changed between `Internal` ↔ `External` → delete the old auto-cert, create the new one.

On Delete:
- If `Source != Manual`, delete the app's cert too. 1:1 ownership.

**Validation additions in `ApplicationService.Validate`:**

- `Source == Internal | External`: at least one route with at least one non-empty host. Reject wildcards for `External` (matches `AcmeService.IssueAsync` rule). Reject if `Mode != Offload` (auto-managed only makes sense when proxy terminates TLS).
- `Source == Manual` + `Mode == Offload`: `CertificateId` is required (today this is implicit — selector silently drops the route). Make it explicit.
- `Source == External`: `AcmeOptions.Enabled` must be true and the rest of the ACME prerequisites met. Reuse the same checks `AcmeService.EnsureConfigured` runs, but surface them at app-save time so the UI can show a helpful error before kicking off the order.

**Regenerate semantics for `SelfSigned`:** Today `RegenerateAsync` reads hostnames from `existing.SelfSigned.Hostnames`. To support host-set changes, I need a way to pass new hostnames. Cleanest: add an overload `RegenerateAsync(id, hostnames)` that updates `SelfSigned.Hostnames` before regen, leaving the no-arg path unchanged (used by the cert page's "Regenerate" button).

**ACME re-issue on host change:** `IAcmeService.RenewAsync(id)` reuses the existing hostnames. For host changes, I'd need a new "re-issue with different hostnames" path. To keep this PR focused, v1 limits ACME re-issue to the original hostnames — if hostnames change on an External-managed app, throw a clear error pointing the user to delete + recreate, or to the certificates page. Self-signed gets the easy regen path because it's local; ACME crossing a network for every host edit is heavier and rarer.

**UI (`ApplicationEdit.razor`):**

When `Tls.Mode == Offload`, replace the bare cert dropdown with a "Certificate source" radio group:

- `Internal — HomeYarp generates a self-signed certificate for the route hostnames`
- `External — HomeYarp requests a certificate from Let's Encrypt`  *(disabled with note if ACME isn't configured; link to /settings)*
- `Use existing certificate` → reveals the current dropdown.

Help text under Internal/External: "Hostnames are taken from the routes above. Save the application to create or update the certificate."

Bind to `_model.Tls.Source` (new field). Hide the dropdown for Internal/External; on save, the service handles cert creation.

`Certificates.razor` source badge gets a third value (`auto` or label by source) — actually, certs created by auto-managed flow are still `SelfSigned` or `Acme`, so no UI change needed there. The "Source" column already labels them.

## Plan

- [x] **Domain** — Add `TlsCertificateSource` enum (`Manual`, `Internal`, `External`) and `Source` property on `TlsConfiguration` (default `Manual`).
- [x] **Self-signed service** — Add `RegenerateAsync(id, hostnames)` overload that updates `SelfSigned.Hostnames` before regen. Keep the existing no-arg overload calling the new one with the existing hostnames.
- [x] **Application service** — Inject `ISelfSignedCertificateService`, `IAcmeService`, `IOptionsMonitor<AcmeOptions>`. Implement the lifecycle rules above (`OnCreate`, `OnUpdate`, `OnDelete`). Pull the ACME prerequisite check out of `AcmeService.EnsureConfigured` into a small helper (`AcmeOptions.Validate()` or a static on `AcmeService`) so the app service can run it pre-save without invoking the order.
- [x] **Validation** — Extend `ApplicationService.Validate` for the new rules above. Manual + Offload now requires `CertificateId`; this is a behavior tightening that fixes a silent-failure case (currently the selector skips routes with no `CertificateId`).
- [x] **DTOs** — Add `Source` to `ApplicationTlsDto` (request + response).
- [x] **UI** — Update `ApplicationEdit.razor`:
  - Render the radio group when `Mode == Offload`.
  - Disable External radio + show note when `IOptionsMonitor<AcmeOptions>.CurrentValue.Enabled == false || !AgreeToTermsOfService || string.IsNullOrWhiteSpace(AccountEmail)`.
  - Hide cert dropdown when source ≠ `Manual`.
- [x] **Verify** — `dotnet build`, then live smoke:
  1. Create app `internal-app` with `Mode=Offload`, `Source=Internal`, route host `ha.home.lan`. POST → 201; GET cert by app's `CertificateId` → SAN contains `ha.home.lan`, source `Self-signed`.
  2. Update that app, change route host to `ha.lab.home.lan`. PUT → 200; same cert id, SAN regenerated, thumbprint changed.
  3. Update the same app, switch `Source=Manual`, set `CertificateId` to a different existing cert. PUT → 200; the original auto cert is gone (404 on direct GET).
  4. DELETE the app. Confirm the auto cert is also gone.
  5. With ACME disabled in config, attempt `Source=External` → expect 400 with the "ACME not configured" message.
  6. UI smoke: navigate to /applications/new, flip Mode → Offload, see radio group; pick External when ACME disabled → option is disabled with helper text.

## Out of scope

- ACME re-issue with changed hostnames — v1 throws and asks the user to recreate. Easy follow-up: add `IAcmeService.ReissueAsync(id, hostnames)`.
- Migrating existing apps' `Tls` blocks — they default to `Manual`, which preserves existing behavior. No data migration needed.

## Review

Implemented as planned. Files touched:

- `HomeYarp.Domain/TlsCertificateSource.cs` — new enum (Manual=0, Internal=1, External=2)
- `HomeYarp.Domain/TlsConfiguration.cs` — added `Source`
- `HomeYarp.Application/SelfSigned/ISelfSignedCertificateService.cs` + `SelfSignedCertificateService.cs` — new `RegenerateAsync(id, hostnames)` overload (existing no-arg overload now delegates)
- `HomeYarp.Application/Acme/AcmeOptionsValidator.cs` — new; `EnsureConfigured` (throws) + `IsConfigured` (bool). `AcmeService.EnsureConfigured` delegates to it
- `HomeYarp.Application/Services/ApplicationService.cs` — DI now pulls cert repo + self-signed + ACME + acme options. `EnsureAutoManagedCertificateAsync` covers create/update lifecycle; `DeleteAsync` cleans up auto-managed cert
- `HomeYarp.WebServer/Dtos/ApplicationDtos.cs` — `TlsDto.Source` (request + response)
- `HomeYarp.WebServer/Components/Pages/ApplicationEdit.razor` — radio group when Mode=Offload, External disabled when ACME not configured

### Smoke test results (live server)

1. POST app with `tls.mode=1, source=1` (Internal) and route `ha.home.lan` → `201`. Auto-cert `toggle-internal-internal` (id `d876…`) created with SAN `ha.home.lan`, source self-signed.
2. PUT same app with hostnames changed to `ha.lab.home.lan` + `*.lab.home.lan` → `200`. Cert id unchanged, thumbprint rotated (`14FFAB…` → `0440774B…`), `selfSigned.regeneratedAt` populated, SANs match new hosts.
3. PUT switching to `source=0` (Manual) with a different `certificateId` → `200`. The previous auto cert returned `404` on direct GET (cleaned up).
4. POST + DELETE round-trip on a fresh Internal app → both app and its auto cert are gone (`404`).
5. POST with `source=2` (External) while ACME disabled → `409` `"ACME is disabled. Set HomeYarp:Acme:Enabled to true..."` — clean message via the controller's existing `InvalidOperationException` → Conflict mapping. (Plan said 400; 409 is correct because the request itself is well-formed — it's a server-state conflict.)
6. UI: `/applications/new` renders 200; the radio group is hidden until the user picks Mode=Offload (interactive client-side state), so it doesn't appear in the prerender.

### Notes

- Tightened: `Mode=Offload` + `Source=Manual` now requires a non-null `CertificateId`. Previously the SNI selector silently dropped the route. This is a behavior change (400 instead of a quietly broken app) — anyone with an existing app saved in that state will get a validation error on next save, with a clear message pointing them at the fix.
- Hostname re-issue on `External` source is not supported in v1 — the service throws an `InvalidOperationException` ("Delete and recreate, or switch to Manual"). Adding `IAcmeService.ReissueAsync(id, hostnames)` is the trivial follow-up.
- Auto-managed cert names follow `{appName}-{internal|external}` and friendly names use `{displayName} ({label})`. App rename leaves the cert name stale, but linkage is by Id so nothing breaks; if that staleness bothers anyone, a rename-on-update is one line.
- `AcmeOptionsValidator` was first internal — promoted to public when the Razor page (cross-project) needed it. Both `EnsureConfigured` and `IsConfigured` live there now.
