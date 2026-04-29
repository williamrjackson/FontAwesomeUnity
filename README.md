# Font Awesome Icon Browser

This package adds a Unity editor workflow for browsing Font Awesome icons and inserting them into TextMeshPro components.

![Banner](Documentation/images/OpenUpmCover.png) 

The package is built around a searchable icon browser window and a small runtime helper for duotone icons.

![Screenshot](Documentation/images/Screenshot.png) 

## Installation

### Via Unity Package Manager (Git URL)

Go to: `Window → Package Manager → + → Add package from Git URL`

Use:

    https://github.com/williamrjackson/FontAwesomeUnity.git

Optionally pin to a specific version:

    https://github.com/williamrjackson/FontAwesomeUnity.git#v1.0.8

------------------------------------------------------------------------
### Via OpenUPM Scoped Registry

Go to: `Edit → Project Settings → Package Manager
`
- Add an OpenUPM package registry:  
   Name: `OpenUPM`   
   URL: `https://package.openupm.com`  
   Scope(s): `com.wrj`    
![Project Settings](Documentation/images/ProjSettings.png) 
- Go to: `Window → Package Manager`
- Select `OpenUPM` under `My Registries`
- Select the `Font Awesome Icon Browser` package and click `Install`    
![Package Manager](Documentation/images/PackageManager.png) 

## What it does

- Provides a searchable grid of Font Awesome icons from `icons.json`
- Lets you target an existing `TMP_Text` or create a new one
- Supports both `TextMeshProUGUI` and world-space `TextMeshPro`
- Automatically download and install the latest free licensed Font Awesome desktop package
- Automatically generates TextMeshPro compatible SDF assets from the installed OTFs
- Supports duotone icons by creating a secondary sync'd TMP layer`

---

![Example](Documentation/images/example.png) 

## Opening the browser

Open:

`Tools > Font Awesome Icon Browser...`

## Basic workflow

1. Open the icon browser.
2. Assign the desired Font Awesome `TMP_FontAsset`
3. Search for an icon by name.
4. Double-click the icon in the grid to apply it.

The browser will:

- reuse a pre-selected `TMP_Text` if one is selected
- otherwise create a new TMP object

New UI TextMeshPro objects are created with:

- `Auto Size` enabled
- `Font Size Max = 500`

## Inline tokens

`FontAwesomeInlineText` lets you embed Font Awesome icons inside ordinary TMP text that uses another primary font asset, such as `Liberation Sans SDF`.

Example:

```text
<b>Font Awesome</b>
<size=350><color=#FDF0D5>:fa-classic fa-regular fa-unity:</color></size>
<i>& Unity</i>
```
<img src="Documentation/images/InlineExample.png" alt="InlineExample" width="50%">

The component supports:

- simple tokens like `:fa-github:`
- selector-aware tokens like `:fa-duotone fa-light fa-axe:`
- automatic fallback font setup for discovered Font Awesome TMP assets
- automatic creation of duotone secondary layers for supported inline duotone icons


When you paste text containing `:fa-...:` into a TMP component in the editor, the package can automatically add `FontAwesomeInlineText` so the token starts rendering as an icon.

## Searching across Font Awesome font assets

The browser search can work in two modes:

- normal mode searches within the currently selected Font Awesome TMP font asset
- `Search Across All Font Awesome Styles/SDFs` searches across compatible Font Awesome TMP assets in the project

Broad search is useful when you want to find an icon in another family or style without manually switching the selected SDF first.

The browser search matches:

- icon name
- label
- Font Awesome `search.terms` metadata

## Browsing style variants in search results

When `Search Across All Font Awesome Styles/SDFs` is enabled, the browser groups matching variants into a single tile per icon instead of showing every style as a separate result.

If an icon has multiple matching variants, the tile shows left/right buttons so you can page through the available families and styles on demand.


<img src="Documentation/images/StyleVariants.gif" alt="StyleVariants" width="35%">

The active variant controls:

- the preview shown in the grid
- `Copy Inline Token`
- double-click placement from the browser

This makes it easy to explore combinations like:

- `classic`
- `duotone`
- `sharp`
- `sharp-duotone`

## Metadata setup

The browser reads icon definitions from a Font Awesome `icons.json` file.

By default it can:

- auto-find a matching `icons.json` in the project
- Persist the path
- Allow you to override the path manually (useful if you have Font Awesome in a custom asset directory or want to point to a Pro package).

## Installing Font Awesome

If Font Awesome content is not found in the project Assets folder, this utility can automatically identify and download the latest Font Awesome Free release. If it fails to find the latest it will fall back to `https://use.fontawesome.com/releases/v7.2.0/fontawesome-free-7.2.0-desktop.zip`, which is the latest at time of release.

The installation:

- downloads the zip file to a temp location
- extracts only the required/useful files into `Assets/Fonts/fontawesome-free-{version}-desktop`
- cleans up temp files when done
- generates TextMeshPro SDF assets from the installed files

Installed package content is intentionally trimmed to:

- `metadata/`
- `otfs/`
- `LICENSE.txt`

## Duotone icons

When the selected font asset is detected as a duotone font, the browser manages a layered glyph behavior automatically behind the scenes.

For duotone icons it:

- previews the icon in the grid using overlaid glyphs
- creates or reuses a secondary TMP child named `FA Secondary Layer`
- adds `FontAwesomeDuotoneSync` to the primary object

### Duotone sync behavior

`FontAwesomeDuotoneSync` keeps the secondary layer aligned with the primary by syncing settings such as:

- font and material
- font size
- alignment
- spacing and wrapping
- UI rect sizing

Color sync behavior is slightly smarter:

- the secondary RGB follows the primary RGB by default
- the secondary alpha remains dimmer than the primary
- if you explicitly change the secondary RGB so it no longer matches, RGB syncing stops and your custom secondary color is preserved

![Duotone Color Sync](Documentation/images/\DuoToneColorMgmt.gif)

## Package contents

| Path | Purpose |
|---|---|
| `Editor/FontAwesomeIconBrowserWindow.cs` | The editor browser window and install/setup workflow. |
| `Runtime/FontAwesomeDuotoneSync.cs` | Runtime/edit-mode helper that keeps duotone secondary layers synced to the primary text object. |
| `Runtime/FontAwesomeInlineText.cs` | Runtime/editor helper that turns inline `:fa-...:` tokens in TMP text into Font Awesome icons, including style-aware font selection and duotone support. |

## Typical usage examples

### Single icon

- Select an existing `TextMeshProUGUI`
- Pick a Font Awesome font asset
- Double-click an icon

The selected TMP object is updated in place.

### New UI icon

- Select a GameObject within a UI `Canvas` hierarchy
- Double-click an icon

The browser creates a new `TextMeshProUGUI` object with the icon already assigned.

### Duotone icon

- Pick a duotone Font Awesome TMP font asset
- Double-click a duotone-capable icon

The browser creates:

- a primary TMP object
- a secondary child layer
- a `FontAwesomeDuotoneSync` component on the primary
