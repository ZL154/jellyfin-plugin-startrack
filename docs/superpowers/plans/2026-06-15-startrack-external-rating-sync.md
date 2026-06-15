# StarTrack External Rating Sync (Trakt / Simkl / Yamtrack + File Export) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let StarTrack two-way-sync user ratings with external services — push StarTrack ratings out to **Trakt**, **Simkl**, and **Yamtrack** and pull theirs in — plus a universal **CSV/JSON export** (the only "export to Letterboxd" path and a daily backup). Resolves issue [#7](https://github.com/ZL154/jellyfin-plugin-startrack/issues/7).

**Architecture:** A shared `IExternalRatingProvider` abstraction (auth + pull + push) behind a `ProviderId` enum, a single per-user/per-provider `ExternalSyncSettings` store (mirrors `LetterboxdSettingsRepository`), a shared `ExternalIdResolver` (Jellyfin `ProviderIds` ⇄ StarTrack `ItemId`) and `RatingScale` mapper (StarTrack 0.5–5.0 ⇄ service 1–10), a shared device-code OAuth helper for Trakt/Simkl, one `ExternalSyncTask : IScheduledTask` iterating all enabled providers, and a `FileExportService` for CSV/JSON. Each provider is a thin adapter; the file export has zero external deps and ships first.

**Tech Stack:** .NET 9, Jellyfin 10.11 plugin APIs (`ILibraryManager`, `IScheduledTask`, `IServerApplicationHost`), `System.Text.Json`, `System.Net.Http.HttpClient`. No new NuGet packages. Follows the existing `Letterboxd/` subsystem patterns verbatim.

---

## ⚠️ Prerequisites (do BEFORE Phase 2 — owner: ZL154, not the implementer)

These block the live API providers but **not** Phase 0/1 (infra + file export), which should ship first regardless.

1. **Register a Trakt API app** at <https://trakt.tv/oauth/applications> → record `client_id` + `client_secret`. Redirect URI `urn:ietf:wg:oauth:2.0:oob` (device flow needs no real redirect). These ship in the plugin (device-code flow, like every other Trakt-integrating app).
2. **Register a Simkl API app** at <https://simkl.com/settings/developer/> → record `client_id` (+ secret). Simkl uses a PIN/device flow.
3. **Verify the Yamtrack API** — Yamtrack is self-hosted (Django). Confirm whether it exposes a **ratings read/write API** (token-auth) or only CSV import/export + webhooks. This gates Task 20 (Yamtrack = API two-way *if* an endpoint exists, else CSV-export-only). Research task is Task 19.
4. **Confirm StarTrack's rating scale** — code shows `Stars` as a `double`; the Letterboxd importer maps 0.5–5.0 half-stars. **Task 1 verifies** the min/max/step from `RatingController` + the rating UI before the scale mapper is written. If it is NOT 0.5–5.0, update `RatingScale` constants in Task 3.

> Store the Trakt/Simkl secrets the same way the codebase already handles config — NOT committed to the repo. Decide during Task 11 whether the shipped `client_id` lives in source (typical for device-flow public clients) or in plugin config.

---

## File Structure

New subsystem under `InternalRatingSystem/ExternalSync/` (mirrors `Letterboxd/`):

