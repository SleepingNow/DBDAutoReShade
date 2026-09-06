# DBD Auto ReShade — Progress

## Stack choice
- **.NET 10 (LTS), C# / WPF** (`net10.0-windows10.0.19041.0`): best Windows integration,
  DPI awareness (PerMonitorV2 manifest), single-file self-contained publish, no admin needed.
- **OCR: in-box Windows OCR (`Windows.Media.Ocr`)** — fully local/offline, zero model files,
  no NuGet/native dependencies. Behind `IOcrEngine` so a Tesseract plug-in can be added later.
  Note: WinRT OCR exposes no per-word confidence, so confidence = string similarity +
  multi-sample agreement. `CryptographicBuffer` is NOT projected to .NET by CsWinRT
  (verified); bytes are wrapped via `DataWriter.DetachBuffer()`, and `IAsyncOperation`
  is awaited directly (no `ConfigureAwait`).
- **Capture: GDI BitBlt + PrintWindow fallback**, plain managed BGRA buffers, handles
  released per capture. No DXGI/GameBar dependencies.
- **Hotkeys: `RegisterHotKey` on a thread message queue** (no window handle, no hooks).
- **Config: JSON** in `%AppData%\DBDAutoReShade\`, atomic saves, corrupt-file quarantine.
- **No external packages** beyond the test SDK (xUnit): smaller, fully offline-buildable,
  portable ZIP.

## Architecture (spec §43)
- `DBDAutoReShade.Core` (net10.0, no Windows deps): models, `TextNormalizationService`,
  `StringSimilarity`, `MapMatchingService`, `OcrRegions` (normalized), `CapturedImage` +
  `ImagePreprocessingService` (pure), `RealmDatabaseService`, `AppConfigService`,
  `HotkeyParser`, `PresetValidationService`, `PresetDecisionService`/`PresetResolver`,
  `ReshadeIniFile` (preserving parser), `ReshadeConfigurationService` (shortcuts),
  `RuntimeStateService` (explicit state machine), `DetectionAggregator`,
  `DatabaseUpdateService`, embedded `RealmData/mapDatabase.json` (v1, 20 realms).
- `DBDAutoReShade.Desktop` (windows): `NativeMethods`, `DbdProcessService` (2 s poll),
  `DbdWindowService`, `GdiWindowCaptureService`, `BmpWriter`, `IOcrEngine` +
  `WindowsOcrEngine`, `DetectionOrchestrator` (10 s window, no overlap), `GlobalHotkeyService`,
  `KeySimulator` (SendInput), `DbdDiscoveryService` (process/Steam/Epic),
  `ReshadeDiscoveryService`, `MatchLifecycleService` (32×32 thumbnails, no OCR),
  `PresetActivationService`.
- `DBDAutoReShade.App` (WPF): `AppController` glue, `MainViewModel`, Status/Presets/Settings,
  first-run wizard, dark theme, `app.manifest` (PerMonitorV2).
- `DBDAutoReShade.Tests` (xUnit): **165 tests, all passing**.

## Completed phases
- Phase 1 — Foundation: solution, architecture, UI shell, config, DB schema, tests. ✅
- Phase 2 — DBD/ReShade config: discovery, preset validation, Global/realm mapping,
  shortcut sync with `.bak` backup + restart notice, persistence. ✅
- Phase 3 — Runtime + hotkey: process/window detection, configurable global F8,
  explicit state machine, no concurrent detection. ✅
- Phase 4/5 — Live loading-screen OCR: normalized regions (lower-left map/realm +
  combined fallback), DPI/multi-monitor via ClientToScreen, managed preprocessing
  (upscale/grayscale/contrast/Otsu/polarity), normalization + confusable-aware fuzzy
  matching, multi-sample agreement, 10 s timeout, debug crops (bounded, failure-only). ✅
- Phase 6 — Lifecycle + activation: pending realm → activate on gameplay, Global restore
  on menu (debounced, conservative, disable-able), manual override + auto-clear. ✅
- Phase 7 — GUI: wizard, Status/Presets/Settings, hotkey editor, Test buttons, Reset. ✅
  (No Detect button, no tray — X exits fully.)
- Phase 8 — DB updater: HTTPS manifest, 8 s timeout, SHA-256, schema validation, atomic
  install, last-known-good rollback, offline-safe. Endpoint configurable, none hard-coded. ✅
- Phase 9 — Release: Release build clean, 139/139 tests green, self-contained
  single-file publish + portable ZIP. ✅

## Important decisions / verified facts
1. **ReShade format verified** against ReShade source (`runtime.cpp`) + official docs:
   `[GENERAL] PresetPath` = current preset; `PresetShortcutKeys` (4 ints: key,ctrl,shift,alt)
   + `PresetShortcutPaths` = per-preset shortcuts, applied in-memory. **ReShade does NOT
   poll ReShade.ini at runtime**, so activation = managed shortcut + SendInput (DBD focused).
   Managed keys: Ctrl+Alt+Shift+F13…F24 then 0…9, deterministic, collision-free; foreign
   (user) entries preserved; stale entries removed via `LastSyncedPresetPaths`.
   Consequence: after changing assignments while DBD runs, the app shows
   **"Restart Dead by Daylight so ReShade loads them."**
2. **Map/realm names verified** against the official DBD wiki (wiki.gg, July 2026:
   21 realms / 46 maps). DB v1 ships 20 realms (Sleepless District omitted — low
   confidence; the updater can add it). No maps invented.
3. **Loading-screen layout assumption**: map name large lower-left, realm beneath
   (regions `MapName 3–55%×70–86%`, `RealmName 3–55%×84–96%`, `Combined` fallback).
   Tunable in `DbdLoadingScreenRegions`; realm-only reads still resolve presets.
4. **Safety boundary kept**: process enumeration, window capture, OCR, filesystem,
   hotkeys, SendInput only. No memory access, injection, hooks, drivers, or traffic
   inspection anywhere.
5. Activation requires DBD focused (SendInput goes to the foreground window);
   failures are reported and retried (≤3 attempts), never silent.
6. Lifecycle menu-restore is a **best-effort external heuristic** (sustained scene change
   after ≥45 s trial, 4-poll debounce, drift tracking); it can be disabled in Settings.

## Test status
`dotnet test` — **165/165 passing**, covering: DB schema/lookups/corruption, normalization,
similarity, fuzzy matching (incl. ambiguity refusal), presets (valid/missing/extension/
duplicates/unconfigured→Global), config (save/reload/reset/corrupt), state machine
(full cycle/timeout/illegal/DBD-exit/override/**F8-from-WaitingForGameplay**), preset decisions, INI preservation,
shortcut sync (deterministic/foreign/stale/idempotent), aggregator (early/agreement/
disagreement/timeout/cancel), OCR regions (1080p→4K/ultrawide), preprocessing on synthetic
images, updater (newer/older/hash/schema/offline/non-HTTPS), hotkey parsing, SendInput
sequences, orchestrator (success/timeout/no-overlap/unavailable/cancel), lifecycle
(trial/menu/drift), discovery, BMP writer.

## Unresolved / known limitations
- **"Says activated but nothing changes in game" (v1.0.4):** the shortcut was fired
  as down+up inside one SendInput batch (zero hold time), which frame-polled input
  can miss entirely. Now sent as down-phase → 80 ms hold → up-phase
  (`SendInputKeySimulator.BuildPress`), plus a read-back confirmation: ~2 s after
  sending, the app re-reads ReShade.ini's PresetPath (relative paths resolved) and
  reports "confirmed by ReShade" vs "keys sent but not confirmed". Covered by updated
  `KeySimulatorTests` + `SuccessfulSwitch_IsConfirmedByReshadeIni` +
  `ReadCurrentPresetAbsolute_ResolvesRelativePaths`.
- **Toggle scanning (user request, overrides spec §7/§8 timeout):** F8 now starts an
  open-ended scan (same ~1 s cadence, still stops by itself on a confident read) and
  a second F8 press stops it (`DetectionOrchestrator.DetectAsync(continuous:)` +
  controller scan CTS; scan also stops on DBD exit/shutdown; user-stop yields a
  distinct `WasStopped` outcome with no debug crop). Covered by
  `ContinuousMode_IgnoresTimeout_StopsOnCancel`, `UserStop_SkipsDebugCrop`,
  `SecondPress_StopsRunningScan`, `Scan_StopsWhenDbdExits`.
- **Root cause "could not send the preset shortcut" (v1.0.3):** the managed INPUT
  union only declared KEYBDINPUT, so it marshaled as 28 bytes instead of the native
  40 (x64) — Windows rejected EVERY SendInput call. Fixed by declaring the full
  native union (MOUSEINPUT/HARDWAREINPUT members) + a `Marshal.SizeOf` regression
  test that would have caught it. Also added integrity-level probing
  (`ProcessIntegrity`, token metadata only): a blocked keystroke now names an
  elevation mismatch explicitly, and the diagnostics report shows both levels.
- **Follow-up "still doesn't switch" (v1.0.2):** added one-click ReShade diagnostics
  (`ReshadeDiagnosticsService`: ini/hook-DLL/shortcut-roundtrip/preset-files/multi-ini
  scan) with Run/Copy in Settings, last-attempt tracking on the Status tab
  (`ActivationAttempt`: what/key/success/message), fixed Global-fallback using the
  realm shortcut instead of the Global one (also for manual override), and stopped
  pointless retries when nothing is assigned at all. Covered by
  `ReshadeDiagnosticsTests` + new `ControllerActivationTests`.
- **Fixed after user report "detected but never switches" (v1.0.1):**
  activation used the realm's shortcut even when the decision was Global (now uses the
  Global shortcut); activation gave up after ~2 attempts with no recovery (now retries
  every 5 s up to 12 times, ~60 s); F8 was rejected while `WaitingForGameplay`, leaving
  the app stuck (now a fresh attempt always starts). Covered by
  `ControllerActivationTests` (retry-then-succeed, F8 recovery, exhaustion guidance,
  unconfigured→Global). If switching still fails, the status line names the cause
  (focus / missing shortcut / missing ReShade.ini / missing preset file — the latter is
  now checked before sending keys).
- OCR accuracy on the real loading-screen font can only be validated live (no game here);
  tuning path: regions → preprocessing → thresholds (`MapMatchingService`,
  `DetectionPolicy`), Debug Mode saves the exact failing crop.
- Fullscreen-exclusive capture falls back to PrintWindow; if both fail, detection reports
  it instead of guessing.
- Menu-restore heuristic (above) may miss exotic flows; F8 re-detection + manual override
  + Return to Automatic always work.
- No production database-update endpoint exists yet (by design); configure one in Settings.

## Next work item
- Live validation against DBD + ReShade; tune regions/thresholds from Debug crops.
