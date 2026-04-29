---
name: frontend
description: Frontend development guidance for the HomeYarp Blazor Server UI. Use when creating, modifying, or reviewing Razor pages, components, layouts, forms, dialogs, tables, or anything in HomeYarp.WebServer/Components/. Enforces MudBlazor-first, dark theme by default, no hand-written JS/CSS when a component covers it, and reusable Blazor components for repeated UI.
---

# HomeYarp Frontend Skill

## Scope

Applies to anything under `HomeYarp.WebServer/Components/` — pages, layouts, shared components, forms, dialogs, navigation. Server-rendered Blazor (`InteractiveServer`) only — there is no WASM project.

## Rules

### 1. MudBlazor first
- Default to MudBlazor components for any new UI: `MudButton`, `MudTextField`, `MudSelect`, `MudTable`/`MudDataGrid`, `MudDialog`, `MudSnackbar`, `MudCard`, `MudPaper`, `MudGrid`, `MudStack`, `MudIconButton`, `MudTooltip`, `MudSwitch`, `MudCheckBox`, `MudRadioGroup`, `MudTabs`, `MudExpansionPanels`, `MudAutocomplete`, etc.
- Use plain HTML only when MudBlazor has no equivalent (e.g., very thin structural wrappers, semantic landmarks where Mud doesn't expose one).
- Existing non-Mud pages should be migrated opportunistically when touched. Don't rewrite an unrelated page just to "Mudify" it — but when editing one, prefer Mud over carrying old patterns.

### 2. Dark theme is the default
- `MainLayout.razor` declares `<MudThemeProvider IsDarkMode="true" />`. Don't flip it to light.
- If a future user-toggle is needed, bind `IsDarkMode` to a setting, but the default state stays dark.
- Don't override Mud's palette with raw CSS variables. If colors must change, supply a `MudTheme` with a custom `PaletteDark`.

### 3. Avoid hand-written JS and CSS when a component exists
- No new `<script>` blocks, `IJSRuntime` calls, or JS interop unless MudBlazor (and the BCL) genuinely don't cover the need. Document the reason in a one-line comment when you do add JS.
- No new `app.css` rules for things Mud handles (spacing, cards, dialogs, tabs, table styling). Use Mud spacing utilities (`Class="ma-4 pa-2 d-flex gap-2"`) and component props (`Dense`, `Outlined`, `Elevation`, `Variant`, `Color`, `Size`).
- CSS isolation (`MyComponent.razor.css`) is acceptable for component-local layout that Mud utilities can't express — keep it tight, no global selectors.

### 4. Reusable bits become components
- If the same UI pattern appears in two places, extract it to `HomeYarp.WebServer/Components/Shared/`. Pattern reference: `Components/Shared/JsonEditor.razor` (parameter-bound, two-way value, focused responsibility).
- Components own their parameters via `[Parameter]`. Two-way binding uses the `Value` / `ValueChanged` pair so consumers can `@bind-Value`.
- One component per file. Keep them small — split when a `.razor` exceeds ~200 lines or holds more than one responsibility.

### 5. Forms and validation
- Use `MudForm` + `MudTextField`/`MudSelect`/etc. with the built-in validation surface (`Required`, `Validation` callbacks, `EditForm` only when integrating with `DataAnnotationsValidator`).
- Surface server-side validation failures (from `ApplicationService.Validate` etc.) via `MudSnackbar.Add(message, Severity.Error)` or inline alert (`MudAlert`), not `alert()` or browser dialogs.

### 6. Feedback and dialogs
- Confirmations: `IDialogService.ShowMessageBox` or a custom `MudDialog`. No `confirm()` JS.
- Toasts: `ISnackbar.Add(...)`. Keep messages short; pick `Severity.Success | Info | Warning | Error` deliberately.
- Loading state: `MudProgressLinear` / `MudProgressCircular` with `Indeterminate`, or `Loading` props on Mud components that expose them.

### 7. Navigation and layout
- Sidebar/top-bar lives in `Components/Layout/`. New top-level pages get a `NavMenu` entry (use `MudNavMenu` / `MudNavLink` when migrating).
- Page shells use `MudContainer` + `MudGrid` / `MudStack` for layout, not raw `<div class="row">`.

### 8. Routing and state
- `@page "/route"` directives stay; routes documented in `CLAUDE.md` are canonical.
- Page state is component-local unless it must outlive a navigation. Don't reach for a singleton service to hold UI state.
- For interactivity that must survive navigation, prefer scoped DI services already in `HomeYarp.Application` (`IApplicationService`, `ICertificateService`).

### 9. Domain types in Razor
- `HomeYarp.Domain.Application` collides with `HomeYarp.Application` namespace and ASP.NET's `WebApplication`. In `.razor` files use the fully qualified `HomeYarp.Domain.Application` (see `Components/Pages/Applications.razor` for the pattern). In tests use the `DomainApplication` global alias.

### 10. Tests follow features
- Per `CLAUDE.md`'s "tests-with-features" rule: UI logic that lives in a `.razor.cs` partial or a service called from a page ships with unit tests in `HomeYarp.Tests`. Pure markup doesn't need a test, but anything with conditional rendering, state transitions, or service calls does.

## Reference: getting started snippets

Page skeleton:
```razor
@page "/example"
@inject ISnackbar Snackbar

<PageTitle>Example</PageTitle>

<MudContainer MaxWidth="MaxWidth.Large" Class="mt-4">
    <MudStack Spacing="3">
        <MudText Typo="Typo.h4">Example</MudText>
        <MudPaper Class="pa-4">
            <!-- content -->
        </MudPaper>
    </MudStack>
</MudContainer>
```

Reusable component skeleton (`Components/Shared/Foo.razor`):
```razor
<MudPaper Class="pa-3">
    <MudText Typo="Typo.subtitle1">@Title</MudText>
    @ChildContent
</MudPaper>

@code {
    [Parameter, EditorRequired] public string Title { get; set; } = default!;
    [Parameter] public RenderFragment? ChildContent { get; set; }
}
```

## Where things live

- Pages: `HomeYarp.WebServer/Components/Pages/*.razor`
- Layout / nav: `HomeYarp.WebServer/Components/Layout/*.razor`
- Reusable components: `HomeYarp.WebServer/Components/Shared/*.razor`
- Global usings (incl. `MudBlazor`): `HomeYarp.WebServer/Components/_Imports.razor`
- Theme + providers: `HomeYarp.WebServer/Components/Layout/MainLayout.razor`
- DI registration: `HomeYarp.WebServer/Program.cs` (`AddMudServices`)
- Asset links (CSS, JS, Roboto font): `HomeYarp.WebServer/Components/App.razor`

## When in doubt

- Mud has it → use Mud.
- Mud doesn't have it but the BCL does → use the BCL.
- Neither → minimal isolated CSS or, last resort, JS interop with a one-line comment explaining why.

## Known gotchas (from real conversions)

- **`Sortable` on `MudDataGrid` is invalid** (analyzer MUD0002). Sorting is column-level via `Sortable="true|false"` on `PropertyColumn` / `TemplateColumn`. Default is on. Use grid-level `SortMode="SortMode.Single|Multiple|None"` to set the sort mode instead.
- **`AlignItems` on `MudGrid` is invalid** (analyzer MUD0002). It's a `MudStack` parameter only. To vertically center children of a `MudGrid` row, put `Class="d-flex align-center"` on the affected `MudItem`s.
- **`MudNumericField T="..."` must match the bound property's nullability.** Binding to `int?` requires `T="int?"`. Mismatched generic args produce CS1503 from the source generator with a confusing line number — check the `T="..."` first.
- **`MudSelect` for `Guid?` (or any nullable struct):** declare `T="Guid?"` on both `MudSelect` and every `MudSelectItem`, and cast values explicitly: `Value="@((Guid?)cert.Id)"`. Forgetting the cast picks the wrong overload.
- **`MudRadioGroup` with side-effects:** when changing the selected value also has side-effects (e.g., clearing a related field), use `Value` + `ValueChanged` callback instead of `@bind-Value`. Each `MudRadio` still needs its own `T="..."` and `Value="..."`.
- **`MudToggleGroup`** is the right component for two-or-more mutually-exclusive view modes (Simple/Advanced JSON in `ApplicationEdit`). Use `Value` + `ValueChanged` so the change handler can run validation before flipping.
- **Nullable XML form values via `@bind` on `<select>`** were happily coerced; in `MudSelect` the binding is stricter — the type must match exactly.
- **`MudDivider` inside `d-flex flex-column` grows vertically** and eats all remaining height. Cause: MudDivider's stylesheet sets `flex-grow: 1` (it's designed for horizontal toolbar contexts where that's harmless). In a column it stretches *along the main axis*, pushing siblings out of place. Workarounds: wrap the divider in a plain `<div>` so it's not a direct flex child; or use a raw `<hr style="margin:0; border:none; border-top: 1px solid var(--mud-palette-divider);" />`; or add inline `Style="flex-grow: 0;"`.

This skill is a living document — extend it as new conventions emerge.
