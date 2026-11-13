<p align="center">
  <img src="Assets/layts-256.png" width="96" alt="layts logo">
</p>

<h1 align="center">layts</h1>

<p align="center">Save game editor for Windows – clean, dark and simple.</p>

---

## Features

**General**
- English (default) and German – switch instantly in Settings, no restart needed
- Settings: language, automatic update check, backup folder

**Library**
- Game overview with cover, hero image and logo (taken from your local Steam cache)
- Favorites, search and filter

**Euro Truck Simulator 2**
- Finds profiles automatically (local, `steam_profiles` and Steam Cloud)
- Money, XP and level (level table taken from the game data)
- Skills including ADR classes
- Repair and refuel all trucks, buses and trailers
- Mark all cities as visited, buy garages or upgrade them to "large"
- **Coaches DLC** (detected per save): bus XP, bus level, bus skills (rank 0–3), discover all bus stops
- Automatic backup before every save, one-click restore

Reads encrypted, binary (BSII) and text saves. Everything you don't edit stays byte-for-byte identical.

> ⚠️ Single player only. Do not use with TruckersMP or Convoy.
> Close the game before saving, then open the save in-game via "Load".

## Install & updates

Download **[`Layts-win-Setup.exe` from the releases](https://github.com/Oelweb/Layts/releases/latest)** and run it.
The installer sets up layts for your user (no admin rights needed), creates Start menu and desktop shortcuts
and registers itself under *Settings → Apps* for uninstalling.

The installed version checks for updates on startup. When one is available, **"Update available"** appears
in the bottom left – click *Update now* to download it and restart layts.
You can also check manually via **"Check for updates"**.

## Creating a release

```powershell
.\release.ps1 -Version 1.2.0           # build locally only (folder .\releases)
.\release.ps1 -Version 1.2.0 -Upload   # build and publish as a GitHub release
```

The script sets the version, builds the app and uses [Velopack](https://velopack.io) to create the installer
and update packages. One-time requirement: `dotnet tool install -g vpk`.

## Building

Requirement: [.NET 10 SDK](https://dotnet.microsoft.com/download)

```
dotnet publish -c Release -o publish
```

Result: `publish/Layts.exe` – a single file that runs without .NET installed.

## Structure

| Folder | Contents |
|---|---|
| `Core/Sii` | Decrypting, reading and writing SII files (binary + text) |
| `Core` | Animations, Steam images, updater, helpers |
| `Core/Strings.cs` | All texts in English and German – new language = new column |
| `Games/GameCatalog.cs` | List of games – a new game = new entry + folder under `Games/` |
| `Games/Ets2` | ETS2 editor (logic, backups, UI) |
| `Themes/Dark.xaml` | Design (colors, controls, animations) |
| `Views`, `ViewModels` | Library, settings and main window |

Backups are stored in `%LOCALAPPDATA%\Layts\Backups`.
