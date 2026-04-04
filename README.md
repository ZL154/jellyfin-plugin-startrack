<div align="center">

<img src="assets/logo.svg" alt="StarTrack Logo" width="600"/>

<br/>

![Jellyfin](https://img.shields.io/badge/Jellyfin-10.9%2B-CC0000?style=for-the-badge&labelColor=0d0d0d&logo=jellyfin&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-8.0-CC0000?style=for-the-badge&labelColor=0d0d0d&logo=dotnet&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-CC0000?style=for-the-badge&labelColor=0d0d0d)
![Version](https://img.shields.io/badge/Version-1.0.0-CC0000?style=for-the-badge&labelColor=0d0d0d)

**Community star ratings for Jellyfin — private, self-hosted, no external services.**

⭐ *If StarTrack is useful to you, please consider starring this repo!* ⭐

</div>

---

## 📖 Overview

StarTrack is a Jellyfin plugin that adds a **1–5 star community rating system** to your movie and TV library. Every user on your server can rate content and see what everyone else gave it — stored in a JSON file on your own machine. Nothing leaves your server.

---

## ✨ Features

| | |
|---|---|
| ⭐ **Star ratings** | 1–5 stars per user per item |
| 📊 **Community average** | Shown as a compact `★ 4.3` pill on detail pages |
| 🎬 **Movies & TV Shows** | Works on both, skips episodes |
| 👥 **Per-user breakdown** | Expandable dropdown of every user's score |
| ✏️ **Update anytime** | Click any star to change your rating |
| 🗑️ **Remove rating** | One-click removal |
| 🎨 **Theme-Compatible** | Works with any Jellyfin CSS theme |

---

## 🚀 Setup — Two steps

### Step 1 — Install the Jellyfin server plugin

**Option A — Plugin repository *(recommended)***

1. Jellyfin → **Dashboard → Plugins → Repositories → +**
2. Add:
   ```
   https://raw.githubusercontent.com/ZL154/jellyfin-plugin-startrack/main/manifest.json
   ```
3. Go to **Catalogue**, find **StarTrack**, install, and restart Jellyfin.

**Option B — Manual**

1. Download `Jellyfin.Plugin.InternalRating_*.zip` from [Releases](https://github.com/ZL154/jellyfin-plugin-startrack/releases)
2. Extract the DLL into:
   ```
   <jellyfin-data>/plugins/StarTrack/Jellyfin.Plugin.InternalRating.dll
   ```
3. Restart Jellyfin.

---

### Step 2 — Install the browser widget script

The rating widget runs in your browser. Install it once and it activates **automatically on every page load** — no config page visits, no per-session steps.

**1.** Install [Tampermonkey](https://www.tampermonkey.net/) for your browser
*(Chrome, Firefox, Edge, and Safari are all supported)*

**2.** Click to install the widget script:

> **[⬇ Install StarTrack Widget Script](https://raw.githubusercontent.com/ZL154/jellyfin-plugin-startrack/main/startrack.user.js)**

Tampermonkey will prompt you to confirm — click **Install**.

That's it. Navigate to any Movie or TV Show in Jellyfin and the `☆` pill will appear automatically.

---

## 🖥️ How it looks

| State | What you see |
|:---|:---|
| **No ratings yet** | `☆` — click to be the first to rate |
| **Has ratings** | `★ 4.3` — click to expand |
| **Expanded** | Large average, 5-star click-to-rate, per-user dropdown |

---

## 🌐 API Reference

All endpoints require a valid Jellyfin session token.

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/Plugins/StarTrack/Ratings/{itemId}` | Average + all user ratings |
| `POST` | `/Plugins/StarTrack/Ratings/{itemId}` | Submit / update your rating `{"stars":4}` |
| `DELETE` | `/Plugins/StarTrack/Ratings/{itemId}` | Remove your rating |
| `GET` | `/Plugins/StarTrack/Stats` | Server-wide statistics |

---

## 🔧 Building from source

```bash
git clone https://github.com/ZL154/jellyfin-plugin-startrack.git
cd jellyfin-plugin-startrack
dotnet publish InternalRatingSystem/InternalRatingSystem.csproj -c Release -o ./publish
```

> **Version note:** targets `Jellyfin.Controller 10.9.0`. Update the `<PackageReference>` in the `.csproj` if your server version differs.

---

## 🤝 Contributing

Issues and pull requests are welcome!

- 🐛 [Report a bug](https://github.com/ZL154/jellyfin-plugin-startrack/issues)
- 💡 [Suggest a feature](https://github.com/ZL154/jellyfin-plugin-startrack/discussions)

---

## ⭐ Support

If StarTrack saves you time, the best thing you can do is **star this repo** — it helps others find it.

```
★  →  top-right of this page  →  Star
```

---

## 📄 License

[MIT](LICENSE) — © 2025 ZL154
