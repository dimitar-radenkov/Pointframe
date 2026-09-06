# Lessons Learned

## Cursor-targeted tray captures must honor capture delay

### Problem

The new clean-window snip action could capture the tray/menu itself when launched from the tray.

### Root cause

The flow captured the window under cursor immediately, but tray launches begin with the cursor on the tray menu item.

### What fixed it

- run clean-window capture through the same capture-delay path as other capture flows
- after countdown, capture the window currently under cursor

### Takeaway

Any capture mode that targets "window under cursor" should not capture immediately from tray callbacks; it should respect capture delay so the user can move the cursor to the intended window.

## WinGet ARP publisher matching must follow the installer metadata exactly

### Problem

WinGet manifest review flagged `AppsAndFeaturesEntries.Publisher` mismatch risk, which can block merge and later affect upgrade detection.

### Root cause

The installer ARP publisher value and winget manifest publisher matching fields drifted apart. Winget ARP matching expects the manifest value to match what the installer actually writes.

### What fixed it

- set `installer/Pointframe.iss` `AppPublisher` to the canonical publisher string
- keep `AppsAndFeaturesEntries.Publisher` in each winget installer manifest exactly equal to the installer ARP publisher for that release

### Takeaway

Treat installer ARP metadata as the source of truth. For every release, verify ARP `Publisher` first, then mirror it in winget `AppsAndFeaturesEntries.Publisher` to keep install/upgrade detection reliable.

## WPF PerMonitorV2: set window bounds before Show(), not in OnSourceInitialized

### Problem

`OverlayWindow` showed with incorrect width on a secondary monitor at 100% DPI when the primary monitor is at a higher DPI. The `SelectionBorder` and dim strips appeared cut off.

### Root cause

`Window.Width/Height/Left/Top` were set inside `OnSourceInitialized` (which fires after the HWND is created). At that point the HWND already exists on the **primary monitor** (its initial default position). In PerMonitorV2 mode, setting `Width = 2560` while the window's HWND is on a 148.5% DPI monitor turns into 3804 physical pixels, which the OS caps to that monitor's physical width (2560 px) → `Width` reads back as `2560 / 1.485 ≈ 1724` WPF units instead of 2560. All subsequent layout (RehostAnnotatingOverlay, LayoutDimStrips) uses this wrong size, causing a 224px overflow and cut-off border.

### What fixed it

Set `Left`, `Top`, `Width`, `Height` inside `InitializeFromSelectionSession` (before `Show()` is called). When bounds are set before the HWND exists, WPF uses `Left`/`Top` to identify the target monitor and creates the HWND at the correct physical size on that monitor. This is the same pattern `SelectionMonitorWindow` uses (sets bounds in its constructor, before `Show()`).

### Takeaway

In a WPF PerMonitorV2 app, always set `Window.Left/Top/Width/Height` **before** `Show()`. If you must set them later (e.g., in `OnSourceInitialized`), the window is already on a monitor and WPF will scale/cap the values against that monitor's DPI. The safe pattern: store the bounds data before `Show()` and assign the four properties at that time, not inside HWND-lifecycle callbacks.

## Tray-launched file dialog can lose focus in a tray-only WPF app

### Problem

The `Open image...` action opened a native file picker that appeared for a moment and then immediately hid.

### Root cause

This happened because the dialog was launched directly from the tray icon context-menu click path in a tray-only application.

- The tray menu still owned the interaction when the dialog was opened.
- The file picker did not have a reliable WPF owner window.
- Windows focus returned to the tray/menu as it closed, so the ownerless dialog lost activation and disappeared behind it.

This is different from the update window flow, which uses a real WPF window and a normal modal/window lifecycle instead of an ownerless native picker.

### What fixed it

Two changes were needed:

1. Defer the open-image workflow until the tray menu has fully unwound.
2. Show the dialog with a real owner window, even when the app has no visible main window.

In practice, the working approach was:

- queue the open-image action with the WPF dispatcher instead of opening the picker inline from the tray click handler
- create a temporary invisible WPF owner window when no active visible window exists
- pass that owner to the file dialog so it stays foregrounded and modal for the duration of the picker

### Takeaway

In tray-first desktop apps, native dialogs should not be opened directly from the tray menu callback without a stable owner window. If there is no visible main window, create an explicit temporary owner or prefer a WPF-native dialog flow that participates correctly in WPF window activation.

## Opened-image overlay layout must target a single monitor, not the full virtual desktop

### Problem

Opened images could span across multiple monitors, making annotation and toolbar placement awkward or unusable.

### Root cause

The opened-image display rect was centered inside the overlay window's full virtual desktop bounds. On multi-monitor setups, centering against the whole virtual screen can place one image across monitor seams.

### What fixed it

- choose a single target monitor for the opened-image session
- calculate the display rect inside that monitor's working area
- center and scale the image within that one monitor instead of the full virtual desktop

### Takeaway

When an overlay spans the virtual desktop, any non-capture content that users must interact with directly should usually be laid out against one monitor's working area, not against the combined multi-monitor bounds.

