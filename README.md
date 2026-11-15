<p align="center">
  <img src="Assets/layts-256.png" width="96" alt="layts logo">
</p>

<h1 align="center">layts</h1>

<p align="center">Save game editor for Windows – clean, dark and simple.</p>

---

## Features

**General**
- English (default) and German – switch instantly in Settings, no restart needed
- Settings: language, automatic update check, backups
- Backups: turn them off completely or per game, back up at most every 5 / 15 / 60 minutes, keep the last 3 / 5 / 10 / 25 per save, delete all, "Back up now" in every editor

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

**Scam With Your Friends** (Early Access)
- Money for every player in the run
- Continue the run from any day, change the shift length
- Quota: starting quota, per extra player, daily increase – with "Easy" and "Default" presets
- Unlock or lock all scams and apps from the KolkataOS store (list is read from your game install, so it stays up to date)
- Unlock all emotes, Mr. Curry suit
- Game settings: FOV, FPS limit, mouse sensitivity, volumes, toggle sprint, hide UI
- Automatic backup of all slots and settings before every save, one-click restore

**Trees Hate You** (Demo)
- Level reached (unlock all levels) and checkpoint
- Unlock Challenge Mode, turn a save into a Challenge run
- Coins, acorns, "enough acorns found"
- Hats: found / not found / lost, worn hat
- Skills: locked / unlocked / active
- Eye colour
- Times and deaths are never touched, so speedrun times stay fair
- Automatic backup before every save, one-click restore

**The Bus** (career mode)
- Company budget and reputation
- Staff: experience and satisfaction per employee, max all / everyone happy
- Repair and clean all buses
- Driving aids (engine, gears, lights, doors, A/C, kneeling, wipers, breaks), autosave interval, depot start, AI buses, live events and more
- Traffic, pedestrian and passenger density, ticket buy chance, how often passengers complain
- Volumes, field of view, camera sensitivity, number plates per bus model
- Unreal saves are changed in place – everything else stays byte-for-byte identical

> ⚠️ Single player and private sessions only. Do not use with TruckersMP, Convoy or in public lobbies.
> Close the game before saving.

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
| `Games/Swyf` | Scam With Your Friends editor |
| `Games/Thy` | Trees Hate You editor |
| `Games/Bus` | The Bus editor (Unreal save reader) |
| `Themes/Dark.xaml` | Design (colors, controls, animations) |
| `Views`, `ViewModels` | Library, settings and main window |

Backups are stored in `%LOCALAPPDATA%\Layts\Backups`. By default layts backs up at most every 15 minutes and keeps the last 10 per save.
