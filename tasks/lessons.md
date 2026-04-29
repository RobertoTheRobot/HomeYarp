# Lessons

## BlazorMonaco + Blazor Server lifecycle

`<StandaloneCodeEditor>` from BlazorMonaco 3.x has lifecycle bugs when toggled in/out of the render tree via `@if` on Blazor Server (InteractiveServer). Symptoms: `ObjectDisposedException` / `ArgumentException` ("DotNetObjectReference instance was already disposed") in `RenderTreeDiffBuilder.InsertNewFrame`. See https://github.com/serdarciplak/BlazorMonaco/issues/181.

**Pattern to use:** keep the editor mounted at all times and toggle visibility with `display: none` (a `.hidden` class on a wrapping `view-pane`). Do NOT swap views with `@if (_view == ViewMode.Advanced) { ... } else { ... }` when one branch contains the editor.

**Do NOT pass `Style="..."` to `<StandaloneCodeEditor>`.** That component renders as `<div id="@Id" class="monaco-editor-container @CssClass"></div>` — it has no `Style` parameter and no `[Parameter(CaptureUnmatchedValues = true)]` collector. Razor compiles the assignment without complaint, but at runtime the parameter-set fails, the component's render throws, and the failure surfaces as `ObjectDisposedException` in `RenderTreeDiffBuilder.InsertNewFrame` (because the renderer/circuit is torn down by the unhandled render error).

**Sizing pattern that works:** make the wrapper relatively positioned with an explicit height, and pin the inner monaco container with absolute positioning + `inset: 0`. Don't rely on `height: 100%` cascading into the inner div — it's unreliable across the wrapper-with-border layout, and `automaticLayout` then locks at the near-zero initial measurement.

```css
.json-editor-wrap { position: relative; height: 600px; /* + borders */ }
.json-editor-wrap > .monaco-editor-container { position: absolute; inset: 0; }
```

## MudBlazor 9: never use bare tag selectors in `app.css`

When migrating to MudBlazor, strip every bare-tag rule from `app.css` (`fieldset`, `legend`, `label`, `input`, `code`, `h1`, `h2`, ...). MudBlazor renders those tags inside its own components and a global rule will leak in.

The trap that bit us: `MudTextField` with `Variant.Outlined` renders `<fieldset class="mud-input-outlined-border">` positioned absolutely **on top of** the `<input>` (it's the notched-border ring). MudBlazor leaves the fieldset's `background` unset (transparent). A leftover global `fieldset { background: var(--panel); }` from the pre-MudBlazor UI made the fieldset opaque, which **covered the typed text** — the input still bound (you'd see the value in the JSON Advanced view) but visually appeared empty.

**Rule:** in any file that ships alongside MudBlazor, only use class selectors scoped to your own components. If you need to style something MudBlazor renders, target the `mud-*` class explicitly and accept that you're depending on MudBlazor's internals.

## App.razor script order: BlazorMonaco BEFORE blazor.web.js

`BlazorMonaco/jsInterop.js` declares `var require = { paths: { vs: ... } }` at module top — that's how Monaco's AMD loader picks up the path. So the BlazorMonaco stack must run before `blazor.web.js`:

```html
<script src="_content/BlazorMonaco/jsInterop.js"></script>
<script src="_content/BlazorMonaco/lib/monaco-editor/min/vs/loader.js"></script>
<script src="_content/BlazorMonaco/lib/monaco-editor/min/vs/editor/editor.main.js"></script>
<script src="_content/MudBlazor/MudBlazor.min.js"></script>
<script src="_framework/blazor.web.js"></script>
```

Don't add an extra `<script>var require = { paths: ... }</script>` block — `jsInterop.js` already does that and a duplicate just before `loader.js` is harmless but confusing.

If you put `blazor.web.js` first, Blazor starts the circuit and prerenders `JsonEditor`. `StandaloneCodeEditor` then calls `monaco.editor.create(...)` while `window.monaco` is still undefined (Monaco hasn't loaded yet). BlazorMonaco logs `BlazorMonaco: monaco is undefined` and **throws — which terminates the circuit**. Symptoms in the user's browser: inputs render but never receive proper focus, no caret blink, typing produces no visible text. The "An unhandled error has occurred. Reload" banner is hidden by the dark MudBlazor theme so it's easy to miss.

Verify with a CDP run that captures `Runtime.exceptionThrown` and `Runtime.consoleAPICalled` — if the circuit dies you'll see "this circuit will be terminated" right after the WebSocket connects.