| File | Responsibility |
|---|---|
| `ExternalSync/IExternalRatingProvider.cs` | Provider contract: `ProviderId`, `EnsureTokenAsync`, `PullRatingsAsync`, `PushRatingsAsync`. |
| `ExternalSync/ExternalSyncSettings.cs` | Per-user/per-provider settings + state model + store wrapper + result DTOs + `ExternalRating` neutral record. |
| `ExternalSync/ExternalSyncSettingsRepository.cs` | JSON persistence (`<data>/InternalRating/external-sync.json`). Mirrors `LetterboxdSettingsRepository`. |
| `ExternalSync/ExternalIdResolver.cs` | Maps StarTrack `ItemId` (Jellyfin GUID) ⇄ external IDs (IMDB/TMDB/TVDB) via `ILibraryManager.GetItemById(...).ProviderIds`; resolves an inbound external ID back to a library item. |
| `ExternalSync/RatingScale.cs` | Pure functions: `ToService10(double stars)` / `FromService10(int rating)`. |
| `ExternalSync/DeviceCodeOAuth.cs` | Shared device-code/PIN flow helper (request code → poll token → refresh). |
| `ExternalSync/RatingGatherer.cs` | Reads a user's StarTrack ratings → `ExternalRating[]` (via `IRatingReader` + `ExternalIdResolver`). |
| `ExternalSync/FileExportService.cs` | CSV (Letterboxd-compatible) + JSON export; CSV/JSON import. |
| `ExternalSync/SyncOrchestrator.cs` | Per-user/per-provider pull+push + dedup. |
| `ExternalSync/Providers/TraktProvider.cs` | Trakt adapter. |
| `ExternalSync/Providers/SimklProvider.cs` | Simkl adapter. |
| `ExternalSync/Providers/YamtrackProvider.cs` | Yamtrack adapter (API or CSV per Task 19). |
| `ExternalSync/ExternalSyncTask.cs` | `IScheduledTask` — iterate users × enabled providers, pull+push. |
| `ExternalSync/AutoExportTask.cs` | `IScheduledTask` — daily CSV/JSON backup. |
| `Controllers/ExternalSyncController.cs` | Endpoints: start-auth, poll-auth, status, manual-sync, export-download, import-upload, disconnect. |
| `Pages/external-sync.html` (+ JS) | Settings UI (connect buttons, device-code display, direction toggles, export). Mirror the Letterboxd settings page. |

Modified:
- `WebInjectionStartup.cs` — register the new services + `IScheduledTask`s.
- `Plugin.cs` — construct `ExternalSyncSettingsRepository` (like `LetterboxdSettings`).
- `PluginConfiguration.cs` — global toggles (e.g. `HideExternalSyncButton`, shipped Trakt/Simkl client IDs if source-stored).
- `Data/RatingRepository.cs` — add an `IRatingReader` interface (Task 8) for testable export.
- Tests project (create if none exists — Task 2 checks).

---

## Phase 0 — Shared infrastructure

### Task 1: Confirm rating scale + repo read/write surface (investigation, no code)

**Files:** read-only — `Controllers/RatingController.cs`, `Data/RatingRepository.cs`, the rating-submit UI JS.

- [ ] **Step 1:** Open `Controllers/RatingController.cs` and the rating submit UI; record the exact min, max, and step of `Stars` (expected 0.5–5.0, half-steps).
- [ ] **Step 2:** Confirm `RatingRepository.GetUserRatingsAsync(userId)` returns `{ItemId, Stars, Review, RatedAt}` and `SaveRatingAsync(itemId,userId,userName,stars,review,ratedAt)` upserts. (Both verified present during planning.)
- [ ] **Step 3:** Record findings as a comment block at the top of `ExternalSync/RatingScale.cs` (created in Task 3). No commit yet.

### Task 2: Test project bootstrap

**Files:** check for `*.Tests.csproj`; create `InternalRatingSystem.Tests/InternalRatingSystem.Tests.csproj` if absent.

- [ ] **Step 1:** `ls` the repo for an existing test project. If one exists, skip to Task 3.
- [ ] **Step 2 (only if absent): Create** `InternalRatingSystem.Tests/InternalRatingSystem.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../InternalRatingSystem/InternalRatingSystem.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Run** `dotnet test` — expect "no tests" success (project builds).
- [ ] **Step 4: Commit** `git add -A && git commit -m "test: bootstrap InternalRatingSystem.Tests"`.

### Task 3: RatingScale mapper (pure, TDD)

**Files:** Create `ExternalSync/RatingScale.cs`; Test `InternalRatingSystem.Tests/RatingScaleTests.cs`.

- [ ] **Step 1: Write the failing test** `RatingScaleTests.cs`:

```csharp
using Jellyfin.Plugin.InternalRating.ExternalSync;
using Xunit;

