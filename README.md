# KobraCache

KobraCache is a Windows desktop utility that gives Anycubic Kobra owners an easier way to clear old G-code files from printer local storage, USB storage, and cloud files instead of deleting each file from the printer screen or resorting to a device reset.

## Current Scope

- Anycubic Slicer Next LAN import from `%APPDATA%\AnycubicSlicerNext\AnycubicSlicerNext.conf`.
- Anycubic Slicer Next cloud import from the `Import Cloud Printers` action.
- Separate cleanup targets for local printer cache, USB storage, and cloud files.
- Local cache selected by default, with USB and Cloud opt-in per view.
- View-before-delete and a second confirmation before deletion.
- Manual file selection with a `Select All` / `Clear All` toggle for loaded deletable files.
- GitHub release update checks and self-update from the latest Windows release zip.
- Delete blocking unless the selected printer is confirmed idle.
- Current or active print files are never eligible.
- Startup and runtime logs under `%LOCALAPPDATA%\KobraCache\Logs`.
- Kobra line-art logo as the window icon, in-app logo, tray icon, and executable icon.

## Printer Compatibility

KobraCache is confirmed working on the Anycubic Kobra S1. It is expected to work on other newer Anycubic Kobra printers that expose the same Slicer Next / Anycubic Cloud file-manager protocol, but those models have not all been personally tested.

Likely candidates, still marked untested, include Kobra S1 Max, Kobra 3, Kobra 3 V2, Kobra 3 Max, and possibly Kobra 2 Pro units with compatible firmware. Kobra X / K4 software-base printers, older Kobra/Kobra 2 variants, and Photon/resin printers are not confirmed.

See [Printer Compatibility](docs/COMPATIBILITY.md) for the current compatibility matrix and reporting notes.

## Important Limits

Printers must be imported from Anycubic Slicer Next. IP-only printer entry is not supported because an IP address does not provide the LAN MQTT or cloud command credentials needed to list and delete files.

KobraCache does not persist Anycubic cloud tokens. Cloud tokens are read from Slicer only during the explicit import action and kept in memory for the current app session.

Cloud REST and MQTT paths are implemented from public behavior references and should be validated against a live logged-in Slicer account before using deletion broadly.

Printer IPs are identifiers, not credentials. Use `Import Slicer LAN` or `Import Cloud Printers`, then select the imported printer row and click `View Files`.

LAN MQTT accepts local printer TLS certificates because Anycubic LAN brokers can use certificates that do not chain to a public certificate authority. The LAN path still requires credentials imported from Slicer.

LAN file commands are published to the Anycubic MQTT `file` command topic with `listLocal`, `listUdisk`, `deleteLocal`, or `deleteUdisk` in the message payload. Read-only LAN file listing also tries the alternate `server/printer` topic family used by some older firmware.

LAN status checks wait for actual `info/report` status payloads and ignore ack-only MQTT packets, because deletion stays blocked until KobraCache can confirm the printer is idle.

Cloud-mode printer-local cache and USB listing use Anycubic cloud MQTT after a Slicer Cloud import. KobraCache lists cloud account files through the REST API, and it lists/deletes printer-local cache or USB files through LAN MQTT when LAN credentials are available or through cloud MQTT when cloud printer command metadata is available.

Self-update downloads the latest public GitHub release asset matching `KobraCache-*-win-x64.zip`. The app stages the update under `%LOCALAPPDATA%\KobraCache\Updates`, closes, copies the new files over the current app folder, then relaunches. If the current app folder is not writable, the update will fail and details are written to `%LOCALAPPDATA%\KobraCache\Logs\updater.log`.

## Logs

KobraCache writes diagnostic logs to:

```text
%LOCALAPPDATA%\KobraCache\Logs
```

The app has an `Open Logs` button and the tray icon has an `Open Logs` menu item. Logs include startup, window initialization, major UI actions, caught UI errors, and unhandled exceptions. Slicer cloud tokens are redacted and are not persisted by the app.

## Icon Assets

The source logo is `src\KobraCache.Desktop\Assets\KobraCacheLogo.svg`. Regenerate the PNG and ICO with Python and Pillow:

```powershell
python tools\generate_logo_assets.py
```

## Build

```powershell
.\.dotnet\dotnet.exe build KobraCache.sln -v:minimal
```

## Test

```powershell
.\.dotnet\dotnet.exe test KobraCache.sln -v:minimal
```

## Publish

```powershell
.\.dotnet\dotnet.exe publish src\KobraCache.Desktop\KobraCache.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\KobraCache-win-x64
```
