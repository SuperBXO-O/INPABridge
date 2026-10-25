# INPA Bridge

INPA Bridge is a Dalamud plugin that connects a locally hosted FF14 role-play front desk page with the game.

It provides a local HTTP service at `127.0.0.1:18500` for:

- reading the current target as `Name@World`
- sending chat messages
- reading inventory items
- placing trade items and gil
- reporting trade events
- showing a local front desk page inside the game
- storing shared front desk data on the user's computer

Actions are initiated from the page or the plugin interface. The plugin does not perform background trading or messaging.

## Author

**SuperBXO-O**

Repository: https://github.com/SuperBXO-O/INPABridge

## Requirements

- Dalamud API Level 15
- .NET 10
- Final Fantasy XIV 7.5

An incompatible Dalamud API level will prevent the plugin from loading.

## Installation

1. Open `/xlsettings` in game.
2. Go to **Experimental** and open **Custom Plugin Repositories**.
3. Add this URL:

```text
https://raw.githubusercontent.com/SuperBXO-O/INPABridge/main/repo.json
```

4. Open `/xlplugins`.
5. Search for `INPA Bridge` and install it.

## Commands

| Command | Action |
|---|---|
| `/inpa` | Show or hide the in-game page window |
| `/inpalock` | Toggle window position locking |
| `/inpareload` | Reload the embedded page |
| `/inpacal` | Open the input-overlay calibration tab |

## Input overlay

The plugin provides a native ImGui input overlay for web fields that cannot receive IME text inside the in-game browser.

Starting with `1.7.89`, new installations automatically scale the overlay from:

- the current game window resolution;
- the Dalamud interface scale.

For example, a 3840×2160 display at 150% UI scale resolves to:

```text
overlay scale: 1.500
font scale: 1.20
```

Existing `field-align.txt` files keep their manually calibrated values. Automatic scaling can be enabled or disabled from the plugin settings.

## Page discovery

The plugin does not bundle a front desk page. It looks for a user-provided page in this order:

1. A page file or directory written in `page.txt` beside the plugin.
2. `app.html` or `index.html` in the current web-data directory, including the localized upload-copy folder.
3. `app.html` or `index.html` beside the plugin.
4. `www/app.html` or `www/index.html` beside the plugin.
5. `www/{profile}/app.html` or `www/{profile}/index.html` beside the plugin.

The profile can be `generic`, `inpa`, or `princeton`. Programs, file paths, and plugin interface labels remain in Chinese for existing users.

## Build from source

```bash
git clone https://github.com/SuperBXO-O/INPABridge.git
cd INPABridge
dotnet build -c Release
```

Build output is written to `bin/Release/`:

- `INPABridge.dll`
- `INPABridge.json`
- `INPABridge.deps.json`

Run the input-scaling unit tests with:

```bash
dotnet run --project tests/FieldScalePolicyTests/FieldScalePolicyTests.csproj -c Release
```

Run the room-alert profile tests with:

```bash
dotnet run --project tests/RoomAlertPolicyTests/RoomAlertPolicyTests.csproj -c Release
```

## Package layout

Versioned plugin archives contain:

```text
INPABridge.dll
INPABridge.json
INPABridge.deps.json
field-theme.txt
LICENSE
NOTICE
```

## Release notes

### 1.7.91

- Added a local physical-modifier endpoint so Ctrl-click multi-select works inside the Browsingway in-game window even when CEF does not include `ctrlKey` on mouse events.
- Added the updated web-page bridge used by the latest Front Desk Helper release.

### 1.7.90

- Fixed room alerts reading the plugin's default data directory instead of the active web profile's shared data file.
- Added regression coverage for `inpa`, `princeton`, and `generic` room-alert profiles, including due alerts and duplicate suppression.
- Unified the ticket whitelist target picker with the target controls used elsewhere.

### 1.7.89

- Added automatic input-overlay sizing from display resolution and Dalamud UI scale.
- Preserved existing manual calibration values.
- Added the complete AGPL-3.0 license and attribution notice to the plugin archive.

### 1.7.88

- Fixed two-item trade placement by waiting for the previous quantity dialog and context menu to close before processing the next item.
- Synchronized the latest page, field-theme, profile-directory, and room-status behavior.

## License

This project is licensed under the GNU Affero General Public License v3.0. See [LICENSE](LICENSE).

The plugin includes work derived from Dalamud, which is also licensed under AGPL-3.0.
