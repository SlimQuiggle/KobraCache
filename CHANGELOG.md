# Changelog

## 0.9.0 - 2026-06-08

- Fixed LAN MQTT connections rejecting local printer TLS certificates during file listing.
- Added a test that verifies LAN MQTT uses the local-printer TLS certificate policy.

## 0.8.0 - 2026-06-08

- Moved the step 3 marker beside the `View Files` button and changed it to orange.
- Removed the `Mode` column from the printer list.

## 0.7.0 - 2026-06-08

- Removed the separate cloud-token import checkbox; clicking `Import Cloud Printers` now performs the Slicer cloud import directly.
- Added numbered workflow labels for import, printer selection, and file viewing.
- Added custom line-drawing icons and distinct colors for the Slicer LAN and Cloud import buttons.

## 0.6.0 - 2026-06-08

- Removed manual IP-only printer entry because IP addresses do not provide file-list or delete credentials.
- Added printer compatibility documentation for confirmed, likely, and unknown Anycubic model families.
- Refined the project description to focus on clearing old G-code files from printer storage.

## 0.5.0 - 2026-06-07

- Added `Check for Updates` in the app header.
- Added GitHub release lookup against `SlimQuiggle/KobraCache`.
- Added a self-update flow that downloads the Windows release zip, verifies GitHub's SHA256 digest when provided, stages the update, closes KobraCache, replaces app files, and relaunches.
- Added tests for GitHub release parsing, version comparison, and Windows asset selection.

## 0.4.1 - 2026-06-07

- Changed `Select All` into a toggle that clears all selectable files when everything is already selected.
- Replaced the file grid checkbox column so delete boxes check and uncheck with a single click.

## 0.4.0 - 2026-06-07

- Added a `Created by Flex3Designs` credit in the bottom-right footer.
- Removed retention/date controls from the desktop workflow.
- Changed file loading actions from `Preview` / `Preview Files` to prominent `View Files` buttons.
- Added `Select All` for loaded deletable files.
- Changed storage defaults to Local cache on, USB and Cloud off.
- Refreshed the color palette and action button styling.

## 0.3.0 - 2026-06-06

- Fixed cloud status mapping so Anycubic `available=1` / `is_printing=1` reports as idle instead of busy.
- Added cloud MQTT local cache and USB file listing for Slicer cloud-imported printers.
- Added cloud MQTT local cache and USB deletion routing for cloud-imported printers.
- Imported cloud printer rows now keep the Anycubic `machine_type` and printer key needed for cloud MQTT topics.
- Added tests for cloud status mapping and cloud MQTT list/delete command routing.

## 0.2.0 - 2026-06-06

- Fixed Slicer cloud import by exchanging the Slicer `access_token` for an Anycubic session token before calling printer/file APIs.
- Added `Preview Files` and `Remove Selected` actions beside the printer list.
- Changed manual IP status from false `Offline` probes to `Needs import` when no LAN/cloud credentials are available.
- Added app version metadata and visible version text.
- Improved preview messages when selected targets cannot be listed because credentials are missing.

## 0.1.0 - 2026-06-06

- Initial WPF app with manual IP entry, Slicer imports, retention preview, guarded delete flow, logging, and branding.
