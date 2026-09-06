> # ⚠️ WARNING: This is a vibe-coded app
>
> **This project was designed and written with AI assistance (vibe-coded).**
> It builds cleanly with 165/165 automated tests passing, but it has not been
> validated live against the real game by its author — review the code yourself
> before running it, and use it at your own risk.

# DBD Auto ReShade

Portable Windows utility for **Dead by Daylight + ReShade**: press a global hotkey on the
loading screen and the app reads the map/realm text with **local OCR**, looks up the realm
in its built-in database, and activates your per-realm ReShade preset when gameplay begins.
It returns to your Global/Menu preset when the match ends.

No screenshots, templates, or training data needed — the app captures the live loading
screen itself. No memory reading, injection, hooks, drivers, or network snooping:
fully external, anti-cheat safe (process enumeration, screen capture, OCR, files,
hotkeys and keystrokes only). Everything runs **offline**; nothing is ever uploaded.

## Install
1. Extract `DBDAutoReShade-portable-win-x64.zip` anywhere.
2. Run `DBDAutoReShade.exe` (normal user, no admin).
3. Complete the first-run wizard: DBD path (auto-detected if possible), ReShade.ini,
   Global preset, hotkey (default **F8**). Realm presets are optional.

## Normal use
1. Launch DBD Auto ReShade, then Dead by Daylight.
2. Status shows `DBD Status: Running` / `Ready — Press F8`.
3. While the match loads, press **F8** to start scanning. The app keeps capturing the
   loading-screen text regions and OCRing them (about once a second) until it
   confidently reads the map — however long the loading takes. Press **F8 again** to
   stop scanning manually; a confident read stops it automatically.
4. When gameplay begins, your realm preset activates (the app confirms with
   "confirmed by ReShade"). Unconfigured realms, unsure reads, and match end all use
   the **Global** preset — the app never guesses.
5. Manual override (Status tab) forces a realm for the current match and auto-clears
   afterwards. Test any preset from the Presets tab (**DBD must be running and focused**).

## How ReShade switching works
The app writes per-preset shortcuts (`Ctrl+Alt+Shift+F13…`) into `[GENERAL]
PresetShortcutKeys/PresetShortcutPaths` of `ReShade.ini` (your own shortcut entries are
preserved; a `.dbdautoreshade.bak` backup is kept) and triggers them with normal
keystrokes. Verified against the ReShade configuration format; ReShade only loads these
shortcuts at game start, so **after changing preset assignments, restart DBD once**
(the app tells you when).

## Settings
- Detection hotkey (F1–F24, A–Z, 0–9 + modifiers), DBD/ReShade paths, Debug Mode
  (saves the failing OCR crop locally, max 20 files, failures only — otherwise the
  pipeline is fully in-memory), auto-restore toggle, optional database-update manifest
  URL (HTTPS; empty = disabled), Reset Settings (config only — your presets are safe).

## Privacy
Local-only by design: captures, OCR text, and decisions never leave the machine.
No telemetry, analytics, or crash uploads.

## Troubleshooting
- **Map detected but preset never switches** — check the status line under the tabs:
  - *"DBD must be focused"* → click into the game; the app now keeps retrying for ~60 s.
  - *"No ReShade shortcut is assigned"* → open Settings and make sure `ReShade.ini` is
    found, then assign the preset again.
  - *"Restart Dead by Daylight so ReShade loads them"* → ReShade only loads shortcuts
    at game start; restart DBD once after assigning presets.
  - Press **Test** on the realm's preset while DBD is focused: if Test works but auto
    switching doesn't, it was timing/focus; if Test fails too, work through the points
    above. If the app gets stuck, just press F8 again — a fresh attempt always starts.
  - Run the game and the app at the same privilege level (both normally, not as admin);
    otherwise Windows blocks the preset keystrokes.
- **"Local OCR unavailable"** — install the English OCR pack:
  Settings → Time & language → Language → English → Options → OCR.
- **"No confident map detected"** — press F8 while the map name is clearly visible;
  enable Debug Mode to inspect the exact crop under `%AppData%\DBDAutoReShade\debug-crops`.
- **Test/activation does nothing** — DBD must be running *and focused*; after editing
  preset assignments, restart DBD once; check ReShade is installed (`dxgi.dll` +
  `ReShade.ini` next to the game).
- **Wrong realm once** — assign the right preset under Presets; repeated misses mean the
  loading-screen regions need tuning (`DbdLoadingScreenRegions`) — please share the
  Debug crop description (never any game-memory tooling).
- **DBD on a second monitor / ultrawide / 4K / scaled** — supported via DPI-aware
  client-area capture; no manual coordinates needed.

## Map database
Built-in v1 (20 realms, verified against the official DBD wiki). Updates are optional:
set an HTTPS manifest URL in Settings (`{schemaVersion:1, databaseVersion:N,
downloadUrl:"https://…", sha256:"…"}`); downloads are hash- and schema-checked,
installed atomically, and rolled back to last-known-good on any failure.

## Build from source
- Requires .NET 10 SDK on Windows.
- `dotnet build DBDAutoReShade.slnx -c Release`
- `dotnet test tests/DBDAutoReShade.Tests/DBDAutoReShade.Tests.csproj` (139 tests)
- Portable: `dotnet publish src/DBDAutoReShade.App -c Release -r win-x64
  --self-contained true -p:PublishSingleFile=true -o publish/DBDAutoReShade`

## Safety boundary (non-negotiable)
This app will never read/write game memory, inject, hook, patch, bypass anti-cheat,
use drivers, sniff traffic, attach debuggers, or use cloud OCR. If a feature idea
conflicts with that, it is out of scope.
