# DBD Auto ReShade — Progress

**Current version: 1.2.1** (1.2.0's Campfire UI without match-end detection: it restored Global in the middle of
a real match). Release build: 0 warnings.
Tests: **1125/1125 passing** (plus 18 opt-in tests, skipped unless enabled: 17 off-screen snapshot renders
and 1 packaging check).

## Stack choice
- **.NET 10 (LTS), C# / WPF** (`net10.0-windows10.0.19041.0`): best Windows integration,
  DPI awareness (PerMonitorV2 manifest), single-file self-contained publish, no admin needed.
- **OCR: in-box Windows OCR (`Windows.Media.Ocr`)** — fully local/offline, zero model files,
  no NuGet/native dependencies. Behind `IOcrEngine` so a Tesseract plug-in can be added later.
  WinRT OCR exposes no per-word confidence, so confidence = string similarity +
  multi-sample agreement. `CryptographicBuffer` is NOT projected to .NET by CsWinRT
  (verified); bytes are wrapped via `DataWriter.DetachBuffer()`, and `IAsyncOperation`
  is awaited directly (no `ConfigureAwait`). A missing OCR language is re-checked at most
  every 10 s (installing the pack needs no app restart); images larger than
  `OcrEngine.MaxImageDimension` are downscaled first.
- **Capture: GDI BitBlt from the screen DC (+ `GdiFlush`)**, with a PrintWindow fallback
  (`PW_CLIENTONLY | PW_RENDERFULLCONTENT`, one full-client render per tick) when BitBlt fails
  or returns an all-black frame. Plain managed BGRA buffers, handles released per capture.
  No DXGI/GameBar dependencies.
- **Hotkeys: `RegisterHotKey` on a dedicated message-pump thread** (no window handle, no
  hooks), always with `MOD_NOREPEAT`, behind `IGlobalHotkeyService`. `Start` returns the real
  registration result; `Stop` joins the pump thread, so re-registering the same combo is safe.
  Two instances: the detection hotkey and (1.2) the Global preset hotkey.
- **Process access: query-limited only** (`PROCESS_QUERY_LIMITED_INFORMATION` +
  `QueryFullProcessImageName`, once per new PID; `GetProcessTimes` for the snapshot key;
  token integrity level). Never `PROCESS_VM_READ`, `Process.MainModule` or module lists.
