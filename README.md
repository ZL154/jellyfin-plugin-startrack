<div align="center">

<img src="assets/logo.svg" alt="StarTrack Logo" width="600"/>

<br/>

![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11%2B-CC0000?style=for-the-badge&labelColor=0d0d0d&logo=jellyfin&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-9.0-CC0000?style=for-the-badge&labelColor=0d0d0d&logo=dotnet&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-CC0000?style=for-the-badge&labelColor=0d0d0d)
![Version](https://img.shields.io/badge/Version-1.6.4-CC0000?style=for-the-badge&labelColor=0d0d0d)

**Letterboxd-style ratings, watchlist, lists & social layer for Jellyfin**

⭐ *If StarTrack is useful to you, please consider starring this repo!* ⭐

</div>

---

## 📑 Table of contents

- [Overview](#overview)
- [Features](#features) - release highlights, ratings, external sync, TV/webOS, views, social, admin
- [Screenshots](#-screenshots)
- [Requirements](#requirements)
- [Installation](#installation)
- [How to use](#how-to-use) - rating controls, My Ratings, external sync, native mirroring
- [API Reference](#api-reference) - ratings, watchlist, diary, lists, Letterboxd, config
- [Data storage](#data-storage)
- [Building from source](#building-from-source)
- [Contributing](#contributing)
- [Earlier release highlights](#earlier-release-highlights) - 1.5.2, 1.5 and 1.4
- [Support the project](#-support-the-project)
- [License](#license)

---

## Overview

StarTrack started as a simple star-rating plugin and has grown into a full **Letterboxd-style social layer for Jellyfin**: ratings, reviews, watchlists, liked films, a chronological diary with rewatches, Top 4 favourites, collaborative lists, recommendations, server-wide watchlist sharing, Letterboxd import, and optional Trakt, Simkl and Yamtrack workflows. Ratings can also mirror into Jellyfin's native per-user rating field for use with Jellyfin filters and backups. Everything is stored as plain JSON on your own server; external services are contacted only when you explicitly configure and connect them.

Designed to integrate cleanly with modern Jellyfin setups: desktop, mobile, TV/webOS remote navigation, and reverse proxies that serve Jellyfin from a BaseURL sub-path.

---

## Features

### 🆕 New in 1.6.4

- **One canonical media-page badge** - removes duplicate, stale and attribute-stripped ghost badges left behind by Jellyfin SPA transitions. The surviving badge is clickable and opens the rating panel.
- **Reliable native-rating replacement** - Jellyfin's delayed community-rating node remains hidden when replacement is enabled, without creating a second StarTrack renderer.
- **TV/webOS whole-star controls** - each whole-star target can receive D-pad focus and be selected with OK/Enter, with webOS-compatible focus feedback.
- **Native rating backfill** - admins can copy every existing StarTrack rating into Jellyfin's native per-user rating field. The elevated operation is safe to run more than once.
- **BaseURL follow-up** - native Jellyfin item, poster and avatar requests consistently include the configured reverse-proxy sub-path.

### 🆕 New in 1.6.3

- **TV/webOS accessibility pass** - the media-page badge, floating Rate pill and post-playback prompt are D-pad reachable; unrated items retain a dimmed `☆ Rate` target on TV.
- **Readable TV presentation** - IMDb-style white rating numbers, gold StarTrack accents and corrected **Large / Large (TV only)** sizing.
- **Per-device rating size** - users can choose Normal, Large or Large (TV only) from **My Ratings → ⚙ Preferences**; large mode is automatically constrained on small phone screens.
- **Reverse-proxy support throughout the widget** - injected assets and StarTrack API calls respect Jellyfin's BaseURL, including installations served from paths such as `/jelly`.
- **Optional native rating mirror** - new and changed StarTrack ratings can be written to Jellyfin's built-in 0–10 per-user field; removing a StarTrack rating clears its mirrored value.
- **Media-page cleanup** - stale, missing and duplicated badges are removed or rebuilt only on the visible detail page.

### 🆕 New in 1.6.2

- **Initial BaseURL support** - fixed widget and API routing behind reverse proxies that mount Jellyfin below the domain root.
- **Native Jellyfin rating integration** - added the opt-in live mirror for Jellyfin filters, native UI and backups.
- **Per-device sizing** - introduced user-level Normal / Large overrides while protecting small phone layouts.
- **Media badge reliability** - improved duplicate, stale and missing badge cleanup on web and iPad.

### 🆕 New in 1.6.1

- **Clickable, deduplicated detail-page badge** - clears Jellyfin's cached/cloned copies and opens the rating panel from the remaining badge.
- **Widget cache-busting** - each plugin update refreshes the injected script token so browsers receive the current widget.
- **Configurable review cap** - admins can set reviews from 1 to 10,000 characters instead of relying on a fixed limit.
- **Display controls** - Normal, Large and Large (TV only) rating sizes plus a compact media-page badge that shows only the rating.
- **Early TV remote support** - the floating pill and media badge can receive D-pad focus and open with OK/Enter.
- **Cleaner placement** - StarTrack appears before IMDb, Rotten Tomatoes and Jellyfin community ratings; a single rating no longer shows a redundant `(1)` count.
- **Eight-language coverage** - all new configuration and user-facing text was translated.

### 🆕 New in 1.6.0

- **External rating sync (Trakt · Simkl · Yamtrack)** - connect an external service and StarTrack keeps your ratings in sync. Trakt uses device-code login, Simkl uses a PIN; admins drop the client ID/secret into the plugin config page, each user connects from the **⇄ External Sync** panel in *My Ratings*. Pick a direction per service - **Off / Export only / Import only / Two-way** - and a background task syncs every 10 minutes.
- **Newer-wins conflict resolution** - in two-way mode, the most recently changed rating wins, so a fresh local edit is never clobbered by a stale remote value (and vice-versa).
- **Watched history + likes push** *(Trakt)* - rated movies/episodes are also marked **watched** on Trakt (so History / Up Next populate), and your ♡ liked items are pushed to a "StarTrack Liked" list (plus Favorites if your Trakt account is VIP).
- **One-shot Backfill** - a button that seeds the whole service from your existing library in one pass. Simkl gets a dedicated date-accuracy repair so watched/rated dates are preserved instead of all stamped "today".
- **Yamtrack CSV export** - no released Yamtrack build exposes a public API yet, so StarTrack exports an **IMDb-format CSV** you import via Yamtrack → *Import → IMDb*. Marked experimental; the native provider is ready for when their REST API ships.
- **Daily auto-export** - a scheduled task writes your ratings out to a file every day for automatic backup.
- **Configurable poster rating badge position** - the gold ★ badge corner is now a setting (**top-right** default / top-left / bottom-right / bottom-left) so it no longer collides with Jellyfin's watched checkmark (issue #8).
- **Full 8-language i18n** for all of the new External Sync UI.

### ⭐ Ratings & reviews
- **Half-star ratings** (0.5 – 5★) per item, per user
- **Written reviews** with an admin-configurable limit from 1 to 10,000 characters
- **Canonical community badge** displayed once on each detail page; click it to open the rating panel
- **Compact badge mode** - show only the rating while keeping the full StarTrack label and count in the tooltip
- **Per-user breakdown** - expandable list of every user's score and review on the detail panel
- **Star tier colours** - five visual tiers (5★ glowing gold → 1★ muted red) so the rating grid is scannable at a glance
- **Optional native mirror** - write StarTrack's 0.5–5★ value into Jellyfin's 0–10 per-user rating field, clear it when the StarTrack rating is removed, and backfill older ratings from the admin page

### 📺 Media-page, TV & reverse-proxy compatibility
- **Duplicate-resistant media badge** - reconciles Jellyfin SPA navigation, cached DOM clones and delayed native-rating nodes so only the current page owns a StarTrack badge
- **TV/webOS controls** - the media badge, Rate pill, post-playback prompt and whole-star targets support D-pad focus plus OK/Enter activation
- **Unrated TV target** - a dimmed `☆ Rate` badge remains available on TV so remote users always have a focusable way to rate
- **Flexible sizing** - Normal, Large and Large (TV only) admin defaults with a per-device override in **My Ratings → ⚙ Preferences**
- **BaseURL-aware routing** - widget assets, StarTrack endpoints and native Jellyfin item/poster/avatar requests work below reverse-proxy paths such as `/jelly`

### 🎞 Letterboxd-parity views
| | |
|---|---|
| **★ Films** | Full poster grid of everything you've rated, with a pinned Top 4 row |
| **☆ Watchlist** | Bookmark films to watch later - toggleable to show **everyone's combined watchlist** with per-user filtering |
| **♡ Liked** | One-tap heart toggle, separate from your star rating |
| **📖 Diary** | Chronological journal of every watch with **rewatch detection**, grouped by month, with visual star bars |
| **✍ Reviews feed** | Server-wide vertical feed of every rating that has a review, with poster, reviewer, star bar, date and review text |
| **✨ For you** | Personalised recommendations weighted by your top genres (movies + series). Reshuffle button rerolls 30 picks from a 60-candidate pool |
| **📃 Lists** | Create collaborative film lists that other users on your server can contribute to. Owner-only delete |

### ⭐ Top 4 favourites (per type)
- Pin up to 4 movies, 4 series and 4 episodes to your profile
- Sub-rows shown in the type tab they belong to (Movies tab → Top 4 Movies, etc)
- Empty slots show a clear "+ pin a film" placeholder so the feature is discoverable
- Hover any pinned slot to reveal an **× remove** button
- Hover any film card to reveal **⭐ pin to Top 4** and **+ add to a list** buttons

### 🔄 Letterboxd integration
- **Drop in your Letterboxd export ZIP** to import everything in one pass: `ratings.csv`, `diary.csv`, `watchlist.csv` and `likes/films.csv`
- **Browser-style User-Agent** so Letterboxd's anti-bot doesn't block sync
- **Sync now** button pulls your latest ratings (RSS), watchlist (RSS), and likes (HTML scrape) - one click, three data types
- **Hourly auto-sync** scheduled task for every user with a Letterboxd username configured
- **Import Top 4** button scrapes your Letterboxd profile's "favourite films" section
- **Export CSV** - download your StarTrack ratings in Letterboxd-compatible format for backup or migration
- **Diagnose** button - runs the library matcher and shows you exactly how many movies are indexed, how many duplicates exist, and how titles are normalised
- **Clean dead ratings** button - removes ratings that point to library items whose underlying file no longer exists (post-HDD-failure cleanup)

### ⇄ External service sync
- **Trakt and Simkl authentication** - users connect with Trakt's device code or Simkl's PIN after an admin configures the server-wide app credentials
- **Per-service direction** - Off / Export only / Import only / Two-way, with newer-wins conflict resolution and an automatic ten-minute scheduled sync
- **Trakt history and likes** - rated movies and episodes can be marked watched; liked items are pushed to a `StarTrack Liked` list and, for VIP accounts, Trakt Favorites
- **Backfill watched history** - seed Trakt or Simkl from existing Jellyfin played state; Simkl preserves the original watched/rated dates
- **Yamtrack workflow** - export an IMDb-compatible CSV for **Yamtrack → Import → IMDb** while native provider support remains experimental
- **Portable import/export** - authenticated CSV/JSON endpoints plus Letterboxd- and IMDb-compatible export formats
- **Daily server backup** - optional scheduled CSV or JSON exports for every user with ratings, written at 03:00 UTC

### 🔍 Search & filter
- **Live search input** in the topbar - filters the active view by title with a 150 ms debounce
- **Discrete star filter dropdown** - `5★ only`, `4.5★ only`, `4★ only` … down to `0.5★ only`
- **Sort dropdown** - date rated, film year, your rating, community rating, runtime, each ↑↓
- **Type tabs** - All / Movies / TV Shows / Episodes - drives both the grid and which Top 4 row is visible

### 👥 Collaborative & social
- **Members directory** - searchable profile cards with avatars, rating totals, average scores and each member's pinned Top 4
- **Detailed profiles** - histograms, favourite genres/people/decades, hours watched, monthly activity, calendar heatmap, on-this-day and most-rewatched views
- **Follow graph and activity feed** - follow members and filter recent ratings, reviews and diary activity between followed users and everyone
- **Taste comparison** - compare two members with a Pearson-based similarity score, rating histograms, disagreements and shared favourites
- **Server-enforced privacy** - hide a profile, followers/following, statistics or recent activity; private lists remain owner-only
- **Everyone's watchlist** view - see what every user on your server wants to watch, sorted by most-wanted first, with a per-user filter dropdown to focus on a specific user
- **Collaborative lists** - owner can mark a list as collaborative and other users can add their own picks. Each item tracks who added it
- **Reviews feed** - see what everyone on the server thinks
- **Per-user attribution** on community averages so you know who rated what

### 🛠 Admin & infrastructure
- **Presentation controls** - badge position and size, compact media badge, poster overlays, native/Media Bar replacement and post-playback prompts
- **Native Jellyfin integration** - opt-in live mirroring plus an elevation-gated, idempotent backfill for existing StarTrack ratings
- **Review policy** - choose a maximum review length between 1 and 10,000 characters
- **External service credentials** - configure Trakt and Simkl once while each user owns their connection and sync direction
- **Automatic backups** - the optional `AutoExportDaily` task writes CSV or JSON snapshots under `<jellyfin-data>/data/InternalRating/exports/`
- **Per-rating cleanup** - purge a single rating without nuking the whole profile
- **Multi-rating cleanup** - purge every rating that points to a missing library file
- **Eight-language UI** - English, French, Spanish, German, Italian, Portuguese, Chinese and Japanese across the widget, user preferences and admin settings
- **BaseURL and cache-safe injection** - supports reverse-proxy sub-paths and refreshes the widget asset token after plugin updates
- **Standalone WebView shell** - authenticated mobile, TV, kiosk and third-party clients can host the complete My Ratings overlay without rendering Jellyfin's surrounding web UI
- **Mobile-responsive UI** - full `@media` query overhaul for phones and tablets
- **Client-side metadata cache** - switching between views is instant after the first load
- **MIT licensed** - fork it, modify it, self-host it, bundle it
- **Pure JSON storage** - no database. Just files in `<jellyfin-data>/data/InternalRating/`

---

## 📸 Screenshots

### Rating pill (detail pages)
A small floating pill appears at the bottom-right of every Movie / Series / Episode detail page. The canonical StarTrack badge beside Jellyfin's native ratings opens the same panel. Both are keyboard- and TV-remote accessible, and their size can be adjusted for large displays.

<p align="center">
  <img alt="Rating pill on detail page" src="assets/screenshots/rating-pill-detail.png" />
</p>

### Rating panel
Half-star precision, optional written review, community average with per-user breakdown, one-click heart / watchlist / Top 4 pin. "Show all ratings" expands every user's score.

<p align="center">
  <img alt="Rating panel expanded" src="assets/screenshots/rating-panel.png" />
</p>

### Recent ratings + Letterboxd sync
The pill also shows your recent ratings at a glance. One tap opens the Letterboxd sync panel - enter your username, enable hourly auto-sync, or drop in your export ZIP.

<p align="center">
  <img alt="Recent ratings list" src="assets/screenshots/recent-ratings.png" />
</p>

<p align="center">
  <img alt="Letterboxd sync panel" src="assets/screenshots/letterboxd-sync.png" />
</p>

### Sidebar entry
Auto-injected into the Jellyfin nav menu - no theme changes required.

<p align="center">
  <img alt="Sidebar entry" src="assets/screenshots/sidebar-entry.png" />
</p>

### Main view + type tabs
Seven top-level views - Media / Watchlist / Liked / Diary / Reviews / For You / Lists - and five type tabs on the Media view: All / Movies / TV Shows / Episodes / Anime. Anime is detected via genre or tag, so a movie or series can count as anime regardless of its underlying Jellyfin type.

<p align="center">
  <img alt="Top-level views and type tabs" src="assets/screenshots/views-and-tabs.png" />
</p>

### Stats, sort, search, Letterboxd + export
Live search with a 150 ms debounce, seven sort options (date rated, film year, your rating, community rating, runtime - each ↑↓), one-click Letterboxd settings pane, and CSV export in Letterboxd-compatible format.

<p align="center">
  <img alt="Controls bar" src="assets/screenshots/controls-bar.png" />
</p>

### Top 4 Movies + full poster grid
Pinned Top 4 sits above the full grid, full-poster cards with a star tier ribbon, year and runtime overlay. Hover any card to reveal Pin / Add to list buttons.

<p align="center">
  <img alt="Top 4 Movies and full poster grid" src="assets/screenshots/top4-and-grid.png" />
</p>

### Top 4 Movies (detail)
Per-type Top 4: 4 movies, 4 series, 4 episodes, max 12 total. Each pinned slot shows its rank, star tier, year and runtime, and hovering reveals an × remove button.

<p align="center">
  <img alt="Top 4 Movies detail" src="assets/screenshots/top4-movies.png" />
</p>

### Reviews feed
Server-wide vertical feed of every rating that has a written review, with poster, reviewer, star bar, date and review text. (Reviewer name and review text blurred in this screenshot.)

<p align="center">
  <img alt="Reviews feed" src="assets/screenshots/reviews-feed.png" />
</p>

### Letterboxd sync
Letterboxd-compatible sync pane: username input, hourly auto-sync toggle, one-click Sync Now (pulls RSS ratings + watchlist + likes scrape), ZIP drop zone for full exports, Import Top 4 from your public profile, Diagnose button for matcher diagnostics, and Clean dead ratings to purge zombie entries after a library rebuild.

<p align="center">
  <img alt="Letterboxd sync settings" src="assets/screenshots/letterboxd-settings.png" />
</p>

---

## Requirements

- **Jellyfin 10.11.x** (built against 10.11.6)
- A modern browser (Chromium / Firefox / Safari)
- TV/webOS clients are supported through focusable D-pad controls; exact focus styling can vary by client theme
- Reverse proxies may use Jellyfin's **BaseURL** setting (for example `/jelly`); StarTrack applies it to injected assets, plugin APIs and native Jellyfin media requests
- For Letterboxd auto-sync: a Letterboxd account with **public** profile, watchlist and likes pages
- For Trakt or Simkl sync: an admin-created OAuth application for the selected service

StarTrack uses ASP.NET Core middleware to inject its widget at runtime. No File Transformation plugin required.

---

## Installation

### Option A - Plugin Repository *(recommended)*

1. Jellyfin → **Dashboard → Plugins → Repositories → +**
2. Add:
   ```
   https://raw.githubusercontent.com/ZL154/jellyfin-plugin-startrack/main/manifest.json
   ```
3. Go to **Catalogue**, find **StarTrack**, install it, and **restart Jellyfin**.

### Option B - Manual

1. Download `Jellyfin.Plugin.InternalRating_*.zip` from [Releases](https://github.com/ZL154/jellyfin-plugin-startrack/releases)
2. Extract the DLL into your Jellyfin plugins folder:
   ```
   <jellyfin-data>/plugins/StarTrack/Jellyfin.Plugin.InternalRating.dll
   ```
3. Restart Jellyfin.

### Verify

After restarting, visit:
```
https://your-jellyfin-server[/your-baseurl]/Plugins/StarTrack/Debug
```

You should see `Plugin loaded: YES`. Then open any Movie, TV Show, or Episode detail page: the `☆ Rate` pill and one StarTrack badge should appear, and clicking either should open the rating panel.

---

## How to use

### Rating controls (detail page)
A floating pill appears at the bottom-right of every Movie / Series / Episode detail page. StarTrack also places one clickable badge alongside Jellyfin's native ratings. Click either control - or focus it with a keyboard/TV D-pad and press Enter/OK - to:
- Set your rating (half-star precision)
- Optionally write a review
- See the community average and per-user breakdown
- One-click **❤ like**, **☆ watchlist**, or **★ pin to Top 4**

Admins can use a compact rating-only badge, move poster badges to any corner and choose Normal, Large or Large (TV only) sizing. Users can override the size for their current device from **My Ratings → ⚙ Preferences**. On TV, unrated media keeps a dimmed `☆ Rate` badge so the remote always has a focus target.

### My Ratings overlay
Click **My Ratings** in the Jellyfin sidebar to open the full overlay. The view selector at the top has seven tabs:

- **★ Films** - your full rating grid + pinned Top 4 row
- **☆ Watchlist** - your watchlist, with a toggle to view everyone's combined watchlist filtered by user
- **♡ Liked** - every film you've hearted
- **📖 Diary** - chronological journal with rewatches and visual star bars
- **✍ Reviews** - server-wide review feed
- **✨ For you** - personalised recommendations
- **📃 Lists** - your and others' collaborative lists

Each view supports search, sort, type filter, and (where applicable) star filter.

### Letterboxd sync
1. Export your data from letterboxd.com → **Settings → Import & Export → Export Your Data**
2. In StarTrack, click the **⚙ Letterboxd** button in the topbar
3. Enter your Letterboxd username, optionally enable hourly auto-sync, save
4. Drop the export ZIP into the upload box - ratings, diary, watchlist and likes import in one pass
5. Click **⭐ Import Top 4** to scrape your Letterboxd profile's favourite films
6. Click **Sync now** any time to pull your latest ratings + watchlist + likes via RSS / HTML scrape

### External sync (Trakt / Simkl / Yamtrack)
1. **Admin (one-time):** Dashboard → Plugins → StarTrack - paste a **Trakt** client ID + secret and/or a **Simkl** client ID (create a free app on each service's developer page). This is server-wide; users don't need their own keys.
2. In *My Ratings*, open the **⇄ External Sync** panel.
3. **Connect** a service:
   - **Trakt** - click connect, you'll get a code to enter at `trakt.tv/activate`.
   - **Simkl** - click connect and approve the PIN.
4. Choose a **direction** for that service: Off / Export only / Import only / Two-way.
5. Click **Sync** (or just wait - it auto-syncs every 10 minutes). In two-way mode the newer rating wins on each side.
6. **Backfill** (optional) seeds the service from your whole library in one pass, and marks rated items watched.
7. **Yamtrack:** click **⇩ Yamtrack CSV** to download an IMDb-format file, then in Yamtrack go to **Import → IMDb** and upload it. (Yamtrack has no public rating API yet - see notes above.)

> Ratings only import for items that exist in your Jellyfin library; titles you rated on a service but don't own can't be matched.

### Native ratings, review limits and automatic backups

Open **Dashboard → Plugins → StarTrack** to configure the server-wide controls:

1. Enable **Also save ratings to Jellyfin's native rating field** to mirror future StarTrack ratings from 0.5–5★ into Jellyfin's 0–10 per-user field. Removing the StarTrack rating clears its mirrored native value.
2. Use **Backfill existing ratings → Backfill now** to copy older ratings. The admin-only operation is idempotent, so it is safe to rerun.
3. Set **Maximum review length** anywhere from 1 to 10,000 characters.
4. For automatic backups, set `AutoExportDaily` to `true` and `AutoExportFormat` to `csv` or `json` in the plugin configuration. The v1.6.4 visual admin form does not expose these two advanced fields yet. The task runs at 03:00 UTC and writes to `<jellyfin-data>/data/InternalRating/exports/`.
5. Configure Trakt/Simkl app credentials once; users connect their own accounts and choose their own sync direction.

If Jellyfin is hosted below a reverse-proxy path, set Jellyfin's normal BaseURL. StarTrack automatically uses that prefix; no separate StarTrack path setting is required.

---

## API Reference

All endpoints are under `/Plugins/StarTrack/`. Every endpoint requires Jellyfin authentication unless otherwise noted.

### Ratings
| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/Ratings/{itemId}` | Average + every user's rating and review |
| `POST` | `/Ratings/{itemId}` | Submit / update your rating `{"stars":4,"review":"..."}` |
| `DELETE` | `/Ratings/{itemId}` | Remove your rating |
| `GET` | `/MyRatings?limit=N` | All your rated items, newest first |
| `GET` | `/Recent?limit=N` | Recent ratings across all users |
| `GET` | `/Stats` | Server-wide rating count |
| `GET` | `/ExportCsv` | Download your ratings as Letterboxd-compatible CSV |
| `POST` | `/BackfillNativeRatings` | Copy all existing StarTrack ratings into Jellyfin's native per-user field (elevated admin only; idempotent) |

### Watchlist / liked / favourites
| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/MyWatchlist` | Your watchlist |
| `GET` | `/EveryonesWatchlist` | Every user's watchlist aggregated, with per-item user lists |
| `POST` | `/Watchlist/{itemId}` | Add an item to your watchlist |
| `DELETE` | `/Watchlist/{itemId}` | Remove an item |
| `GET` | `/MyLikes` | Your liked films |
| `POST` | `/Likes/{itemId}` | Like an item |
| `DELETE` | `/Likes/{itemId}` | Unlike an item |
| `GET` | `/MyFavorites` | Your Top 4 (max 12 across types) |
| `POST` | `/MyFavorites` | Replace your favourites `{"itemIds":[...]}` |
| `GET` | `/Interactions/{itemId}` | Combined watchlisted/liked/favourite status for one item |
| `GET` | `/Recommendations?limit=N` | Personalised picks weighted by your top genres |

### Diary
| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/MyDiary?limit=N` | Your chronological diary with rewatches |
| `POST` | `/Diary` | Manually add a diary entry (`{"itemId","watchedAt","stars","review","rewatch"}`) |
| `DELETE` | `/Diary/{entryId}` | Remove a diary entry |

### Lists
| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/Lists` | All lists on the server |
| `POST` | `/Lists` | Create a list `{"name","description","collaborative"}` |
| `GET` | `/Lists/{listId}` | Single list with all items |
| `DELETE` | `/Lists/{listId}` | Delete (owner only) |
| `POST` | `/Lists/{listId}/Items` | Add a film `{"itemId":"..."}` |
| `DELETE` | `/Lists/{listId}/Items/{itemId}` | Remove a film |

### Letterboxd sync
| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/Letterboxd/Settings` | Your Letterboxd username + auto-sync state |
| `POST` | `/Letterboxd/Settings` | Save username + auto-sync toggle |
| `POST` | `/Letterboxd/SyncNow` | Pull ratings (RSS) + watchlist (RSS) + likes (HTML scrape) |
| `POST` | `/Letterboxd/Import` | Upload ZIP or CSV - auto-extracts ratings.csv, diary.csv, watchlist.csv, likes/films.csv |
| `POST` | `/Letterboxd/ScrapeFavorites` | Scrape your Letterboxd profile's "favourite films" section |
| `POST` | `/Letterboxd/Cleanup` | Purge ratings whose library item no longer has a file on disk |
| `GET` | `/Letterboxd/Diagnose` | Library matcher diagnostic + sample of normalised titles |

### External sync *(new in 1.6)*
All under `/Plugins/StarTrack/ExternalSync/`. `{provider}` is `trakt`, `simkl`, or `yamtrack`.
| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/Status` | Connection state + direction for every provider |
| `POST` | `/{provider}/StartAuth` | Begin device-code / PIN login |
| `POST` | `/{provider}/PollAuth` | Poll for auth completion + store token |
| `POST` | `/Yamtrack/Connect` | Connect Yamtrack with a base URL + token |
| `POST` | `/{provider}/SetDirection` | Set Off / ExportOnly / ImportOnly / TwoWay |
| `POST` | `/{provider}/Sync` | Run a sync now |
| `POST` | `/{provider}/BackfillWatched` | One-shot library backfill (marks watched / repairs dates) |
| `POST` | `/{provider}/Disconnect` | Remove the stored token for a provider |
| `GET` | `/Export?format=csv\|json\|imdb\|yamtrack` | Download Letterboxd CSV (default), JSON, or IMDb/Yamtrack-compatible CSV |
| `POST` | `/Import?format=csv\|json` | Import ratings from an uploaded CSV/JSON body (5 MB limit) |

### Members & social *(new in 1.5)*
| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/Members` | All visible members with avatar, totals, Top 4, follow flag |
| `GET` | `/MembersSearch?q=` | Same as `/Members`, filtered by name |
| `GET` | `/Members/{userId}/Profile` | Light profile bundle (recents, top-4, watchlist, likes, diary preview) |
| `GET` | `/Members/{userId}/Stats` | Heavy stats - histogram, genres, directors, actors, decades, hours, calendar heatmap, on-this-day, most-rewatched, year cards |
| `GET` | `/Members/{userId}/Reviews` | All of a user's reviews |
| `GET` | `/Members/{userId}/Followers` / `/Following` | Follow graph |
| `POST` | `/Members/{userId}/Follow` / `/Unfollow` | Follow / unfollow a member |
| `GET` | `/Activity?scope=following\|everyone&limit=N` | Chronological feed of recent ratings + diary + reviews |
| `GET` | `/Compare?a={userIdA}&b={userIdB}` | Pearson similarity, both histograms, biggest disagreements, films-each-loved |
| `GET` | `/MyPrivacy` | Your privacy flags (hide from members / follower count / following / stats / activity) |
| `POST` | `/MyPrivacy` | Update your privacy flags |

### Config & translations *(new in 1.4)*
| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/PublicConfig` | Admin toggle state + supported languages (no auth) |
| `GET` | `/Translations/{lang}` | JSON translation bundle for a language (no auth) |
| `GET` | `/AdminConfig` | Current plugin configuration (admin only) |
| `POST` | `/AdminConfig` | Save plugin configuration (admin only) |

### Misc
| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/Widget` | Embedded widget JavaScript (no auth) |
| `GET` | `/StandalonePage?token=...&userId=...` | Self-contained My Ratings WebView shell; fetchable before it seeds the supplied Jellyfin credentials |
| `GET` | `/WhoAmI` | Auth debug for the current session |
| `GET` | `/Debug` | Diagnostic report (no auth) |

---

## Data storage

All data is stored as plain JSON in `<jellyfin-data>/data/InternalRating/`:

| File | Contents |
|---|---|
| `ratings.json` | Per-item rating + review per user |
| `user_interactions.json` | Per-user watchlist, liked films, Top 4 favourites |
| `diary.json` | Chronological diary entries with rewatch flag |
| `lists.json` | All collaborative lists |
| `letterboxd.json` | Per-user Letterboxd sync settings (username, auto-sync, last-synced state) |
| `follows.json` *(new in 1.5)* | Per-user follow graph |
| `privacy.json` *(new in 1.5)* | Per-user privacy flags (hide stats / activity / followers / etc) |
| `external-sync.json` *(new in 1.6)* | Per-user, per-provider external-sync settings: direction, OAuth tokens, last-sync state |
| `exports/<userId>-<date>.csv\|json` *(optional)* | Daily per-user rating backups when automatic export is enabled |

Back them up or migrate them like any other data file - no database required. Native-rating mirroring supplements these files; StarTrack's JSON remains the source of truth.

---

## Building from source

```bash
git clone https://github.com/ZL154/jellyfin-plugin-startrack.git
cd jellyfin-plugin-startrack/InternalRatingSystem
dotnet publish -c Release -o ./publish_out
```

The compiled DLL is at `publish_out/Jellyfin.Plugin.InternalRating.dll`.

> **Version note:** The plugin must be compiled against the `Jellyfin.Controller` NuGet package version that exactly matches your server. The csproj currently targets `10.11.6`. Update `<PackageReference>` if your server version differs.

---

## Contributing

Issues and pull requests are welcome.

- [Report a bug](https://github.com/ZL154/jellyfin-plugin-startrack/issues)
- [Suggest a feature](https://github.com/ZL154/jellyfin-plugin-startrack/discussions)

---

## Earlier release highlights

The current 1.6.x changes stay at the top of [Features](#features). Highlights from older feature releases live here so the main feature reference remains easy to scan.

### New in 1.5.2

- **Standalone page endpoint** - `GET /Plugins/StarTrack/StandalonePage` returns a self-contained HTML page that hosts the StarTrack overlay without the surrounding Jellyfin web shell. It is designed for WebViews, mobile apps, smart-TV launchers, third-party clients and kiosk displays. The shell accepts `?token=...&userId=...`, seeds Jellyfin's credential storage, loads the existing widget and opens My Ratings without changing normal Jellyfin Web behaviour.

### New in 1.5

- **Members tab** - every visible server user gets a profile card with avatar, rating total, average and pinned Top 4 strip, with grid sorting and search.
- **Profile pages and statistics** - rating histograms, genres, directors, actors, decades, longest film, hours watched, monthly activity, a 365-day heatmap, on-this-day, most-rewatched and year cards.
- **Follow graph and activity feed** - follow/unfollow members and browse ratings, reviews and diary activity from followed users or everyone.
- **Reviews profile tab** - browse an individual member's written reviews.
- **Profile comparison** - Pearson taste similarity mapped to 0–100%, side-by-side histograms, biggest disagreements and films both users loved.
- **Privacy controls** - hide a profile from Members or hide follower counts, following, statistics and recent activity; enforcement is server-side.
- **Private lists** - list owners can keep a list visible only to themselves.
- **Diary polish** - clearer `↻ Rewatch` pills and a denser three-column row layout.
- **Performance and security** - stale-while-revalidate profile/stat caches, pre-warmed member statistics, shared item/people caches and endpoint hardening.

### New in 1.4

- **Eight-language i18n** - English, French, Spanish, German, Italian, Portuguese, Chinese and Japanese across the widget, panel, overlay and admin page.
- **Per-device language and preferences** - change language, hide the floating Rate pill and hide the post-playback prompt from **My Ratings → ⚙ Preferences**.
- **Admin presentation controls** - default language; global Recent, Letterboxd and Rate-pill visibility; native/Media Bar rating replacement; poster overlays; and post-playback prompts.
- **Poster rating badges** - gold StarTrack ratings on library cards.
- **Media detail and Media Bar replacement** - substitute StarTrack averages for Jellyfin's native community rating and compatible Media Bar rating pills.
- **Post-playback rating prompt** - ask for a rating after a movie or episode completes, skipping items already rated.

---

## ❤ Support the project

StarTrack is built and maintained in my spare time. If it's useful to you and you'd like to support ongoing development, any of these means a lot:

- ⭐ **Star this repo** - it's free and helps others find it
- 💖 **[Sponsor on GitHub](https://github.com/sponsors/ZL154)** - one-off or monthly, every dollar reaches the project
- ☕ **[Buy me a coffee on Ko-fi](https://ko-fi.com/zl154)** - one-off tips

Not expected, just appreciated. Contributions - issues, PRs, translation fixes - are equally valuable.

---

## License

[MIT](LICENSE) - © 2025 ZL154

---

<div align="center">
<sub>Built by ZL154 · AI-assisted development with <a href="https://claude.ai/claude-code">Claude Code</a></sub>
</div>
