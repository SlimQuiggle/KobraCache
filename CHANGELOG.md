# Changelog

## 0.2.0 - 2026-06-06

- Fixed Slicer cloud import by exchanging the Slicer `access_token` for an Anycubic session token before calling printer/file APIs.
- Added `Preview Files` and `Remove Selected` actions beside the printer list.
- Changed manual IP status from false `Offline` probes to `Needs import` when no LAN/cloud credentials are available.
- Added app version metadata and visible version text.
- Improved preview messages when selected targets cannot be listed because credentials are missing.

## 0.1.0 - 2026-06-06

- Initial WPF app with manual IP entry, Slicer imports, retention preview, guarded delete flow, logging, and branding.