## Pin capture must not restore the live overlay before the overlay window closes

### Problem

Pinning an annotated capture could leave the original annotation layer visible behind the new pinned window, sometimes with stray border fragments from the old overlay.

### Root cause

The screen-overlay capture path temporarily hid the overlay window to capture a clean background, but then always restored the overlay visibility before the Pin flow closed the overlay window.

That created a UI-lifecycle race where the pinned window showed a composed bitmap while the old transparent overlay briefly became visible again underneath it.

### What fixed it

- let overlay bitmap composition optionally keep the overlay hidden after capture
- use that mode for the Pin action so the overlay stays hidden until the window is closed

### Takeaway

If a flow captures UI from a temporary overlay and then immediately transitions to a new window, the capture step must coordinate with the teardown step. Do not blindly restore overlay visibility after capture when the caller is about to replace or close that overlay.

## Selection-adjacent toolbars need a compact fallback for small snips

### Problem

Small capture selections could cause the annotation tool rail and action bar to collide or visually overstep each other.

### Root cause

The overlay positioned each floating bar independently at full size, without a shared layout decision or a compact fallback when the available space around the selection was too tight.

### What fixed it

- calculate tool rail and action bar placement together instead of independently
- switch the action bar to a compact variant when the full-width bar cannot fit cleanly near the selection
- fall back to edge docking only after nearby placements are exhausted

### Takeaway

Floating controls around a selection should be treated as one adaptive layout system. If multiple bars compete for the same small area, use a compact mode instead of only trying different coordinates for the full-size UI.

## Coverage workflows should run for fix branches and not hard-require a Codecov token on public repos

### Problem

Coverage uploads could silently disappear for normal bugfix work because CI only ran on `master` and `feature/**`, and Codecov uploads depended entirely on a configured secret token.

### Root cause

The workflow branch filters excluded `fix/**` pushes, and the upload step had only the token-based path even though public repositories can upload coverage without a token.

### What fixed it

- include `fix/**` in CI push triggers
- keep the token-based upload when a token exists
- add a tokenless fallback upload path for public repositories

### Takeaway

If coverage is part of the default engineering workflow, CI branch filters must include the team's normal branch prefixes. For public repositories, coverage uploads should not rely solely on a secret token when the provider supports tokenless uploads.

## Modal window services must guard against late UI events after close

### Problem

The update download window service could crash or hang tests when the window closed before the queued `ContentRendered` callback started the download.

### Root cause

The service subscribed to `ContentRendered` with an `async void` starter that captured the cancellation token source directly. If the window closed first, `ShowDialog()` returned, the source was disposed, and the queued callback could still run afterward.

### What fixed it

- capture the `CancellationToken` value before any asynchronous window lifecycle begins
- unsubscribe the `ContentRendered` handler when the window closes
- short-circuit the queued start callback if the window has already closed

### Takeaway

For modal WPF services, do not assume a late-render event cannot fire after a close path wins the race. Cache disposable-derived values like `CancellationToken` up front and explicitly guard queued UI callbacks against closed-window state.

## Overlay capture must yield the dispatcher after hiding the overlay window

### Problem

Pinned or copied captures could still contain a second ghosted copy of the live annotations even though the overlay window was set to hidden before screen capture.

### Root cause

The capture path hid the overlay and then immediately blocked the UI thread with `Sleep` before calling `CopyFromScreen`. In WPF, that does not guarantee the hide has actually propagated through the dispatcher and render pipeline, so the live overlay can still be present in the captured screen image.

### What fixed it

- hide the overlay window
- explicitly flush the dispatcher through render priority

## Recording border windows must be positioned in physical screen pixels on mixed-DPI multi-monitor setups

### Problem

The live recording border could appear offset from the actual recording region when the user started a recording on a non-primary monitor.

### Root cause

The recording capture rectangle was correct, but the separate top-level `RecordingBorderWindow` was positioned with WPF `Window.Left` and `Window.Top` values. Under PerMonitorV2 on mixed-DPI multi-monitor setups, WPF top-level window placement can resolve those coordinates against the wrong monitor and shift the window.

### What fixed it

- stop using a separate top-level border window for the recording outline
- keep the recording border inside the existing full-screen `OverlayWindow`
- switch the overlay into a transparent, click-through recording mode after recording starts
- draw the dashed border in overlay-local coordinates around the selected region

### Takeaway

For precision overlays on mixed-DPI multi-monitor systems, avoid separate top-level helper windows when the visual can live in the owning overlay. Keeping the recording border inside the already-correct full-screen overlay avoids an entire class of cross-monitor WPF placement bugs.
- only then perform the screen capture

## Recording annotation windows must be positioned in physical screen pixels on mixed-DPI multi-monitor setups

### Problem

During recording, switching to drawing mode could make annotation input appear broken on a non-primary monitor even though the recorder region itself was still correct.

### Root cause

