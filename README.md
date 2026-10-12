> # ⚠️ WARNING: This is a vibe-coded app
>
> **This project was designed and written with AI assistance (vibe-coded).**
> It builds cleanly with 722/722 automated tests passing, but it has not been
> validated live against the real game by its author — review the code yourself
> before running it, and use it at your own risk.

# DBD Auto ReShade

Portable Windows utility for **Dead by Daylight + ReShade**: press a global hotkey on the
loading screen and the app reads the map/realm text with **local OCR**, looks up the realm
in its built-in database, and switches to your per-realm ReShade preset as soon as it has
read the map — usually while the loading screen is still up. It returns to your
Global/Menu preset when the match ends.

No screenshots, templates, or training data needed — the app captures the live loading
screen itself. No memory reading, injection, hooks, drivers, or network snooping:
fully external, anti-cheat safe (process enumeration with query-only access, screen
capture, window focus checks, OCR, files, hotkeys and keystrokes only). Everything runs
**offline**; nothing is ever uploaded.

## Install
1. Extract `DBDAutoReShade-portable-win-x64.zip` anywhere.
2. Run `DBDAutoReShade.exe` (normal user, no admin). Only one copy runs at a time;
   starting it again just says "DBD Auto ReShade is already running."
3. Complete the first-run wizard: DBD path (auto-detected if possible; the game exe or
   the launcher `DeadByDaylight.exe` both work), ReShade.ini, Global preset, hotkey
   (default **F8**). Realm presets are optional.

## Normal use
1. Launch DBD Auto ReShade, then Dead by Daylight.
2. Status shows `DBD Status: Running` / `Ready — Press F8`.
3. While the match loads, press **F8** to start scanning. The app OCRs the map and realm
   lines in the lower-left corner of the loading screen about once a second until it
   confidently reads the map — however long the loading takes. The scan reads the
   loading screen while Dead by Daylight is the active window, or while nothing covers
   the map/realm text along the bottom-left half of it (e.g. DBD on one monitor while you
   use another). If a window
   covers it, the scan pauses and resumes when you switch back to the game (or move
   that window away). Press **F8 again** to stop scanning; a confident read stops it
   automatically.
4. As soon as the map is read, the app switches to that realm's preset — DBD must be the
   active window for that. If you are in another window, the status says
   "Waiting for DBD focus to switch to …" and the switch happens when you click into
   the game. The status then says "confirmed by ReShade" (or "already active in ReShade").
5. The **Global** preset covers everything else — the app never guesses:
   - a realm without its own preset;
   - a scan you stop, or one that ends without a confident read (unless ReShade is
     known to be on Global already);
   - match end, when "Restore Global preset when the match ends" is on and the app
     tracked the match after an F8 detection (it notices the match end only while DBD
     is the active window);
   - DBD exit (same setting): Global is written into ReShade.ini as ReShade's startup
     preset, so the next launch starts with it. If you picked another preset in the
     ReShade overlay after Global was confirmed active, your pick is kept.

   Without a Global preset these cases leave the current preset alone.
6. **Manual override** (Status tab) forces a realm's preset (Global if it has none) and
   waits up to 60 s for you to click into DBD. It stays until **Return to Automatic**
   (which switches to Global), a new F8 scan or DBD exit; it also clears at the match end
   when the app is tracking the match and auto-restore is on. **Test** any preset from
   the Presets tab: DBD must be running, then you have 15 s to click into the game.

## How ReShade switching works
The app writes one shortcut per assigned preset (`Ctrl+Alt+Shift+F13…F24`, then
`Ctrl+Alt+Shift+0…9`, 22 at most) into `[GENERAL] PresetShortcutKeys/PresetShortcutPaths`
of `ReShade.ini` and triggers them with normal keystrokes. Shortcuts are **stable**:
assigning or clearing one preset doesn't move any other preset's shortcut (a shortcut only
moves if one of your own ReShade shortcuts later takes its key). Your own shortcut
entries are kept exactly as they are — also one that points at a preset file you assign
in the app — and their keys are never reused; the rest of the file (spacing, line
endings) is left untouched. A `.dbdautoreshade.bak` backup is made on the first change.
After each switch the app watches `PresetPath` in ReShade.ini for about 2 s to confirm
that ReShade really switched.

ReShade only loads these shortcuts at game start. If you assign or change a preset while
DBD runs, a banner names the presets that need a **DBD restart**; the app won't send their
keys until then (ReShade could switch to the wrong preset), and all other presets keep
switching. Restarting only DBD Auto ReShade doesn't clear this — restart the game.

Keep ReShade's **Force correct modifier keys** option on (ReShade's default). With it
off, a shortcut also fires while extra modifier keys are held, so the app's
`Ctrl+Alt+Shift+F13` would also trigger your own `F13` or `Ctrl+F13` shortcut. The app
avoids such keys when it assigns shortcuts; if the option is turned off while the game
runs, or no free key is left, the switch or Test fails right away with
"… would also trigger your ReShade shortcut … Turn that option on in ReShade's settings."
Turning it back on works at once, without a DBD restart.

## Settings
- **Detection hotkey**: F1–F24 alone or with Ctrl/Alt/Shift; letters, numbers and NumPad
  keys only together with Ctrl, Alt or Shift (alone they would block typing and game
  controls). The current hotkey is paused while you press the new one; Esc cancels. A new
  hotkey is kept only if Windows accepts it — otherwise the previous one stays.
