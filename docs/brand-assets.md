# Dark Obsidian Fluent Reverse

The user-supplied artwork is copied, without changing the originals, to:

| Source filename | Repository asset | Purpose |
| --- | --- | --- |
| `evoflow-fluent-reverse.ico` | `native/EvoFlow/Assets/AppIcon.ico` | Native window/titlebar and compiled executable icon. |
| `evoflow-fluent-reverse.png` | `native/EvoFlow/Assets/AppIcon.png` | Original PNG, island branding and package image source. |
| `evoflow-borderless-glyph.png` | `native/EvoFlow/Assets/TrayGlyph.png` | Transparent artwork for the existing notification-area feature. |

The supplied ICO contains 16, 24, 32, 48, 64, 128 and 256 pixel images.
`scripts/prepare-brand-assets.py` reproducibly creates scale 100/125/150/200/400
package images, taskbar target-size variants and a multi-resolution `TrayIcon.ico`.
Images retain aspect ratio and alpha; rectangular package images center the
artwork without stretching or recoloring it. The tray uses its separate glyph.

The project embeds the supplied ICO through `ApplicationIcon`, includes all
image/ICO content in the delivered package, and resolves runtime icons relative
to `AppContext.BaseDirectory`. Main, Settings, Memory and Results windows set
their own native `AppWindow` icon. The existing package identity/AUMID is preserved;
there is no Python AppUserModelID workaround.

```powershell
.venv/Scripts/python.exe scripts/prepare-brand-assets.py
```

The [delivery report](BRANDING-MONITORING-VERIFICATION-2026-10-31.md) records original
and delivered hashes, native verification and screenshots. No broad Windows icon
cache deletion is performed.