`RecordingAnnotationWindow` remained a separate top-level WPF window positioned with logical `Left` and `Top` values derived from the overlay. That is the same PerMonitorV2 mixed-DPI placement trap as the old recording border window: the recording rectangle can be right while the helper window itself lands at the wrong screen coordinates.

### What fixed it

- compute the recording annotation window screen rect from the overlay with `PointToScreen`
- keep the logical WPF rect for canvas sizing and annotation math
- place the annotation window HWND with `MoveWindow` in physical pixels
- use the two-step move so the first move updates DPI context and the second lands on the final exact bounds

### Takeaway

If a recording helper remains a separate top-level WPF window on mixed-DPI multi-monitor setups, treat its placement like native screen geometry, not logical overlay coordinates. A correct capture rectangle does not imply a correct helper-window position.

## Recording overlays need native click relays for interactive mode, not only `HTTRANSPARENT`

### Problem

After starting a recording, the user could not click into the recorded app area even though the HUD showed the session in `Interactive` mode.

### Root cause

The recording overlay relied on `WM_NCHITTEST` returning `HTTRANSPARENT` to pass mouse input through its topmost window. That is not sufficient for a recording overlay that sits above other applications, so the first click in the capture region was still consumed by the overlay instead of reaching the underlying app.

### What fixed it

- keep drawing mode on the overlay itself
- in interactive mode, detect left-clicks inside the capture canvas
- temporarily switch the recording overlay HWND into native transparent mouse mode
- relay a synthetic click to the current screen point so the underlying app receives the interaction and focus

### Takeaway

For topmost recording overlays, WPF hit testing and `HTTRANSPARENT` are not enough to guarantee cross-application click-through. If the overlay must stay visible for borders, cursors, or HUD content, interactive mode needs a native passthrough or click-relay path.

## Full-screen recording HUDs need a compact default, not the region-recording layout

### Problem

When recording the whole screen, the full recording HUD could appear inside the captured video and steal too much usable screen space.

### Root cause

The recording HUD used the same placement strategy for both region and full-screen capture: position the controls just outside the capture area when possible, then clamp them into the monitor work area. That works for region capture, but for full-screen capture there is no outside area, so clamping places the full HUD on top of recorded content.

### What fixed it

- add separate expanded and compact recording HUD presentations
- start full-screen recordings in the compact HUD state by default
- anchor full-screen HUD placement to the top center of the monitor work area instead of the region-below-selection layout
- keep stop and expand available in the compact pill so the user can recover controls without relying on extra shortcuts

### Takeaway

Recording HUD placement must adapt to the capture mode, not just the available screen bounds. A layout that is appropriate for region capture can be wrong for whole-screen capture even when the math is technically valid. For full-screen recording, prefer a compact default with an explicit recovery path over hiding the controls entirely.

## ffmpeg microphone capture must use Windows capture-device names compatible with the recording backend

### Problem

Microphone recording can appear wired up correctly in settings and service code but still fail at runtime if the app passes the wrong kind of device name to ffmpeg.

### Root cause

The screen-recording path uses ffmpeg's Windows `dshow` input for microphone capture. Those inputs expect Windows capture-device names that match the recording backend, not arbitrary audio-endpoint labels gathered from a different API surface.

### What fixed it

- enumerate microphone devices from the Windows capture-device surface used for recording
- persist the selected capture-device name in settings
- resolve microphone recording against the available capture-device names before starting ffmpeg
- warn the user and continue with video-only recording if microphone recording is enabled but no compatible device is available

### Takeaway

When ffmpeg owns microphone capture on Windows, device selection must use names that are compatible with ffmpeg's capture backend. Do not assume endpoint-friendly names from another audio API can be passed through unchanged.

## ffmpeg screen-plus-microphone recordings must stop when the video input ends

### Problem

After a microphone-enabled recording stopped, the app could appear stuck or unable to start the next recording promptly because the ffmpeg process never finished cleanly.

### Root cause

The recording pipeline fed ffmpeg from two live inputs: raw video frames over stdin and a live microphone device. Closing the video pipe ended only the video input; the microphone input remained live, so ffmpeg kept running until the app's timeout killed it.

### What fixed it

- explicitly map the video and microphone streams in the ffmpeg command
- add `-shortest` so the output finishes when the video input ends

### Takeaway

When a recording session combines a finite video pipe with a live microphone input, ffmpeg will not necessarily terminate just because the video writer closes. Make the output stop on the shortest stream or the process may hang on stop and poison the next recording flow.

## Recording HUD microphone toggles must restore the device's original mute state

### Problem

Adding a live microphone mute toggle to the recording HUD can accidentally turn into a persistent system-wide microphone mute if the recording session directly changes the capture endpoint state and never restores it.

### Root cause

The simplest runtime microphone toggle for ffmpeg-owned capture is muting the Windows capture endpoint itself, but that endpoint state lives beyond the recording session unless the app restores it explicitly.

### What fixed it

- capture the microphone's initial mute state when recording starts
- apply HUD mute/unmute changes only for the active session
- restore the original mute state after ffmpeg has finished closing the recording

