# Capture library

Finding saved captures: the library window, search, and the capture catalog with its OCR index.

Part of the [Pointframe knowledge base](../knowledge-base.md). Read the cross-cutting rules there first; this file holds what only this area needs.

## Features

| ID | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |
|---|---|---|---|---|---|---|
| F-25 | Capture library: browse, search, open | Tray "Library" | `Pointframe/ViewModels/LibraryViewModel.cs`, `Pointframe/Services/Capture/CaptureLibraryService.cs` | `library_open_used`, `library_ocr_search_used` | `Pointframe.Tests/ViewModels/LibraryViewModelTests.cs`, `Pointframe.Tests/Services/CaptureLibrarySearchTests.cs`, `Pointframe.Tests/Services/CaptureLibraryOcrSearchTests.cs` | [Capture library](#capture-library-and-data-layer) |
| F-26 | Capture catalog indexing and import | Background worker in the app and the MCP host | `Pointframe.Engine/Library/CaptureIndexWorker.cs`, `Pointframe.Engine/Library/CaptureImportService.cs` | — | `Pointframe.Tests/Services/CaptureCatalogPersistenceTests.cs`, `Pointframe.Tests/Services/CaptureCatalogIntegrityTests.cs` | [Capture library](#capture-library-and-data-layer) |

## Capture library and data layer

**Responsibility.** Let the user find any saved screenshot by date, file name, or indexed text visible in the image, and open it in the annotation overlay without maintaining a second WPF OCR index.

**Entry points.**

| Trigger | Path |
|---|---|
| Tray "Library" | `ShowLibraryWindowRequestedMessage` through the event aggregator; `LibraryWindow` is transient |
| Search | `LibraryViewModel.SearchAsync(query, from, to, progress, ct)` calls `ICaptureLibraryService.SearchAsync` |
| Open item | The library closes first, then launches the overlay in `OpenedImage` mode |

**Flow.**

1. `CaptureLibraryService.GetCaptures()` retains direct-folder browsing, while its asynchronous search pages through `ICaptureCatalogService` and maps available catalog paths back to `CaptureItem` values.
2. `CaptureTextLookupService` and its evictable cache remain for compatibility, but the WPF library search does not OCR files while the user types.
3. Every completed WPF save, Save As, autosave, and beautifier export is registered by `ICaptureRegistrationService` only after its output stream closes. Direct CLI/MCP monitor and window captures use the same service; if registration fails, it writes a hash-bound receipt under the local data directory and a long-lived host replays it.
4. WPF and MCP run cancellable initial and periodic non-recursive reconciliation plus OCR indexing. Reconciliation indexes PNG/JPEG/BMP files, trusts a direct-capture sidecar ID only when it is bound to the same file and SHA-256, and marks only direct children of a completely enumerated root missing.

**Data layer (`Pointframe.Data`).**

- EF Core with SQLite at `pointframe.db` in the local app data folder. The connection string is built in `AppServiceRegistration`.
- The evictable `capture_text_cache` remains separate from the durable catalog foundation: `capture_artifacts` holds one immutable metadata generation per artifact ID and verified hash, while `capture_locations` maps a normalized absolute path to its current generation. A later overwrite must create a new artifact generation rather than make an existing ID resolve to different bytes.
- `AddPointframeDataServices` registers `PointframeDataContext`, `IPointframeDataUnitOfWork`, `ICaptureTextCacheRepository`, `ICaptureCatalogRepository`, and `IMigrationService` as scoped. Resolve them inside `IServiceProvider.CreateScope()`; the app's singletons must not capture them.
- Host-neutral catalog contracts and the shared local-data/database/default-standalone-screenshot paths live in `Pointframe.Engine/Library/`, keeping standalone hosts independent of the WPF assembly.
- `CaptureCatalogService` verifies a closed PNG/JPEG/BMP file's hash, decoded format and dimensions before it writes a pending-OCR artifact. Re-observing the same normalized path and hash is idempotent. A different hash at that path creates a fresh artifact ID and marks the old generation superseded, so an existing ID never silently starts identifying replacement bytes.
- `CaptureImportService` coalesces overlapping reconciliation requests. An enumerated but locked file remains observed, and a root-level access failure never marks any catalog location missing.
- `CaptureIndexWorker` atomically claims bounded pending work with an owner and expiry lease, recovers expired leases, OCRs a locked byte snapshot outside database work, and completes only when that snapshot hash and lease still match the current artifact. A replacement file therefore cannot attach text to an old generation.
- Catalog search uses cursor pagination and reports pending, failed, and unavailable index counts. `get_capture` verifies the same byte snapshot it uses for a preview and returns paged OCR text; it rejects missing, changed, unreadable, or oversized content rather than returning stale metadata/text.
- Migrations live in `Pointframe.Data/Migrations/` and run at startup from `App.ApplyDataMigrations`. Add one with the `dotnet ef` tool from `dotnet-tools.json` (`dotnet tool restore` first); `PointframeDataContextFactory` is the design-time factory.
- Generic `IRepository<T>` and `IReadOnlyRepository<T>` in `Pointframe.Data/Abstractions/` back the concrete repositories.

**Tests.** `Pointframe.Tests/ViewModels/LibraryViewModelTests.cs`, `Pointframe.Tests/Services/CaptureLibraryServiceTests.cs`, `Pointframe.Tests/Services/CaptureLibrarySearchTests.cs`, `Pointframe.Tests/Services/CaptureLibraryOcrSearchTests.cs`, `Pointframe.Tests/Services/CaptureTextLookupServiceTests.cs`, `Pointframe.Tests/Services/SqliteCaptureTextCacheRepositoryTests.cs`, `Pointframe.Tests/Services/CaptureCatalogPersistenceTests.cs`, `Pointframe.Tests/Services/CaptureCatalogIntegrityTests.cs`.

**Files.** `Pointframe/Views/LibraryWindow.xaml.cs`, `Pointframe/ViewModels/LibraryViewModel.cs`, `Pointframe/Services/Capture/ICaptureLibraryService.cs`, `Pointframe/Services/Capture/CaptureLibraryService.cs`, `Pointframe/Services/Capture/ICaptureTextLookupService.cs`, `Pointframe/Services/Capture/CaptureTextLookupService.cs`, `Pointframe/Models/CaptureItem.cs`, `Pointframe/Models/CaptureSearchProgress.cs`, `Pointframe/App.xaml.cs`, `Pointframe/AppServiceRegistration.cs`, `Pointframe.Mcp/Program.cs`, `Pointframe.Mcp/Tools/PointframeMcpTools.cs`, `Pointframe.Data/DependencyInjection.cs`, `Pointframe.Data/Context/PointframeDataContext.cs`, `Pointframe.Data/Context/PointframeDataContextFactory.cs`, `Pointframe.Data/Entities/CaptureTextCacheEntry.cs`, `Pointframe.Data/Entities/CaptureArtifactEntry.cs`, `Pointframe.Data/Entities/CaptureLocationEntry.cs`, `Pointframe.Data/Repository/CaptureCatalogRepository.cs`, `Pointframe.Data/Repository/CaptureTextCacheRepository.cs`, `Pointframe.Data/Repository/PointframeDataUnitOfWork.cs`, `Pointframe.Engine/Capture/Services/DirectCaptureService.cs`, `Pointframe.Engine/Library/CaptureCatalogContracts.cs`, `Pointframe.Engine/Library/CaptureCatalogService.cs`, `Pointframe.Engine/Library/CaptureImportService.cs`, `Pointframe.Engine/Library/CaptureIndexWorker.cs`, `Pointframe.Engine/Library/CaptureRegistrationService.cs`, `Pointframe.Engine/Library/StandaloneCaptureLibrarySources.cs`, `Pointframe.Engine/Library/PointframePaths.cs`, `Pointframe/Services/Capture/WpfCaptureLibrarySources.cs`.
