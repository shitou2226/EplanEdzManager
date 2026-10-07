# Real Product Media

The PNG files in this directory are captures from the real `v1.0.0-beta.6` application. They are not mockups or AI-generated UI.

## Published captures

| File | What it demonstrates |
|---|---|
| `main-window.png` | Chinese main catalog, EDZ library list, normalized descriptions, part details and live local index counts. |
| `double-click-part-preview.png` | Double-click image preview using a real picture embedded in the source EDZ. The picture may be a 2D drawing or a vendor-provided 3D render. |
| `safe-import-zh.png` | Fully localized eight-step safe MDB import wizard before a target database is selected. No import was executed for the screenshot. |
| `my-library-source-status.png` | My Library records retained with an explicit warning when their original EDZ source is no longer available. |

## Still optional

| File | Suggested content |
|---|---|
| `demo-search.gif` | A 10–20 second real workflow: search, select a result, double-click preview, and add it to My Library. |
| `selected-export.png` | Export Selected EDZ confirmation/result with private paths excluded. |
| `eplan-context.png` | Connected EPLAN context without customer or project identifiers. |
| `diagnostics.png` | Environment diagnostics with private paths and logs excluded. |

## Capture rules

- Use a consistent application-window size and Windows scaling across all stills; `1600×900` or `1920×1080` at 100% scaling is preferred.
- Capture only the application window, not the whole desktop.
- Use a non-sensitive demo library. Do not show a Windows username, home directory, customer/project name, private network path, license information, complete diagnostic log, token, password, or API key.
- Keep the exact verified release version visible where practical. Do not combine screens from different builds without labeling them.
- Use PNG for still images. Crop empty borders but do not remove warnings, errors, limitations, or other real UI state.
- Do not use the EPLAN official logo or design the image to look like an official EPLAN product.

The main English and Chinese READMEs already reference the four published PNG files. Add a GIF link only after a real recording has been captured and reviewed.

For a broader release/DPI checklist, also see [Screenshot Plan](../SCREENSHOT_PLAN.md).