public class RatingScaleTests
{
    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(2.5, 5)]
    [InlineData(5.0, 10)]
    public void ToService10_DoublesStars(double stars, int expected)
        => Assert.Equal(expected, RatingScale.ToService10(stars));

    [Theory]
    [InlineData(1, 0.5)]
    [InlineData(5, 2.5)]
    [InlineData(10, 5.0)]
    public void FromService10_HalvesRating(int rating, double expected)
        => Assert.Equal(expected, RatingScale.FromService10(rating));

    [Fact]
    public void ToService10_Clamps() => Assert.Equal(10, RatingScale.ToService10(7.0));
}
```

- [ ] **Step 2: Run** `dotnet test --filter RatingScaleTests` — expect FAIL (type missing).
- [ ] **Step 3: Implement** `ExternalSync/RatingScale.cs`:

```csharp
using System;

namespace Jellyfin.Plugin.InternalRating.ExternalSync
{
    /// <summary>StarTrack stores 0.5–5.0 half-stars; Trakt/Simkl use integer 1–10.</summary>
    public static class RatingScale
    {
        public static int ToService10(double stars)
            => Math.Clamp((int)Math.Round(stars * 2, MidpointRounding.AwayFromZero), 1, 10);

        public static double FromService10(int rating)
            => Math.Clamp(rating, 1, 10) / 2.0;
    }
}
```

- [ ] **Step 4: Run** `dotnet test --filter RatingScaleTests` — expect PASS.
- [ ] **Step 5: Commit** `git commit -am "feat(extsync): rating-scale mapper (0.5–5.0 ⇄ 1–10)"`.

### Task 4: Provider contract + settings model

**Files:** Create `ExternalSync/IExternalRatingProvider.cs`, `ExternalSync/ExternalSyncSettings.cs`.

- [ ] **Step 1: Create** `ExternalSync/ExternalSyncSettings.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.InternalRating.ExternalSync
{
    public enum SyncDirection { Off = 0, ExportOnly = 1, ImportOnly = 2, TwoWay = 3 }

    /// <summary>One external account a user has connected (Trakt/Simkl/Yamtrack).</summary>
    public sealed class ProviderConnection
    {
        [JsonPropertyName("direction")]     public SyncDirection Direction { get; set; } = SyncDirection.Off;
        [JsonPropertyName("accessToken")]   public string? AccessToken { get; set; }
        [JsonPropertyName("refreshToken")]  public string? RefreshToken { get; set; }
        [JsonPropertyName("tokenExpiresAt")]public DateTime? TokenExpiresAt { get; set; }
        // Yamtrack: base URL + API token instead of OAuth.
        [JsonPropertyName("baseUrl")]       public string? BaseUrl { get; set; }
        [JsonPropertyName("apiToken")]      public string? ApiToken { get; set; }
        [JsonPropertyName("lastSyncedAt")]  public DateTime? LastSyncedAt { get; set; }
        [JsonPropertyName("lastPushed")]    public int LastPushed { get; set; }
        [JsonPropertyName("lastPulled")]    public int LastPulled { get; set; }
        [JsonPropertyName("lastError")]     public string? LastError { get; set; }
    }

    /// <summary>providerKey ("Trakt"/"Simkl"/"Yamtrack") → connection.</summary>
    public sealed class ExternalSyncUserSettings
    {
        [JsonPropertyName("providers")]
        public Dictionary<string, ProviderConnection> Providers { get; set; } = new();
    }

    public sealed class ExternalSyncStore
    {
        [JsonPropertyName("users")]
        public Dictionary<string, ExternalSyncUserSettings> Users { get; set; } = new();
    }

    public sealed class SyncResult
    {
        [JsonPropertyName("pushed")]  public int Pushed { get; set; }
        [JsonPropertyName("pulled")]  public int Pulled { get; set; }
        [JsonPropertyName("skipped")] public int Skipped { get; set; }
        [JsonPropertyName("error")]   public string? Error { get; set; }
    }

    /// <summary>One rating in neutral form, used across providers + file export.</summary>
    public sealed record ExternalRating(
        string? Imdb, int? Tmdb, int? Tvdb,
        string Title, int? Year, string MediaType /* "movie"|"show"|"episode" */,
        double Stars, DateTime RatedAt);
}
```

- [ ] **Step 2: Create** `ExternalSync/IExternalRatingProvider.cs`:

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.InternalRating.ExternalSync
{
    public enum ProviderId { Trakt, Simkl, Yamtrack }

    public interface IExternalRatingProvider
    {
        ProviderId Id { get; }

        /// <summary>Pull the user's ratings FROM the service (neutral form).</summary>
        Task<IReadOnlyList<ExternalRating>> PullRatingsAsync(ProviderConnection conn, CancellationToken ct);

        /// <summary>Push StarTrack ratings TO the service. Returns count accepted.</summary>
        Task<int> PushRatingsAsync(ProviderConnection conn, IReadOnlyList<ExternalRating> ratings, CancellationToken ct);

        /// <summary>Refresh the access token if expired/expiring. Returns true if conn was mutated.</summary>
        Task<bool> EnsureTokenAsync(ProviderConnection conn, CancellationToken ct);
    }
}
```

