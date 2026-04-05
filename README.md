<div align="center">

<img src="assets/logo.svg" alt="StarTrack Logo" width="600"/>

<br/>

![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11%2B-CC0000?style=for-the-badge&labelColor=0d0d0d&logo=jellyfin&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-9.0-CC0000?style=for-the-badge&labelColor=0d0d0d&logo=dotnet&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-CC0000?style=for-the-badge&labelColor=0d0d0d)
![Version](https://img.shields.io/badge/Version-1.0.15-CC0000?style=for-the-badge&labelColor=0d0d0d)

**Community star ratings for Jellyfin — private, self-hosted, no external services.**

⭐ *If StarTrack is useful to you, please consider starring this repo!* ⭐

</div>

---

## Overview

StarTrack adds a **1–5 star community rating system** to your Jellyfin server. Every user can rate movies, TV shows and episodes, leave written reviews, and browse their personal ratings in a Letterboxd-style library — all stored as JSON on your own server. Nothing leaves your network.

---

## Features

| | |
|---|---|
| ⭐ **Half-star ratings** | 0.5–5 stars per item, per user |
| 📝 **Written reviews** | Optional text review on any rating |
| 📊 **Community average** | `★ 4.3` badge shown above the metadata row on detail pages |
| 🎬 **Movies, TV Shows & Episodes** | Works on all three item types |
| 👥 **Per-user breakdown** | Expandable list of every user's score and review |
| 🕒 **Recent ratings pill** | Home screen pill shows your 15 most recent ratings |
| 📚 **My Ratings library** | Sidebar link opens a full-screen Letterboxd-style poster grid |
| 🔀 **10 sort options** | Date rated, film year, your rating, avg rating, runtime — each asc/desc |
| 🗂️ **Category tabs** | Filter your library by Movies, TV Shows, or Episodes |
| 🎨 **Theme-compatible** | Works alongside any Jellyfin CSS theme |
---

## Requirements

- **Jellyfin 10.11.x** (tested on 10.11.6)

StarTrack uses ASP.NET Core middleware to inject its widget at runtime.

---

## Installation

### Option A — Plugin Repository *(recommended)*

1. Jellyfin → **Dashboard → Plugins → Repositories → +**
2. Add:
   ```
   https://raw.githubusercontent.com/ZL154/jellyfin-plugin-startrack/main/manifest.json
   ```
3. Go to **Catalogue**, find **StarTrack**, install it, and **restart Jellyfin**.

### Option B — Manual

1. Download `Jellyfin.Plugin.InternalRating_*.zip` from [Releases](https://github.com/ZL154/jellyfin-plugin-startrack/releases)
2. Extract the DLL into your Jellyfin plugins folder:
   ```
   <jellyfin-data>/plugins/StarTrack/Jellyfin.Plugin.InternalRating.dll
   ```
3. Restart Jellyfin.

### Verify

After restarting, visit:
```
https://your-jellyfin-server/Plugins/StarTrack/Debug
```

You should see `Plugin loaded: YES`. Then open any Movie, TV Show, or Episode detail page — the `☆ Rate` pill will appear in the bottom-right corner.

---

## How it works

### Rating pill (detail pages)

| State | What you see |
|:---|:---|
| No ratings yet | `☆ Rate` — click to be the first |
| Has ratings | `★ 4.3` — click to expand |
| Expanded | Large average, half-star input, review field, Save button, per-user list |

The average is also shown as a standalone badge above the IMDb/MDBList metadata row.

### Recent ratings (home / library)

When browsing without an item selected, the pill shows your **15 most recent ratings** as a compact list.

### My Ratings library (sidebar)

Click **My Ratings** in the Jellyfin sidebar to open a full-screen poster grid of everything you have rated.

- **Tabs** — All / Movies / TV Shows / Episodes
- **Sort** — 10 options: date rated, film year, your rating, avg rating, length (each ↑↓)
- Click any card to navigate to that item's detail page
- Press **Escape** or click **Close** to exit

---

## API Reference

All endpoints are under `/Plugins/StarTrack/`.

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET` | `/Ratings/{itemId}` | Required | Average + all user ratings and reviews |
| `POST` | `/Ratings/{itemId}` | Required | Submit or update your rating `{"stars":4,"review":"..."}` |
| `DELETE` | `/Ratings/{itemId}` | Required | Remove your rating |
| `GET` | `/MyRatings?limit=N` | Required | All your rated items, newest first (max 10 000) |
| `GET` | `/Recent?limit=N` | Required | Recent ratings across all users |
| `GET` | `/Stats` | Required | Server-wide rating count |
| `GET` | `/Widget` | None | The embedded widget JavaScript |
| `GET` | `/WhoAmI` | Required | Auth debug for the current session |
| `GET` | `/Debug` | None | Diagnostic report (plugin version, injection status) |

---

## Data storage

Ratings are stored in a plain JSON file:
```
<jellyfin-data>/data/InternalRating/ratings.json
```

Back it up or migrate it like any other data file — no database required.

---

## Building from source

```bash
git clone https://github.com/ZL154/jellyfin-plugin-startrack.git
cd jellyfin-plugin-startrack/InternalRatingSystem
dotnet publish -c Release -o ./publish_out
```

The compiled DLL is at `publish_out/Jellyfin.Plugin.InternalRating.dll`.

> **Version note:** The plugin must be compiled against the Jellyfin.Controller NuGet package version that exactly matches your server. The csproj currently targets `10.11.6`. Update `<PackageReference>` if your server version differs.

---

## Contributing

Issues and pull requests are welcome.

- [Report a bug](https://github.com/ZL154/jellyfin-plugin-startrack/issues)
- [Suggest a feature](https://github.com/ZL154/jellyfin-plugin-startrack/discussions)

---

## License

[MIT](LICENSE) — © 2025 ZL154

---

<div align="center">
<sub>Built by ZL154 · AI-assisted development with <a href="https://claude.ai/claude-code">Claude Code</a></sub>
</div>