### Takeaway

If a recording feature temporarily controls a shared OS audio endpoint, treat that state as session-scoped and restore the original value on shutdown. Otherwise a recording-only toggle becomes a persistent system setting.

## Dropped recording frames shorten the final MP4 duration

### Problem

The recording HUD timer can show a longer wall-clock session than the saved MP4 duration, for example a roughly 4-5 second recording producing a file closer to 2 seconds.

## Rename migrations must update hardcoded delivery paths in workflows and installer assets together

### Problem

CD can fail immediately after a product or folder rename even when the project still builds locally.

### Root cause

The delivery pipeline depended on hardcoded top-level paths in more than one place.

- the GitHub Actions signing step still targeted the old publish output folder
- the Inno Setup script still referenced the old icon asset folder

That means a rename can leave publish, signing, and installer compilation pointing at different directory trees.

### What fixed it

- update the workflow signing step to use the renamed publish output path
- update the installer script to use the renamed asset path
- validate the full publish-to-installer path after the rename instead of checking only project build success

### Takeaway

When a desktop app is renamed, treat workflow scripts, installer assets, and publish-output paths as one delivery surface. A successful build does not prove CD is safe if any hardcoded path still points at the legacy tree.

### Root cause

`ScreenRecordingService` uses a bounded frame queue with `DropWrite` behavior and a four-buffer pool. When `_writer.WriteFrame()` falls behind, the capture loop logs `Frame skipped — buffer pool exhausted` and discards frames. The current ffmpeg rawvideo pipeline timestamps video effectively by delivered frame count, so dropped frames remove time from the output instead of preserving wall-clock duration.

### What fixed or clarified it

- inspect the recording log for `Frame skipped — buffer pool exhausted`
- compare dropped frame count against the missing output duration at the configured fps
- add per-session recording diagnostics: first-write delay plus attempted, written, and dropped frame counts with derived output and dropped durations

### Takeaway

In the current screen-recording architecture, backpressure is not just a visual quality problem. Every dropped frame also shortens the saved recording. If wall-clock duration must be preserved, the pipeline needs explicit timestamps or a non-dropping strategy, not just a higher buffer count.

## Recording-time controls and annotation surfaces are most reliable when hosted inside the main overlay window

### Problem

Even after fixing physical-pixel placement for separate recording helper windows, the live recording controls and annotation surface could still drift or behave inconsistently because each helper window introduced its own placement and hit-testing lifecycle.

### Root cause

The recording flow kept splitting one logical overlay experience across multiple top-level WPF windows. On mixed-DPI multi-monitor setups, each extra helper window adds another screen-placement, focus, and hit-test boundary that can diverge from the owning overlay.

### What fixed it

- keep the dashed border, recording controls, and live recording annotation surface inside `OverlayWindow`
- use the overlay's own local coordinate space for layout
- use one overlay HWND hook to pass clicks through everywhere except the embedded recording UI that should stay interactive

### Takeaway

For recording-mode UI on mixed-DPI multi-monitor setups, prefer one owning overlay window with embedded surfaces over multiple top-level helper windows. Removing window boundaries is often more robust than trying to perfect each helper window's placement separately.

### Takeaway

When a WPF flow captures the desktop immediately after hiding a temporary overlay, changing `Visibility` is not enough on its own. Yield the dispatcher through a render pass before taking the screenshot, or the supposedly hidden overlay may still be captured.

## Replacement windows should not be shown until the full-screen overlay has fully closed

### Problem

Even after pin capture kept the overlay hidden during bitmap composition, the old annotation overlay could still remain visibly present on screen during the transition to the pinned window, especially on a single-monitor setup where the replacement window did not cover the old annotation area.

### Root cause

The Pin flow showed `PinnedScreenshotWindow` before the overlay window had fully closed. That left a teardown race where the replacement window appeared while the old full-screen overlay was still alive.

### What fixed it

- store the pinned bitmap as pending state
- keep the overlay hidden when pinning starts
- close the overlay first
- only create and show the pinned window from `OnClosed`

### Takeaway

If one top-level WPF window replaces another, do not show the replacement while the old full-screen overlay is still tearing down. Close the source window first, then show the replacement from the close-completion path.

## Automation-mode window replacement should not rely on OnLastWindowClose

### Problem

Desktop automation runs that closed one window and then opened a replacement window could terminate before the replacement appeared, especially in the opened-image `Pin` flow where `OverlayWindow` hands off to `PinnedScreenshotWindow`.

### Root cause

Automation mode used `ShutdownMode.OnLastWindowClose`. That works for single-window automation targets like Settings or About, but it races against flows that close one WPF window and then show another from a deferred close-completion path.

### What fixed it

- keep automation mode on explicit shutdown
- register automation windows centrally in `App.xaml.cs`
- when an automation window closes, queue a late dispatcher check and shut down only if no visible windows remain

### Takeaway

