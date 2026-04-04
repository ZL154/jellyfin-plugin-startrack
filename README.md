<div align="center">

<img src="assets/logo.svg" alt="StarTrack Logo" width="600"/>

<br/>

![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11%2B-CC0000?style=for-the-badge&labelColor=0d0d0d&logo=jellyfin&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-9.0-CC0000?style=for-the-badge&labelColor=0d0d0d&logo=dotnet&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-CC0000?style=for-the-badge&labelColor=0d0d0d)
![Version](https://img.shields.io/badge/Version-1.0.13-CC0000?style=for-the-badge&labelColor=0d0d0d)

**Community star ratings for Jellyfin — private, self-hosted, no external services.**

⭐ *If StarTrack is useful to you, please consider starring this repo!* ⭐

</div>

---

## 📖 Overview

StarTrack is a Jellyfin plugin that adds a **1–5 star community rating system** to your movie and TV library. Every user on your server can rate content, leave written reviews, and browse a personal Letterboxd-style library of everything they've rated — all stored as JSON on your own server. Nothing leaves your network.

---

## ✨ Features

| | |
|---|---|
| ⭐ **Half-star ratings** | 0.5–5 stars per user per item (click left or right half of each star) |
| 📝 **Written reviews** | Optional text review attached to any rating |
| 📊 **Community average** | Compact `★ 4.3` pill shown above the metadata row on detail pages |
| 🎬 **Movies, TV Shows & Episodes** | Works on all three item types |
| 👥 **Per-user breakdown** | Expandable dropdown of every user's score and review |
| 🕒 **Recent ratings** | Home screen pill shows your 15 most recent ratings |
| 📚 **My Ratings library** | Sidebar link opens a full-screen poster grid of everything you've rated |
| 🔀 **10 sort options** | Sort by date rated, film year, your rating, avg rating, or runtime — each ascending or descending |
| ✏️ **Update anytime** | Click any star to change your pending selection, then submit |
| 🗑️ **Remove rating** | One-click removal |
| 🎨 **Theme-compatible** | Works with any Jellyfin CSS theme |
| 📱 **Works for everyone** | No browser extensions or Tampermonkey required — works on mobile too |

---

## ⚠️ Requirements

- **Jellyfin 10.11.x** (tested on 10.11.6)
- No other plugins required

> StarTrack v1.0.13+ uses ASP.NET Core middleware to inject the widget script at runtime — no File Transformation plugin needed.

---

## 🚀 Installation

**Option A — Plugin repository *(recommended)***

1. Jellyfin → **Dashboard → Plugins → Repositories → +**
2. Add:
   ```
   https://raw.githubusercontent.com/ZL154/jellyfin-plugin-startrack/main/manifest.json
   ```
3. Go to **Catalogue**, find **StarTrack**, install it, and **restart Jellyfin**.

**Option B — Manual**

1. Download `Jellyfin.Plugin.InternalRating_*.zip` from [Releases](https://github.com/ZL154/jellyfin-plugin-startrack/releases)
2. Extract the DLL into your Jellyfin plugins folder:
   ```
   <jellyfin-data>/plugins/StarTrack/Jellyfin.Plugin.InternalRating.dll
   ```
3. Restart Jellyfin.

### Verify it's working

After restarting, visit:
```
https://your-jellyfin-server/Plugins/StarTrack/Debug
```

You should see `Plugin loaded: YES`. Then navigate to any Movie, TV Show, or Episode — the `☆ Rate` pill will appear in the bottom-right corner of the detail page.

---

## 🖥️ How it looks

### Detail page rating pill

| State | What you see |
|:---|:---|
| **No ratings yet** | `☆ Rate` — click to be the first |
| **Has ratings** | `★ 4.3` — click to expand |
| **Expanded** | Large average, half-star click-to-rate, written review field, Submit button, per-user list |

The average rating is also shown as a standalone badge above the IMDb/MDBList metadata row on detail pages.

### Home screen recent pill

When no item is selected (home screen or library browse), the pill shows your **15 most recently rated items** as a compact feed.

### My Ratings library

Click **My Ratings** in the Jellyfin sidebar to open a full-screen overlay showing every item you've ever rated. Poster cards display:
- Item name and year
- Your star rating
- Community average
- Runtime

Use the **Sort by** dropdown to reorder by any of 10 criteria:

| Sort option | |
|---|---|
| Date rated (newest first) | Date rated (oldest first) |
| Film year (newest first) | Film year (oldest first) |
| My rating (highest first) | My rating (lowest first) |
| Avg rating (highest first) | Avg rating (lowest first) |
| Length (longest first) | Length (shortest first) |

Click any card to navigate to that item's detail page. Press **Escape** or click outside the overlay to close.

---

## 🌐 API Reference

All endpoints are under `/Plugins/StarTrack/`.

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET` | `/Ratings/{itemId}` | Required | Average + all user ratings and reviews |
| `POST` | `/Ratings/{itemId}` | Required | Submit or update your rating `{"stars":4,"review":"Great film"}` |
| `DELETE` | `/Ratings/{itemId}` | Required | Remove your rating |
| `GET` | `/MyRatings?limit=N` | Required | All your rated items, newest first (max 10 000) |
| `GET` | `/Recent?limit=N` | Required | Most recent ratings across all users (max 100) |
| `GET` | `/Stats` | Required | Server-wide total items and rating count |
| `GET` | `/Widget` | None | Serves the embedded widget JavaScript |
| `GET` | `/WhoAmI` | Required | Auth debug info for the current user |
| `GET` | `/Debug` | None | Diagnostic report (plugin version, injection status) |

---

## 💾 Data storage

All ratings are stored in:
```
<jellyfin-data>/data/InternalRating/ratings.json
```

This is a plain JSON file. Back it up or migrate it like any other data file — no database required.

---

## 🔧 Building from source

```bash
git clone https://github.com/ZL154/jellyfin-plugin-startrack.git
cd jellyfin-plugin-startrack/InternalRatingSystem
dotnet publish -c Release -o ./publish_out
```

The compiled DLL will be at `publish_out/Jellyfin.Plugin.InternalRating.dll`.

> **Compatibility note:** The plugin must be compiled against the exact Jellyfin.Controller NuGet package version that matches your server. The csproj currently targets `10.11.6`. If your server is a different patch version, update the `<PackageReference>` accordingly.

---

## 🤝 Contributing

Issues and pull requests are welcome!

- 🐛 [Report a bug](https://github.com/ZL154/jellyfin-plugin-startrack/issues)
- 💡 [Suggest a feature](https://github.com/ZL154/jellyfin-plugin-startrack/discussions)

---

## 📄 License

[MIT](LICENSE) — © 2025 ZL154