- **Paths**: DBD and ReShade.ini (auto-detected; Browse to change).
- **Restore Global preset when the match ends** (on by default; also controls the
  startup-preset write on DBD exit).
- **Debug Mode**: when a scan ends without a confident read (failed or stopped), saves
  that scan's last map/realm crops (as OCR'd, plus the preprocessed versions) and a `.txt`
  with their OCR text to `%AppData%\DBDAutoReShade\debug-crops`. The newest 20 sets are
  kept. Paused moments (another window in front) are never saved. Off: everything stays
  in memory.
- **Map database update manifest URL**: must be an `https://` address; empty = disabled.
  An invalid URL is shown with its error and not saved.
- **ReShade Diagnostics**: Run checks / Copy — verifies ReShade.ini, the hook DLL, every
  stored shortcut, the preset files, other ReShade.ini files, and the live state (DBD
  focused, privilege levels, shortcuts the running ReShade hasn't loaded).
- **Reset Settings**: removes this app's own shortcuts from ReShade.ini (yours stay),
  resets the settings to defaults, re-detects the paths and re-registers F8. Your preset
  files and the DBD and ReShade installs are never touched.

## Privacy
Local-only by design: captures, OCR text, and decisions never leave the machine.
No telemetry, analytics, or crash uploads.

## Troubleshooting
- **Map detected but preset never switches** — check the status line under the tabs:
  - *"Waiting for DBD focus to switch to …"* → click into the game. A detected realm waits
    without a time limit (until the match ends or something newer replaces it); an override or
    Return to Automatic waits 60 s, a Test 15 s, the Global switch after a match or a
    scan 10 min.
  - *"Restart Dead by Daylight so ReShade loads the shortcut for …"* → ReShade only loads
    shortcuts at game start; restart DBD (not just this app) after assigning presets.
  - *"ReShade.ini was not found at …"* / *"ReShade.ini is not set"* → set the right
    ReShade.ini under Settings, then restart DBD.
  - *"… would also trigger your ReShade shortcut …"* → turn ReShade's
    "Force correct modifier keys" option back on.
  - *"No ReShade shortcut is assigned"* → open Settings, make sure `ReShade.ini` is found,
    then assign the preset again.
  - *"Keys sent for …, but ReShade hasn't confirmed the switch"* → open the ReShade
    overlay (Home key) to check the active preset; if it didn't change, restart DBD.
  - Press **Test** on the realm's preset: if Test works but auto switching doesn't, it
    was timing/focus; if Test fails too, work through the points above. If the app gets
    stuck, press F8 again — a fresh attempt always starts.
  - Run the game and the app at the same privilege level (both normally, not as admin);
    otherwise Windows blocks the preset keystrokes (the status and diagnostics say so).
- **"Scan paused — Dead by Daylight is not the active window."** → click into the game,
  or move the window that covers the map/realm text (bottom-left half of DBD). A minimized DBD, or one on
  another virtual desktop, can't be read. A very tall taskbar along the bottom of DBD's
  monitor (e.g. 1080p at 150 % scaling or more, or a two-row taskbar) counts as covering
  the realm line, so there the scan reads only while DBD is the active window.
- **"Local OCR unavailable"** — install the English OCR pack:
  Settings → Time & language → Language → English → Options → OCR. No app restart is
  needed: wait about 10 s and press F8 again.
- **"No confident map detected"** — press F8 while the map name is clearly visible;
  enable Debug Mode to inspect the exact crops under `%AppData%\DBDAutoReShade\debug-crops`.
- **Test/activation does nothing** — DBD must be running *and focused*; after editing
  preset assignments, restart DBD once; check ReShade is installed (`dxgi.dll` +
  `ReShade.ini` next to the game, `DeadByDaylight\Binaries\Win64`).
- **Wrong realm once** — assign the right preset under Presets; repeated misses mean the
  loading-screen regions need tuning (`DbdLoadingScreenRegions`) — please share the
  Debug Mode crops and their `.txt` (never any game-memory tooling).
- **DBD on a second monitor / ultrawide / 4K / scaled** — supported via DPI-aware
  client-area capture; no manual coordinates needed.
- **A warning about a corrupt configuration at startup** — the broken `config.json` was
  moved aside (the message says where) and defaults were loaded, so the setup wizard runs
  again.

## Map database
Built-in v1 (20 realms, 44 maps, verified against the official DBD wiki). Updates are
optional: set an HTTPS manifest URL in Settings (`{schemaVersion:1, databaseVersion:N,
downloadUrl:"https://…", sha256:"…"}`). The check runs at startup; downloads (5 MB max)
are hash-, schema- and version-checked (never a downgrade or a re-download of the same
version), installed atomically, and rolled back to last-known-good on any failure. A bad
or older downloaded database falls back to the built-in one.

## Build from source
- Requires .NET 10 SDK on Windows.
- `dotnet build DBDAutoReShade.slnx -c Release`
- `dotnet test tests/DBDAutoReShade.Tests/DBDAutoReShade.Tests.csproj -c Release`
  (722 tests; no game needed — only a few hotkey tests briefly register `Ctrl+Alt+F21/F22`)
- Portable: `dotnet publish src/DBDAutoReShade.App -c Release -r win-x64
  --self-contained true -p:PublishSingleFile=true -o publish/DBDAutoReShade`

## Safety boundary (non-negotiable)
This app will never read/write game memory, inject, hook, patch, bypass anti-cheat,
use drivers, sniff traffic, attach debuggers, or use cloud OCR. If a feature idea
conflicts with that, it is out of scope.