If an automation scenario can replace one window with another asynchronously, do not let `OnLastWindowClose` drive process lifetime. Centralize automation shutdown in the app so replacement windows like pinned or recording overlays have time to appear before exit is decided.

## Desktop automation test assemblies should disable xUnit parallelization

### Problem

Desktop smoke tests that launched separate app instances on the same interactive Windows desktop could fail intermittently while switching windows or locating automation elements, even though each test passed in isolation.

### Root cause

`SnippingTool.AutomationTests` was running under xUnit's default parallel test execution. FlaUI and the app instances were then competing on one shared desktop for focus, top-level window discovery, and replacement-window timing.

### What fixed it

- add `[assembly: CollectionBehavior(DisableTestParallelization = true)]` to the automation test assembly
- keep the desktop smoke project serial so only one app instance drives the interactive desktop at a time

### Takeaway

Desktop UI automation that shares one real Windows desktop should run serially per test assembly. Parallelizing those tests turns focus and window-discovery contention into false failures that look like product regressions.

## Visible topmost WPF overlays can be captured by screen recording and still toggle click-through input at runtime

### Problem

It was unclear whether the existing `Graphics.CopyFromScreen` recording pipeline could support live recording-time annotation without redesigning the recorder or compositing video frames manually.

### Root cause

The architecture question depended on two separate runtime behaviors that were not yet validated in this app:

- whether a transparent topmost WPF overlay over the capture region would be present in recorded output
- whether that same overlay could switch between interactive and click-through modes at runtime without recreating the window

### What fixed it

- create a region-sized transparent topmost WPF spike overlay during recording
- toggle `WS_EX_TRANSPARENT` on the same layered window to switch between annotate and interact modes
- verify manually that the overlay appears in saved recordings and that pass-through input still works when disarmed

### Takeaway

## Full-desktop selection overlays are safer in a system-aware DPI context while monitor-scoped recording hosts stay PerMonitorV2

### Problem

After enabling explicit PerMonitorV2 process awareness, the initial snip overlay could show badly offset dim regions as soon as selection started on a mixed-DPI multi-monitor desktop.

### Root cause

The full virtual-desktop selection overlay was being created under the same PerMonitorV2 context as the monitor-scoped recording host. That is the wrong tradeoff for a window whose job is to span the whole desktop: one global selection surface became subject to the same cross-monitor WPF DPI-context ambiguity that the recording-host split was meant to avoid.

### What fixed it

- create the selection `OverlayWindow` under a temporary system-aware thread DPI context
- keep the dedicated `RecordingOverlayWindow` created under PerMonitorV2
- treat the handoff between those two windows as an explicit mixed-mode DPI boundary

### Takeaway

For this app, the virtual-desktop selection overlay and the monitor-scoped recording host should not share the same DPI context. Use mixed-mode DPI intentionally: system-aware for the desktop-spanning selection surface, PerMonitorV2 for the recording host that must align precisely on one monitor.

For this app's recording architecture, a visible topmost WPF overlay can be burned into recorded output through `CopyFromScreen`, and the same overlay can be reused for interaction by toggling click-through mode at runtime. That makes a recording-time annotation window viable without changing the recorder or video-writer contracts.

## Recording-time desktop capture and HUD placement must use the target monitor's coordinate system

### Problem

Recording blur or redact regions and the recording HUD can drift or land on the wrong screen when DPI scaling or multi-monitor layouts are involved.

### Root cause

- recording-time blur capture sampled desktop pixels from logical WPF coordinates instead of physical screen pixels
- HUD placement used the primary monitor work area instead of the working area for the monitor containing the recording region

### What fixed it

- convert recording blur capture bounds from overlay DIPs into physical pixels using the session DPI values
- choose HUD placement bounds from the screen that contains the recording region instead of `SystemParameters.WorkArea`

### Takeaway

For recording overlays, treat blur capture and companion HUD placement as monitor-scoped operations. Use session DPI for any desktop pixel capture, and anchor floating recording UI to the working area of the monitor that owns the capture region rather than assuming the primary display.

## Active-window capture must map Win32 screen coordinates into overlay space instead of dividing by one overlay DPI

### Problem

Active-window snips could select the wrong area when the foreground window was on a secondary monitor, especially with mixed DPI scaling.

### Root cause

The active-window path took the `GetWindowRect` result in Win32 screen coordinates and converted it into overlay coordinates by dividing with one overlay DPI value and subtracting the virtual-screen origin.

That assumes one uniform DPI transform across the whole virtual desktop, which is not reliable once the active window lives on another monitor with a different scale.

### What fixed it

- convert both Win32 rectangle corners from screen coordinates into overlay coordinates through the overlay window itself
- use `PointFromScreen` for that mapping instead of manual `physical / dpi` math
- normalize and clamp the resulting overlay rect before committing the selection

### Takeaway

When a WPF overlay spans the virtual desktop but a source rectangle comes from Win32 screen APIs like `GetWindowRect`, do not convert with one shared DPI scalar. Map the screen points into the overlay's coordinate space first, then build the selection rect from those mapped points.