- [ ] **Step 3: Run** `dotnet build InternalRatingSystem/InternalRatingSystem.csproj` — expect success (no consumers yet).
- [ ] **Step 4: Commit** `git commit -am "feat(extsync): provider contract + settings model"`.

### Task 5: Settings repository (persistence)

**Files:** Create `ExternalSync/ExternalSyncSettingsRepository.cs` (mirror `Letterboxd/LetterboxdSettingsRepository.cs`); Test `ExternalSyncSettingsRepositoryTests.cs`.

- [ ] **Step 1:** Open `Letterboxd/LetterboxdSettingsRepository.cs`; copy its structure (ctor takes `IApplicationPaths`, JSON file under `<data>/InternalRating/`, `SemaphoreSlim` lock, atomic write-then-rename, `GetAllAsync`).
- [ ] **Step 2: Write the failing test** (round-trip a connection) with a minimal `TestPaths : IApplicationPaths` stub returning a `Path.GetTempPath()`-based dir:

```csharp
[Fact]
public async Task SaveThenGet_RoundTrips()
{
    var repo = new ExternalSyncSettingsRepository(new TestPaths());
    await repo.SetConnectionAsync("user1", "Trakt", new ProviderConnection { Direction = SyncDirection.TwoWay, AccessToken = "tok" });
    var got = await repo.GetConnectionAsync("user1", "Trakt");
    Assert.Equal(SyncDirection.TwoWay, got!.Direction);
    Assert.Equal("tok", got.AccessToken);
}
```

- [ ] **Step 3: Run** — FAIL.
- [ ] **Step 4: Implement** `ExternalSyncSettingsRepository` with `GetAllAsync()`, `GetConnectionAsync(userId, providerKey)`, `SetConnectionAsync(userId, providerKey, conn)`, `RemoveConnectionAsync(userId, providerKey)` over `ExternalSyncStore`, persisting to `<data>/InternalRating/external-sync.json` using the Letterboxd repo's exact lock + atomic-write pattern.
- [ ] **Step 5: Run** — PASS. **Commit.**

### Task 6: ExternalIdResolver (TDD with a fake library manager)

**Files:** Create `ExternalSync/ExternalIdResolver.cs`; Test `ExternalIdResolverTests.cs`.

