# Pointframe privacy

## Privacy Policy

This policy covers the Pointframe desktop app, the Pointframe CLI, the Pointframe MCP server, and this website. The same text is published at <https://dimitar-radenkov.github.io/Pointframe/privacy.html>.

**Data collection.** Pointframe is free, open source, and needs no account. Screenshots, recordings, OCR text, file names, and file paths are never collected by the project. They stay on your machine under `%LOCALAPPDATA%\Pointframe`, apart from what you choose to send somewhere yourself: an MCP capture tool returns the image to your MCP client (pass `includeImage: false` to return metadata only), and Upload & copy link sends one capture to the HTTPS destination you configured.

**Usage telemetry of the CLI and the MCP server.** Official release builds of the CLI and the MCP server send one anonymous event per MCP tool call or CLI command to Azure Application Insights (from this version, a verification scenario's wait for the app to exit before a restart counts as one call). The event holds only these fixed values:

| Property | Values |
|---|---|
| `name` | the MCP tool or CLI command, from a fixed list; anything else is `other` |
| `outcome` | `success`, `error`, `cancelled`, `denied`, `verification_failed`, or `invalid_request` (never an error message) |
| `duration_bucket` | `lt_1s`, `1_5s`, `5_30s`, or `gt_30s` |
| `host` | `cli` or `mcp` |
| `client` (MCP only) | `claude-desktop`, `claude-code`, `codex`, `vscode`, `cursor`, or `other` |
| `version` | the Pointframe version |
| `agent_install_id` | a random ID created on first use and stored in `%LOCALAPPDATA%\Pointframe\agent-install-id`, shared by the CLI and the MCP server on that account; not tied to a name, account, or machine |

No arguments, paths, screenshots, text, error messages, user names, or machine names are sent, and the exporter identifies itself with the fixed names `Pointframe.Cli` or `Pointframe.Mcp`. The random install ID lets the project count how many installs use the CLI and MCP server and whether they come back; opting out deletes it, so opting back in starts a new, unrelated ID. Source builds send nothing, because the connection string is empty in the repository and injected only by the release pipeline. The first run prints a one-time notice to standard error (the MCP server never writes to standard output outside the protocol, so it also adds the notice to its server instructions once).

**Opting out of CLI and MCP telemetry.** Set the environment variable `POINTFRAME_TELEMETRY_OPTOUT=1` (or the standard `DO_NOT_TRACK=1`), or create `%LOCALAPPDATA%\Pointframe\agent-telemetry.json` containing `{"optOut": true}`. A config file that cannot be read counts as an opt-out. When opted out, no telemetry request is made at all.

**Desktop app telemetry.** Official installer builds also send the usage telemetry described in [Privacy & Telemetry](#privacy--telemetry) below, including a random install ID. The desktop app has no in-app telemetry switch; the opt-out above applies to the CLI and the MCP server only.

**This website.** The pages of this website at dimitar-radenkov.github.io/Pointframe, except the Screen Explainer pages, load a small script that measures how the site is used. It sends these data to Azure Application Insights:

- Page views are recorded with the Application Insights JavaScript SDK's standard page-view and page-load timing data; see [Microsoft's documentation](https://learn.microsoft.com/azure/azure-monitor/app/javascript-sdk). Because the site turns off the library's cookies and storage, the random identifiers the library attaches to each page view are created anew on every page load and cannot link one visit to the next.
- The event `website_session_started`, sent once per browser tab session, with the referring page and the browser's user agent.
- The event `website_cta_clicked`, sent when you click a tracked button or link such as Download, with the button's type and location on the page and the address it links to.
- The events `winget_command_copied` and `winget_command_copy_failed`, sent when you use the copy button for the install command, with the package manager and the command shown.
- The page path and address, a timestamp, and the UTM campaign parameters (`utm_source`, `utm_medium`, `utm_campaign`, `utm_content`, `utm_term`) when the address you arrived on carries them.

The site configures the analytics library not to set cookies and not to use browser storage, and the site's own code sets no cookies and writes nothing to local storage. The only thing the site's own code keeps in the browser is, in the tab's session storage, a random session ID created by your browser, a marker that this tab's visit was already counted, and, when the address you arrived on carries UTM parameters, the attribution of the visit (those UTM parameters, the landing page path, the time it was first seen, and the referring page); the browser deletes them when the tab closes. The session ID is not tied to your name or account and lets the project count how many visits it sees. An earlier version of the site kept an event log and campaign parameters in local storage; the site now deletes those two entries when you visit. These data go to the same Application Insights resource as the telemetry above, under the Third-party sharing and Retention terms below. The Screen Explainer pages load no scripts. To avoid website measurement, block scripts for this site or use a tracker blocker; the site works without them.

**Third-party sharing.** Telemetry goes to Microsoft Azure Application Insights, which acts as a processor on the project's behalf. Application Insights receives the IP address of the connection, derives a coarse country and city from it, and stores the address masked (IP masking is on); the exporter attaches no host name, user name, or other machine details. Nothing is sold or shared with anyone else.

**Retention.** Telemetry is kept in the project's Application Insights resource under Application Insights' default retention (90 days unless the maintainer changes the resource setting) and is used only to see which tools and commands are used and whether they fail, and how visitors use this website. Local files stay until you delete them.

**Contact.** Questions, deletion requests, and privacy concerns: open an issue at <https://github.com/dimitar-radenkov/Pointframe/issues>.

## Privacy & Telemetry

Official builds send usage telemetry to Azure Application Insights. The payload contains a random install ID, a per-run session ID, app version, event name, and only properties declared for that event in the catalog below. `os_build` and `screen_count` are sent with `app_started`; other declared properties are sent only with their corresponding event. The envelope also includes `telemetry_channel` and `telemetry_schema_version`. The exporter uses the fixed service identity `service.name=Pointframe` and `service.instance.id=desktop`; it does not attach the PC name, Windows user or domain, or automatic host, process, and OS resource details. Azure derives country and city from the connection IP address.

### What is collected

Every event below is defined in [`TelemetryEventCatalog.cs`](Pointframe/Services/Infrastructure/TelemetryEventCatalog.cs), which is the single source of truth. A unit test fails the build if this table and the catalog ever disagree.

**App lifecycle**

| Event | Properties |
|---|---|
| `app_started` | `os_build`, `screen_count` |
| `hotkey_status` | `status` (installed / failed) |
| `startup_completed` | `duration_ms` |
| `app_heartbeat` | `uptime_minutes` (sent every 4 hours while the tray app remains open) |
| `app_closed` | `session_minutes` |

**Capture**

| Event | Properties |
|---|---|
| `snip_started` | `type` (region / whole_screen / window_clean / scrolling), `source` (tray / hotkey / onboarding) |
| `onboarding_shown` | — |
| `onboarding_action` | `action` (capture / dismiss) |
| `snip_cancelled` | `type` (region / whole_screen / scrolling) |
| `scrolling_capture_completed` | `count` (frames captured), `stop_reason` (end_of_content / no_overlap / frame_limit / height_limit) |
| `capture_delay_used` | `delay_seconds` |
| `capture_completed` | `action` (copy / save / save_as / auto_save / upload_link) |
| `capture_closed` | `annotated` (true / false; sent when a capture is closed without copying, saving or uploading it) |
| `capture_pinned` | — |
| `first_capture_completed` | `capture_type`, `first_action`, `time_from_install_minutes` when available |
| `open_image_used` | — |
| `annotation_committed` | `tool`, `count` (one event per tool, sent once when the annotation surface closes) |

**Recording**

| Event | Properties |
|---|---|
| `recording_started` | `type` (region / whole_screen) |
| `recording_completed` | `duration_seconds` when available |
| `recording_failed` | `phase`, `reason`, `inner_types`, and `ffmpeg_exit_code` when known |
| `transcript_completed` | `success`, `duration_seconds`, plus `segment_count` on success or `skip_reason` when skipped |
| `transcript_failed` | `exception_type` |
| `first_recording_completed` | `with_audio`, `duration_seconds` and `time_from_install_minutes` when available |
| `recording_hud_pause_toggled` | `state` |
| `recording_hud_stopped` | `duration_seconds` |
| `recording_hud_microphone_toggled` | `state` |
| `recording_hud_display_mode_changed` | `display_mode` |
| `recording_hud_annotation_input_toggled` | `annotation_input_state` |
| `recording_hud_tool_selected` | `annotation_tool` |
| `recording_hud_undo_annotations` | — |
| `recording_hud_clear_annotations` | — |
| `ffmpeg_missing` | — |
| `microphone_unavailable` | — |

**Export and editing**

| Event | Properties |
|---|---|
| `gif_export_started` | — |
| `gif_export_completed` | `success`, `duration_seconds` |
| `video_trim_opened` | — |
| `video_trim_started` | — |
| `video_trim_completed` | `success`, `canceled` |
| `beautify_opened` | — |
| `screenshot_beautified` | — |
| `screenshot_beautified_copied` | — |

**OCR and library**

| Event | Properties |
|---|---|
| `ocr_attempted` | `selection_width_px`, `selection_height_px` |
| `ocr_no_text` | `selection_width_px`, `selection_height_px` |
| `ocr_used` | `selection_width_px`, `selection_height_px` |
| `library_open_used` | — |
| `library_ocr_search_used` | — |

**Settings and About**

| Event | Properties |
|---|---|
| `settings_opened` | `app_section` |
| `settings_section_changed` | `app_section` |
| `settings_saved` | `app_section` |
| `settings_section_reset` | `app_section` |
| `settings_defaults_restored` | — |
| `settings_canceled` | — |
| `about_opened` | — |
| `about_closed` | — |
| `about_url_opened` | `url_host` (host name only, never a full URL) |

**Updates and diagnostics**

| Event | Properties |
|---|---|
| `update_check_manual` | — |
| `update_available` | `from_version`, `target_version` |
| `update_confirmed` | `from_version`, `target_version` |
| `update_dismissed` | `from_version`, `target_version` |
| `update_card_shown` | `target_version` |
| `update_card_dismissed` | `target_version` |
| `update_download_failed` | `reason` |
| `update_installer_launched` | `from_version`, `target_version` |
| `update_applied` | `from_version`, `target_version` |
| `unhandled_exception` | `exception_type`, `context`, `last_action` when available |

Every event includes an app `version`, a per-run `session_id`, a `telemetry_channel` (`product` or `diagnostic`), a `telemetry_schema_version` (currently `2`), and an `install_id` when one is available. The install ID is a random GUID generated once on first launch and stored locally. It is used only to count unique installs; it is not tied to an account or identity. Azure may derive country and city from the IP address used to connect to Application Insights.

Properties are allow-listed per event in the catalog: anything a caller passes that the event does not declare is dropped before export and reported locally as a schema violation without logging its value. Every declared value is truncated to 200 characters. OpenTelemetry resource environment overrides (`OTEL_RESOURCE_ATTRIBUTES`, `OTEL_SERVICE_NAME`, and related `OTEL_*` settings) are ignored by the product telemetry pipeline. These measures keep paths, file names, and recognised text out of telemetry by construction rather than by convention.

The `last_action` value attached to `unhandled_exception` is the name of the most recent **product** event — background diagnostic events such as `app_heartbeat` never overwrite it.

Screenshots, recordings, OCR output, file names, file paths, exception messages, and stack traces are not sent as telemetry. Captures stay local until you use Upload & copy link; then only the capture you choose to upload is sent to the HTTPS destination you configured in Settings → Sharing. Local diagnostic logs are stored under `%LOCALAPPDATA%\Pointframe\logs\` and may include local paths to help troubleshoot issues; they are not uploaded automatically.

### Source builds

Telemetry is disabled automatically when the `ApplicationInsights:ConnectionString` value in `appsettings.json` is empty (which is the default in the source repository). Only official builds distributed via the installer include the real connection string.

### For contributors

To enable telemetry locally during development, create `Pointframe/appsettings.Local.json` (gitignored):

```json
{
  "ApplicationInsights": {
    "ConnectionString": "<your-connection-string>"
  }
}
```

To set up your own Azure Application Insights resource, follow the [Azure Monitor setup guide](https://learn.microsoft.com/en-us/azure/azure-monitor/app/create-workspace-resource).

### Feature usage report

Use the ready-to-run KQL report pack in [docs/appinsights-feature-usage-queries.kql](docs/appinsights-feature-usage-queries.kql) to track:

- Weekly active installs and sessions
- Per-feature adoption (% installs that used each feature)
- Feature funnel conversion (snip -> annotate -> pin/ocr)
- Power-user and stickiness indicators
- Version split and regression spotting after releases