## Recording mode must use one authoritative geometry model

### Problem

Recording border placement, HUD hit testing, cursor highlighting, click-through behavior, and blur capture could disagree on secondary or mixed-DPI monitors even when the recorder itself captured the correct region.

### Root cause

Recording mode mixed several coordinate models at once:

- overlay-local selection rectangles in DIPs
- physical capture bounds in screen pixels
- ad hoc `PointToScreen` / `PointFromScreen` conversions
- per-feature local scale calculations for HUD and cursor logic

That let different recording features answer the same geometry question in different ways.

### What fixed it

- introduce one immutable `RecordingSessionGeometry` model for each recording session
- store the physical capture bounds and the matching overlay-local recording rect together
- route recording HUD placement, hit testing, annotation placement, cursor mapping, and blur capture through that shared model only

### Takeaway

For recording mode on mixed-DPI multi-monitor setups, geometry must be a session object, not a scattered set of helper calculations. If border, HUD, cursor, and capture logic are allowed to recompute their own transforms independently, they will drift apart off the primary monitor.

## Monitor-scoped recording hosts must settle before capture geometry is computed

### Problem

After switching the overlay into a monitor-scoped recording host, the dashed recording border could jump slightly away from the selected region as soon as recording started.

### Root cause

The overlay host was being moved to the target monitor with `SetWindowPos`, and recording geometry was then computed immediately from WPF layout values like `Root.ActualWidth` and `Root.ActualHeight`.

On mixed-DPI systems, a top-level WPF window may still be processing its monitor/DPI/layout transition after the native move, so those values can briefly describe an intermediate state instead of the final settled host.

### What fixed it

- move the overlay host to the target monitor first
- wait through render/layout cycles until the host window bounds and root size settle
- only then build the recording session geometry and position the border, HUD, and annotation surface

### Takeaway

For monitor-scoped recording overlays, native window placement and WPF layout are not synchronized at one instant. After moving the host window across monitors, do not compute recording geometry until the host has finished its DPI/layout settle path.

## Mixed-DPI multi-monitor capture features need PerMonitorV2 process DPI awareness

### Problem

Active-window capture could still land on the wrong monitor region even after the capture math and monitor-relative placement logic looked internally consistent in logs.

### Root cause

The process had no application manifest and was therefore not explicitly PerMonitorV2 DPI-aware. On mixed-DPI multi-monitor setups, Windows can virtualize coordinates for a system-DPI-aware WPF process, which makes secondary-monitor screen bounds and overlay placement unreliable even when the app's own calculations are otherwise correct.

### What fixed it

- add an application manifest to the WPF project
- declare `dpiAwareness` as `PerMonitorV2`
- rebuild and relaunch the app so monitor coordinates and WPF window sizing use per-monitor DPI behavior

### Takeaway

If a WPF feature depends on accurate screen or window coordinates across multiple monitors, process-level DPI awareness is part of the feature, not a deployment detail. Without PerMonitorV2, secondary-monitor coordinate math can stay wrong even when local placement code appears correct.

## Visible recording adornments are burned into CopyFromScreen output

### Problem

The dashed recording border appeared in the final video when recording started.

### Root cause

The recording pipeline uses `Graphics.CopyFromScreen`, so any visible topmost window over the capture region is captured as part of the output. The border window was being shown inside the region that was actively being recorded.

### What fixed it

- show the decorative recording border outside the actual recorded rectangle
- keep the recorder bounds unchanged so only the visual cue moves, not the captured area

### Takeaway

With screen-copy recording, decorative UI must either live outside the captured region or stay hidden while frames are being captured. If a visual overlay is only for user guidance, offset it outside the actual capture bounds instead of layering it on top of the recorded pixels.

## Recording HUD tool selection should not duplicate the annotation-tool allowlist

### Problem

The recording HUD command rejected valid `AnnotationTool` values because it kept its own hard-coded subset of supported names.

### Root cause

The HUD duplicated a partial tool list instead of treating `AnnotationTool` as the single source of truth.

### What fixed it

- let the command parse any valid `AnnotationTool` enum value
- keep the HUD button set responsible only for what is shown, not for what the viewmodel can accept

### Takeaway

When a viewmodel command already works from an enum, avoid a second name allowlist unless it is enforcing a real security or validation boundary. Duplicated tool lists drift as soon as the app grows another annotation mode.

## Window-local WPF resources must stay self-contained when tests instantiate windows directly

### Problem

`SettingsWindow` tests started failing with `XamlParseException` after the redesign introduced a new `BoolToVis` resource reference.

### Root cause

The window was relying on a converter that normally comes from application-level merged resources. The unit tests instantiate `SettingsWindow` directly, so they do not automatically recreate every app-level resource path the full application uses at runtime.

### What fixed it

- declare `BoolToVis` inside `SettingsWindow.xaml`
- keep the window able to load on its own during direct window tests

### Takeaway