- [ ] **Step 1: Write the failing test** — fake `ILibraryManager` whose item has `ProviderIds = {Imdb:"tt0111161", Tmdb:"278"}`; `ResolveExternalIds(itemId)` returns those; `FindItemId(rating with Imdb=tt0111161)` returns that item's GUID string.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement** `ExternalIdResolver(ILibraryManager)`:
  - `ExternalRating? ResolveExternalIds(string itemId, double stars, DateTime ratedAt)` — `GetItemById(Guid.Parse(itemId))`, read `item.ProviderIds["Imdb"/"Tmdb"/"Tvdb"]`, `item.Name`, `item.ProductionYear`, map type → "movie"/"show"/"episode"; null if item missing.
  - `string? FindItemId(ExternalRating r)` — query library by IMDB/TMDB (`ILibraryManager.GetItemList` with a `HasAnyProviderId`/provider filter), fall back to normalized title+year.
- [ ] **Step 4: Run** — PASS. **Commit.**

> If Letterboxd's title-normalizer is private in `LetterboxdSyncService`, copy the normalization method into `ExternalIdResolver` with a `// DRY-debt: shared with LetterboxdSyncService` comment rather than refactoring the 1314-line service mid-feature.

---

## Phase 1 — File export/import (ships first, zero external deps)

### Task 7: FileExportService — CSV (Letterboxd format) + JSON (TDD)

**Files:** Create `ExternalSync/FileExportService.cs`; Test `FileExportServiceTests.cs`.

- [ ] **Step 1: Write the failing test:** given two `ExternalRating`s, `BuildLetterboxdCsv(ratings)` returns header `Date,Name,Year,Rating` (Rating 0.5–5.0) + one line each; `BuildJson(ratings)` returns a parseable JSON array; `ParseCsv(BuildLetterboxdCsv(x))` round-trips count + stars.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement** `FileExportService`: `BuildLetterboxdCsv` (header `Date,Name,Year,Rating`, date `yyyy-MM-dd`, CSV-escape names containing quotes/commas), `BuildJson`, `ParseCsv`, `ParseJson`.
- [ ] **Step 4: Run** — PASS. **Commit.**

### Task 8: RatingGatherer + IRatingReader (TDD)

**Files:** Create `ExternalSync/RatingGatherer.cs`; Modify `Data/RatingRepository.cs` (add `IRatingReader`); Test `RatingGathererTests.cs`.

- [ ] **Step 1:** Extract `public interface IRatingReader { Task<List<UserRatingEntry>> GetUserRatingsAsync(string userId, int limit = 10000); }`; implement it on `RatingRepository` (method already exists — just declare the interface on the class).
- [ ] **Step 2: Write the failing test:** fake `IRatingReader` (2 entries) + fake resolver → `GatherAsync(userId)` returns 2 `ExternalRating`s with mapped IDs + preserved `Stars`/`RatedAt`; entries whose item no longer resolves are skipped.
- [ ] **Step 3: Run** — FAIL.
- [ ] **Step 4: Implement** `RatingGatherer(IRatingReader, ExternalIdResolver)` → `GatherAsync(userId)`.
- [ ] **Step 5: Run** — PASS. **Commit.**

### Task 9: Export/import endpoints

**Files:** Create `Controllers/ExternalSyncController.cs` (export/import actions only this task); Modify `WebInjectionStartup.cs`.

- [ ] **Step 1:** `GET /ExternalSync/Export?format=csv|json` → `RatingGatherer.GatherAsync(currentUser)` → `FileExportService.Build*` → `File(bytes, contentType, filename)`. Auth: mirror `RatingController`'s auth attributes + current-user resolution.
- [ ] **Step 2:** `POST /ExternalSync/Import` (multipart) → parse CSV/JSON → for each, `ExternalIdResolver.FindItemId` → `RatingRepository.SaveRatingAsync(...)` (skip if same stars already present). Return a `SyncResult`.
- [ ] **Step 3:** Register `FileExportService`, `RatingGatherer`, `ExternalIdResolver` as singletons in `WebInjectionStartup`.
- [ ] **Step 4:** Build; manual smoke (curl the export endpoint with a session token) — verify CSV downloads.
- [ ] **Step 5: Commit** `feat(extsync): CSV/JSON export + import endpoints`.

