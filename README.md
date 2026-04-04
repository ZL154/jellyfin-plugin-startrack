<div align="center">

<img src="assets/logo.svg" alt="StarTrack Logo" width="600"/>

<br/>

![Jellyfin](https://img.shields.io/badge/Jellyfin-10.9%2B-CC0000?style=for-the-badge&labelColor=0d0d0d&logo=jellyfin&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-8.0-CC0000?style=for-the-badge&labelColor=0d0d0d&logo=dotnet&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-CC0000?style=for-the-badge&labelColor=0d0d0d)
![Version](https://img.shields.io/badge/Version-1.0.0-CC0000?style=for-the-badge&labelColor=0d0d0d)

**Community star ratings for Jellyfin — private, self-hosted, no external services.**

⭐ [If you find this useful, please consider starring the repo!](#-support) ⭐

</div>

---

## 📖 Overview

StarTrack is a Jellyfin plugin that adds a **1–5 star community rating system** to your movie and TV library. Every user on your server can rate content and see what everyone else gave it — all stored privately in a SQLite database on your own machine.

No external APIs. No tracking. No data leaving your server.

---

## ✨ Features

| Feature | Details |
|---|---|
| ⭐ **Star ratings** | 1–5 stars per user per item |
| 📊 **Community average** | Shown inline as a compact `★ 4.3` pill |
| 🎬 **Movies & TV Shows** | Works on both, ignores episodes |
| 👥 **Per-user breakdown** | Expandable dropdown showing everyone's score |
| ✏️ **Update anytime** | Click any star to change your rating |
| 🗑️ **Remove rating** | One-click removal of your own score |
| 🔒 **100% private** | SQLite database stays on your server |

---

## 🖥️ Screenshots

> Screenshots coming soon — install the plugin and see it for yourself!

| View | Description |
|:---:|:---|
| `★ 4.3` | Compact pill shown next to item controls |
| Expanded panel | Large average, click-to-rate stars, user list dropdown |

---

## 🚀 Installation

### Option A — Plugin Repository *(recommended)*

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**
2. Click **+** and add:
   ```
   https://raw.githubusercontent.com/ZL154/jellyfin-plugin-startrack/main/manifest.json
   ```
3. Go to **Catalogue**, find **StarTrack**, and install
4. Restart Jellyfin

### Option B — Manual

1. Download `Jellyfin.Plugin.InternalRating_*.zip` from [Releases](https://github.com/ZL154/jellyfin-plugin-startrack/releases)
2. Extract the DLL into your Jellyfin plugins folder:
   ```
   <jellyfin-data>/plugins/StarTrack/Jellyfin.Plugin.InternalRating.dll
   ```
3. Restart Jellyfin

---

## ⚡ Activating the Widget

Because Jellyfin's web client is a single-page app, the rating widget needs to be activated once per browser session:

1. Go to **Dashboard → Plugins → StarTrack**
2. You'll see a green **"Widget is active"** confirmation
3. Navigate to any Movie or TV Show — the `★` pill appears automatically

> 💡 **Tip:** Bookmark the config page and visit it on startup for a seamless experience.

---

## 🔧 Building from Source

**Requirements:** .NET 8 SDK

```bash
git clone https://github.com/ZL154/jellyfin-plugin-startrack.git
cd jellyfin-plugin-startrack
dotnet publish InternalRatingSystem/InternalRatingSystem.csproj -c Release -o ./publish
```

To create a release ZIP:
```bash
zip Jellyfin.Plugin.InternalRating_1.0.0.0.zip publish/Jellyfin.Plugin.InternalRating.dll
```

> **Jellyfin version note:** The project targets `Jellyfin.Controller 10.9.0`.
> If you run a different server version, update the `<PackageReference>` in the `.csproj` to match (e.g. `10.10.0`, `10.11.0`).

---

## 🌐 API Reference

All endpoints require a valid Jellyfin session token in the `Authorization` header.

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/Plugins/StarTrack/Ratings/{itemId}` | Average + all user ratings |
| `POST` | `/Plugins/StarTrack/Ratings/{itemId}` | Submit / update your rating `{ "stars": 4 }` |
| `DELETE` | `/Plugins/StarTrack/Ratings/{itemId}` | Remove your rating |
| `GET` | `/Plugins/StarTrack/Stats` | Server-wide statistics |

---

## 🤝 Contributing

Issues and pull requests are welcome!

- Found a bug? Open an [issue](https://github.com/ZL154/jellyfin-plugin-startrack/issues)
- Got a feature idea? Start a [discussion](https://github.com/ZL154/jellyfin-plugin-startrack/discussions)
- Works on a Jellyfin version not listed? Let us know!

---

## ⭐ Support

If StarTrack is useful to you, the best thing you can do is **star this repository** — it helps others find it!

```
★ → top right of this page → Star
```

> 🙏 Thank you to everyone who tries it, reports bugs, or suggests improvements.

---

## 📄 License

[MIT](LICENSE) — © 2025 ZL154
