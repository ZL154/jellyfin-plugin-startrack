# Internal Rating System – Jellyfin Plugin

A Jellyfin plugin (10.9+, tested on 10.11.6) that lets every user rate movies and TV shows on a **1–5 star scale** and shows the community average inline on detail pages.

---

## Features

| | |
|---|---|
| **Compact view** | `★ 4.3` pill on movie/TV detail pages |
| **Expanded panel** | Click the pill → full details, your rating input, user breakdown |
| **Rate / update** | Click any star to submit or change your rating |
| **Remove rating** | "Remove" button clears your score |
| **Individual scores** | Dropdown lists every user + their star rating |
| **Scope** | Movies and TV Series only (episodes excluded) |
| **Storage** | SQLite database in Jellyfin's data directory |

---

## Installation

### From release ZIP (recommended)

1. Download the latest `Jellyfin.Plugin.InternalRating_*.zip` from the Releases page.
2. In Jellyfin Dashboard → **Plugins → My Plugins** click **Install from file** (or place the ZIP in your `plugins/` folder and restart).
3. Restart Jellyfin.

### Build from source

```bash
dotnet publish InternalRatingSystem/InternalRatingSystem.csproj -c Release -o ./publish
```

The output DLL goes in `<jellyfin-data>/plugins/InternalRating/`.

> **Package version note:** `InternalRatingSystem.csproj` references `Jellyfin.Controller 10.9.0`.
> If you are on a different Jellyfin version, update the package version to match (e.g. `10.10.0`, `10.11.0`).

---

## Activating the widget

The rating widget is injected into Jellyfin's web UI via the plugin's config page.

1. **Dashboard → Plugins → Internal Rating System** (or Admin panel → scroll to it in the sidebar).
2. The script activates automatically when the page loads and stays active for the rest of your browser session.
3. Navigate to any Movie or TV Show detail page to see the `★` rating pill.

> You only need to visit the config page **once per browser session**. For persistent activation across full page reloads, bookmark the config page and visit it on startup.

---

## API endpoints

All endpoints require a valid Jellyfin `Authorization` header.

| Method | Path | Description |
|--------|------|-------------|
| `GET`  | `/Plugins/InternalRating/Ratings/{itemId}` | Get average + all user ratings |
| `POST` | `/Plugins/InternalRating/Ratings/{itemId}` | Submit/update your rating `{ "stars": 4 }` |
| `DELETE` | `/Plugins/InternalRating/Ratings/{itemId}` | Remove your rating |
| `GET`  | `/Plugins/InternalRating/Stats` | Server-wide rating statistics |

---

## Project structure

```
internal rating system/
├── build.yaml
├── meta.yaml
└── InternalRatingSystem/
    ├── InternalRatingSystem.csproj
    ├── Plugin.cs                     # Plugin entry point
    ├── PluginConfiguration.cs        # Config model (extendable)
    ├── PluginServiceRegistrator.cs   # DI registration
    ├── Controllers/
    │   └── RatingController.cs       # REST API
    ├── Data/
    │   └── RatingRepository.cs       # SQLite storage
    ├── Models/
    │   ├── Rating.cs
    │   ├── RatingsResponse.cs
    │   └── SubmitRatingRequest.cs
    └── Configuration/
        └── configPage.html           # Admin page + widget JS/CSS
```

---

## License

MIT — see [LICENSE](LICENSE) for details.