### Task 10: Daily auto-export option

**Files:** Modify `PluginConfiguration.cs` (`AutoExportDaily`, `AutoExportFormat`); create `ExternalSync/AutoExportTask.cs`.

- [ ] Add config props → implement `IScheduledTask` (daily trigger) iterating users with ratings → write `<data>/InternalRating/exports/<user>-<yyyy-MM-dd>.csv|json` → register in `WebInjectionStartup`. Build + commit.

---

## Phase 2 — Trakt (the live-provider template)

### Task 11: DeviceCodeOAuth helper (TDD against a stub HttpMessageHandler)

**Files:** Create `ExternalSync/DeviceCodeOAuth.cs`; Test `DeviceCodeOAuthTests.cs`.

- [ ] **Step 1: Write the failing test:** stub handler returns Trakt's `/oauth/device/code` JSON (`device_code`,`user_code`,`verification_url`,`interval`,`expires_in`), then `/oauth/device/token` → 400 `authorization_pending` once, then 200 with `access_token`/`refresh_token`/`expires_in`. Assert `RequestCodeAsync` surfaces `user_code`+`verification_url`, and `PollTokenAsync` returns tokens after the pending response.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement** `DeviceCodeOAuth` with `RequestCodeAsync(...)`, `PollTokenAsync(...)`, `RefreshAsync(...)`, generic over endpoint URLs/headers so Simkl reuses it. Take an injected `HttpClient` (or `IHttpClientFactory`) so tests stub it.

> **Verify live before trusting the parser:** Trakt device flow — `POST https://api.trakt.tv/oauth/device/code`, `POST .../oauth/device/token`, headers `trakt-api-version: 2`, `trakt-api-key: <client_id>`. Confirm exact JSON field names with one real call.

- [ ] **Step 4: Run** — PASS. **Commit.**

### Task 12: TraktProvider.PushRatingsAsync (TDD against stub handler)

**Files:** Create `ExternalSync/Providers/TraktProvider.cs`; Test `TraktProviderTests.cs`.

- [ ] **Step 1: Write the failing test:** stub asserts `POST https://api.trakt.tv/sync/ratings` body shape `{ "movies":[{ "ids":{"imdb":"tt..."}, "rating":8, "rated_at":"..." }], "shows":[...] }` and returns Trakt's `added` summary; `PushRatingsAsync(conn,[movieRating])` returns 1.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement** push: group `ExternalRating` by `MediaType` into `movies`/`shows`; map IDs (`imdb`/`tmdb`); `rating = RatingScale.ToService10(stars)`; `rated_at = RatedAt` ISO-8601; send with `Authorization: Bearer` + trakt headers; parse `added.movies + added.shows`.
- [ ] **Step 4: Run** — PASS. **Commit.**

### Task 13: TraktProvider.PullRatingsAsync + EnsureTokenAsync

**Files:** same `TraktProvider`; extend `TraktProviderTests.cs`.

- [ ] **Step 1: Write the failing tests:** (a) stub `GET /sync/ratings/movies` + `/sync/ratings/shows` returns Trakt rating items → `PullRatingsAsync` returns neutral `ExternalRating`s with `Stars = FromService10(rating)`; (b) `EnsureTokenAsync` with an expired `TokenExpiresAt` calls refresh and mutates `conn`.
- [ ] **Step 2–4:** Run-fail → implement (`GET /sync/ratings/{type}`, map `ids`+`rating`+`rated_at`; `EnsureTokenAsync` refreshes when `TokenExpiresAt <= now+5min`) → run-pass → **commit.**

