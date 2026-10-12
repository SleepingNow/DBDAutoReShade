# DBD Auto ReShade — Progress

**Current version: 1.1.0** (audit fix pass on top of the 1.0.x line). Release build: 0 warnings.
Tests: **722/722 passing**.

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
- **Process access: query-limited only** (`PROCESS_QUERY_LIMITED_INFORMATION` +
  `QueryFullProcessImageName`, once per new PID; `GetProcessTimes` for the snapshot key;
  token integrity level). Never `PROCESS_VM_READ`, `Process.MainModule` or module lists.
- **Config: JSON** in `%AppData%\DBDAutoReShade\`, thread-safe atomic saves (unique temp file,
  flushed to disk), corrupt-file quarantine with a unique name, locked files retried and never
  replaced by defaults.
- **No external packages** beyond the test SDK (xUnit): smaller, fully offline-buildable,
  portable ZIP.

## Architecture (spec §43)
- `DBDAutoReShade.Core` (net10.0, no Windows deps): models, `TextNormalizationService`,
  `StringSimilarity`, `MapMatchingService` (map ≥ 0.78, realm line ≥ 0.85, 0.06 ambiguity
  margin — adaptive for near-identical names in one realm — and a map/realm-line cross-check),
  `OcrRegions` (normalized), `CapturedImage` + `ImagePreprocessingService` (pure),
  `RealmDatabaseService` (version-aware override, never throws for a bad override),
  `AppConfigService`, `HotkeyParser`, `PresetValidationService`,
  `PresetDecisionService`/`PresetResolver`, `ReshadeIniFile` (byte-preserving parser),
  `ReshadeConfigurationService` (stable shortcut allocation, entry ownership, startup-preset
  write, modifier-matching rules), `ReshadeSearchLocations` (where ReShade.ini can be, shared
  by discovery and diagnostics), `ReshadeDiagnosticsService`, `RuntimeStateService` (explicit
  state machine), `DetectionAggregator` (sliding window of 8 samples with contradiction veto),
  `DatabaseUpdateService`, embedded `RealmData/mapDatabase.json` (v1, 20 realms / 44 maps).
- `DBDAutoReShade.Desktop` (windows): `NativeMethods`, `DbdProcessService` (2 s poll,
  re-entrancy guard, PID changes reported), `ProcessIntegrity`, `DbdWindowService` (client
  bounds, focus, loading-screen occlusion probe), `GdiWindowCaptureService`, `BmpWriter`,
  `IOcrEngine` + `WindowsOcrEngine`, `DetectionOrchestrator` (~0.9 s cadence, no overlap,
  window handle re-read every tick, paused ticks while DBD isn't visible, per-sample
  `SampleEvaluated`), `DebugCropStore`, `IGlobalHotkeyService` + `GlobalHotkeyService`,
  `KeySimulator` (SendInput, 80 ms hold, key-ups always sent), `DbdDiscoveryService`
  (process/Steam/Epic, deduplicated), `ReshadeDiscoveryService`, `MatchLifecycleService`
  (32×32 thumbnails, no OCR), `PresetActivationService` (typed failure reasons).
- `DBDAutoReShade.App` (WPF): `AppController` glue, `ReshadeLoadedSnapshotStore`,
  `MainViewModel`, Status/Presets/Settings, first-run wizard, dark theme,
  `SingleInstanceGuard`, `ProcessStartTime`, `app.manifest` (PerMonitorV2).
- `DBDAutoReShade.Tests` (xUnit): **722 tests, all passing**.

## Runtime model (v1.1.0)
- **Scan**: the detection hotkey toggles an open-ended scan (no timeout). Each tick OCRs the
  map and realm regions; a tick is read only when DBD is the foreground window or none of the
  10 × 3 probe points per region hits another top-level window (DBD not minimized or cloaked).
  Otherwise the tick is a pause: nothing is captured, OCR'd, counted or kept for Debug Mode,
  and the status says so once. A confident read ends the scan: within the last 8 samples,
  one sample ≥ 0.90 with no rival realm, or ≥ 2 agreeing samples ≥ 0.78 that outnumber every
  other realm. The orchestrator's
  single-shot mode (`DetectionPolicy.TimeoutSeconds`) is kept for its API but not used by the app.
- **Activation**: every switch (detected realm, manual override, Return to Automatic, Test,
  match end, scan without a confident read) becomes the ONE desired target; a newer one
  supersedes the older. It is sent immediately when DBD runs and is focused, otherwise by a
  worker that checks every 250 ms until success, supersession, a final failure or its deadline
  (detected: none while waiting for gameplay; override and Return to Automatic: 60 s; Test:
  15 s; match end and scan without result: 10 min). Waiting for focus costs nothing; rejected
  sends are retried up to 3 times per request, then reported with the fix. The active preset
  changes only after a successful send; ReShade.ini's `PresetPath` is read before the send
  ("already active in ReShade") and watched for ~2 s after it ("confirmed by ReShade").
- **Shortcuts**: a snapshot of what the running DBD's ReShade loaded (PID + creation time;
  persisted next to the config until DBD exits) blocks names whose current (combo, path)
  it doesn't contain (`RestartRequired`). A combo that would first fire an earlier entry for
  another preset (ReShade's `ForceShortcutModifiers=0`) is never sent (`ShortcutClash`).
- **Lifecycle**: armed with the loading screen at a confident detection; InTrial only when
  observed (3 consecutive far polls, 2.5 s apart); BackToMenus after ≥ 45 s in the trial and
  4 consecutive polls far from the gameplay reference (drift re-based). Only frames of DBD in
  front count; idle phases capture nothing.
- **DBD exit**: auto-restore on + Global set → `PresetPath` in ReShade.ini becomes Global,
  unless ReShade.ini shows the user's own pick made after a confirmed switch to Global.
- **Concurrency** (`AppController` class doc): lock order `_sendLock` | `_lifecycleCommandGate`
  → `_gate` → { lifecycle lock, `_iniWriteLock` } (leaf locks). No capture, key send, hotkey
  (un)registration or lifecycle command runs under `_gate`; every ReShade.ini write holds
  `_iniWriteLock`; status writes go through `SetStatusLocked` / `SetRequestStatusLocked`.

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
- Phase 7 — GUI: wizard, Status/Presets/Settings, hotkey editor, Test buttons, Reset. ✅
  (No Detect button, no tray — X exits fully.)
- Phase 8 — DB updater: HTTPS manifest (8 s timeout), download (30 s, ≤ 5 MB), SHA-256,
  schema and version validation, atomic install, last-known-good rollback, offline-safe.
  Endpoint configurable, none hard-coded. ✅
- Phase 9 — Release: Release build clean, self-contained single-file publish + portable ZIP. ✅
- Phase 10 — v1.1.0 audit fix pass (below): 0 warnings, 722/722 tests. ✅
  The portable ZIP and `publish/` folder in the repo root predate 1.1.0; re-run the publish
  command from the README to ship it.

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
   window focus and hit-testing queries, OCR, filesystem, hotkeys, SendInput only. No memory
   access, injection, hooks, drivers, or traffic inspection anywhere.
5. Activation requires DBD focused (SendInput goes to the foreground window). Every request
   waits for focus truthfully and says so; nothing is reported as switched before the keys
   went out.
6. Lifecycle match tracking is a **best-effort external heuristic** (see Runtime model); it
   can be disabled in Settings ("Restore Global preset when the match ends").

## Test status
`dotnet test -c Release` — **722/722 passing** (0 warnings), covering: DB schema/lookups/
corruption/override precedence/BOM, normalization (incl. Unicode dashes), similarity, fuzzy
matching (ambiguity refusal, East/West Wing, map/realm cross-check), presets (valid/missing/
folder/extension/commas/duplicates incl. Global/unconfigured→Global), config (save/reload/
reset/corrupt/locked/concurrent saves), state machine (incl. Error recovery), INI round-trip
fidelity (CRLF, spacing, idempotent), shortcut sync (stable allocation, foreign entries incl.
the owner's real layout, reserved and modifier-fired combos, stale removal, pair alignment,
no-op first sync), diagnostics, aggregator (window/veto/fast path), OCR regions, preprocessing
(Otsu on large images), OCR re-probe/downscale, capture fallback, orchestrator (continuous,
stop races, handle changes, paused ticks, debug sets and retention), scan occlusion probe,
updater (newer/older/hash/schema/version/size/rollback/non-HTTPS/cancel), hotkey parsing and
real registration, SendInput sequences and stuck-key release, process monitor and integrity
levels, Steam discovery, ReShade search locations, lifecycle (trial/menu/drift/motion/focus,
deadlock stress), controller (focus-aware activation for every source, verification, restart
and clash blocking, DBD exit startup preset, Reset Settings, hotkey update), view model,
single instance, XAML theme wiring.
Controller tests use a fake hotkey service; only `GlobalHotkeyServiceTests` registers real
hotkeys (Ctrl+Alt+F21/F22, taking turns across test processes). Discovery and setup-check
tests don't depend on files under `%TEMP%` or `%LOCALAPPDATA%`.

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

**Match start / end**
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
  the match end clears it.

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
- Match tracking is heuristic and watches only while DBD is the foreground window; exotic flows
  may be missed. F8 re-detection, manual override and Return to Automatic always work.
- The unfocused-scan check hit-tests top-level windows: click-through overlays over DBD's
  map/realm text regions aren't seen as covering it.
- Fullscreen-exclusive capture falls back to PrintWindow; a frame that stays black is read as
  is, and detection reports nothing instead of guessing.
- If the stored ReShade snapshot can't be written, or an older app version rewrote ReShade.ini
  while the same DBD kept running, an app restart judges that DBD by ReShade.ini as it is then.
- Save failures during the setup wizard appear only on the main window's status line.
- Not covered by automated tests: a second instance next to an elevated first one; WPF visuals
  (theme, wizard → main window hand-off) are checked by review and XAML tests only.
- If a hotkey handler blocks for more than 2 s, `Stop` returns with the hotkey still registered.
- No production database-update endpoint exists yet (by design); configure one in Settings.

## Next work item
- Live validation against DBD + ReShade; tune regions/thresholds from Debug Mode crops;
  re-publish the portable ZIP as 1.1.0.
