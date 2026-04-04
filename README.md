<div align="center">

# ⭐ StarTrack — Internal Rating System

**A Jellyfin plugin that lets your users rate movies and TV shows with 1–5 stars and see the community average, right on the detail page.**

![Jellyfin](https://img.shields.io/badge/Jellyfin-10.9%2B-00A4DC?style=for-the-badge&logo=jellyfin&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)

</div>

---

## What it does

StarTrack injects a small rating pill onto every **Movie** and **TV Series** detail page in your Jellyfin library.

```
★ 4.3
```

Click the pill to expand a full panel:

- See the community average in large type
- Submit or update your own 1–5 star rating with a single click
- Hit **Show individual ratings** to see exactly what each user gave it
- Remove your rating at any time with the **Remove** button

All data is stored in a SQLite database on your Jellyfin server — nothing ever leaves your network.

---

## Screenshots

> *(Add screenshots here once the plugin is installed)*

| Collapsed pill | Expanded panel |
|:-:|:-:|
| `★ 4.3` next to the play button | Full average + your rating + user list |

---

## Installation

### Option A — Manual (recommended for now)

1. Build the plugin (see below) or grab the ZIP from [Releases](https://github.com/ZL154/jellyfin-plugin-startrack/releases).
2. Extract the DLL into your Jellyfin plugins directory:
   ```
   <jellyfin-data>/plugins/StarTrack/Jellyfin.Plugin.InternalRating.dll
   ```
3. Restart Jellyfin.

### Option B — Plugin repository

Add this URL as a custom plugin repository in **Dashboard → Plugins → Repositories**:

```
https://raw.githubusercontent.com/ZL154/jellyfin-plugin-startrack/main/manifest.json
```

Then install **StarTrack** from the catalogue and restart.

---

## Build from source

**Requirements:** .NET 8 SDK

```bash
git clone https://github.com/ZL154/jellyfin-plugin-startrack.git
cd jellyfin-plugin-startrack
dotnet build InternalRatingSystem/InternalRatingSystem.csproj -c Release
```

Output: `InternalRatingSystem/bin/Release/net8.0/Jellyfin.Plugin.InternalRating.dll`

> **Version note:** The project targets `Jellyfin.Controller 10.9.0`.
> If your server is on 10.10.x or 10.11.x, update the package version in the `.csproj` to match.

---

## Activating the widget

Because Jellyfin's web client is a single-page app, the rating widget is activated via the plugin's config page:

1. Go to **Dashboard → Plugins → StarTrack**
2. The widget script loads automatically — you'll see a green **"Widget is active"** message
3. Navigate to any Movie or TV Show — the `★` pill will appear

> You only need to do this **once per browser session**. Bookmark the config page and visit it on startup for the smoothest experience.

---

## API reference

All endpoints require a valid Jellyfin `Authorization` header.

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/Plugins/InternalRating/Ratings/{itemId}` | Get average + all user ratings for an item |
| `POST` | `/Plugins/InternalRating/Ratings/{itemId}` | Submit or update your rating `{ "stars": 4 }` |
| `DELETE` | `/Plugins/InternalRating/Ratings/{itemId}` | Remove your rating |
| `GET` | `/Plugins/InternalRating/Stats` | Server-wide statistics (total items rated, total ratings) |

---

## Project layout

```
jellyfin-plugin-startrack/
├── manifest.json                          ← Jellyfin plugin repository manifest
├── build.yaml                             ← Jellyfin build config
├── meta.yaml                              ← Plugin metadata
└── InternalRatingSystem/
    ├── InternalRatingSystem.csproj
    ├── Plugin.cs                          ← Entry point & web page registration
    ├── PluginConfiguration.cs             ← Config model (extendable)
    ├── PluginServiceRegistrator.cs        ← DI service registration
    ├── Controllers/
    │   └── RatingController.cs            ← REST API
    ├── Data/
    │   └── RatingRepository.cs            ← SQLite storage layer
    ├── Models/
    │   ├── Rating.cs
    │   ├── RatingsResponse.cs
    │   └── SubmitRatingRequest.cs
    └── Configuration/
        └── configPage.html                ← Admin page + widget JS/CSS
```

---

## Contributing

Issues and PRs are welcome. If you hit a Jellyfin version where the widget doesn't inject correctly, open an issue with your Jellyfin version and browser and I'll get it sorted.

---

## License

[MIT](LICENSE) — © 2025 ZL154