### Task 14: SyncOrchestrator (per-user, per-provider pull+push + dedup)

**Files:** Create `ExternalSync/SyncOrchestrator.cs`; Test `SyncOrchestratorTests.cs`.

- [ ] **Step 1: Write the failing test:** fake provider + fake `IRatingReader`/`RatingGatherer`; for `TwoWay`, orchestrator pulls (writes new ratings via `SaveRatingAsync`, skipping ones already at the same stars) and pushes (sends repo ratings the service lacks). Assert push/pull counts + dedup-by-(external-id,stars).
- [ ] **Step 2–4:** Run-fail → implement `SyncOneAsync(userId, providerKey, conn, provider)`: `EnsureTokenAsync` (persist if mutated); Import/TwoWay → pull → `FindItemId` → `SaveRatingAsync` (skip unchanged); Export/TwoWay → `GatherAsync` → diff against pulled set → `PushRatingsAsync`; update `conn` state. → run-pass → **commit.**

### Task 15: Auth + status + sync endpoints

**Files:** Modify `Controllers/ExternalSyncController.cs`; Modify `WebInjectionStartup.cs`.

- [ ] **Step 1:** `POST /ExternalSync/{provider}/StartAuth` → `DeviceCodeOAuth.RequestCodeAsync` → return `{userCode, verificationUrl, deviceCode, interval}` (cache `deviceCode` server-side per user, short TTL).
- [ ] **Step 2:** `POST /ExternalSync/{provider}/PollAuth` → `PollTokenAsync` → on success persist tokens into `ProviderConnection`; return status.
- [ ] **Step 3:** `GET /ExternalSync/Status` → per-provider state (connected?, direction, lastSyncedAt, lastError) — **never** return tokens.
- [ ] **Step 4:** `POST /ExternalSync/{provider}/Sync` → `SyncOrchestrator.SyncOneAsync` now. `POST /ExternalSync/{provider}/SetDirection`. `POST /ExternalSync/{provider}/Disconnect` → `RemoveConnectionAsync`.
- [ ] **Step 5:** Register `TraktProvider` + provider lookup (`IEnumerable<IExternalRatingProvider>` or a keyed dict), `DeviceCodeOAuth`, `SyncOrchestrator` in DI. Build + **commit.**

### Task 16: ExternalSyncTask (scheduled)

**Files:** Create `ExternalSync/ExternalSyncTask.cs`; Modify `WebInjectionStartup.cs`.

- [ ] Mirror `LetterboxdSyncTask`: `IScheduledTask`, interval trigger (default 1h — APIs rate-limit harder than RSS), iterate `GetAllAsync()` users × providers with `Direction != Off`, call `SyncOrchestrator.SyncOneAsync`, report progress, swallow per-provider errors into `conn.LastError`. Register as `IScheduledTask`. Build + **commit.**

### Task 17: Trakt UI (settings page)

**Files:** Create `Pages/external-sync.html` (+ inline JS) modeled on the Letterboxd settings page.

- [ ] Connect button → `StartAuth` → show `user_code` + "go to trakt.tv/activate" → poll `PollAuth` until connected → show status + Direction dropdown (Off/Export/Import/Two-way) → "Sync now" + "Disconnect" + "Export CSV/JSON". Reuse Letterboxd page styling. Manual browser smoke. **Commit.**

---

## Phase 3 — Simkl (adapter; reuses Phase 0–2 infra)

### Task 18: SimklProvider (TDD against stub handler, per method)

**Files:** Create `ExternalSync/Providers/SimklProvider.cs`; Test `SimklProviderTests.cs`.

- [ ] Reuse `DeviceCodeOAuth` (Simkl PIN flow: `GET https://api.simkl.com/oauth/pin?client_id=...` → poll `GET /oauth/pin/{code}?client_id=...`). Implement `PullRatingsAsync` + `PushRatingsAsync` (`POST https://api.simkl.com/sync/ratings`, `{movies:[{ids,rating}], shows:[...]}`, rating 1–10; headers `simkl-api-key: <client_id>`, `Authorization: Bearer`). Same neutral-form + `RatingScale`. TDD each method against a stub, register in DI, add Simkl to the same UI component, **commit** per method.

