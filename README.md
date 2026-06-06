# KobraCache

KobraCache is a Windows desktop utility for clearing old Anycubic Kobra S1 print files without subnet scanning.

## Current Scope

- Manual printer IP entry.
- Anycubic Slicer Next LAN import from `%APPDATA%\AnycubicSlicerNext\AnycubicSlicerNext.conf`.
- Anycubic Slicer Next cloud import after explicit opt-in.
- Separate cleanup targets for local printer cache, USB storage, and cloud files.
- Retention presets for 30, 60, 90, or custom cutoff dates.
- Preview-before-delete and a second confirmation before deletion.
- Delete blocking unless the selected printer is confirmed idle.
- Current or active print files are never eligible.
- Files without a reliable date are excluded from automatic retention selection and must be selected manually.

## Important Limits

Manual IP entry does not provide delete credentials by itself. A manually entered printer stays in probe-only mode until matching LAN or cloud credentials are imported from Slicer.

KobraCache does not persist Anycubic cloud tokens. Cloud tokens are read from Slicer only during the explicit import action and kept in memory for the current app session.

Cloud API paths are implemented from public behavior references and should be validated against a live logged-in Slicer account before using cloud deletion broadly.

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