If a WPF window is instantiated directly in unit tests, do not assume every application-level merged resource will be present. Any converter or resource the window cannot load without should either be declared locally or explicitly loaded by the test harness.

## Restore-defaults flows must update hidden persisted settings directly, not through a sticky mode flag

### Problem

The redesigned `SettingsWindow` could silently overwrite hidden persisted fields after a user clicked `Restore defaults`, even if they later changed only visible settings before saving.

### Root cause

`SettingsViewModel.Save()` switched between the current persisted settings and `new UserSettings()` based on a sticky `_persistFromDefaults` flag. That made save behavior depend on a past button click instead of the current in-memory settings state.

### What fixed it

- keep hidden persisted fields such as recording FPS, HUD gap, and last update check as explicit viewmodel state
- reset those hidden fields directly inside `RestoreDefaults()`
- save from the current viewmodel state instead of branching through a historical mode flag

### Takeaway

If a settings screen does not expose every persisted field, model the hidden values as real state and update them explicitly during reset flows. Do not make save semantics depend on whether the user previously clicked a restore action.

## Winget package renames need a one-time upstream bootstrap before automated updates can work

### Problem

The Winget Release workflow can start failing immediately after switching the package identifier to a renamed app brand even though release artifacts are being published correctly.

### Root cause

The automated release action can update an existing package in `microsoft/winget-pkgs`, but it cannot publish the first version of a brand new package identifier when that identifier has no existing manifest history upstream.

### What fixed it

- create the first multi-file manifest PR in `microsoft/winget-pkgs` for the new package identifier
- keep the repository workflow pointed at the new identifier only after that upstream package exists

### Takeaway

Treat a winget package rename as a bootstrap event, not just a workflow-config change. If the new package path does not exist upstream yet, the first renamed release needs a manual manifest submission before automated winget update workflows can succeed.

## Renamed winget packages need a distinct installer identity if they are published as a new package ID

### Problem

An initial winget PR for a renamed package can look valid in schema checks but still create package-correlation conflicts with the old package.

### Root cause

The new package identifier reused the same Inno Setup AppId and derived `ProductCode` as the old package line. Winget uses installer and Apps & Features metadata to correlate installed packages, so two package IDs advertising the same uninstall identity can produce ambiguous upgrade behavior.

### What fixed it

- assign the renamed package a new Inno Setup `AppId`
- update the new package's winget `ProductCode` values to match that new installer identity
- keep the old package manifests on their existing uninstall identity

### Takeaway

If a rename is modeled as a new winget package identifier instead of an in-place continuation, it must also get a new installer identity. Reusing the old AppId/ProductCode turns the rename into two package IDs pointing at one installed product.

## WinMM device names are truncated — use WASAPI (MMDeviceEnumerator) for microphone enumeration

### Problem

All recordings with microphone enabled silently failed. ffmpeg exited immediately with code -5, the pipe broke, and the saved MP4 was a single-frame ~50 ms stub.

### Root cause

`MicrophoneDeviceService.GetAvailableCaptureDeviceNames()` used `WaveIn.GetCapabilities()` (WinMM API). The `WAVEINCAPS.szPname` field is limited to `MAXPNAMELEN = 32` characters including the null terminator, so long device names are silently truncated. The stored/selected name (for example a 31-character stub such as `"Microphone Array (Vendor(R) Aud"`) didn't match any DirectShow device in ffmpeg's `dshow` backend, which uses the full untruncated registry name.

### What fixed it

Replaced `WaveIn.GetCapabilities()` with `MMDeviceEnumerator.EnumerateAudioEndPoints()` (WASAPI via NAudio) in both `GetAvailableCaptureDeviceNames()` and `GetDefaultCaptureDeviceName()`. `MMDevice.FriendlyName` returns the full device name that DirectShow also uses — no truncation.

The existing fallback logic in `ScreenRecordingService.ResolveMicrophoneDeviceName()` handles any stale truncated names in settings: it falls back to the default capture device when an exact match is not found.

### Takeaway

Never use WinMM (`WaveIn`) for device enumeration when the device names will be passed to ffmpeg's `dshow` input. WinMM truncates device names at 31 characters. Use `MMDeviceEnumerator` (WASAPI) throughout — it's already a project dependency and returns the same names that DirectShow and ffmpeg expect.

## Window picker overlays must enumerate capturable windows before showing any picker UI

### Problem

A window picker that enumerates `EnumWindows` after showing its own overlay windows will include those overlays in the list. The z-order is also no longer reliable because the overlay windows just moved to the top.

### Root cause

`EnumWindows` returns windows in current z-order (topmost first). If the picker overlays are already visible when enumeration runs, they are at the top of the z-order, and the own-process filter is the only guard against them appearing. More subtly, windows that were topmost _before_ the picker are now behind the picker, so the first hit in a screen-coordinate hit test is wrong.

### What fixed it

- call `IWindowCaptureService.EnumerateCapturableWindows()` before creating any `WindowPickerMonitorWindow` instances
- own-process windows are filtered by PID at enumeration time as a secondary guard
- the pre-enumerated list is immutable for the picker's lifetime — click hit testing uses the snapshot, not a live query

