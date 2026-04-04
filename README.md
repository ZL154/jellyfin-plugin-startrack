<div align="center">

<img src="assets/logo.svg" alt="StarTrack Logo" width="600"/>

<br/>

![Jellyfin](https://img.shields.io/badge/Jellyfin-10.9%2B-CC0000?style=for-the-badge&labelColor=0d0d0d&logo=jellyfin&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-8.0-CC0000?style=for-the-badge&labelColor=0d0d0d&logo=dotnet&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-CC0000?style=for-the-badge&labelColor=0d0d0d)
![Version](https://img.shields.io/badge/Version-1.0.4-CC0000?style=for-the-badge&labelColor=0d0d0d)

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
| 📱 **Mobile-friendly** | Works for all users including mobile — no Tampermonkey needed |

---

## ⚠️ Requirements

StarTrack requires **two** Jellyfin plugins to be installed:

| Plugin | Why it's needed |
|--------|-----------------|
| **StarTrack** *(this plugin)* | Stores ratings, serves the REST API and the widget script |
| **[File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)** | Injects the widget script into Jellyfin's web app on every page load — without modifying files on disk |

> **File Transformation must be installed before StarTrack.**

---

## 🚀 Setup

### Step 1 — Install File Transformation

1. In Jellyfin go to **Dashboard → Plugins → Repositories → +**
2. Add the File Transformation repository:
   ```
   https://raw.githubusercontent.com/IAmParadox27/jellyfin-plugin-file-transformation/main/manifest.json
   ```
3. Go to **Catalogue**, find **File Transformation**, install it, and restart Jellyfin.

### Step 2 — Install StarTrack

**Option A — Plugin repository *(recommended)***

1. Jellyfin → **Dashboard → Plugins → Repositories → +**
2. Add:
   ```
   https://raw.githubusercontent.com/ZL154/jellyfin-plugin-startrack/main/manifest.json
   ```
3. Go to **Catalogue**, find **StarTrack**, install, and restart Jellyfin.

**Option B — Manual**

1. Download `Jellyfin.Plugin.InternalRating_*.zip` from [Releases](https://github.com/ZL154/jellyfin-plugin-startrack/releases)
2. Extract the DLL into your Jellyfin plugins folder:
   ```
   <jellyfin-data>/plugins/StarTrack/Jellyfin.Plugin.InternalRating.dll
   ```
3. Restart Jellyfin.

### Step 3 — Verify

After restarting Jellyfin, visit:
```
https://your-jellyfin-server/Plugins/StarTrack/Debug
```

You should see `FileTransformation: registered OK`. Then navigate to any Movie or TV Show — the `☆ Rate` pill will appear in the bottom-right corner.

---

## 🖥️ How it looks

| State | What you see |
|:---|:---|
| **No ratings yet** | `☆ Rate` — click to be the first |
| **Has ratings** | `★ 4.3` — click to expand |
| **Expanded** | Large average, 5-star click-to-rate, per-user list |

The widget appears as a fixed pill in the **bottom-right corner** of every Movie and TV Show detail page. It works with any Jellyfin theme and on mobile.

---

## 🌐 API Reference

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET` | `/Plugins/StarTrack/Ratings/{itemId}` | Required | Average + all user ratings |
| `POST` | `/Plugins/StarTrack/Ratings/{itemId}` | Required | Submit / update your rating `{"stars":4}` |
| `DELETE` | `/Plugins/StarTrack/Ratings/{itemId}` | Required | Remove your rating |
| `GET` | `/Plugins/StarTrack/Stats` | Required | Server-wide statistics |
| `GET` | `/Plugins/StarTrack/Widget` | None | Serves the widget JavaScript |
| `GET` | `/Plugins/StarTrack/Debug` | None | Diagnostic report |

---

## 🔧 Building from source

```bash
git clone https://github.com/ZL154/jellyfin-plugin-startrack.git
cd jellyfin-plugin-startrack
dotnet publish InternalRatingSystem/InternalRatingSystem.csproj -c Release -o ./publish
```

---

## 🤝 Contributing

Issues and pull requests are welcome!

- 🐛 [Report a bug](https://github.com/ZL154/jellyfin-plugin-startrack/issues)
- 💡 [Suggest a feature](https://github.com/ZL154/jellyfin-plugin-startrack/discussions)

---

## 📄 License

[MIT](LICENSE) — © 2025 ZL154