> Verify Simkl's exact ratings endpoint + response shape against one live call — Simkl's docs are thinner than Trakt's.

---

## Phase 4 — Yamtrack (research-gated)

### Task 19: Yamtrack API research (no code)

- [ ] Determine from Yamtrack docs/source whether it exposes a token-auth **ratings** read+write API. Record findings in this plan file.
  - **If yes:** Task 20 = `YamtrackProvider` (token auth via `ProviderConnection.BaseUrl`+`ApiToken`, no OAuth) implementing pull+push, TDD'd against a stub handler.
  - **If no (CSV/webhook only):** Yamtrack "export" = a `BuildYamtrackCsv` added to `FileExportService` (match Yamtrack's import columns); skip the live provider and note it on issue #7.

### Task 20: YamtrackProvider OR Yamtrack CSV (per Task 19)

- [ ] Implement whichever Task 19 selected. TDD. Register/expose in UI if a live provider. **Commit.**

---

## Phase 5 — Polish, i18n, docs, release

### Task 21: i18n

- [ ] Add all new UI strings to every locale the plugin ships (find the real translation source — repo has a `startrack_strings.json` scratch artifact; locate the in-repo source). Mirror StarTrack's existing i18n mechanism.

### Task 22: Global config + button exposure

- [ ] `PluginConfiguration`: `HideExternalSyncButton`; expose the settings page entry the same way the Letterboxd one is exposed. Build.

### Task 23: Full build + test + manual smoke matrix

- [ ] `dotnet build` clean (match the csproj's warning settings). `dotnet test` all green. Manual on the home server: connect Trakt, rate in StarTrack, Sync, confirm on Trakt; rate on Trakt, Sync, confirm in StarTrack. Repeat for Simkl. Export CSV → import into a fresh user.

### Task 24: Release (StarTrack release runbook — user-approved, post-smoke only)

- [ ] Version bump (`InternalRatingSystem.csproj` + `meta.json`), release notes, README, tag, manifest. **Do NOT tag/release/manifest without explicit user approval after a home-server smoke test.**

---

## Self-Review

**Spec coverage:** #7 = (a) write StarTrack ratings TO Trakt/Simkl/Letterboxd/Yamtrack, (b) two-way where possible, (c) daily export-file fallback. → Push = Tasks 12/18/20; Pull = Tasks 13/14; Letterboxd export = Phase 1 CSV (Letterboxd has no write API — documented); daily file = Task 10; all-three = Phases 2/3/4; device-code OAuth = Task 11. ✅

**Open risks flagged as explicit verification tasks (not placeholders):** exact Trakt/Simkl JSON shapes (verify live in Tasks 11–13/18), Yamtrack API existence (Task 19 gate), StarTrack rating scale (Task 1 gate), test project existence (Task 2 gate), `RatingRepository` testability (`IRatingReader` extraction, Task 8).

**Type consistency:** `ExternalRating`, `ProviderConnection`, `SyncDirection`, `SyncResult`, `RatingScale.ToService10/FromService10`, `IExternalRatingProvider.{PullRatingsAsync,PushRatingsAsync,EnsureTokenAsync}`, `ExternalIdResolver.{ResolveExternalIds,FindItemId}`, `RatingGatherer.GatherAsync`, `SyncOrchestrator.SyncOneAsync` are used consistently across Tasks 3–20.

**Sequencing:** Phase 0 (infra) → Phase 1 (file export, independently shippable) → Phase 2 (Trakt, full template) → Phase 3 (Simkl adapter) → Phase 4 (Yamtrack, gated) → Phase 5 (polish/release). Each phase produces working, testable software.