### Takeaway

For any picker UI that lists other application windows, enumerate the candidates before showing picker overlays. The z-order at enumeration time must reflect the pre-picker state.

## Smart redaction phone regex can shadow IPv4 when OCR inserts spaces around dot separators

### Problem

IPv4 values were sometimes still redacted as `Phone` (or remained redacted even when the IPv4 category was excluded) when OCR tokenized them with spacing artifacts like `203 . 0 . 113 . 42`.

### Root cause

The phone pattern allows digits, dots, and spaces, so OCR-spaced IPv4 strings matched the phone regex. The IPv4 guard only checked strict contiguous IPv4 text, so values with inserted whitespace were not rejected as phone candidates.

### What fixed it

- normalize matched phone candidates by removing whitespace before IPv4 validation
- treat normalized strict-IPv4 matches as IPv4-like and reject them from phone classification
- add regression tests for OCR-spaced IPv4 and for the "IPv4 excluded" setting to ensure no fallback to phone

### Takeaway

When matching OCR-derived text, classification predicates must account for OCR formatting noise (especially inserted whitespace) before overlap checks between broad and specific regex categories.

## Smart redaction on real captures needs OCR-confusable digit fallback for numeric patterns

### Problem

Phone and IPv4 redaction could fail on real screenshots even though regexes were correct. OCR misread digits in those lines (for example `555` as `SSS`, `168` as `16B`, and trailing `7` as `'`), so strict numeric patterns never matched.

### Root cause

Detection only evaluated raw OCR text (plus spacing/compact fallbacks). It had no pass that normalizes common OCR character confusions for numeric tokens, so misrecognized digits were treated as hard mismatches.

### What fixed it

- add an OCR-digit-normalization fallback pass for `Phone` and `Ipv4` rules
- normalize common confusables (`S→5`, `B→8`, `O→0`, `I/l→1`, `±→5`, `'→7`) before pattern evaluation in that fallback
- keep strict regex/predicate checks as the final gate so fallback broadens recall without weakening category validation

### Takeaway

For OCR-driven redaction, strict regexes should stay strict, but numeric-sensitive categories need a bounded confusable-character fallback to remain robust on real UI captures.

## Turning off single-file native bundling silently breaks the installer, not the dev build

### Problem

Whisper.net resolves its native runtime by probing `runtimes\win-x64` on disk, so `IncludeNativeLibrariesForSelfExtract` was flipped to `false` in `win-x64.pubxml`. Everything still worked when running from `bin\`, and the automated tests stayed green, but the installer would have shipped an app that could not start at all.

### Root cause

That flag is all-or-nothing. Turning it off pushes *every* native out of the bundle — `e_sqlite3.dll`, `wpfgfx_cor3.dll`, `PresentationNative_cor3.dll`, `vcruntime140_cor3.dll` and friends — not just the one that needed to be loose. `installer/Pointframe.iss` shipped only `Pointframe.exe` + `appsettings.json`, under a comment claiming "everything is bundled inside it", so those natives were never packaged. The first failure was the EF Core migration on startup, which reads as a database problem rather than a packaging one.

### What fixed it

- add `{#PublishDir}\*.dll` and `{#PublishDir}\runtimes\win-x64\*` to the installer `[Files]` section, plus `{app}\runtimes` and `{app}\models` to `[UninstallDelete]`
- assert the natives exist after install in `InstallerSmokeTests.AssertNativeLibrariesInstalled`, so the failure names the missing file instead of a vague launch error

### Takeaway

A dev machine cannot detect this class of bug — the DLLs resolve from elsewhere. Any change to single-file publish settings must be validated by inspecting the *publish output* and the *installed* directory, not by running from `bin\`. If a publish property changes what lands next to the exe, the installer file list has to change in the same commit.

## Encoding.UTF8 emits a BOM, which corrupts the first SRT cue

### Problem

Generated `.srt` files started with `EF BB BF` before the `1` cue index. Players and strict parsers treat the first subtitle as malformed and drop it. The `.srt` also used bare LF where SRT expects CRLF.

### Root cause

`File.WriteAllTextAsync(path, text, Encoding.UTF8)` writes a byte-order mark, because `Encoding.UTF8` is constructed with `encoderShouldEmitUTF8Identifier: true`. The feature plan actually specified "UTF-8 without BOM"; the implementation just used the obvious-looking constant. Nothing caught it because assertions compared decoded strings, where the BOM is invisible.

### What fixed it

- write with `new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)`
- emit CRLF explicitly in `SubtitleFormatter`, and skip blank segments instead of writing empty-bodied cues
- assert on raw *bytes* in `TranscriptionServiceTests`, not on the decoded string

### Takeaway

For any file consumed by an external parser, use `new UTF8Encoding(false)` rather than `Encoding.UTF8`, and test the bytes. String-level assertions cannot see a BOM or a wrong line ending.
