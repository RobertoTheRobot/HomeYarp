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