- **Config: JSON** in `%AppData%\DBDAutoReShade\`, thread-safe atomic saves (unique temp file,
  flushed to disk), corrupt-file quarantine with a unique name, locked files retried and never
  replaced by defaults. A development-only override (`DBDAUTORESHADE_DATA_DIR`) moves all app
  data elsewhere and turns off DBD/ReShade.ini auto-detection.
- **UI (1.2): plain WPF in the Campfire design** — one theme dictionary (`Themes/Campfire.xaml`)
  with templates for every control used (never Windows' light templates), `WindowChrome` for the
  custom title bar, embedded fonts and map pictures as WPF `Resource` items (pack URIs work in the
  single-file publish), motion on WPF animation clocks behind one `MotionPolicy`.
- **Sound (1.2): winmm `PlaySound` (`SND_MEMORY | SND_ASYNC`)** from an in-memory, volume-scaled
  copy of a 16-bit PCM WAV, behind `ISoundEffects`. No audio library.
- **No external packages** beyond the test SDK (xUnit): smaller, fully offline-buildable,
  portable ZIP.

## Architecture (spec §43)
- `DBDAutoReShade.Core` (net10.0, no Windows deps): models, `TextNormalizationService`,
  `StringSimilarity`, `MapMatchingService` (map ≥ 0.78, realm line ≥ 0.85, 0.06 ambiguity
  margin — adaptive for near-identical names in one realm — and a map/realm-line cross-check),
  `OcrRegions` (normalized), `CapturedImage` + `ImagePreprocessingService` (pure),
  `RealmDatabaseService` (version-aware override, never throws for a bad override),
  `AppConfigService`, `HotkeyParser` (+ `SameCombo`, `IsReshadeShortcutCombo`),
  `PresetValidationService`, `PresetDecisionService`/`PresetResolver`, `ReshadeIniFile`
  (byte-preserving parser), `ReshadeConfigurationService` (stable shortcut allocation, entry
  ownership, startup-preset write, modifier-matching rules), `ReshadeSearchLocations` (where
  ReShade.ini can be, shared by discovery and diagnostics), `ReshadeDiagnosticsService`,
  `RuntimeStateService` (explicit state machine), `DetectionAggregator` (sliding window of 8
  samples with contradiction veto), `DatabaseUpdateService`, `WavPcm16` / `Pcm16Audio` (WAV
  parsing and volume² gain), embedded `RealmData/mapDatabase.json` (v1, 20 realms / 44 maps).
- `DBDAutoReShade.Desktop` (windows): `NativeMethods`, `DbdProcessService` (2 s poll,
  re-entrancy guard, PID changes reported), `ProcessIntegrity`, `DbdWindowService` (client
  bounds, focus, loading-screen occlusion probe), `GdiWindowCaptureService`, `BmpWriter`,
  `IOcrEngine` + `WindowsOcrEngine`, `DetectionOrchestrator` (~0.9 s cadence, no overlap,
  window handle re-read every tick, paused ticks while DBD isn't visible, per-sample
  `SampleEvaluated`), `DebugCropStore`, `IGlobalHotkeyService` + `GlobalHotkeyService`,
  `KeySimulator` (SendInput, 80 ms hold, key-ups always sent), `DbdDiscoveryService`
  (process/Steam/Epic, deduplicated), `ReshadeDiscoveryService`,
  `PresetActivationService` (typed failure reasons),
  `ISoundEffects` + `SoundEffectsService` (one voice, never throws) + `NullSoundEffects`.
- `DBDAutoReShade.App` (WPF):
  - Glue: `AppController` (+ `ActivateGlobalNow`, `MapDetected`, the 1.2 settings),
    `ReshadeLoadedSnapshotStore`, `MapImageCatalog`, `SingleInstanceGuard`, `ProcessStartTime`,
    `WindowPlacement` (fit to the work area, maximized inset), `app.manifest` (PerMonitorV2).
  - View models: `MainViewModel` (partials per page: Status, Presets, Settings), `StatusScreen`
    (what the Status page presents), `PresetRows`, `GreatCoordinator` (the Great landing and
    the sound's timing).
  - Windows and pages: `MainWindow` (+ `MainWindow.Motion.cs`), `SetupWizardWindow`,
    `Views/StatusView`, `Views/PresetsView`, `Views/SettingsView`.
  - Theme: `Themes/Campfire.xaml` (tokens, type ramp, control templates), `Themes/EntityGeometry.xaml`
    (the Entity's legs, path data ported from the prototype's SVG).
  - Controls: `MapPlate`, `SkillCheck` + `SkillCheckMath`, `ScoreEvent`, `CampfireScene`,
    `FogBackdrop`, `EntityLegs`, `MapPicture` / `AspectFrame`, `MapStrip`, `Keycap`,
    `CountdownRing`, `StatusDot`, `RestartBanner`, `CrossfadeText`, `CampfireType` (the
    prototype's clamp() type ramp), `CampfireAssist` / `CampfireAutomation` (placeholders,
    icons, live regions, focus hand-off).
  - Media: `MotionPolicy` (+ `MotionPolicyWatch`), `MotionMoment` / `AmbientLoop` / `Anim`,
    `StateMoment`, `Ease`, `PressFeedback`, `PopupMotion`, `MotionDiagnostics` (test hook),
    `CampfirePalette`, `CampfireFonts`.
  - Assets: `Maps/*.png` + `map-images.json`, `Fonts/*.ttf` + OFL texts, `Sounds/great.wav`,
    `CREDITS.md`.
- `DBDAutoReShade.Tests` (xUnit): **1125 tests passing, 18 opt-in** (`Campfire/` holds the UI tests).

## Runtime model (v1.2.1)
- **Scan**: the detection hotkey toggles an open-ended scan (no timeout). Each tick OCRs the
  map and realm regions; a tick is read only when DBD is the foreground window or none of the
  10 × 3 probe points per region hits another top-level window (DBD not minimized or cloaked).
  Otherwise the tick is a pause: nothing is captured, OCR'd, counted or kept for Debug Mode,
  and the status says so once. A confident read ends the scan: within the last 8 samples,
  one sample ≥ 0.90 with no rival realm, or ≥ 2 agreeing samples ≥ 0.78 that outnumber every
  other realm. The orchestrator's
  single-shot mode (`DetectionPolicy.TimeoutSeconds`) is kept for its API but not used by the app.
- **Activation**: every switch (detected realm, manual override, Return to automatic, Test,
  scan without a confident read, Global preset hotkey / "Switch to Global") becomes
  the ONE desired target; a newer one supersedes the older. It is sent immediately when DBD runs
  and is focused, otherwise by a worker that checks every 250 ms until success, supersession, a
  final failure or its deadline (detected: none; override, Return to automatic and Global preset
  hotkey: 60 s; Test: 15 s; scan without result: 10 min). Waiting for focus costs nothing;
  rejected sends are retried up to 3 times per request, then the request fails for good and the
  status says how to recover (a detected preset: state → Ready, "press F8 on a loading screen to
  try again, or use the manual override"; 1.2.1 — before, it stayed the target until gameplay
  started). The active preset changes only after a successful send;
  ReShade.ini's `PresetPath` is read before the send ("already active in ReShade") and watched
  for ~2 s after it ("confirmed by ReShade").
- **Global preset hotkey** (1.2, default F9, `globalHotkey`): a second registration, never the
  detection hotkey's combo and, like the detection hotkey, never a ReShade shortcut combo (the
  managed pool or an assigned one: RegisterHotKey would swallow the app's own SendInput
  keystrokes). The handler only queues the press (leaf `_globalPressLock`); presses run one at a
  time on the thread pool, a press waiting to start absorbs later ones. `ActivateGlobalNow`:
  DBD not running / no Global preset → status only; otherwise the running scan is cancelled
  without its own "no read → Global" request, the pending target and the override are dropped,
  state → Ready, and Global is requested (origin `Hotkey`, or `GlobalRequest` from the Status
  override's "Global / Menu"). It is the way back to Global after a match.
- **Map found** (1.2): `MapDetected(realm, map, confidence)` is raised once per accepted
  confident detection, before the switch and with no controller lock held. `GreatCoordinator`
  lands the skill check when the window is shown, not minimized and active, the Status page is
  shown and its check is scanning; the sound is scheduled on a `DispatcherTimer` from the
  landing's computed hit delay (needle at 0.3°/ms to 4° into the great block), so it is exact
  even if rendering stalls. Otherwise (and with animations off: a still flash, no delay) it plays
  at once. `AppController.PlayGreatSound` applies `soundEnabled` and `soundVolume` (gain volume²).
- **Motion** (1.2): `MotionPolicy.ShouldAnimate` = "Play animations" on, window shown and
  active; deactivation stops every running moment, so behind the game state changes are instant
  and no animation clock runs. Ambient loops additionally need no reduced motion (Windows
  animation effects, `SystemParameters.ClientAreaAnimation`, re-read on WM_SETTINGCHANGE) and run
  at 20–30 fps; under reduced motion every moment is clamped to ≤ 200 ms (≤ 160 ms delay) except
  the exact-timing ones (skill check, score event, countdown).
- **Shortcuts**: a snapshot of what the running DBD's ReShade loaded (PID + creation time;
  persisted next to the config until DBD exits) blocks names whose current (combo, path)
  it doesn't contain (`RestartRequired`). A combo that would first fire an earlier entry for
  another preset (ReShade's `ForceShortcutModifiers=0`) is never sent (`ShortcutClash`).
- **No match tracking** (1.2.1): the app never tries to tell when gameplay starts or a match
  ends, and the screen is captured only by a running scan (its two OCR regions;
  `IWindowCaptureService` has no whole-frame capture API, though the PrintWindow fallback renders
  one in-memory client frame per tick and crops the regions from it). A switched preset stays until the user's
  next switch: a new scan (F8), the Global preset hotkey / "Switch to Global" (F9), an override or
  Return to automatic, a Test, a scan without a confident read, or DBD exit. A manual override
  lasts until Return to automatic, F8, F9 or DBD exit.
- **DBD exit**: "Start the next game with the Global preset" on (`autoRestoreGlobalOnMenu`,
  default true; the C# property is `StartNextGameWithGlobal`) + Global set → `PresetPath` in
  ReShade.ini becomes Global, unless ReShade.ini shows the user's own pick made after a confirmed
  switch to Global. The setting gates only this write.
- **Concurrency** (`AppController` class doc): lock order `_sendLock` → `_gate` →
  `_iniWriteLock` (a leaf lock); `_globalPressLock` is a leaf taken alone. No capture, key send or
  hotkey (un)registration (either hotkey) runs under `_gate`; every ReShade.ini write holds
  `_iniWriteLock`; status writes go through `SetStatusLocked` / `SetRequestStatusLocked`;
  `MapDetected` is raised with no lock held.

## Completed phases
- Phase 1 — Foundation: solution, architecture, UI shell, config, DB schema, tests. ✅
- Phase 2 — DBD/ReShade config: discovery, preset validation, Global/realm mapping,
  shortcut sync with `.bak` backup + restart notice, persistence. ✅
- Phase 3 — Runtime + hotkey: process/window detection, configurable global F8,
  explicit state machine, no concurrent detection. ✅
- Phase 4/5 — Live loading-screen OCR: normalized lower-left map/realm regions,
  DPI/multi-monitor via ClientToScreen, managed preprocessing (upscale/grayscale/contrast/
  Otsu/polarity), normalization + confusable-aware fuzzy matching, multi-sample agreement,
  toggle scanning, Debug Mode crop sets (bounded, failed scans only). ✅
- Phase 6 — Lifecycle + activation: confident read → immediate focus-aware switch, Global
  restore on menu (debounced, conservative, disable-able), manual override + auto-clear. ✅
  (The match-end restore and the override's auto-clear were removed in 1.2.1.)
- Phase 7 — GUI: wizard, Status/Presets/Settings, hotkey editor, Test buttons, Reset. ✅
  (No Detect button, no tray — X exits fully.)
- Phase 8 — DB updater: HTTPS manifest (8 s timeout), download (30 s, ≤ 5 MB), SHA-256,
  schema and version validation, atomic install, last-known-good rollback, offline-safe.
  Endpoint configurable, none hard-coded. ✅
- Phase 9 — Release: Release build clean, self-contained single-file publish + portable ZIP. ✅
- Phase 10 — v1.1.0 audit fix pass (below): 0 warnings, 722/722 tests. ✅
- Phase 11 — v1.2.0 Campfire UI (below): Phase A (theme, controls, motion kernel, assets;
  backend: Global preset hotkey, sound, new settings) and Phase B (window, pages, wizard,
  screen-level motion), each reviewed and fixed; 0 warnings, 1144/1144 tests + 18 opt-in. ✅
  The portable ZIP and `publish/` folder in the repo root predate 1.1.0; re-run the publish
  command from the README and zip the whole publish folder (the exe and its `Licenses\`
  folder; the WPF native libraries and the Campfire assets are bundled into the exe) to ship it.
- Phase 12 — v1.2.1 (below): match-end detection removed after a false trigger in a real match;
  0 warnings, 1125/1125 tests + 18 opt-in. ✅

## Important decisions / verified facts
1. **ReShade format verified** against ReShade source (`runtime.cpp`, `ini_file.cpp`) + official
   docs: `[GENERAL] PresetPath` = current preset; `PresetShortcutKeys` (4 ints: key,ctrl,shift,alt)
   + `PresetShortcutPaths` = per-preset shortcuts (ReShade list syntax, `,,` = escaped comma),
   applied in-memory. **ReShade does NOT poll ReShade.ini at runtime** and loads the shortcut
   lists only at game start, so activation = managed shortcut + SendInput (DBD focused).
   Managed keys: Ctrl+Alt+Shift+F13…F24 then 0…9 (22). Allocation is **stable**: a name keeps
   its combo, new names get the lowest free slot, and combos of the user's own entries (plus,
   with `[INPUT] ForceShortcutModifiers=0`, the pool combo on each of their keys) are never
   handed out. An entry is the app's only when it is a pool combo for a managed (or previously
   managed) path; every other entry is preserved byte-for-byte. A preset assigned while DBD
   runs is blocked with "Restart Dead by Daylight so ReShade loads the shortcut for {name}."
2. **Map/realm names verified** against the official DBD wiki (wiki.gg, July 2026:
   21 realms / 46 maps). DB v1 ships 20 realms / 44 maps (Sleepless District omitted — low
   confidence; the updater can add it). No maps invented.
3. **Loading-screen layout assumption**: map name large lower-left, realm beneath
   (regions `MapName 3–55%×70–86%`, `RealmName 3–55%×84–96%`). Only these two regions are
   OCR'd; `Combined` is a reference rectangle for tuning tools, not a fallback. Tunable in
   `DbdLoadingScreenRegions`; realm-only reads still resolve presets.
4. **Safety boundary kept**: process enumeration (query-limited), window/screen capture,
   window focus and hit-testing queries, OCR, filesystem, hotkeys, SendInput, sound playback only.
   No memory access, injection, hooks, drivers, or traffic inspection anywhere.
5. Activation requires DBD focused (SendInput goes to the foreground window). Every request
   waits for focus truthfully and says so; nothing is reported as switched before the keys
   went out.
6. **No match tracking** (1.2.1): the 1.0–1.2.0 screen-change heuristic that guessed the match
   end (32×32 thumbnails every 2.5 s) restored Global in the middle of the owner's real match, so
   it was removed rather than tuned. F9 (the Global preset hotkey) is the way back to Global.
7. **Map pictures** (1.2): Behaviour Interactive's in-game map icons from Dead by Daylight, via
   the Dead by Daylight Wiki (deadbydaylight.wiki.gg) — 44 pictures, one per built-in map (a test
   checks every map of the built-in database has one), cropped to the picture (torn frames
   removed) and saved losslessly. **No upscaling of any kind** (the owner ruled out AI and
   ESRGAN-style upscalers): they are drawn as they are with `BitmapScalingMode=HighQuality`,
   UniformToFill (centred crop, never stretched), decoded once, frozen and cached (pre-decoded one
   per idle moment after start-up), and every frame is capped at 1.8× the narrowest/shortest
   built-in picture (362 × 239 px) in physical pixels; the fog fills the rest. Unknown maps get
   the fog placeholder with the map's name.
8. **Great sound** (1.2): an original synthesized recreation (variant "a", "Shing"; no game
   audio), 16-bit PCM 44.1 kHz mono normalized to a −1.0 dBFS peak (exact linear gain, no
   clipping). Played at amplitude volume² (50 % ≈ −13 dBFS, 100 % = −1 dBFS, 0 % = skipped), one
   voice (a new sound stops the previous one), never throws.
9. **Fonts** (1.2): Barlow (400/500/600/700) and Big Shoulders Display (600/700/800 static
   instances), SIL OFL 1.1, embedded as Resources. `CREDITS.md` and both OFL texts ship as loose
   files in `Licenses\` next to the exe (Content items excluded from the single-file bundle).
10. **New settings** (1.2): `animationsEnabled` (true), `soundEnabled` (true), `soundVolume`
    (0.5; missing in older configs → 0.5; clamped to 0..1), `globalHotkey` (F9 when missing;
    an explicit JSON null = cleared and stays cleared; a value that collides with the detection
    hotkey or a ReShade combo is kept as saved, left unregistered, and the reason shown).
11. **No computer use** (owner's hard rule): the app is never launched on the desktop and no
    desktop screenshot is taken. All UI verification is off-screen: the real windows are rendered
    with `RenderTargetBitmap` far off-screen and never activated (activation is simulated with
    WM_ACTIVATE), and compared with the prototype's headless-Chromium reference renders.

## Test status
`dotnet test -c Release` — **1125/1125 passing** (0 warnings; 1143 in total, 18 opt-in skipped),
covering: DB schema/lookups/
corruption/override precedence/BOM, normalization (incl. Unicode dashes), similarity, fuzzy
matching (ambiguity refusal, East/West Wing, map/realm cross-check), presets (valid/missing/
folder/extension/commas/duplicates incl. Global/unconfigured→Global), config (save/reload/
reset/corrupt/locked/concurrent saves, the 1.2 settings and their defaults for older configs),
state machine (incl. Error recovery), INI round-trip
fidelity (CRLF, spacing, idempotent), shortcut sync (stable allocation, foreign entries incl.
the owner's real layout, reserved and modifier-fired combos, stale removal, pair alignment,
no-op first sync), diagnostics, aggregator (window/veto/fast path), OCR regions, preprocessing
(Otsu on large images), OCR re-probe/downscale, capture fallback, orchestrator (continuous,
stop races, handle changes, paused ticks, debug sets and retention), scan occlusion probe,
updater (newer/older/hash/schema/version/size/rollback/non-HTTPS/cancel), hotkey parsing and
real registration (incl. refused ReShade combos), SendInput sequences and stuck-key release,
process monitor and integrity levels, Steam discovery, ReShade search locations, no match
tracking (nothing captured outside a scan, presets and overrides stay until the user switches, the
startup-preset setting and its old JSON key), controller (focus-aware activation for every
source, verification, restart and clash blocking, DBD exit startup preset, Reset Settings,
hotkey update; the Global preset hotkey: queueing, folding, no interleaving, refused combos, the
1.1 → 1.2 upgrade path), the WAV maths and the sound service, the embedded `great.wav`, the
Great coordinator (landing vs. immediate, exactly once), view models (Status/Presets/Settings
outcomes, Test ring vs. pending test, volume slider), single instance, packaging (`Licenses\`),
and the Campfire UI on the real windows off-screen: theme (no light templates, context menus),
controls, type ramp, skill check timing and landing contract, map catalog (a picture for every
built-in map, the 1.8× cap), motion policy (no animation clock while inactive, minimized, hidden
or with animations off; reduced motion), state moments, window layout (work-area fit, maximized
inset, tab order), accessibility (live regions, focus hand-off) and text contrast ≥ 4.5:1 over
every map's backdrop.
Controller tests use a fake hotkey service; only `GlobalHotkeyServiceTests` registers real
hotkeys (Ctrl+Alt+F21/F22, taking turns across test processes). Discovery and setup-check
tests don't depend on files under `%TEMP%` or `%LOCALAPPDATA%`. Opt-in: `DBD_SNAPSHOTS=<dir>`
renders every state on every page (1280×800 and 1024×680), the wizard and motion contact sheets;
`DBD_PUBLISH=<dir>` runs the README's publish command and checks the package.

## v1.2.1 — removed match-end detection
The owner's first real in-game test: the app decided the match had finished while it was still
running and switched back to the Global preset. Match-end detection (and the gameplay-start
detection behind it) was a best-effort heuristic on screen changes, so it was removed after a
false trigger in a real match instead of being tuned again.

**Behaviour now**
- The app never tries to detect gameplay start or a match end. The realm's preset stays on until
  you switch: **F9** (the Global preset hotkey, or "Switch to Global" on the Status page), a new
  scan (**F8**), a manual override or Return to automatic, a Test, or a scan without a confident
  read. Nothing captures the screen outside a scan any more (no periodic whole-frame capture).
- A manual override lasts until Return to automatic, a new scan, F9 or DBD exit (it no longer
  clears at a match end); the Status page's help text says so.
- Settings → Behaviour: "Restore the Global preset when a match ends" is now **"Start the next game
  with the Global preset"** ("When Dead by Daylight closes, the app sets your Global preset as
  ReShade's startup preset."). Same config key (`autoRestoreGlobalOnMenu`, default true), so an
  existing choice carries over; it gates only the startup-preset write at DBD exit, which it
  already did before.
- The "Match ended" Status state, its plate ("Global preset restored" / "Last match: …") and the
  Entity's yank are gone; the map still sinks into the fog when F9 takes you back to Global.
- A detected preset whose sends Windows rejected 3 times now fails for good (state → Ready, "Press
  F8 on a loading screen to try again, or use the manual override."). Before, it stayed the
  pending target ("waiting for you to click into the game") because gameplay starting retried it;
  with that gone nothing would ever have sent it.

**Code**
- Removed `MatchLifecycleService` (+ `LifecyclePhase`, `LifecycleOptions`), its thumbnail helpers
  (`ImagePreprocessingService.Thumbnail32` / `ThumbnailDistance`), the whole-frame
  `IWindowCaptureService.CaptureClientArea`, `AppState.ReturningToMenu`,
  `PresetDecisionService.DecideOnMatchEnd`, `ScreenState.MatchEnded`, `EntityLegs.Retract`, and in
  `AppController` the lifecycle tickets, `_lifecycleCommandGate`, the arm/disarm calls,
  `ReturnToGlobalAfterMatch`, the `MatchEnd` activation origin, `MatchEndFocusWindow` and
  `ActivationRequest.Exhausted` (only gameplay start ever renewed an exhausted request). The lock
  order is now `_sendLock` → `_gate` → `_iniWriteLock`.
- `AppConfig.AutoRestoreGlobalOnMenu` → `StartNextGameWithGlobal` (JSON name unchanged);
  `AppController.SetAutoRestore` → `SetStartNextGameWithGlobal`.
- Tests: the lifecycle tests are gone; the motion and snapshot tests that used the match end now
  use F9; new regression tests prove that nothing is captured outside a scan, that a detected
  preset and a manual override stay until the user ends them, that the setting gates only the
  DBD-exit write, and that old configs keep their `autoRestoreGlobalOnMenu` choice.
- `docs/screenshots/settings.png` re-rendered off-screen with the new checkbox.

## v1.2.0 — Campfire UI
The owner chose the "Campfire" prototype (`prototypes/a-campfire`) and asked for the app in
that design, better-looking map pictures **without any upscaler**, three new settings (turn
animations off for performance, turn the sound off, a skill-check volume slider at 50 % on first
run) and a hotkey for the Global preset (F9). Everything 1.1.0 does still works the same; the
additions are below.

**Window and pages**
- Custom chrome (36 px caption, real minimize/maximize/close buttons with accessible names);
  1280×800 by default, minimum 1024×680, resizable, PerMonitorV2. Windows that open centred are
  fitted to their monitor's work area (the wizard too); the maximized inset is SM_CXFRAME +
  SM_CXPADDEDBORDER at the window's DPI.
- Left rail: Status / Presets / Settings (arrow keys move between them without motion; a red
  dot on Presets while a preset is blocked, named "Restart needed" — or "ReShade.ini needed"
  when ReShade.ini is missing — for screen readers), DBD
  running dot, the F8 keycap
  ("Press F8 on the loading screen to scan"), the F9 keycap ("Global preset"; dimmed with the
  reason when it can't be registered), last switch time (", failed" in red).
- Backdrop: the current realm's map blurred and darkened, layered fog, a campfire glow, and the
  Entity's three legs with a red rim light (static geometry ported from the prototype).
- Status: state hero, status line, active preset with "Confirmed by ReShade" / "Next: … when
  the game has focus" / "Forced for … until you return to automatic" (only once the forced file
  is active), Detection and Last switch lines, the map plate (picture or vector campfire
  clearing), the skill check and the "Great skill check / Map found: …" score event, the
  restart and hotkey banners, and the manual override bar — now with "Global / Menu" first
  ("Switch to Global" → `ActivateGlobalNow()`).
- Presets: filter by realm or map, Global / Menu pinned, realm rows with map strips, the detail
  with a 2:1 hero picture and clickable thumbnails, and the preset panel (shortcut, Test with a
  15 s countdown ring tied to the controller's pending test, Change/Choose file, Clear with an
  inline confirmation); a row without a preset has its map strip fogged over.
- Settings: Detection hotkey, Global preset hotkey, Paths (selectable), Behaviour (auto-restore,
  renamed in 1.2.1; Play animations, Great sound, volume + Play sound, Debug mode), Map database, ReShade check
  (tagged lines; selectable text when the clipboard is busy), Danger zone (Reset with an inline
  confirmation). Every action shows its outcome next to its group
  (`AppController.LastSaveError`, the controller's status after its lead sentence).
- Setup wizard restyled, with a new step for the Global preset hotkey.
- Theme: themed templates for every control (buttons in four styles, CheckBox, Slider, TextBox
  incl. its Cut/Copy/Paste menu, ComboBox, ScrollBar, ToolTip, ListBox, menus, Keycap), visible
  keyboard focus, text contrast ≥ 4.5:1 (also over every map backdrop), tab order rail → page →
  window buttons, live regions for outcomes, banners and the score event.

**Map pictures** — see decision 7; `MapImageCatalog`, `MapPicture`, `AspectFrame`; credits in
`Assets/CREDITS.md` (shipped as `Licenses\CREDITS.md`).

**Skill check and the Great sound**
- The skill check sits on the plate while scanning (one revolution per read, ~1.4 s; the zone
  relocates every revolution) or paused (frozen, grey): F8 keycap in the centre, light-grey ring,
  hollow white good zone, solid white great block, red needle.
- Map found → `MapDetected` → `GreatCoordinator`: the needle runs on at 0.3°/ms and stops 4° into
  the great block (white flash, burst ring, sparks, the sound), the score event, a 0.3 s hold,
  the dial lifts away, then the fog clears off the map. The sound and the hit come from the same
  computed delay; with the window minimized, hidden, inactive or on another page the sound plays
  at once; exactly once per detection, never at startup or on restored states.
- `Assets/Sounds/great.wav` (decision 8), `SoundEffectsService` / `ISoundEffects`, `WavPcm16`.
  Settings: "Play the Great skill-check sound when a map is found" (on), "Skill-check sound
  volume" 0–100 % (50 %; saved when the slider settles; slider and Play sound disabled while the
  sound is off). The ReShade check reports "Great sound: OK (volume N %)" / "OK (turned off under
  Settings)" / "unavailable — …".

**Global preset hotkey (F9)** — see Runtime model. Change / Clear in Settings and the wizard
(same capture as the detection hotkey; both hotkeys are released while capturing; Esc cancels).
Refused: the other hotkey's combo ("F8 is already the detection hotkey." / "F9 is already the
Global preset hotkey.") and any ReShade shortcut combo ("Ctrl+Shift+Alt+F19 is reserved for
ReShade preset shortcuts."). A 1.1 config with F9 as its detection hotkey keeps it; the Global
preset hotkey then stays unregistered ("F9 is also the detection hotkey; choose another key for
the Global preset.") until one of them moves, and moving detection registers it at once.
Behaviour change: the detection hotkey now refuses the ReShade combos too (a stored one is
kept but not registered, with the reason shown). Diagnostics list both hotkeys and their problems.

**Animations and performance**
- "Play animations" (on; hint "Turn off to save performance. Animations also stop when Windows
  animation effects are off."). Off: no storyboards, ambient loops or particles, instant state
  changes, a still skill check (the needle steps once per read) and a still Great flash (an
  opacity swap, no delay); the sound still plays.
- Moments ported from the prototype: one orchestrated moment per state change (a new state
  interrupts the previous one from where it is), fog-clears map reveal, ember confirm, match-end
  sink with the legs' yank (1.2.1: the match end is gone; the sink plays when F9 switches to Global,
  the yank was removed), campfire catch / die-out / flicker with 12 rising embers, the skill
  check, the waiting-for-focus heartbeat, the restart rim-light run and banner drop, legs sway and
  lean-in, blur-bridged text crossfades, Presets hero swap / selection glide / clear fog / filter
  fade-up, tab crossfade (pointer only), button press scale. Transform and opacity only (plus a
  short blur on images). A Status change made by the user (override, Return to automatic) is a
  quick swap instead of the detection story.
- Performance (the app runs next to the game): nothing animates while the window is inactive,
  minimized or hidden; Windows animation effects off = short fades only; ambient loops at 20–30
  fps; the plate's glow effect is attached only while it shows. Tests assert that no animation
  clock runs in those cases.

**Fonts** — decision 9.

**Verification** — Release build 0 warnings; 1144/1144 tests (+ 18 opt-in). The real windows
were rendered off-screen in every state, on every page, at 1280×800 and 1024×680, plus motion
contact sheets, and compared with the prototype's references. Nothing was launched on the
desktop (decision 11).

## v1.1.0 — audit fix pass
A static audit of 1.0.x found the problems below; all are fixed with regression tests.

**Preset switching**
- Test, Manual override and Return to Automatic always failed, because clicking the button
  focused this app instead of the game. They now wait for you to click into DBD (15 s / 60 s /
  60 s) and switch then; no status promises a retry that never happens.
- The detected preset is switched as soon as the map is read and DBD is focused (usually
  still on the loading screen); "Waiting for DBD focus to switch to …" while it isn't.
- "Active preset" changes only after the keys really went out; every switch is checked against
  ReShade.ini ("confirmed by ReShade" / "already active in ReShade"), and an old check never
  overwrites newer news. A ReShade confirmation counts even when another message replaced the
  status line before it arrived.
- Assigning or removing one realm shifted other realms' shortcuts while the running ReShade
  still had the old ones, so the app could switch to a different preset. Shortcuts are stable now.
- The "restart DBD" notice is per preset: only presets ReShade hasn't loaded are blocked, the
  others keep working; a window change no longer clears it, an app restart doesn't lose it, and
  a missing ReShade.ini says to set it instead of "restart DBD".
- Your own ReShade shortcut was silently deleted when its preset file was also assigned in the
  app (e.g. Ctrl+Shift+F9 → 00.STX.Universal.ini). It is kept now, and its key is never reused.
- With ReShade's "Force correct modifier keys" off, the app no longer sends a shortcut that
  would trigger one of your own shortcuts first; it avoids such keys, and a switch or Test that
  would hit one fails right away and says to turn the option on (no DBD restart needed after).
- Keystrokes from different sources never interleave, and keys are always released even when
  Windows rejects part of a send (no stuck modifiers).
- F8 always starts a fresh attempt, also after an error ("Busy (Error)" no longer sticks).

**Match start / end** (1.2.1 removed the match tracking: the first four points no longer apply)
- Global was restored mid-match: the loading-screen switch counted as "in the match" with the
  loading screen as reference. Gameplay now has to be observed first.
- Camera motion alone no longer counts as "back to menus", and alt-tabbed moments are ignored.
- A lock-order deadlock between match tracking and the controller is gone.
- The match-end Global switch waits up to 10 min for focus instead of trying once; a match that
  ends while its preset was still waiting also restores Global unless ReShade is known to run it.
- Unsure reads really switch to Global now (a stopped or failed scan used to only say "Global
  retained"). If ReShade never confirmed an earlier Global switch, Global is sent again.
- On DBD exit, Global becomes ReShade's startup preset (decided by what ReShade.ini says), so
  the next launch no longer starts with the last realm's preset. A preset you picked in the
  overlay after a confirmed Global switch is kept and shown as the active preset.
- Return to Automatic only acts during an override; the override text says truthfully when
  the match end clears it (since 1.2.1 nothing clears it but the user or DBD exit).

**Detection / OCR**
- The scan reads only Dead by Daylight itself: while DBD is the active window, or while nothing
  covers the map/realm text regions (bottom-left half; e.g. DBD on another monitor). Otherwise it pauses and resumes
  by itself; a switch still waits for you to click into the game. A very tall taskbar over the
  realm line counts as covering. Stopping a scan no longer flashes "Scan paused" first.
- Otsu thresholding overflowed on every real crop (garbage thresholds); fixed.
- Installing the OCR language pack no longer needs an app restart (re-checked after 10 s);
  oversized crops are downscaled to what Windows OCR accepts.
- A weak raw read now gets the preprocessed retry; near-identical names (RPD East/West Wing)
  can resolve; map and realm lines are cross-checked; Unicode dashes split words.
- Only the last 8 samples count and contradicting reads veto a confirmation.
- The scan follows window-handle changes, and a stop that races a read always wins.
- The Detection line shows each sample's outcome ("last read: …").
- Capture: `GdiFlush` before reading pixels; an all-black BitBlt frame falls back to PrintWindow.

**Debug Mode**
- It never saved anything (continuous scans skipped the save) and its retention deleted the
  newest files. Now a failed or stopped scan saves its last crops (map + realm, raw and
  preprocessed) with their OCR text; the newest 20 sets are kept, and nothing is deleted unless
  the new set was written.

**Hotkeys**
- Bare letters and digits are refused (they would block typing and game controls); NumPad keys
  register as NumPad keys (not top-row digits); auto-repeat is ignored.
- Registration results are real; a new hotkey is saved only once Windows accepted it; the
  current hotkey is paused while you press a new one, and Esc cancels.

**Settings, config, ReShade.ini, database**
- Config saves are thread-safe and flushed; a locked config is never replaced by defaults; a
  corrupt one is moved aside under a unique name and explained before the setup wizard.
- Setting errors (save failures, ReShade.ini write errors, too many presets) are shown instead
  of "Assigned …", and a failed assignment is rolled back.
- Reset Settings removes the app's own shortcuts from ReShade.ini and resets the runtime state.
- ReShade.ini edits keep everything else byte-for-byte (spacing, CRLF, no extra blank line); a
  first sync with nothing assigned writes nothing; preset paths with commas are refused; relative
  paths resolve against ReShade.ini's folder; shortcut lists stay paired.
- ReShade.ini is found automatically when the DBD path is the launcher (`DeadByDaylight.exe`).
- Diagnostics check every stored shortcut and path, duplicate combos, other ReShade.ini files and
  the modifier option.
- Database updates: the manifest URL must be HTTPS, a download must carry the announced, newer
  version (no downgrades or re-downloads), size is capped, rollback uses only this run's backup,
  a BOM is accepted, and an older or broken downloaded database falls back to the built-in one.

**App shell / UI**
- Closing the first-run wizard no longer quits the app; a startup failure shows a message
  instead of leaving an invisible process; only one instance runs.
- The dark theme now applies to the windows; the realm rows no longer rebuild on every scan
  sample (which closed the override dropdown mid-click); texts use the configured hotkey.

**Safety**
- The DBD process is opened with query-limited access only (it was opened with memory-read
  rights on every 2 s poll); integrity levels are mapped correctly (elevation mismatches are
  reported right).

## Earlier versions (1.0.x)
- **1.0.4** — preset shortcut sent as down → 80 ms hold → up (a zero-hold press could be
  missed), plus the ReShade.ini read-back confirmation.
- **1.0.3** — the managed `INPUT` union marshaled as 28 bytes instead of 40 (x64), so Windows
  rejected every SendInput; fixed with a `Marshal.SizeOf` regression test. Integrity-level
  probing names elevation mismatches.
- **1.0.2** — one-click ReShade diagnostics, last-attempt tracking on the Status tab, Global
  decisions sent with the Global shortcut (not the realm's).
- **1.0.1** — F8 from "waiting for gameplay" starts a fresh attempt; timed activation retries
  (replaced in 1.1.0 by the focus-aware desired target).
- **Toggle scanning** (user request, overrides spec §7/§8 timeout): F8 starts an open-ended
  scan that stops on a confident read; a second press stops it (`WasStopped`).

## Unresolved / known limitations
- OCR accuracy on the real loading-screen font can only be validated live (no game here);
  tuning path: regions → preprocessing → thresholds (`MapMatchingService`, `DetectionPolicy`);
  Debug Mode saves the exact crops.
- Nothing switches back to Global by itself after a match (by design since 1.2.1): press F9, or
  F8 on the next loading screen. On DBD exit Global becomes the startup preset (setting on).
- The unfocused-scan check hit-tests top-level windows: click-through overlays over DBD's
  map/realm text regions aren't seen as covering it.
- Fullscreen-exclusive capture falls back to PrintWindow; a frame that stays black is read as
  is, and detection reports nothing instead of guessing.
- If the stored ReShade snapshot can't be written, or an older app version rewrote ReShade.ini
  while the same DBD kept running, an app restart judges that DBD by ReShade.ini as it is then.
- The setup wizard shows only path problems; other save failures during the wizard appear only
  on the Status page's status line afterwards.
- Not covered by automated tests: a second instance next to an elevated first one.
- If a hotkey handler blocks for more than 2 s, `Stop` returns with the hotkey still registered.
  The detection hotkey's handler still starts the scan on its pump thread (the first capture runs
  there until the OCR await); the Global preset hotkey's handler only queues.
- No production database-update endpoint exists yet (by design); configure one in Settings.
- The Campfire window has never been shown on a real desktop (decision 11): GPU cost of the blur
  and effects, real per-monitor DPI above 100 % (the test host isn't per-monitor aware; the 150 %
  frame cap is checked by its maths), Narrator actually announcing the live regions, and keyboard
  focus returning after the inline confirmations are unverified live.
- The Great sound has never played through a real audio device (winmm path untested live), and
  RegisterHotKey swallowing injected keystrokes (the reason for the refused ReShade combos) rests
  on its documented exact-modifier matching, not on a live reproduction.
- While you play, the window is inactive, so the moments that happen then (the ReShade confirm,
  the sink when F9 switches to Global) are applied instantly and not replayed; the Great lands visibly only when the
  window is in front on the Status page as the map is found (e.g. DBD on another monitor).
- The setup wizard is not resizable (it is fitted to the work area and its steps scroll).

## Next work item
- Live validation against DBD + ReShade, including the Campfire window, F9 and the Great sound;
  tune regions/thresholds from Debug Mode crops; re-publish the portable ZIP as 1.2.1.
