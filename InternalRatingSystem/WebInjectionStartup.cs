using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Data;
using Jellyfin.Plugin.InternalRating.ExternalSync;
using Jellyfin.Plugin.InternalRating.ExternalSync.Providers;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating
{
    /// <summary>Registers plugin services and the HTTP middleware startup filter.</summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
        {
            // Primary injection: IStartupFilter inserts our middleware at the very front
            // of Jellyfin's ASP.NET Core pipeline. This intercepts every index.html
            // HTTP response and injects the widget script tag — no file permissions
            // or third-party plugins required.
            services.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>();

            // Fallback: also try patching index.html on disk in case middleware
            // somehow doesn't reach it (very unusual setups).
            services.AddHostedService<WebInjectionService>();

            // Records a diary entry when playback finishes. Server-side so it
            // catches every client, not just the web UI.
            services.AddHostedService<PlaybackDiaryService>();

            // Expose the existing repositories as DI singletons so controllers
            // and services can request them via constructor injection. Both are
            // constructed by Plugin.cs with the ApplicationPaths the base class
            // provides, so we just forward the already-built instances.
            services.AddSingleton<RatingRepository>(_ => Plugin.Instance!.Repository);
            services.AddSingleton<LetterboxdSettingsRepository>(_ => Plugin.Instance!.LetterboxdSettings);
            services.AddSingleton<UserInteractionsRepository>(_ => Plugin.Instance!.Interactions);
            services.AddSingleton<DiaryRepository>(_ => Plugin.Instance!.Diary);
            services.AddSingleton<ListsRepository>(_ => Plugin.Instance!.Lists);

            // Letterboxd sync service — gets ILibraryManager + logger from DI,
            // repositories from the singletons above.
            services.AddSingleton<LetterboxdSyncService>();

            // Scheduled task: register as IScheduledTask so Jellyfin's task
            // scheduler picks it up and runs it hourly by default.
            services.AddSingleton<IScheduledTask, LetterboxdSyncTask>();

            // ---- Letterboxd write-back (v1.6.5) ----
            // Ledger of diary entries already pushed. Its own JSON file rather
            // than part of letterboxd.json: it grows with the user's library and
            // the settings store is cloned on every read.
            services.AddSingleton<LetterboxdPushLedger>(sp =>
                new LetterboxdPushLedger(sp.GetRequiredService<IApplicationPaths>()));

            // Push orchestrator. Takes IRatingGatherer/ILikedGatherer registered
            // further down for ExternalSync — same local data, different sink.
            // Diary + watchlist readers and the id resolver are optional
            // constructor deps, so DI supplies the real ones here while tests
            // can leave them null or fake them.
            services.AddSingleton<IWatchDiaryReader>(_ => Plugin.Instance!.Diary);
            services.AddSingleton<IWatchlistReader>(_ => Plugin.Instance!.Interactions);
            services.AddSingleton<LetterboxdPushService>();

            // Runner owns the session lifetime + credential decryption; the task
            // and the controller both drive pushes through it.
            services.AddSingleton<LetterboxdPushRunner>();
            services.AddSingleton<IScheduledTask, LetterboxdPushTask>();

            // ---- Serializd (TV ratings) ----
            // Shares the Letterboxd push ledger under its own key namespace, and
            // the same Data Protection key ring for the stored password.
            services.AddSingleton<Serializd.SerializdSettingsRepository>(_ => Plugin.Instance!.SerializdSettings);
            services.AddSingleton<Serializd.ISerializdGatherer, Serializd.SerializdGatherer>();
            services.AddSingleton<Serializd.SerializdPushService>();
            services.AddSingleton<Serializd.SerializdPushRunner>();
            services.AddSingleton<IScheduledTask, Serializd.SerializdPushTask>();

            // Import half. Needs no credentials at all - the Serializd diary
            // endpoint is public - so it is registered independently of the
            // push path and works for a user who only ever typed a username.
            services.AddSingleton<Serializd.SerializdPullService>();
            services.AddSingleton<Serializd.SerializdSyncRunner>();
            services.AddSingleton<IScheduledTask, Serializd.SerializdSyncTask>();

            // ---- ExternalSync services ----
            // FileExportService has no dependencies (pure serialisation helper).
            services.AddSingleton<FileExportService>();

            // ExternalIdResolver needs ILibraryManager from the host DI.
            services.AddSingleton<ExternalIdResolver>();
            // Expose as the interface so RatingGatherer (and tests) can consume it.
            services.AddSingleton<IExternalIdResolver>(sp => sp.GetRequiredService<ExternalIdResolver>());

            // IRatingReader backed by the existing RatingRepository singleton.
            services.AddSingleton<IRatingReader>(_ => Plugin.Instance!.Repository);

            // RatingGatherer depends on IRatingReader + IExternalIdResolver — both above.
            services.AddSingleton<RatingGatherer>();

            // ExternalSyncSettingsRepository — constructed by Plugin.cs, forwarded here.
            services.AddSingleton<ExternalSyncSettingsRepository>(_ => Plugin.Instance!.ExternalSyncSettings);

            // Scheduled task: daily auto-export.
            services.AddSingleton<IScheduledTask, AutoExportTask>();

            // ---- OAuth + external-provider sync (Tasks 15–16 + DI) ----

            // Ensure IHttpClientFactory is available (no-op if Jellyfin already registers it).
            services.AddHttpClient();

            // TraktProvider: creds default to live PluginConfiguration at call time.
            services.AddSingleton<IExternalRatingProvider>(sp =>
                new TraktProvider(MakeApiClient(sp)));

            // SimklProvider: creds default to live PluginConfiguration at call time.
            services.AddSingleton<IExternalRatingProvider>(sp =>
                new SimklProvider(MakeApiClient(sp)));

            // YamtrackProvider: uses conn.BaseUrl + conn.ApiToken (no plugin-level config).
            services.AddSingleton<IExternalRatingProvider>(sp =>
                new YamtrackProvider(MakeApiClient(sp)));

            // DeviceCodeOAuth: stateless helper, one instance per plugin lifetime.
            services.AddSingleton<DeviceCodeOAuth>(sp =>
                new DeviceCodeOAuth(MakeApiClient(sp)));

            // IRatingGatherer → RatingGatherer (already registered as concrete above).
            services.AddSingleton<IRatingGatherer>(sp => sp.GetRequiredService<RatingGatherer>());

            // ILikedGatherer → LikedGatherer (liked items for watched/liked library sync).
            services.AddSingleton<ILikedGatherer>(sp =>
                new LikedGatherer(Plugin.Instance!.Interactions, sp.GetRequiredService<IExternalIdResolver>()));

            // IWatchedGatherer → WatchedGatherer (reads Jellyfin played-state for the
            // one-shot watched-history backfill).
            services.AddSingleton<IWatchedGatherer, WatchedGatherer>();

            // IRatingSink → Plugin.Instance.Repository (the concrete RatingRepository
            // implements both IRatingReader and IRatingSink).
            services.AddSingleton<IRatingSink>(_ => Plugin.Instance!.Repository);

            // SyncOrchestrator: depends on IRatingGatherer, IRatingSink, IExternalIdResolver, ILogger.
            services.AddSingleton<SyncOrchestrator>();

            // Scheduled task: hourly external-rating sync.
            services.AddSingleton<IScheduledTask, ExternalSyncTask>();
        }

        /// <summary>
        /// Creates an HttpClient for external rating APIs with a non-empty
        /// User-Agent. REQUIRED: Trakt (and others) sit behind Cloudflare, which
        /// returns HTTP 403 Forbidden for requests with no User-Agent — .NET's
        /// HttpClient sends none by default, which is what broke "Connect Trakt".
        /// </summary>
        private static HttpClient MakeApiClient(IServiceProvider sp)
        {
            var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
            if (!client.DefaultRequestHeaders.UserAgent.TryParseAdd("StarTrack-Jellyfin/1.5 (+https://github.com/ZL154/jellyfin-plugin-startrack)"))
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "StarTrack-Jellyfin/1.5");
            return client;
        }
    }

    /// <summary>
    /// Fallback hosted service: patches index.html on disk.
    /// The primary injection is handled by ScriptInjectionMiddleware.
    /// </summary>
    public class WebInjectionService : IHostedService
    {
        private const string Marker    = "<!-- startrack-widget -->";
        private static string ScriptTag => WidgetAsset.ScriptTag;

        // Diagnostics for the debug endpoint
        public static string DiagWebPath     = "not set";
        public static bool   DiagIndexFound;
        public static bool   DiagIndexPatched;
        public static string DiagPatchedPath = "none";
        public static string DiagLastError   = "none";
        public static string DiagFtStatus    = "not used (middleware is primary)";

        private readonly IApplicationPaths _appPaths;
        private readonly ILogger<WebInjectionService> _logger;

        public WebInjectionService(IApplicationPaths appPaths, ILogger<WebInjectionService> logger)
        {
            _appPaths = appPaths;
            _logger   = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            DiagWebPath = _appPaths.WebPath ?? "null";
            _logger.LogInformation("[StarTrack] v1.0.9 WebInjectionService starting (fallback). WebPath={P}", _appPaths.WebPath);

            await TryPatchIndexHtmlAsync().ConfigureAwait(false);

            _ = Task.Run(async () =>
            {
                await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                if (!DiagIndexPatched)
                    await TryPatchIndexHtmlAsync().ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// Removes every trace of a previous injection: the marker comment, an
        /// intact StarTrack script tag, and the malformed self-referential tags
        /// left behind by the cleanup this replaces. That last one is a repair
        /// rather than prevention — servers patched by an earlier build still
        /// have those orphans sitting in index.html, one per widget update, each
        /// costing a wasted full-page download and a console error.
        /// </summary>
        /// <summary>Pattern for the broken tags an earlier build left behind.</summary>
        private const string OrphanPattern = @"<script[^>]*src=""\?v=[0-9a-fA-F]+""[^>]*>\s*</script>";

        /// <summary>True when index.html still carries wreckage that needs clearing.</summary>
        internal static bool HasOrphanedTags(string html) =>
            Regex.IsMatch(html, OrphanPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static string StripInjection(string html)
        {
            const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

            html = Regex.Replace(html, @"<!--\s*startrack-widget\s*-->", string.Empty, Opts);

            // A well-formed StarTrack tag, with or without a base-path prefix.
            html = Regex.Replace(
                html,
                @"<script[^>]*src=""[^""]*/Plugins/StarTrack/Widget\?v=[^""]*""[^>]*>\s*</script>",
                string.Empty, Opts);

            // The orphans: src is nothing but the cache-busting query, so the
            // browser fetches the current page and parses HTML as a script.
            html = Regex.Replace(html, OrphanPattern, string.Empty, Opts);

            return html;
        }

        private async Task TryPatchIndexHtmlAsync()
        {
            var candidates = new[]
            {
                Path.Combine(_appPaths.WebPath, "index.html"),
                "/usr/share/jellyfin/web/index.html",
                "/usr/lib/jellyfin/web/index.html",
                "/jellyfin/jellyfin-web/index.html",
                "/var/lib/jellyfin/web/index.html"
            };

            foreach (var path in candidates)
            {
                try
                {
                    if (!File.Exists(path)) continue;

                    DiagIndexFound = true;
                    var html = await File.ReadAllTextAsync(path).ConfigureAwait(false);

                    if (html.Contains(Marker, StringComparison.Ordinal))
                    {
                        // Already injected. If the current (content-hashed) script tag is
                        // present we're up to date. Otherwise the token is stale (the widget
                        // changed since the last patch) — strip the old injection and fall
                        // through to re-add the fresh one, which busts browser/CDN caches.
                        // "Up to date" has to mean the CURRENT tag is present AND
                        // no wreckage is left beside it. Checking only the tag
                        // meant a server whose widget never changed again kept
                        // its orphans for good, because the repair below only
                        // ran when the token moved.
                        if (html.Contains(ScriptTag, StringComparison.Ordinal) && !HasOrphanedTags(html))
                        {
                            DiagIndexPatched = true;
                            DiagPatchedPath  = path;
                            return;
                        }
                        // The regex this replaces matched the PATH rather than the
                        // whole tag, so it deleted "/Plugins/StarTrack/Widget" out
                        // of the src and left <script src="?v=abc123"></script>
                        // behind. That resolves to index.html itself, so the browser
                        // re-downloaded the page and tried to parse HTML as
                        // JavaScript — one "Unexpected token '<'" per orphan, in
                        // every browser. One accumulated on disk per widget update
                        // and nothing ever cleaned them up.
                        html = StripInjection(html);
                    }

                    if (!html.Contains("</body>", StringComparison.OrdinalIgnoreCase)) continue;

                    await File.WriteAllTextAsync(path,
                        html.Replace("</body>", $"{Marker}{ScriptTag}</body>", StringComparison.OrdinalIgnoreCase))
                        .ConfigureAwait(false);

                    DiagIndexPatched = true;
                    DiagPatchedPath  = path;
                    _logger.LogInformation("[StarTrack] Patched index.html at {P}", path);
                    return;
                }
                catch (UnauthorizedAccessException uex)
                {
                    DiagLastError = uex.Message;
                }
                catch (Exception ex)
                {
                    DiagLastError = ex.Message;
                }
            }
        }
    }
}
