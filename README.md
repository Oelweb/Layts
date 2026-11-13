<p align="center">
  <img src="Assets/layts-256.png" width="96" alt="layts Logo">
</p>

<h1 align="center">layts</h1>

<p align="center">Spielstand-Editor für Windows – schlicht, dunkel, übersichtlich.</p>

---

## Funktionen

**Bibliothek**
- Spiele-Übersicht mit Cover, Titelbild und Logo (aus dem lokalen Steam-Cache)
- Favoriten, Suche und Filter

**Euro Truck Simulator 2**
- Findet Profile automatisch (lokal, `steam_profiles` und Steam Cloud)
- Geld, XP und Level (Level-Tabelle aus den Spieldaten)
- Fähigkeiten inkl. ADR-Klassen
- Alle LKW, Busse und Anhänger reparieren und volltanken
- Alle Städte als besucht markieren, Garagen kaufen oder auf „Groß“ ausbauen
- **Coaches-DLC** (wird pro Spielstand erkannt): Bus-XP, Bus-Level, Bus-Fähigkeiten (Stufe 0–3), alle Bushaltestellen entdecken
- Automatisches Backup vor jedem Speichern, Wiederherstellen mit einem Klick

Liest verschlüsselte, binäre (BSII) und Text-Spielstände. Alles, was nicht bearbeitet wird, bleibt Byte für Byte erhalten.

> ⚠️ Nur für den Einzelspieler. Nicht mit TruckersMP oder Convoy verwenden.
> Vor dem Speichern das Spiel beenden und den Spielstand danach im Spiel über „Laden“ öffnen.

## Bauen

Voraussetzung: [.NET 10 SDK](https://dotnet.microsoft.com/download)

```
dotnet publish -c Release -o publish
```

Ergebnis: `publish/Layts.exe` – eine einzelne Datei, die ohne installiertes .NET läuft.

## Aufbau

| Ordner | Inhalt |
|---|---|
| `Core/Sii` | Entschlüsseln, Lesen und Schreiben von SII-Dateien (binär + Text) |
| `Core` | Animationen, Steam-Bilder, Hilfsklassen |
| `Games/GameCatalog.cs` | Liste der Spiele – ein neues Spiel = neuer Eintrag + Ordner unter `Games/` |
| `Games/Ets2` | ETS2-Editor (Logik, Backups, Oberfläche) |
| `Themes/Dark.xaml` | Design (Farben, Controls, Animationen) |
| `Views`, `ViewModels` | Bibliothek und Hauptfenster |

Backups liegen unter `%LOCALAPPDATA%\Layts\Backups`.
