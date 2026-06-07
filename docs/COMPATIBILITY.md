# Printer Compatibility

KobraCache is built for Anycubic printers that expose the same Slicer Next / Anycubic Cloud printer file-management protocol used by the Kobra S1.

The short version: Kobra S1 is confirmed. The Kobra S1 Max and Kobra 3 family look like the best candidates because they share the newer Kobra OS / GoKlipper-style ecosystem and Anycubic Slicer Next / cloud workflow, but they should remain marked untested until users verify file listing and deletion on real printers.

## Confirmed

| Model | Status | Notes |
| --- | --- | --- |
| Anycubic Kobra S1 | Confirmed working | Tested with Slicer Cloud import, cloud status, local cache file listing, USB file listing, and guarded delete flow. |
| Anycubic Kobra S1 Combo | Expected same as Kobra S1 | The app talks to the printer file manager, not the ACE accessory directly. Treat as confirmed only after testing on your unit. |

## Expected But Not Yet Personally Tested

These models are likely candidates because public Kobra firmware documentation groups them with the same newer Anycubic Kobra / GoKlipper-style software family, and their manuals or product pages reference AC Cloud / Anycubic Slicer Next connectivity.

| Model | Expected status | What should work if the protocol matches |
| --- | --- | --- |
| Anycubic Kobra S1 Max / S1 Max Combo | Likely compatible, untested | Slicer Cloud import, status, local cache, USB, and delete commands. |
| Anycubic Kobra 3 / Kobra 3 Combo | Likely compatible, untested | Slicer Cloud import, status, local cache, USB, and delete commands. |
| Anycubic Kobra 3 V2 / Kobra 3 V2 Combo | Likely compatible, untested | Same as Kobra 3 if exposed by Slicer Next and Anycubic Cloud. |
| Anycubic Kobra 3 Max / Kobra 3 Max Combo | Likely compatible, untested | Same as Kobra 3 if exposed by Slicer Next and Anycubic Cloud. |
| Anycubic Kobra 2 Pro | Possible, untested | Public firmware docs group some Kobra 2 Pro units with the newer Kobra family, but hardware/firmware variation is higher. Treat as experimental. |

## Not Confirmed / Not Targeted

| Model family | Status | Notes |
| --- | --- | --- |
| Anycubic Kobra X / K4 software-base printers | Not expected yet | Public firmware documentation describes Kobra X as a different KlipperC++ / K4 software base. KobraCache has not validated its cloud file-manager behavior. |
| Older Kobra, Kobra 2, Kobra 2 Neo, Kobra 2 Plus, Kobra 2 Max | Unknown | These may not expose the same Slicer Next cloud MQTT file-manager commands. |
| Photon / resin printers | Not targeted | KobraCache is designed around FDM printer cache/USB G-code file workflows, not resin printer storage workflows. |

## Compatibility Requirements

A printer must meet these requirements for full cleanup support:

- It appears in Anycubic Slicer Next Cloud import for the signed-in user.
- The cloud printer record includes a printer id, key, and machine type.
- The printer responds to Anycubic file-manager commands for `listLocal`, `listUdisk`, `deleteLocal`, and `deleteUdisk`.
- KobraCache can confirm the printer is idle before deletion.

Manual IP entry alone is not enough to list or delete files. Manual IP rows need matching Slicer LAN credentials or matching Slicer Cloud metadata.

## Reference Basis

KobraCache does not copy code from these projects. They are used only as public behavior and compatibility references:

- [Rinkhals documentation](https://jbatonnet.github.io/Rinkhals/) lists the newer supported Kobra / GoKlipper family as Kobra 2 Pro, Kobra 3, Kobra 3 V2, Kobra 3 Max, Kobra S1, and Kobra S1 Max, with Kobra X listed separately as unsupported.
- [Rinkhals Kobra printer notes](https://jbatonnet.github.io/Rinkhals/printers/) separately group the Kobra 2 Pro, Kobra 3 series, Kobra S1, and Kobra S1 Max under the same GoKlipper / K3 family.
- [Anycubic Cloud MCP](https://pypi.org/project/anycubic-cloud-mcp/) documents Slicer Next token extraction, Anycubic cloud REST/MQTT behavior, printer list/status, and cloud file operations for the Anycubic cloud ecosystem.
- Anycubic product pages for [Kobra S1](https://store.anycubic.com/products/kobra-s1), [Kobra S1 Max](https://store.anycubic.com/products/kobra-s1-max), and [Kobra 3 Max](https://store.anycubic.com/products/kobra-3-max) document Anycubic Slicer Next / app or cloud-oriented connectivity for those current models.

## How To Report Compatibility

When testing another model, report:

- Exact model name.
- Firmware version.
- Whether the printer is in Cloud mode or LAN mode.
- Whether Slicer Cloud import finds it.
- Whether `View Files` returns Local cache and/or USB files.
- Whether deletion was tested on an idle printer with a safe old file.

Do not share Slicer tokens, MQTT passwords, or full logs that include private account data.
