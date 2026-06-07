# KobraCache

KobraCache is a Windows desktop utility for clearing old Anycubic Kobra S1 print files without subnet scanning.

## Current Scope

- Manual printer IP entry.
- Anycubic Slicer Next LAN import from `%APPDATA%\AnycubicSlicerNext\AnycubicSlicerNext.conf`.
- Anycubic Slicer Next cloud import after explicit opt-in.
- Separate cleanup targets for local printer cache, USB storage, and cloud files.
- Local cache selected by default, with USB and Cloud opt-in per view.
- View-before-delete and a second confirmation before deletion.
- Manual file selection with a `Select All` action for loaded deletable files.
- Delete blocking unless the selected printer is confirmed idle.
- Current or active print files are never eligible.
- Startup and runtime logs under `%LOCALAPPDATA%\KobraCache\Logs`.
- Kobra line-art logo as the window icon, in-app logo, tray icon, and executable icon.

## Important Limits

Manual IP entry does not provide delete credentials by itself. A manually entered printer stays in probe-only mode until matching LAN or cloud credentials are imported from Slicer.

KobraCache does not persist Anycubic cloud tokens. Cloud tokens are read from Slicer only during the explicit import action and kept in memory for the current app session.

Cloud REST and MQTT paths are implemented from public behavior references and should be validated against a live logged-in Slicer account before using deletion broadly.

Printer IPs are identifiers, not credentials. If a printer is only added by IP, KobraCache cannot list or delete files from it. Import Slicer Cloud or Slicer LAN credentials, then use the imported or matched printer row and click `View Files`.

Cloud-mode printer-local cache and USB listing use Anycubic cloud MQTT after a Slicer Cloud import. KobraCache lists cloud account files through the REST API, and it lists/deletes printer-local cache or USB files through LAN MQTT when LAN credentials are available or through cloud MQTT when cloud printer command metadata is available.

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
