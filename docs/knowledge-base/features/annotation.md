# Annotation

Drawing on a capture: tools, undo, color picker, style presets.

Part of the [Pointframe knowledge base](../knowledge-base.md). Read the cross-cutting rules there first; this file holds what only this area needs.

## Features

| ID | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |
|---|---|---|---|---|---|---|
| F-11 | Annotation tools: arrow, line, rectangle, circle, pen, highlight, text, number, blur, callout, pixel ruler | Overlay toolbar `Tool.*` | `Pointframe/ViewModels/AnnotationViewModel.cs`, `Pointframe/Services/Annotation/AnnotationCanvasRenderer.cs` | `annotation_committed` | `Pointframe.Tests/ViewModels/AnnotationViewModelTests.cs`, `Pointframe.Tests/Services/AnnotationCanvasRendererTests.cs`, `Pointframe.AutomationTests/Smoke/AnnotationToolSmokeTests.cs`, `Pointframe.AutomationTests/Smoke/McpAnnotationWorkflowTests.cs` | [Annotation engine](#annotation-engine), [Add an annotation tool](#add-an-annotation-tool) |
| F-12 | Undo and redo | Keyboard shortcuts in the overlay | `Pointframe/ViewModels/AnnotationViewModel.cs` (`CommitGroup`) | — | `Pointframe.Tests/ViewModels/AnnotationViewModelTests.cs` | [Undo groups are added only on commit](#undo-groups-are-added-only-on-commit) |
| F-13 | Color picker | Overlay toolbar `Tool.ColorPicker` | `Pointframe/ViewModels/OverlayViewModel.cs` (`PickColor`), `Pointframe/Views/OverlayWindow.ColorPicker.cs` | — | `Pointframe.Tests/ViewModels/OverlayViewModelTests.cs` | [Annotation engine](#annotation-engine) |
| F-14 | Annotation style presets | Annotation toolbar and settings | `Pointframe/ViewModels/AnnotationStylePresetViewModel.cs`, `Pointframe/Models/AnnotationStylePreset.cs` | — | — | [Annotation engine](#annotation-engine), [User settings](settings.md#user-settings) |

## Annotation engine

**Responsibility.** Own everything about drawing on a captured image: the active tool, its color and stroke, the draft shape during a drag, committed elements, and undo and redo. The same engine serves the screenshot overlay and the recording overlay.

**Key types.**

| Type | Role |
|---|---|
| `AnnotationTool` enum | Arrow, Rectangle, Text, Highlight, Pen, Line, Circle, Number, Blur, Callout, ColorPicker, PixelRuler |
| `ShapeParameters` sealed records | Immutable description of one shape per tool, produced by `AnnotationViewModel.TryGetShapeParameters()` |
| `AnnotationViewModel` | Tool, color, thickness, style presets, number counter, undo and redo stacks of element groups |
| `RecordingAnnotationViewModel` | Recording-time variant with the reduced tool set |
| `AnnotationCanvasRenderer` | Maps each `AnnotationTool` to an `IAnnotationShapeHandler` and drives the active one through a drag |
| `IAnnotationShapeHandler` | `Begin(point, brush, thickness, canvas)`, `Update(point)`, `Commit(canvas, trackElement)`, `Cancel(canvas)`; one class per tool under `Pointframe/Services/Annotation/Handlers/` |
| `AnnotationCanvasInteractionController` | Translates mouse events into renderer calls |
| `IAnnotationGeometryService` | Pure math: arrowheads, bounding boxes, hit tests; unit-tested without WPF |

**Flow of one drag.**

1. Mouse down: the controller asks the renderer to begin. The renderer picks the handler for `SelectedTool` and calls `Begin` with the current style.
2. Mouse move: `Update(point)` mutates the draft element only.
3. Mouse up: `Commit` adds final elements to the canvas and calls `trackElement` for each; the ViewModel records them as one undo group.
4. Escape or tool switch mid-drag: `Cancel` removes the draft. Exactly one of `Commit` or `Cancel` runs per drag, or the next drag throws on a stale draft.

Text and Callout edit in a live `TextBox`; `LostFocus` converts it to a `TextBlock`. Removing that handler leaves editable boxes in the exported bitmap. Number resets its counter through the ViewModel on undo and redo.

**Invariants.**

- Undo groups are added only on commit. See [Undo groups are added only on commit](#undo-groups-are-added-only-on-commit).
- The recording HUD's tool list derives from the annotation allowlist; there is no second list to keep in sync.
- Shape definitions are records in `Pointframe/Models/ShapeParameters.cs`; handlers hold no state between drags.

**Tests.** `Pointframe.Tests/ViewModels/AnnotationViewModelTests.cs`, `Pointframe.Tests/ViewModels/RecordingAnnotationViewModelTests.cs`, `Pointframe.Tests/Services/AnnotationCanvasRendererTests.cs`, `Pointframe.Tests/Services/AnnotationCanvasInteractionControllerTests.cs`, `Pointframe.Tests/Services/AnnotationGeometryServiceTests.cs`, per-handler tests under `Pointframe.Tests/Services/Handlers/`. Smoke coverage: `Pointframe.AutomationTests/Smoke/AnnotationToolSmokeTests.cs` and `Pointframe.AutomationTests/Smoke/RecordingAnnotationToolSmokeTests.cs`, driven by ids in `Pointframe.AutomationTests/Support/AutomationIds.cs`.

**Files.** `Pointframe/Models/AnnotationTool.cs`, `Pointframe/Models/ShapeParameters.cs`, `Pointframe/Models/AnnotationStylePreset.cs`, `Pointframe/ViewModels/AnnotationViewModel.cs`, `Pointframe/ViewModels/RecordingAnnotationViewModel.cs`, `Pointframe/ViewModels/AnnotationStylePresetViewModel.cs`, `Pointframe/Services/Annotation/AnnotationCanvasRenderer.cs`, `Pointframe/Services/Annotation/AnnotationCanvasInteractionController.cs`, `Pointframe/Services/Annotation/IAnnotationGeometryService.cs`, `Pointframe/Services/Annotation/AnnotationGeometryService.cs`, `Pointframe/Services/Annotation/Handlers/IAnnotationShapeHandler.cs`.

**Lessons.**

- Lesson: Recording HUD tool selection should not duplicate the annotation-tool allowlist

## Undo groups are added only on commit

**Rule.** The undo stack in `AnnotationViewModel` grows in exactly one place: when a drag commits. A shape handler calls the `trackElement` callback only from `Commit`, never from `Begin` or `Update`. Redo re-adds the same group; nothing else adds to the stack.

**Why.** A drag is one user-visible action. If the draft element is tracked at `Begin`, Ctrl+Z restores half-drawn shapes and the redo stack fills with junk. Text and Callout replace their `TextBox` with a `TextBlock` through `ReplaceTrackedElement`, which relies on the group containing only committed elements.

**Enforced by.** `Pointframe.Tests/ViewModels/AnnotationViewModelTests.cs` and `Pointframe.Tests/Services/AnnotationCanvasRendererTests.cs`. There is no analyzer; review any new call to `TrackElement` or the undo stack by hand.

**Symptoms when violated.** Undo restores a partial shape or removes two shapes at once. `UndoCount` disagrees with what the user drew. Number badges renumber incorrectly after undo because the counter reset runs per group.

**Files.** `Pointframe/ViewModels/AnnotationViewModel.cs`, `Pointframe/Services/Annotation/AnnotationCanvasRenderer.cs`, `Pointframe/Services/Annotation/Handlers/IAnnotationShapeHandler.cs`. See [Annotation engine](#annotation-engine) and [Add an annotation tool](#add-an-annotation-tool).

## Add an annotation tool

**When.** A new drawing primitive the user picks from the toolbar. Not for style presets (those are `AnnotationStylePreset`) and not for actions that do not draw (those are `OverlayViewModel` commands).

**Steps.**

1. Add the value to the `AnnotationTool` enum in `Pointframe/Models/AnnotationTool.cs`.
2. Add a sealed record to `Pointframe/Models/ShapeParameters.cs` with the geometry and style the tool needs.
3. Return it from `AnnotationViewModel.TryGetShapeParameters()` for the new tool.
4. Create `Pointframe/Services/Annotation/Handlers/<Name>ShapeHandler.cs` implementing `IAnnotationShapeHandler`. Draft in `Begin` and `Update`; add final elements and call `trackElement` only in `Commit`; remove the draft in `Cancel`. Copy `RectShapeHandler` for a simple drag shape or `TextShapeHandler` for an editable one.
5. Register it in the `_handlers` dictionary in `AnnotationCanvasRenderer`, passing `GetShapeParameters` and any ViewModel callbacks it needs.
6. Put pure math in `IAnnotationGeometryService` and `AnnotationGeometryService`, not in the handler, so it is unit-testable without WPF.
7. Add the toolbar button in `Pointframe/Views/OverlayWindow.xaml` with an `AutomationProperties.AutomationId`. Decide whether the tool is allowed during recording; the HUD derives its list from the annotation allowlist.
8. Tests: a handler test under `Pointframe.Tests/Services/Handlers/`, a `TryGetShapeParameters` case in `AnnotationViewModelTests`, geometry cases in `AnnotationGeometryServiceTests`.
9. Smoke coverage: add the id to `Pointframe.AutomationTests/Support/AutomationIds.cs` and the tool to `AnnotationToolSmokeTests.cs` (and `RecordingAnnotationToolSmokeTests.cs` if allowed while recording).
10. Telemetry records the tool name through the existing `annotation_tool` property; check `TelemetryEventCatalog` only if the event constrains allowed values.

**Verify.**

```powershell
dotnet format Pointframe/Pointframe.csproj
dotnet test Pointframe.Tests/Pointframe.Tests.csproj --filter "FullyQualifiedName~Annotation"
```

Then draw with the tool, undo once, redo once, and export. The shape must survive export, and undo must remove exactly that one shape.

**Files.** `Pointframe/Models/AnnotationTool.cs`, `Pointframe/Models/ShapeParameters.cs`, `Pointframe/ViewModels/AnnotationViewModel.cs`, `Pointframe/Services/Annotation/Handlers/IAnnotationShapeHandler.cs`, `Pointframe/Services/Annotation/AnnotationCanvasRenderer.cs`, `Pointframe/Services/Annotation/IAnnotationGeometryService.cs`, `Pointframe/Services/Annotation/AnnotationGeometryService.cs`, `Pointframe/Views/OverlayWindow.xaml`, `Pointframe.AutomationTests/Support/AutomationIds.cs`, `Pointframe.AutomationTests/Smoke/AnnotationToolSmokeTests.cs`. See [Annotation engine](#annotation-engine) and [Undo groups are added only on commit](#undo-groups-are-added-only-on-commit).
