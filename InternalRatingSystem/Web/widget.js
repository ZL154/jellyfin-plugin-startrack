(function () {
    'use strict';

    console.log('[StarTrack] widget.js loaded — v1.4.6');

    // ── i18n + config runtime (StarTrack v1.4) ────────────────────────────
    // Runtime translation for UI text. We load the translation bundle once
    // from /Plugins/StarTrack/Translations/{lang} and every piece of text
    // that was previously hardcoded now flows through tr().
    //
    // Strategy for gradually i18n-ing the widget without rewriting every
    // string site: we also run a DOM scrub pass after renders that swaps
    // English literals (exact match against en.json values) to the active
    // language. tr(key) is used for new/modified strings; the scrub pass
    // handles the 200+ pre-existing literals.

    var _STARTRACK_CONFIG = {
        language:                  'en',
        hideRecentButton:          false,
        hideLetterboxdButton:      false,
        rateButtonOnlyInMediaItem: false,
        replaceMediaDetailsRating: true,
        replaceMediaBarRating:     true,
        showRatingsOnPosters:      true,
        postPlaybackRatingPopup:   true,
        communityRecentMode:       false,
        hiddenOverlayViews:        [],
        supportedLanguages:        ['en','fr','es','de','it','pt','zh','ja']
    };

    var _STARTRACK_STRINGS_EN = null;   // en.json (reference for substitution)
    var _STARTRACK_STRINGS    = null;   // active language bundle
    var _STARTRACK_SWAPMAP    = null;   // { englishValue: localizedValue }
    var _STARTRACK_READY      = false;

    function _userLangOverride() {
        try { return localStorage.getItem('startrack_lang') || null; } catch (e) { return null; }
    }

    // Per-user preferences live in localStorage, not on the server —
    // they're for "I personally don't want the Rate pill cluttering
    // my home screen" overrides that should survive refreshes but
    // only affect this browser / user.
    function _userPrefs() {
        try {
            var raw = localStorage.getItem('startrack_user_prefs');
            if (raw) return JSON.parse(raw);
        } catch (e) {}
        return {};
    }
    function _saveUserPrefs(p) {
        try { localStorage.setItem('startrack_user_prefs', JSON.stringify(p || {})); } catch (e) {}
    }
    window.StarTrackUserPrefs = _userPrefs;
    window.StarTrackSaveUserPrefs = _saveUserPrefs;
    function setLanguage(lang) {
        try { localStorage.setItem('startrack_lang', lang); } catch (e) {}
        loadTranslations(lang).then(function () {
            try { scrubTranslations(document.body); } catch (e) {}
        });
    }
    window.StarTrackSetLanguage = setLanguage;

    function tr(key, vars, fallback) {
        var s = (_STARTRACK_STRINGS && _STARTRACK_STRINGS[key]) ||
                (_STARTRACK_STRINGS_EN && _STARTRACK_STRINGS_EN[key]);
        // Key missing from both bundles → use the caller's fallback,
        // or return the key as a last resort so the UI has *something*
        // to render. Never return null/undefined because esc(null) ==
        // "null" and would surface as literal text in the DOM.
        if (!s) s = fallback || key;
        if (vars) {
            s = s.replace(/\{(\w+)\}/g, function (_, k) {
                return (k in vars) ? String(vars[k]) : ('{' + k + '}');
            });
        }
        return s;
    }
    window.StarTrackTr = tr;

    // Set of canonical English strings we know how to translate — used to
    // identify text nodes on first visit that should be tagged with their
    // original English so we can always re-translate from canonical (not
    // whatever the current rendered language happens to be).
    function buildSwapMap() {
        if (!_STARTRACK_STRINGS_EN) { _STARTRACK_SWAPMAP = {}; return; }
        var m = {};
        for (var key in _STARTRACK_STRINGS_EN) {
            var en = _STARTRACK_STRINGS_EN[key];
            var tr = (_STARTRACK_STRINGS && _STARTRACK_STRINGS[key]) || en;
            if (typeof en === 'string' && typeof tr === 'string' && en.indexOf('{') === -1) {
                m[en] = tr;
            }
        }
        _STARTRACK_SWAPMAP = m;
    }

    function _isKnownEnglish(t) {
        return !!(t && _STARTRACK_SWAPMAP && _STARTRACK_SWAPMAP.hasOwnProperty(t));
    }

    // Two-phase scrub.
    //   Phase 1 (tag): for every text node whose trimmed value matches a
    //     known English string we haven't tagged yet, stash the English in
    //     a synthetic property on the node (`_stOrig`) plus — for attribute
    //     lookups — a data attr on the parent element.
    //   Phase 2 (paint): for every tagged node, set its text to the
    //     current language's translation of the stashed English.
    // Calling this after changing languages always picks up from the
    // canonical English baseline, so switching fr → de → en → ja always
    // works instead of only working once.
    function scrubTranslations(root) {
        if (!_STARTRACK_SWAPMAP || !root) return;
        var map = _STARTRACK_SWAPMAP;
        var enStrings = _STARTRACK_STRINGS_EN || {};
        var active = _STARTRACK_STRINGS || enStrings;

        // Text nodes
        var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null, false);
        var n, txt, t;
        while ((n = walker.nextNode())) {
            txt = n.nodeValue;
            if (!txt) continue;
            if (n._stOrig) {
                // Already tagged: repaint from canonical English → current lang.
                // We replace the currently-painted text (tracked in _stPaint)
                // with the new translation, preserving surrounding whitespace.
                var canonical = n._stOrig;
                var prev = n._stPaint || canonical;
                var target = _translateCanonical(canonical, enStrings, active);
                if (prev !== target && txt.indexOf(prev) !== -1) {
                    n.nodeValue = txt.split(prev).join(target);
                }
                n._stPaint = target;
                continue;
            }
            t = txt.trim();
            if (t && _isKnownEnglish(t)) {
                // First visit: tag + paint
                n._stOrig = t;
                var translated = _translateCanonical(t, enStrings, active);
                n._stPaint = translated;
                n.nodeValue = txt.replace(t, translated);
            }
        }

        // Attributes
        var attrs = ['placeholder', 'title', 'aria-label'];
        var nodes = root.querySelectorAll('[placeholder],[title],[aria-label]');
        for (var i = 0; i < nodes.length; i++) {
            var el = nodes[i];
            for (var j = 0; j < attrs.length; j++) {
                var a = attrs[j], v = el.getAttribute(a);
                if (!v) continue;
                var stashKey = 'stOrig-' + a;
                var original = el.dataset[stashKey];
                if (!original && _isKnownEnglish(v)) {
                    original = v;
                    el.dataset[stashKey] = v;
                }
                if (original) {
                    var tr = _translateCanonical(original, enStrings, active);
                    if (v !== tr) el.setAttribute(a, tr);
                }
            }
        }
    }

    function _translateCanonical(englishText, enStrings, activeStrings) {
        // Find the key that maps to this English value, then look it up in
        // the active language bundle. If the active bundle doesn't have it,
        // fall back to the English canonical.
        for (var k in enStrings) {
            if (enStrings[k] === englishText) {
                return activeStrings[k] || englishText;
            }
        }
        return englishText;
    }

    function _previousPaint(node) {
        return node._stPaint || null;
    }

    function loadTranslations(lang) {
        return fetch('/Plugins/StarTrack/Translations/' + encodeURIComponent(lang))
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (j) {
                if (j) _STARTRACK_STRINGS = j;
                buildSwapMap();
            })
            .catch(function () {});
    }

    function loadEnglishThenActive(lang) {
        return fetch('/Plugins/StarTrack/Translations/en')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (en) {
                _STARTRACK_STRINGS_EN = en || {};
                if (lang === 'en') { _STARTRACK_STRINGS = _STARTRACK_STRINGS_EN; buildSwapMap(); return; }
                return loadTranslations(lang);
            });
    }

    function loadPublicConfig() {
        return fetch('/Plugins/StarTrack/PublicConfig', { cache: 'no-store' })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (cfg) {
                if (cfg) {
                    for (var k in cfg) _STARTRACK_CONFIG[k] = cfg[k];
                }
            })
            .catch(function () {});
    }

    function startI18nWatchdog() {
        if (!_STARTRACK_SWAPMAP) return;
        // Sweep periodically to catch async-rendered pieces (overlay, lists…).
        setInterval(function () {
            try {
                var el = document.getElementById('ir-widget');
                if (el) scrubTranslations(el);
                var ov = document.getElementById('ir-overlay');
                if (ov) scrubTranslations(ov);
            } catch (e) {}
        }, 1500);
    }

    init();

    // ── Auth ──────────────────────────────────────────────────────────────

    function getCredentials() {
        var c = window.ApiClient;
        if (c) {
            var t = typeof c.accessToken === 'function' ? c.accessToken() : c._accessToken;
            var u = typeof c.getCurrentUserId === 'function' ? c.getCurrentUserId()
                  : (c._currentUser && c._currentUser.Id) || c.currentUserId;
            if (t) return { token: t, userId: u || null };
        }
        var cm = window.connectionManager;
        if (cm && typeof cm.currentApiClient === 'function') {
            var cc = cm.currentApiClient();
            if (cc) {
                var t2 = typeof cc.accessToken === 'function' ? cc.accessToken() : cc._accessToken;
                var u2 = typeof cc.getCurrentUserId === 'function' ? cc.getCurrentUserId()
                       : (cc._currentUser && cc._currentUser.Id) || cc.currentUserId;
                if (t2) return { token: t2, userId: u2 || null };
            }
        }
        try {
            var raw = localStorage.getItem('jellyfin_credentials');
            if (raw) {
                var obj = JSON.parse(raw), servers = obj.Servers || obj.servers || [], origin = window.location.origin;
                for (var i = 0; i < servers.length; i++) {
                    var s = servers[i], addr = s.LocalAddress || s.ManualAddress || s.RemoteAddress || '';
                    if (addr && addr.indexOf(origin) !== -1 && (s.AccessToken || s.accessToken))
                        return { token: s.AccessToken || s.accessToken, userId: s.UserId || s.userId || null };
                }
                for (var j = 0; j < servers.length; j++) {
                    var sv = servers[j], tk = sv.AccessToken || sv.accessToken;
                    if (tk) return { token: tk, userId: sv.UserId || sv.userId || null };
                }
            }
        } catch (e) {}
        return null;
    }

    function getAuth()   { var c = getCredentials(); return c ? 'MediaBrowser Token="' + c.token + '"' : null; }
    function getUserId() { var c = getCredentials(); return c ? c.userId : null; }

    // ── Utilities ─────────────────────────────────────────────────────────

    function esc(s) { var d = document.createElement('div'); d.appendChild(document.createTextNode(String(s))); return d.innerHTML; }

    function timeAgo(iso) {
        var s = Math.floor((Date.now() - new Date(iso)) / 1000);
        if (s < 60) return 'just now';
        if (s < 3600) return Math.floor(s / 60) + 'm ago';
        if (s < 86400) return Math.floor(s / 3600) + 'h ago';
        return Math.floor(s / 86400) + 'd ago';
    }

    function formatRuntime(ticks) {
        if (!ticks) return '';
        var m = Math.floor(ticks / 600000000), h = Math.floor(m / 60), rem = m % 60;
        return h > 0 ? h + 'h ' + rem + 'm' : rem + 'm';
    }

    function starsText(v) { return v ? v.toFixed(1) + ' ★' : ''; }

    // ── URL helpers ───────────────────────────────────────────────────────

    function getItemId() {
        var src = decodeURIComponent(window.location.hash + '&' + window.location.search);
        var m = src.match(/[?&#]id=([0-9a-f]{20,})/i);
        return m ? m[1] : null;
    }

    function getItemType(id) {
        var auth = getAuth(), uid = getUserId();
        if (!auth || !uid) return Promise.resolve(null);
        return fetch('/Users/' + uid + '/Items/' + id, { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (item) { return item ? item.Type : null; })
            .catch(function () { return null; });
    }

    // ── Jellyfin metadata (bulk) ──────────────────────────────────────────

    // Session-level cache of item metadata so repeated view switches
    // (Films → Watchlist → back to Films) don't re-fetch everything.
    // Invalidated implicitly by page reload.
    var _metaCache = {};

    function getItemsMeta(ids) {
        var auth = getAuth(), uid = getUserId();
        if (!auth || !uid || !ids.length) return Promise.resolve({});

        // Filter down to ids we don't already have cached
        var needed = [];
        var result = {};
        ids.forEach(function (id) {
            if (_metaCache[id]) result[id] = _metaCache[id];
            else needed.push(id);
        });
        if (needed.length === 0) return Promise.resolve(result);

        var batches = [], bs = 100;
        for (var i = 0; i < needed.length; i += bs) batches.push(needed.slice(i, i + bs));
        return Promise.all(batches.map(function (batch) {
            return fetch(
                '/Users/' + uid + '/Items?Ids=' + batch.join(',') +
                '&Fields=RunTimeTicks,ProductionYear,CommunityRating,Genres,Tags&Limit=' + batch.length,
                { headers: { Authorization: auth } }
            ).then(function (r) { return r.ok ? r.json() : null; }).catch(function () { return null; });
        })).then(function (results) {
            results.forEach(function (res) {
                if (!res || !res.Items) return;
                res.Items.forEach(function (item) {
                    // Detect anime via either the Genres or Tags arrays.
                    // Jellyfin convention: anime series/movies get the
                    // genre "Anime" — but some libraries tag instead.
                    var genres = item.Genres || [];
                    var tags   = item.Tags   || [];
                    var isAnime = false;
                    for (var gi = 0; gi < genres.length; gi++) {
                        if (genres[gi] && genres[gi].toLowerCase().indexOf('anime') !== -1) { isAnime = true; break; }
                    }
                    if (!isAnime) {
                        for (var ti = 0; ti < tags.length; ti++) {
                            if (tags[ti] && tags[ti].toLowerCase().indexOf('anime') !== -1) { isAnime = true; break; }
                        }
                    }
                    var meta = {
                        name: item.Name || 'Unknown',
                        year: item.ProductionYear || 0,
                        runtime: item.RunTimeTicks || 0,
                        communityRating: item.CommunityRating || 0,
                        imageTag: item.ImageTags && item.ImageTags.Primary ? item.ImageTags.Primary : null,
                        type: item.Type || 'Unknown',
                        isAnime: isAnime
                    };
                    _metaCache[item.Id] = meta;
                    result[item.Id] = meta;
                });
            });
            return result;
        });
    }

    // ── StarTrack API ─────────────────────────────────────────────────────

    function apiFetch(path, opts) {
        var auth = getAuth();
        if (!auth) { console.warn('[StarTrack] no auth token'); return Promise.resolve(null); }
        opts = opts || {};
        opts.headers = { Authorization: auth, 'Content-Type': 'application/json' };
        return fetch(path, opts).then(function (r) {
            if (!r.ok) console.warn('[StarTrack]', opts.method || 'GET', path, '→', r.status);
            return r.ok ? r : null;
        }).catch(function (e) { console.error('[StarTrack] fetch error:', e); return null; });
    }

    function apiGet(id)              { return apiFetch('/Plugins/StarTrack/Ratings/' + id).then(function (r) { return r ? r.json() : null; }); }
    function apiPost(id, stars, rev) { return apiFetch('/Plugins/StarTrack/Ratings/' + id, { method: 'POST', body: JSON.stringify({ stars: stars, review: rev || null }) }).then(function (r) { return r !== null; }); }
    function apiDel(id)              { return apiFetch('/Plugins/StarTrack/Ratings/' + id, { method: 'DELETE' }).then(function (r) { return r !== null; }); }
    function apiMyRatings(limit)     { return apiFetch('/Plugins/StarTrack/MyRatings' + (limit ? '?limit=' + limit : '')).then(function (r) { return r ? r.json() : null; }); }

    // ── Styles ────────────────────────────────────────────────────────────

    function injectStyles() {
        if (document.getElementById('ir-styles')) return;
        var s = document.createElement('style');
        s.id = 'ir-styles';
        s.textContent = [
            // Widget
            '#ir-widget{position:fixed!important;bottom:24px!important;right:24px!important;z-index:2147483647!important;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif!important;font-size:14px!important;line-height:1!important;box-sizing:border-box!important;display:none!important}',
            '#ir-widget.ir-on{display:block!important}',
            '#ir-widget *{box-sizing:border-box!important;font-family:inherit!important}',
            '.ir-pill{display:flex!important;align-items:center!important;gap:6px!important;cursor:pointer!important;padding:8px 16px!important;border-radius:24px!important;background:rgba(10,10,10,.93)!important;border:1px solid rgba(255,255,255,.22)!important;backdrop-filter:blur(10px)!important;box-shadow:0 4px 24px rgba(0,0,0,.65)!important;transition:background .2s,transform .15s!important;user-select:none!important;white-space:nowrap!important;color:#fff!important}',
            '.ir-pill:hover{background:rgba(30,30,30,.98)!important;transform:scale(1.05)!important}',
            '.ir-star-icon{color:#f4c430!important;font-size:1.1em!important}',
            '.ir-avg-text{font-size:.95em!important;font-weight:700!important}',
            '.ir-label{color:rgba(255,255,255,.5)!important;font-size:.75em!important;text-transform:uppercase!important;letter-spacing:.05em!important}',
            // Panel
            '.ir-panel{position:absolute!important;bottom:calc(100% + 10px)!important;right:0!important;width:320px!important;background:rgba(12,12,12,.98)!important;border:1px solid rgba(255,255,255,.16)!important;border-radius:12px!important;padding:16px!important;backdrop-filter:blur(16px)!important;box-shadow:0 -6px 40px rgba(0,0,0,.85)!important;display:none!important;color:#fff!important}',
            '.ir-panel.ir-open{display:block!important}',
            '.ir-ph{display:flex!important;align-items:center!important;gap:8px!important;padding-bottom:12px!important;border-bottom:1px solid rgba(255,255,255,.1)!important;margin-bottom:12px!important}',
            '.ir-big-star{color:#f4c430!important;font-size:2.2em!important;line-height:1!important}',
            '.ir-big-avg{color:#fff!important;font-size:2.2em!important;font-weight:700!important;line-height:1!important}',
            '.ir-count{color:rgba(255,255,255,.5)!important;font-size:.78em!important}',
            // Action buttons (like / watchlist / favorite) — sit at the right of .ir-ph
            '.ir-actions{margin-left:auto!important;display:flex!important;gap:4px!important;align-items:center!important}',
            '.ir-act{background:rgba(255,255,255,.06)!important;border:1px solid rgba(255,255,255,.15)!important;color:rgba(255,255,255,.5)!important;border-radius:6px!important;width:28px!important;height:28px!important;display:flex!important;align-items:center!important;justify-content:center!important;font-size:.95em!important;cursor:pointer!important;padding:0!important;line-height:1!important;transition:all .12s!important}',
            '.ir-act:hover{background:rgba(255,255,255,.12)!important;border-color:rgba(255,255,255,.3)!important;color:#fff!important;transform:scale(1.08)!important}',
            // Active states — heart turns red, watchlist turns gold, favorite turns gold-solid
            '.ir-act.ir-on.ir-act-like{color:#ff5068!important;border-color:rgba(255,80,104,.5)!important;background:rgba(255,80,104,.12)!important}',
            '.ir-act.ir-on.ir-act-watch{color:#f4c430!important;border-color:rgba(244,196,48,.5)!important;background:rgba(244,196,48,.12)!important}',
            '.ir-act.ir-on.ir-act-fav{color:#f4c430!important;border-color:rgba(244,196,48,.7)!important;background:rgba(244,196,48,.22)!important}',
            // Stars input
            '.ir-yr{margin-bottom:8px!important}',
            '.ir-yl{color:rgba(255,255,255,.65)!important;font-size:.85em!important;margin-bottom:6px!important;display:block!important}',
            '.ir-si{display:flex!important;gap:2px!important;margin-bottom:4px!important}',
            '.ir-sw{position:relative!important;display:inline-block!important;font-size:1.8em!important;width:1.1em!important;line-height:1!important;cursor:pointer!important;color:rgba(255,255,255,.2)!important;transition:transform .1s!important}',
            '.ir-sw::before{content:"★"!important}',
            '.ir-sw.ir-full{color:#f4c430!important}',
            '.ir-sw.ir-half::after{content:"★"!important;position:absolute!important;left:0!important;top:0!important;width:100%!important;height:100%!important;color:#f4c430!important;display:block!important;clip-path:inset(0 50% 0 0)!important}',
            '.ir-sw:hover{transform:scale(1.2)!important}',
            '.ir-sl{position:absolute!important;left:0!important;top:0!important;width:50%!important;height:100%!important;z-index:1!important}',
            '.ir-sr{position:absolute!important;left:50%!important;top:0!important;width:50%!important;height:100%!important;z-index:1!important}',
            '.ir-yc{font-size:.78em!important;color:#f4c430!important;font-weight:600!important;height:1.2em!important;display:block!important;margin-bottom:6px!important}',
            // Review textarea
            '.ir-rev-wrap{margin-bottom:8px!important}',
            '.ir-rev{width:100%!important;background:rgba(255,255,255,.05)!important;border:1px solid rgba(255,255,255,.14)!important;border-radius:6px!important;color:#fff!important;font-size:.8em!important;padding:6px 8px!important;resize:vertical!important;min-height:52px!important;outline:none!important;transition:border-color .2s!important}',
            '.ir-rev:focus{border-color:rgba(244,196,48,.5)!important}',
            '.ir-rev::placeholder{color:rgba(255,255,255,.25)!important}',
            '.ir-rev-hint{font-size:.72em!important;color:rgba(255,255,255,.3)!important;display:block!important;margin-top:2px!important;text-align:right!important}',
            // Submit row
            '.ir-submit-row{display:flex!important;align-items:center!important;gap:8px!important;margin-bottom:12px!important;flex-wrap:wrap!important}',
            '.ir-submit{background:#f4c430!important;color:#000!important;border:none!important;border-radius:6px!important;padding:6px 16px!important;font-size:.82em!important;font-weight:700!important;cursor:pointer!important;transition:all .15s!important;opacity:.35!important;pointer-events:none!important}',
            '.ir-submit.ir-ready{opacity:1!important;pointer-events:auto!important}',
            '.ir-submit.ir-ready:hover{background:#ffd84d!important;transform:scale(1.04)!important}',
            '.ir-flash{font-size:.78em!important;opacity:0!important;transition:opacity .3s!important;font-weight:600!important}',
            '.ir-flash.ir-show{opacity:1!important}',
            '.ir-rb{background:none!important;border:1px solid rgba(255,70,70,.35)!important;color:rgba(255,100,100,.75)!important;border-radius:4px!important;padding:3px 8px!important;font-size:.76em!important;cursor:pointer!important;margin-left:auto!important;transition:all .2s!important}',
            '.ir-rb:hover{background:rgba(255,40,40,.15)!important;color:#ff7070!important}',
            // Ratings list
            '.ir-tb{width:100%!important;background:none!important;border:1px solid rgba(255,255,255,.12)!important;color:rgba(255,255,255,.55)!important;border-radius:6px!important;padding:5px 10px!important;font-size:.8em!important;cursor:pointer!important;text-align:left!important;transition:all .2s!important}',
            '.ir-tb:hover{background:rgba(255,255,255,.07)!important;color:#fff!important}',
            '.ir-list{margin-top:8px!important;max-height:220px!important;overflow-y:auto!important;scrollbar-width:thin!important}',
            '.ir-li{padding:6px 2px!important;border-bottom:1px solid rgba(255,255,255,.05)!important;font-size:.86em!important}',
            '.ir-li:last-child{border-bottom:none!important}',
            '.ir-li-top{display:flex!important;align-items:center!important;gap:8px!important}',
            '.ir-ln{flex:1!important;font-weight:500!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;color:#fff!important;cursor:pointer!important}',
            '.ir-ln:hover{color:#f4c430!important;text-decoration:underline!important}',
            '.ir-ls{color:#f4c430!important;font-size:.9em!important;white-space:nowrap!important}',
            '.ir-lv{color:rgba(255,255,255,.4)!important;font-size:.72em!important;white-space:nowrap!important}',
            '.ir-li-review{font-size:.78em!important;color:rgba(255,255,255,.55)!important;margin-top:4px!important;padding:4px 6px!important;background:rgba(255,255,255,.04)!important;border-radius:4px!important;line-height:1.45!important;display:none!important;white-space:pre-wrap!important;word-break:break-word!important}',
            '.ir-li-review.ir-open{display:block!important}',
            '.ir-empty{color:rgba(255,255,255,.38)!important;text-align:center!important;padding:12px 0!important;margin:0!important;font-size:.88em!important}',
            // Recent panel
            '.ir-recent-panel{padding:0!important}',
            '.ir-rec-title{font-size:.72em!important;text-transform:uppercase!important;letter-spacing:.08em!important;color:rgba(255,255,255,.4)!important;margin-bottom:10px!important;font-weight:600!important;display:flex!important;align-items:center!important;justify-content:space-between!important}',
            '.ir-rec-open-btn{font-size:.9em!important;color:#f4c430!important;background:none!important;border:none!important;cursor:pointer!important;padding:0!important;letter-spacing:0!important;text-transform:none!important;font-weight:700!important}',
            '.ir-rec-open-btn:hover{text-decoration:underline!important}',
            '.ir-rec-list{max-height:260px!important;overflow-y:auto!important;scrollbar-width:thin!important}',
            '.ir-rec-item{display:flex!important;align-items:flex-start!important;gap:8px!important;padding:7px 4px!important;border-bottom:1px solid rgba(255,255,255,.05)!important;font-size:.84em!important;cursor:pointer!important;transition:background .15s!important;border-radius:4px!important}',
            '.ir-rec-item:last-child{border-bottom:none!important}',
            '.ir-rec-item:hover{background:rgba(255,255,255,.04)!important}',
            '.ir-rec-info{flex:1!important;overflow:hidden!important}',
            '.ir-rec-name{color:#fff!important;font-weight:600!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;margin-bottom:2px!important}',
            '.ir-rec-meta{color:rgba(255,255,255,.45)!important;font-size:.82em!important}',
            '.ir-rec-by{color:#f4c430!important;font-weight:600!important}',
            '.ir-rec-rev{font-size:.78em!important;color:rgba(255,255,255,.4)!important;margin-top:3px!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;font-style:italic!important}',
            '.ir-rec-stars{color:#f4c430!important;font-size:.92em!important;white-space:nowrap!important;font-weight:700!important;flex-shrink:0!important}',
            // Page badge
            '#ir-page-badge{display:block!important;margin-bottom:8px!important;background:rgba(10,10,10,.85)!important;border:1px solid rgba(244,196,48,.5)!important;border-radius:4px!important;padding:3px 10px!important;font-size:.82em!important;font-weight:700!important;color:#f4c430!important;cursor:pointer!important;white-space:nowrap!important;line-height:1.6!important;width:fit-content!important}',
            '#ir-page-badge:hover{background:rgba(30,30,30,.95)!important}',
            // ── My Ratings overlay — red/black theme ──────────────────────
            '#ir-overlay{position:fixed!important;top:0!important;left:0!important;right:0!important;bottom:0!important;width:100vw!important;height:100vh!important;height:100dvh!important;max-width:100vw!important;max-height:100vh!important;z-index:2147483646!important;background:#080808!important;display:none!important;flex-direction:column!important;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif!important;color:#fff!important;margin:0!important;padding:0!important;transform:none!important;box-sizing:border-box!important;overflow:hidden!important}',
            'html.ir-ov-locked,body.ir-ov-locked{overflow:hidden!important}',
            '#ir-overlay.ir-ov-open{display:flex!important}',
            // Sticky topbar
            '.ir-ov-topbar{background:#0e0e0e!important;border-bottom:2px solid #b81c1c!important;flex-shrink:0!important;padding:0 36px!important}',
            '.ir-ov-header{display:flex!important;align-items:center!important;gap:16px!important;height:64px!important;flex-wrap:wrap!important}',
            '.ir-ov-title{font-size:1.2em!important;font-weight:800!important;color:#fff!important;margin:0!important;flex:1!important;min-width:120px!important;letter-spacing:-.02em!important}',
            '.ir-ov-title-star{color:#cc2020!important;margin-right:6px!important}',
            '.ir-ov-count{font-size:.75em!important;color:rgba(255,255,255,.4)!important;white-space:nowrap!important;background:rgba(255,255,255,.06)!important;border:1px solid rgba(255,255,255,.1)!important;padding:4px 12px!important;border-radius:20px!important;font-weight:500!important}',
            '.ir-ov-sort{background:#1a1a1a!important;border:1px solid rgba(255,255,255,.12)!important;color:#fff!important;border-radius:8px!important;padding:8px 14px!important;font-size:.8em!important;cursor:pointer!important;outline:none!important;transition:border-color .15s!important}',
            '.ir-ov-sort:hover{border-color:rgba(200,30,30,.6)!important}',
            '.ir-ov-sort:focus{border-color:#cc2020!important}',
            '.ir-ov-sort option{background:#141414!important;color:#fff!important}',
            '.ir-ov-close{background:none!important;border:1px solid rgba(200,30,30,.4)!important;color:rgba(220,100,100,.8)!important;border-radius:8px!important;padding:8px 18px!important;font-size:.8em!important;font-weight:600!important;cursor:pointer!important;transition:all .15s!important;white-space:nowrap!important;letter-spacing:.02em!important}',
            '.ir-ov-close:hover{background:rgba(180,20,20,.15)!important;border-color:#cc2020!important;color:#ff8080!important}',
            // Overlay Letterboxd button + dropdown panel
            '.ir-ov-lb{background:none!important;border:1px solid rgba(244,196,48,.35)!important;color:rgba(244,196,48,.85)!important;border-radius:8px!important;padding:8px 16px!important;font-size:.8em!important;font-weight:600!important;cursor:pointer!important;transition:all .15s!important;white-space:nowrap!important;letter-spacing:.02em!important}',
            '.ir-ov-lb:hover{background:rgba(244,196,48,.12)!important;border-color:#f4c430!important;color:#fff!important}',
            '.ir-ov-prefs{background:none!important;border:1px solid rgba(255,255,255,.18)!important;color:rgba(255,255,255,.75)!important;border-radius:8px!important;padding:8px 16px!important;font-size:.8em!important;font-weight:600!important;cursor:pointer!important;transition:all .15s!important;white-space:nowrap!important;letter-spacing:.02em!important}',
            '.ir-ov-prefs:hover{background:rgba(255,255,255,.08)!important;border-color:rgba(255,255,255,.35)!important;color:#fff!important}',
            '.ir-ov-lb.ir-ov-lb-active{background:rgba(244,196,48,.18)!important;border-color:#f4c430!important;color:#fff!important}',
            '.ir-ov-lb-panel{background:#141414!important;border:1px solid rgba(244,196,48,.22)!important;border-radius:10px!important;padding:14px 18px!important;margin:0 0 14px!important}',
            '.ir-ov-lb-row{display:flex!important;align-items:center!important;gap:10px!important;margin-bottom:10px!important;flex-wrap:wrap!important}',
            '.ir-ov-lb-row:last-child{margin-bottom:0!important}',
            '.ir-ov-lb-label{color:rgba(255,255,255,.6)!important;font-size:.75em!important;text-transform:uppercase!important;letter-spacing:.05em!important;font-weight:700!important;white-space:nowrap!important}',
            '.ir-ov-lb-user{background:rgba(255,255,255,.05)!important;border:1px solid rgba(255,255,255,.14)!important;border-radius:6px!important;color:#fff!important;font-size:.85em!important;padding:7px 12px!important;outline:none!important;transition:border-color .2s!important;min-width:180px!important;flex:1!important;max-width:280px!important}',
            '.ir-ov-lb-user:focus{border-color:rgba(244,196,48,.5)!important}',
            '.ir-ov-lb-user::placeholder{color:rgba(255,255,255,.25)!important}',
            '.ir-ov-lb-check{display:flex!important;align-items:center!important;gap:6px!important;color:rgba(255,255,255,.85)!important;font-size:.8em!important;cursor:pointer!important;white-space:nowrap!important}',
            '.ir-ov-lb-check input{accent-color:#f4c430!important;width:15px!important;height:15px!important}',
            '.ir-ov-lb-save,.ir-ov-lb-sync{background:#f4c430!important;color:#000!important;border:none!important;border-radius:6px!important;padding:7px 16px!important;font-size:.8em!important;font-weight:700!important;cursor:pointer!important;transition:transform .1s,background .15s!important}',
            '.ir-ov-lb-save:hover,.ir-ov-lb-sync:hover{background:#ffd84d!important;transform:scale(1.04)!important}',
            '.ir-ov-lb-sync{background:rgba(200,30,30,.9)!important;color:#fff!important}',
            '.ir-ov-lb-sync:hover{background:#d42828!important}',
            '.ir-ov-lb-diag{background:rgba(255,255,255,.08)!important;color:rgba(255,255,255,.85)!important;border:1px solid rgba(255,255,255,.2)!important;border-radius:6px!important;padding:7px 14px!important;font-size:.78em!important;font-weight:600!important;cursor:pointer!important;transition:all .15s!important}',
            '.ir-ov-lb-diag:hover{background:rgba(255,255,255,.15)!important;border-color:rgba(255,255,255,.4)!important;color:#fff!important}',
            '.ir-ov-lb-scrapefav{background:rgba(244,196,48,.1)!important;color:#f4c430!important;border:1px solid rgba(244,196,48,.4)!important;border-radius:6px!important;padding:7px 14px!important;font-size:.78em!important;font-weight:600!important;cursor:pointer!important;transition:all .15s!important}',
            '.ir-ov-lb-scrapefav:hover{background:rgba(244,196,48,.18)!important;border-color:#f4c430!important;color:#fff!important}',
            '.ir-ov-lb-clean{background:rgba(200,30,30,.12)!important;color:rgba(255,140,140,.9)!important;border:1px solid rgba(200,30,30,.35)!important;border-radius:6px!important;padding:7px 14px!important;font-size:.78em!important;font-weight:600!important;cursor:pointer!important;transition:all .15s!important}',
            '.ir-ov-lb-clean:hover{background:rgba(200,30,30,.22)!important;border-color:#cc2020!important;color:#ff8080!important}',
            '.ir-ov-lb-upload{display:inline-flex!important;align-items:center!important;cursor:pointer!important;background:rgba(244,196,48,.08)!important;border:1px dashed rgba(244,196,48,.4)!important;border-radius:6px!important;padding:8px 16px!important;font-size:.82em!important;color:rgba(255,255,255,.88)!important;font-weight:600!important;transition:all .15s!important}',
            '.ir-ov-lb-upload:hover{background:rgba(244,196,48,.15)!important;border-color:#f4c430!important;color:#fff!important}',
            '.ir-ov-lb-upload input{display:none!important}',
            '.ir-ov-lb-hint{font-size:.72em!important;color:rgba(255,255,255,.45)!important;line-height:1.5!important;flex:1!important;min-width:200px!important}',
            '.ir-ov-lb-status{font-size:.8em!important;color:rgba(255,255,255,.65)!important;margin-top:6px!important;line-height:1.5!important;min-height:1em!important}',
            '.ir-ov-lb-status.ir-ov-lb-ok{color:#7fd97a!important}',
            '.ir-ov-lb-status.ir-ov-lb-err{color:#ff8080!important}',
            '.ir-ov-lb-unmatched{margin-top:8px!important;padding:8px 10px!important;background:rgba(0,0,0,.35)!important;border:1px solid rgba(255,255,255,.08)!important;border-radius:6px!important;max-height:140px!important;overflow-y:auto!important;font-size:.74em!important;color:rgba(255,255,255,.5)!important;font-family:ui-monospace,Menlo,Consolas,monospace!important;line-height:1.5!important}',
            '.ir-ov-lb-unmatched-title{color:rgba(255,255,255,.75)!important;font-weight:700!important;margin-bottom:4px!important;font-family:inherit!important}',
            // v1.2.0 — view selector + search input + favorites row + star filter + export
            '.ir-ov-starfilter{background:#141414!important;border:1px solid rgba(255,255,255,.12)!important;color:#fff!important;border-radius:8px!important;padding:8px 12px!important;font-size:.78em!important;cursor:pointer!important;outline:none!important;transition:border-color .15s!important}',
            '.ir-ov-starfilter:hover{border-color:rgba(200,30,30,.6)!important}',
            '.ir-ov-starfilter option{background:#141414!important;color:#fff!important}',
            '.ir-ov-export{background:transparent!important;border:1px solid rgba(255,255,255,.2)!important;color:rgba(255,255,255,.85)!important;border-radius:8px!important;padding:8px 14px!important;font-size:.78em!important;font-weight:600!important;cursor:pointer!important;transition:all .15s!important;white-space:nowrap!important}',
            '.ir-ov-export:hover{background:rgba(255,255,255,.08)!important;border-color:rgba(255,255,255,.4)!important;color:#fff!important}',
            // Diary entries: different card layout, shows date prominently
            '.ir-ov-diary-list{display:flex!important;flex-direction:column!important;gap:12px!important;padding-bottom:48px!important;max-width:900px!important}',
            '.ir-ov-diary-row{display:flex!important;gap:14px!important;background:#141414!important;border:1px solid rgba(255,255,255,.06)!important;border-radius:10px!important;padding:12px!important;cursor:pointer!important;transition:all .15s!important}',
            '.ir-ov-diary-row:hover{border-color:rgba(200,30,30,.4)!important;background:#1a1010!important;transform:translateX(3px)!important}',
            '.ir-ov-diary-poster{width:60px!important;height:90px!important;border-radius:6px!important;object-fit:cover!important;background:#0a0a0a!important;flex-shrink:0!important}',
            '.ir-ov-diary-main{flex:1!important;min-width:0!important;display:flex!important;flex-direction:column!important;justify-content:center!important}',
            '.ir-ov-diary-head{display:flex!important;align-items:baseline!important;gap:10px!important;margin-bottom:4px!important;flex-wrap:wrap!important}',
            '.ir-ov-diary-title{font-weight:800!important;font-size:1.02em!important;color:#fff!important}',
            '.ir-ov-diary-year{color:rgba(255,255,255,.45)!important;font-size:.82em!important}',
            '.ir-ov-diary-rewatch{background:rgba(96,165,250,.18)!important;color:#60a5fa!important;border:1px solid rgba(96,165,250,.4)!important;border-radius:10px!important;padding:1px 8px!important;font-size:.68em!important;font-weight:700!important;text-transform:uppercase!important;letter-spacing:.05em!important}',
            '.ir-ov-diary-meta{color:rgba(255,255,255,.5)!important;font-size:.78em!important;display:flex!important;gap:12px!important;flex-wrap:wrap!important}',
            '.ir-ov-diary-stars{color:#f4c430!important;font-weight:700!important}',
            // Visual star bar — five glyphs left-to-right, filled by stars value
            '.ir-starbar{display:inline-flex!important;gap:1px!important;align-items:center!important;letter-spacing:0!important;font-size:.95em!important;line-height:1!important}',
            '.ir-starbar-full{color:#f4c430!important}',
            '.ir-starbar-empty{color:rgba(255,255,255,.18)!important}',
            // Half-star: render the filled glyph but clip to the left half via background-clip
            '.ir-starbar-half{position:relative!important;color:rgba(255,255,255,.18)!important;display:inline-block!important}',
            '.ir-starbar-half::before{content:"\u2605"!important;position:absolute!important;left:0!important;top:0!important;width:50%!important;overflow:hidden!important;color:#f4c430!important}',
            '.ir-ov-diary-stars-num{color:#f4c430!important;font-weight:700!important;margin-left:4px!important}',
            '.ir-ov-diary-review{color:rgba(255,255,255,.55)!important;font-size:.78em!important;font-style:italic!important;margin-top:4px!important;line-height:1.5!important}',
            '.ir-ov-diary-month{font-size:.75em!important;text-transform:uppercase!important;letter-spacing:.1em!important;color:rgba(255,255,255,.4)!important;font-weight:800!important;padding:14px 0 6px!important;border-bottom:1px solid rgba(255,255,255,.06)!important;margin-top:14px!important}',
            '.ir-ov-diary-month:first-child{margin-top:0!important}',
            // Watchlist scope toggle (mine vs everyone)
            '.ir-ov-scope{display:flex!important;gap:8px!important;margin:0 0 16px!important;flex-wrap:wrap!important}',
            '.ir-ov-scope-btn{background:#161616!important;border:1px solid rgba(255,255,255,.12)!important;color:rgba(255,255,255,.65)!important;border-radius:8px!important;padding:9px 18px!important;font-size:.78em!important;font-weight:700!important;cursor:pointer!important;letter-spacing:.02em!important;transition:all .15s!important}',
            '.ir-ov-scope-btn:hover{border-color:rgba(200,30,30,.5)!important;color:#fff!important}',
            '.ir-ov-scope-btn.ir-ov-scope-active{background:#cc2020!important;border-color:#cc2020!important;color:#fff!important;box-shadow:0 0 14px rgba(200,30,30,.4)!important}',
            '.ir-ov-scope-user{background:#161616!important;border:1px solid rgba(255,255,255,.12)!important;color:#fff!important;border-radius:8px!important;padding:9px 14px!important;font-size:.78em!important;cursor:pointer!important;outline:none!important;transition:border-color .15s!important;font-weight:600!important}',
            '.ir-ov-scope-user:hover{border-color:rgba(200,30,30,.6)!important}',
            '.ir-ov-scope-user:focus{border-color:#cc2020!important}',
            '.ir-ov-scope-user option{background:#161616!important;color:#fff!important}',
            // Wanted-by sub-line on cards in everyone\'s watchlist mode
            '.ir-ov-card-wantedby{font-size:.66em!important;color:rgba(96,165,250,.85)!important;font-weight:600!important;margin-top:3px!important;text-shadow:0 1px 4px rgba(0,0,0,1)!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important}',
            // Reshuffle button in the For You view
            '.ir-ov-reshuffle{background:linear-gradient(135deg,#cc2020,#ff5050)!important;border:none!important;color:#fff!important;padding:10px 20px!important;border-radius:8px!important;font-size:.85em!important;font-weight:700!important;cursor:pointer!important;box-shadow:0 4px 14px rgba(200,30,30,.3)!important;transition:all .15s!important;margin:0 auto 18px!important;display:block!important;letter-spacing:.03em!important}',
            '.ir-ov-reshuffle:hover{transform:translateY(-1px)!important;box-shadow:0 6px 20px rgba(200,30,30,.45)!important}',
            '.ir-ov-reshuffle:active{transform:translateY(0)!important}',
            // Lists view
            '.ir-ov-lists-header{display:flex!important;align-items:center!important;gap:14px!important;margin-bottom:18px!important;flex-wrap:wrap!important}',
            '.ir-ov-lists-new{background:#cc2020!important;border:none!important;color:#fff!important;padding:9px 18px!important;border-radius:8px!important;font-size:.82em!important;font-weight:700!important;cursor:pointer!important;transition:background .15s!important}',
            '.ir-ov-lists-new:hover{background:#d42828!important}',
            '.ir-ov-lists-grid{display:grid!important;grid-template-columns:repeat(auto-fill,minmax(280px,1fr))!important;gap:14px!important;padding-bottom:48px!important}',
            '.ir-ov-list-card{background:#141414!important;border:1px solid rgba(255,255,255,.08)!important;border-radius:10px!important;padding:16px!important;cursor:pointer!important;transition:all .15s!important}',
            '.ir-ov-list-card:hover{border-color:rgba(200,30,30,.5)!important;background:#1a1010!important;transform:translateY(-2px)!important}',
            '.ir-ov-list-name{font-weight:800!important;font-size:1.05em!important;color:#fff!important;margin-bottom:4px!important}',
            '.ir-ov-list-owner{font-size:.72em!important;color:rgba(255,255,255,.4)!important;text-transform:uppercase!important;letter-spacing:.05em!important;margin-bottom:8px!important}',
            '.ir-ov-list-desc{font-size:.82em!important;color:rgba(255,255,255,.6)!important;margin:8px 0!important;line-height:1.5!important}',
            '.ir-ov-list-stats{display:flex!important;gap:12px!important;font-size:.72em!important;color:rgba(255,255,255,.5)!important}',
            '.ir-ov-list-collab{color:#60a5fa!important}',
            '.ir-ov-list-detail{padding:18px 0!important;width:100%!important;max-width:100%!important;overflow-x:hidden!important;word-wrap:break-word!important}',
            '.ir-ov-list-back{background:transparent!important;border:1px solid rgba(255,255,255,.2)!important;color:rgba(255,255,255,.8)!important;border-radius:6px!important;padding:6px 14px!important;font-size:.78em!important;font-weight:600!important;cursor:pointer!important;margin-bottom:18px!important;display:inline-flex!important;align-items:center!important;gap:6px!important}',
            '.ir-ov-list-back:hover{background:rgba(255,255,255,.08)!important;color:#fff!important}',
            '.ir-ov-list-detail-actions{display:flex!important;gap:10px!important;align-items:center!important;margin-bottom:18px!important;flex-wrap:wrap!important}',
            '.ir-ov-list-delete{background:rgba(200,30,30,.12)!important;border:1px solid rgba(200,30,30,.4)!important;color:rgba(255,140,140,.9)!important;border-radius:6px!important;padding:6px 14px!important;font-size:.78em!important;font-weight:600!important;cursor:pointer!important;transition:all .15s!important}',
            '.ir-ov-list-delete:hover{background:rgba(200,30,30,.25)!important;border-color:#cc2020!important;color:#ff8080!important}',
            '.ir-ov-list-title{font-size:1.5em!important;font-weight:900!important;color:#fff!important;margin-bottom:4px!important;word-wrap:break-word!important;overflow-wrap:break-word!important}',
            '.ir-ov-list-byline{color:rgba(255,255,255,.45)!important;font-size:.85em!important;margin-bottom:14px!important;word-wrap:break-word!important}',
            // Delete button on list cards in the index
            '.ir-ov-list-card{position:relative!important}',
            '.ir-ov-list-card-actions{position:absolute!important;top:10px!important;right:10px!important;opacity:0!important;transition:opacity .15s!important}',
            '.ir-ov-list-card:hover .ir-ov-list-card-actions{opacity:1!important}',
            '.ir-ov-list-card-del{background:rgba(0,0,0,.85)!important;border:1px solid rgba(200,30,30,.5)!important;color:rgba(255,100,100,.9)!important;width:28px!important;height:28px!important;border-radius:6px!important;font-size:.85em!important;cursor:pointer!important;display:flex!important;align-items:center!important;justify-content:center!important;padding:0!important;line-height:1!important;backdrop-filter:blur(4px)!important;font-weight:700!important}',
            '.ir-ov-list-card-del:hover{background:rgba(60,0,0,.95)!important;border-color:#cc2020!important;color:#ff5068!important}',
            // v1.2.0 — view selector + search input + favorites row
            '.ir-ov-search{background:#141414!important;border:1px solid rgba(255,255,255,.14)!important;color:#fff!important;border-radius:8px!important;padding:8px 14px!important;font-size:.82em!important;min-width:200px!important;outline:none!important;transition:border-color .15s!important}',
            '.ir-ov-search:focus{border-color:#cc2020!important}',
            '.ir-ov-search::placeholder{color:rgba(255,255,255,.35)!important}',
            '.ir-ov-views{display:flex!important;gap:8px!important;padding:12px 0 6px!important;flex-wrap:wrap!important;border-bottom:1px solid rgba(255,255,255,.06)!important}',
            '.ir-ov-view{background:transparent!important;border:none!important;color:rgba(255,255,255,.5)!important;font-size:.82em!important;font-weight:700!important;padding:8px 14px!important;cursor:pointer!important;letter-spacing:.03em!important;text-transform:uppercase!important;border-bottom:2px solid transparent!important;transition:all .15s!important}',
            '.ir-ov-view:hover{color:#fff!important}',
            '.ir-ov-view.ir-ov-view-active{color:#fff!important;border-bottom-color:#cc2020!important}',
            // Top 4 favorites row (only shows in Films view)
            '.ir-ov-favs{padding:20px 0 16px!important;margin-bottom:16px!important;border-bottom:1px solid rgba(255,255,255,.08)!important;display:flex!important;flex-direction:column!important;gap:18px!important}',
            '.ir-ov-favs.ir-ov-favs-hidden{display:none!important}',
            '.ir-ov-favs-section{display:flex!important;flex-direction:column!important;gap:10px!important}',
            '.ir-ov-favs-title{font-size:.78em!important;text-transform:uppercase!important;letter-spacing:.1em!important;color:#f4c430!important;font-weight:800!important}',
            '.ir-ov-favs-grid{display:grid!important;grid-template-columns:repeat(4,minmax(140px,1fr))!important;gap:16px!important;max-width:720px!important}',
            '.ir-ov-favs-grid .ir-ov-card{aspect-ratio:2/3!important}',
            '.ir-ov-favs-grid .ir-ov-fav-empty{border:2px dashed rgba(244,196,48,.25)!important;border-radius:10px!important;display:flex!important;flex-direction:column!important;align-items:center!important;justify-content:center!important;gap:6px!important;color:rgba(244,196,48,.5)!important;aspect-ratio:2/3!important;background:rgba(244,196,48,.04)!important;transition:all .15s!important}',
            '.ir-ov-favs-grid .ir-ov-fav-empty:hover{border-color:#f4c430!important;background:rgba(244,196,48,.08)!important}',
            '.ir-ov-fav-empty-num{font-size:.82em!important;font-weight:800!important;letter-spacing:.06em!important;color:rgba(244,196,48,.65)!important}',
            '.ir-ov-fav-empty-plus{font-size:2.4em!important;line-height:1!important;color:rgba(244,196,48,.45)!important;font-weight:300!important}',
            '.ir-ov-fav-empty-hint{font-size:.68em!important;text-transform:uppercase!important;letter-spacing:.08em!important;color:rgba(255,255,255,.3)!important;font-weight:700!important}',
            // Tab bar — boxed pill buttons
            '.ir-ov-tabs{display:flex!important;gap:10px!important;padding:14px 0!important;flex-shrink:0!important}',
            '.ir-ov-tab{background:#161616!important;border:1px solid rgba(255,255,255,.1)!important;border-radius:8px!important;color:rgba(255,255,255,.45)!important;font-size:.78em!important;font-weight:700!important;padding:9px 22px!important;cursor:pointer!important;transition:all .15s!important;white-space:nowrap!important;letter-spacing:.05em!important;text-transform:uppercase!important}',
            '.ir-ov-tab:hover{border-color:rgba(200,30,30,.5)!important;color:rgba(255,255,255,.8)!important;background:#1e1010!important}',
            '.ir-ov-tab.ir-ov-tab-active{background:#cc2020!important;border-color:#cc2020!important;color:#fff!important;box-shadow:0 0 16px rgba(200,30,30,.4)!important}',
            // Scroll area
            '.ir-ov-inner{display:flex!important;flex-direction:column!important;flex:1!important;overflow:hidden!important;padding:32px 36px!important}',
            '.ir-ov-body{flex:1!important;overflow-y:auto!important;scrollbar-width:thin!important;scrollbar-color:rgba(200,30,30,.3) transparent!important}',
            '.ir-ov-loading{text-align:center!important;color:rgba(255,255,255,.3)!important;padding:80px 0!important;font-size:.9em!important;letter-spacing:.06em!important;text-transform:uppercase!important}',
            '.ir-ov-empty{text-align:center!important;color:rgba(255,255,255,.25)!important;padding:80px 0!important;font-size:.9em!important;letter-spacing:.04em!important}',
            '.ir-ov-grid{display:grid!important;grid-template-columns:repeat(auto-fill,minmax(165px,1fr))!important;gap:20px!important;padding-bottom:48px!important}',
            // Card — full-poster with gradient + info overlay
            '.ir-ov-card{position:relative!important;border-radius:10px!important;overflow:hidden!important;cursor:pointer!important;transition:transform .2s cubic-bezier(.34,1.56,.64,1),box-shadow .2s!important;box-shadow:0 4px 16px rgba(0,0,0,.6)!important;aspect-ratio:2/3!important;background:#141414!important;border:1px solid rgba(255,255,255,.06)!important}',
            '.ir-ov-card:hover{transform:translateY(-6px) scale(1.03)!important;box-shadow:0 16px 40px rgba(0,0,0,.8),0 0 0 1px rgba(200,30,30,.4)!important}',
            '.ir-ov-card:hover .ir-ov-card-overlay{opacity:1!important}',
            '.ir-ov-poster{position:absolute!important;inset:0!important;width:100%!important;height:100%!important;object-fit:cover!important;display:block!important}',
            '.ir-ov-poster-ph{position:absolute!important;inset:0!important;display:flex!important;align-items:center!important;justify-content:center!important;color:rgba(255,255,255,.08)!important;font-size:3.5em!important}',
            '.ir-ov-card-gradient{position:absolute!important;inset:0!important;background:linear-gradient(to top,rgba(0,0,0,.96) 0%,rgba(0,0,0,.4) 45%,rgba(0,0,0,.0) 100%)!important;pointer-events:none!important}',
            '.ir-ov-card-overlay{position:absolute!important;inset:0!important;background:rgba(0,0,0,.45)!important;display:flex!important;align-items:center!important;justify-content:center!important;opacity:0!important;transition:opacity .18s!important}',
            '.ir-ov-card-play{width:46px!important;height:46px!important;border-radius:50%!important;background:#cc2020!important;display:flex!important;align-items:center!important;justify-content:center!important;color:#fff!important;font-size:1em!important;box-shadow:0 0 20px rgba(200,30,30,.6)!important}',
            '.ir-ov-card-info{position:absolute!important;bottom:0!important;left:0!important;right:0!important;padding:12px 10px 10px!important;z-index:1!important}',
            '.ir-ov-card-stars-badge{display:inline-flex!important;align-items:center!important;gap:3px!important;background:rgba(0,0,0,.82)!important;border:1px solid rgba(244,196,48,.55)!important;border-radius:5px!important;padding:3px 9px!important;font-size:.75em!important;font-weight:800!important;color:#f4c430!important;margin-bottom:6px!important;letter-spacing:.03em!important;backdrop-filter:blur(4px)!important}',
            // ── Star tier colors — five distinct visual classes ─────────
            // 5★ (4.5+) → bright gold + soft glow
            '.ir-ov-card-stars-badge.ir-tier-5{border-color:#ffd700!important;color:#ffe47a!important;background:rgba(80,55,0,.85)!important;box-shadow:0 0 14px rgba(255,215,0,.45)!important}',
            // 4★ (4-4.4) → solid gold
            '.ir-ov-card-stars-badge.ir-tier-4{border-color:#f4c430!important;color:#f4c430!important;background:rgba(60,40,0,.85)!important}',
            // 3★ (3-3.9) → silver-blue
            '.ir-ov-card-stars-badge.ir-tier-3{border-color:#9fb3c8!important;color:#cfdce8!important;background:rgba(20,30,40,.85)!important}',
            // 2★ (2-2.9) → bronze
            '.ir-ov-card-stars-badge.ir-tier-2{border-color:#cd8c52!important;color:#e0a878!important;background:rgba(40,25,10,.85)!important}',
            // <2★ → muted red
            '.ir-ov-card-stars-badge.ir-tier-1{border-color:#a23838!important;color:#ff8888!important;background:rgba(40,10,10,.85)!important}',
            // ── Hover action buttons on grid cards ──────────────────────
            '.ir-ov-card-actions{position:absolute!important;top:8px!important;right:8px!important;display:flex!important;gap:5px!important;opacity:0!important;transform:translateY(-4px)!important;transition:all .15s!important;z-index:3!important}',
            '.ir-ov-card:hover .ir-ov-card-actions{opacity:1!important;transform:translateY(0)!important}',
            '.ir-ov-card-act{background:rgba(0,0,0,.85)!important;border:1px solid rgba(255,255,255,.3)!important;color:rgba(255,255,255,.85)!important;width:28px!important;height:28px!important;border-radius:6px!important;font-size:.85em!important;cursor:pointer!important;display:flex!important;align-items:center!important;justify-content:center!important;padding:0!important;line-height:1!important;backdrop-filter:blur(4px)!important;transition:all .12s!important;font-weight:700!important}',
            '.ir-ov-card-act:hover{background:rgba(0,0,0,.95)!important;border-color:#fff!important;color:#fff!important;transform:scale(1.1)!important}',
            '.ir-ov-card-act-fav:hover{border-color:#f4c430!important;color:#f4c430!important;background:rgba(60,40,0,.9)!important}',
            '.ir-ov-card-act-list:hover{border-color:#60a5fa!important;color:#60a5fa!important;background:rgba(0,30,60,.9)!important}',
            '.ir-ov-card-act-x:hover{border-color:#ff5068!important;color:#ff5068!important;background:rgba(60,0,0,.9)!important}',
            '.ir-ov-card-name{font-weight:700!important;font-size:.8em!important;color:#fff!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;line-height:1.3!important;text-shadow:0 1px 8px rgba(0,0,0,1),0 0 20px rgba(0,0,0,.9)!important}',
            '.ir-ov-card-meta{font-size:.68em!important;color:rgba(255,255,255,.7)!important;margin-top:3px!important;white-space:nowrap!important;overflow:hidden!important;text-overflow:ellipsis!important;text-shadow:0 1px 4px rgba(0,0,0,1)!important}',
            '.ir-ov-card-rev{font-size:.67em!important;color:rgba(255,255,255,.38)!important;margin-top:3px!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;font-style:italic!important}',
            // ── Letterboxd sync view (inside the main panel) ───────────────
            // Inline "⚙ Letterboxd sync" footer button — appears at the bottom
            // of both the item view and the recent view, so every panel state
            // has an obvious entry point into Letterboxd sync without fighting
            // the "View all →" button or the main rating controls.
            '.ir-lb-open-btn{display:block!important;width:100%!important;margin-top:10px!important;background:none!important;border:1px dashed rgba(244,196,48,.28)!important;color:rgba(244,196,48,.75)!important;border-radius:6px!important;padding:6px 10px!important;font-size:.76em!important;font-weight:600!important;cursor:pointer!important;text-align:center!important;letter-spacing:.02em!important;transition:all .15s!important}',
            '.ir-lb-open-btn:hover{background:rgba(244,196,48,.08)!important;border-color:#f4c430!important;color:#fff!important}',
            '.ir-lb-view{color:#fff!important}',
            '.ir-lb-header{display:flex!important;align-items:center!important;gap:10px!important;margin-bottom:14px!important;padding-bottom:10px!important;border-bottom:1px solid rgba(255,255,255,.1)!important}',
            '.ir-lb-back{background:none!important;border:none!important;color:rgba(255,255,255,.6)!important;font-size:1.2em!important;cursor:pointer!important;padding:0 4px!important;border-radius:4px!important;transition:color .15s!important}',
            '.ir-lb-back:hover{color:#fff!important}',
            '.ir-lb-title{font-size:.95em!important;font-weight:700!important;color:#fff!important;flex:1!important}',
            '.ir-lb-row{margin-bottom:10px!important}',
            '.ir-lb-label{display:block!important;font-size:.72em!important;color:rgba(255,255,255,.55)!important;text-transform:uppercase!important;letter-spacing:.05em!important;margin-bottom:4px!important;font-weight:600!important}',
            '.ir-lb-user{width:100%!important;background:rgba(255,255,255,.05)!important;border:1px solid rgba(255,255,255,.14)!important;border-radius:6px!important;color:#fff!important;font-size:.85em!important;padding:7px 10px!important;outline:none!important;transition:border-color .2s!important}',
            '.ir-lb-user:focus{border-color:rgba(244,196,48,.55)!important}',
            '.ir-lb-user::placeholder{color:rgba(255,255,255,.28)!important}',
            '.ir-lb-check{display:flex!important;align-items:center!important;gap:8px!important;cursor:pointer!important;font-size:.82em!important;color:rgba(255,255,255,.8)!important}',
            '.ir-lb-check input{accent-color:#f4c430!important;width:15px!important;height:15px!important}',
            '.ir-lb-btn-row{display:flex!important;gap:8px!important;margin-top:12px!important;flex-wrap:wrap!important}',
            '.ir-lb-save,.ir-lb-sync{background:#f4c430!important;color:#000!important;border:none!important;border-radius:6px!important;padding:6px 14px!important;font-size:.8em!important;font-weight:700!important;cursor:pointer!important;transition:transform .1s,background .15s!important}',
            '.ir-lb-save:hover,.ir-lb-sync:hover{background:#ffd84d!important;transform:scale(1.04)!important}',
            '.ir-lb-sync{background:rgba(200,30,30,.9)!important;color:#fff!important}',
            '.ir-lb-sync:hover{background:#d42828!important}',
            '.ir-lb-sep{height:1px!important;background:rgba(255,255,255,.1)!important;margin:14px 0 12px!important}',
            '.ir-lb-csv-title{font-size:.78em!important;font-weight:700!important;color:rgba(255,255,255,.85)!important;margin-bottom:4px!important}',
            '.ir-lb-csv-hint{font-size:.72em!important;color:rgba(255,255,255,.45)!important;line-height:1.5!important;margin-bottom:8px!important}',
            '.ir-lb-csv-hint code{background:rgba(255,255,255,.08)!important;padding:1px 5px!important;border-radius:3px!important;font-family:monospace!important;color:#f4c430!important}',
            '.ir-lb-upload{display:inline-block!important;cursor:pointer!important;background:rgba(255,255,255,.06)!important;border:1px dashed rgba(255,255,255,.25)!important;border-radius:6px!important;padding:7px 14px!important;font-size:.78em!important;color:rgba(255,255,255,.75)!important;transition:all .15s!important}',
            '.ir-lb-upload:hover{background:rgba(244,196,48,.08)!important;border-color:#f4c430!important;color:#fff!important}',
            '.ir-lb-upload input{display:none!important}',
            '.ir-lb-status{font-size:.76em!important;color:rgba(255,255,255,.6)!important;margin-top:10px!important;line-height:1.5!important;min-height:1em!important}',
            '.ir-lb-status.ir-lb-ok{color:#52b54b!important}',
            '.ir-lb-status.ir-lb-err{color:#ff7070!important}',
            // ── Reviews feed view ────────────────────────────────────────
            '.ir-ov-reviews-feed{display:flex!important;flex-direction:column!important;gap:14px!important;padding-bottom:48px!important;max-width:820px!important}',
            '.ir-ov-review{display:flex!important;gap:16px!important;background:#141414!important;border:1px solid rgba(255,255,255,.07)!important;border-radius:12px!important;padding:16px!important;cursor:pointer!important;transition:all .15s!important}',
            '.ir-ov-review:hover{border-color:rgba(200,30,30,.4)!important;background:#1a1010!important;transform:translateX(3px)!important}',
            '.ir-ov-review-poster{width:80px!important;height:120px!important;border-radius:7px!important;object-fit:cover!important;background:#0a0a0a!important;flex-shrink:0!important}',
            '.ir-ov-review-body{flex:1!important;min-width:0!important;display:flex!important;flex-direction:column!important;gap:6px!important}',
            '.ir-ov-review-head{display:flex!important;align-items:baseline!important;gap:10px!important;flex-wrap:wrap!important}',
            '.ir-ov-review-title{font-weight:800!important;font-size:1.05em!important;color:#fff!important}',
            '.ir-ov-review-year{color:rgba(255,255,255,.45)!important;font-size:.82em!important}',
            '.ir-ov-review-meta{font-size:.78em!important;color:rgba(255,255,255,.55)!important;display:flex!important;align-items:center!important;gap:8px!important;flex-wrap:wrap!important}',
            '.ir-ov-review-user{color:#f4c430!important;font-weight:700!important}',
            '.ir-ov-review-date{color:rgba(255,255,255,.4)!important}',
            '.ir-ov-review-text{color:rgba(255,255,255,.85)!important;font-size:.88em!important;line-height:1.55!important;font-style:italic!important;border-left:3px solid rgba(244,196,48,.3)!important;padding-left:12px!important;margin-top:4px!important}',
            // ── Add-to-list modal ────────────────────────────────────────
            '.ir-modal-backdrop{position:fixed!important;inset:0!important;background:rgba(0,0,0,.78)!important;backdrop-filter:blur(4px)!important;z-index:2147483647!important;display:flex!important;align-items:center!important;justify-content:center!important;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif!important;animation:irFadeIn .15s ease-out!important}',
            '@keyframes irFadeIn{from{opacity:0}to{opacity:1}}',
            '.ir-modal{background:#161616!important;border:1px solid rgba(200,30,30,.3)!important;border-radius:14px!important;width:100%!important;max-width:480px!important;max-height:80vh!important;overflow:hidden!important;display:flex!important;flex-direction:column!important;box-shadow:0 20px 60px rgba(0,0,0,.85)!important;color:#fff!important}',
            '.ir-modal-head{display:flex!important;align-items:center!important;justify-content:space-between!important;padding:18px 22px!important;border-bottom:1px solid rgba(255,255,255,.08)!important;flex-shrink:0!important}',
            '.ir-modal-title{font-size:1.1em!important;font-weight:800!important;color:#fff!important;margin:0!important;letter-spacing:.02em!important}',
            '.ir-modal-close{background:transparent!important;border:none!important;color:rgba(255,255,255,.5)!important;font-size:1.2em!important;cursor:pointer!important;padding:4px 10px!important;border-radius:6px!important;transition:all .12s!important}',
            '.ir-modal-close:hover{background:rgba(255,255,255,.08)!important;color:#fff!important}',
            '.ir-modal-body{padding:18px 22px!important;overflow-y:auto!important;flex:1!important;display:flex!important;flex-direction:column!important;gap:14px!important}',
            '.ir-modal-listrows{display:flex!important;flex-direction:column!important;gap:8px!important;max-height:320px!important;overflow-y:auto!important}',
            '.ir-modal-listrow{display:block!important;text-align:left!important;background:rgba(255,255,255,.05)!important;border:1px solid rgba(255,255,255,.1)!important;color:#fff!important;border-radius:8px!important;padding:12px 14px!important;cursor:pointer!important;transition:all .12s!important;width:100%!important}',
            '.ir-modal-listrow:hover{background:rgba(200,30,30,.18)!important;border-color:#cc2020!important;transform:translateX(3px)!important}',
            '.ir-modal-listrow-name{font-weight:700!important;font-size:.95em!important;color:#fff!important;margin-bottom:3px!important}',
            '.ir-modal-listrow-meta{font-size:.75em!important;color:rgba(255,255,255,.5)!important}',
            '.ir-modal-empty{color:rgba(255,255,255,.4)!important;font-size:.85em!important;text-align:center!important;padding:20px 0!important}',
            '.ir-modal-divider{font-size:.72em!important;text-transform:uppercase!important;letter-spacing:.1em!important;color:rgba(255,255,255,.35)!important;text-align:center!important;font-weight:700!important;border-top:1px solid rgba(255,255,255,.08)!important;padding-top:14px!important}',
            '.ir-modal-newrow{display:flex!important;gap:8px!important}',
            '.ir-modal-name{flex:1!important;background:rgba(255,255,255,.05)!important;border:1px solid rgba(255,255,255,.14)!important;color:#fff!important;border-radius:8px!important;padding:10px 14px!important;font-size:.88em!important;outline:none!important;transition:border-color .15s!important}',
            '.ir-modal-name:focus{border-color:#cc2020!important}',
            '.ir-modal-name::placeholder{color:rgba(255,255,255,.3)!important}',
            '.ir-modal-create{background:#cc2020!important;border:none!important;color:#fff!important;padding:10px 18px!important;border-radius:8px!important;font-size:.82em!important;font-weight:700!important;cursor:pointer!important;white-space:nowrap!important;transition:background .15s!important}',
            '.ir-modal-create:hover{background:#d42828!important}',
            // Create-list modal extras
            '.ir-modal-label{font-size:.72em!important;text-transform:uppercase!important;letter-spacing:.06em!important;color:rgba(255,255,255,.55)!important;font-weight:700!important;display:block!important}',
            '.ir-modal-desc{width:100%!important;background:rgba(255,255,255,.05)!important;border:1px solid rgba(255,255,255,.14)!important;color:#fff!important;border-radius:8px!important;padding:10px 14px!important;font-size:.85em!important;outline:none!important;resize:vertical!important;font-family:inherit!important;min-height:60px!important;transition:border-color .15s!important}',
            '.ir-modal-desc:focus{border-color:#cc2020!important}',
            '.ir-modal-desc::placeholder{color:rgba(255,255,255,.3)!important}',
            '.ir-modal-check{display:flex!important;align-items:center!important;gap:8px!important;font-size:.82em!important;color:rgba(255,255,255,.78)!important;cursor:pointer!important;line-height:1.45!important}',
            '.ir-modal-check input{accent-color:#cc2020!important;width:16px!important;height:16px!important;flex-shrink:0!important}',
            '.ir-modal-cancel{background:transparent!important;border:1px solid rgba(255,255,255,.2)!important;color:rgba(255,255,255,.8)!important;padding:10px 18px!important;border-radius:8px!important;font-size:.82em!important;font-weight:600!important;cursor:pointer!important;transition:all .15s!important}',
            '.ir-modal-cancel:hover{background:rgba(255,255,255,.08)!important;border-color:rgba(255,255,255,.4)!important;color:#fff!important}',
            // Flash toast for action confirmations
            '.ir-flash-toast{position:fixed!important;bottom:30px!important;left:50%!important;transform:translateX(-50%) translateY(20px)!important;background:#161616!important;border:1px solid rgba(82,181,75,.5)!important;color:#7fd97a!important;padding:12px 22px!important;border-radius:10px!important;font-size:.85em!important;font-weight:700!important;z-index:2147483647!important;opacity:0!important;transition:all .25s ease-out!important;box-shadow:0 10px 40px rgba(0,0,0,.7)!important;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif!important}',
            '.ir-flash-toast.ir-flash-toast-show{opacity:1!important;transform:translateX(-50%) translateY(0)!important}',
            // ── Mobile / narrow-viewport responsive overrides ──────────
            // The desktop layout assumes ~1200px+ for the topbar buttons.
            // On phones the header was overlapping itself, the view tabs
            // were stacking weirdly, and the favorites grid had zero room.
            // These media queries fix all of that.
            '@media (max-width: 768px) {' +
                '.ir-ov-topbar{padding:0 16px!important}' +
                '.ir-ov-header{height:auto!important;padding:14px 0!important;gap:8px!important}' +
                '.ir-ov-title{font-size:1em!important;flex:1 1 100%!important;margin-bottom:6px!important}' +
                '.ir-ov-count{order:2!important;flex:0 0 auto!important;font-size:.65em!important;padding:3px 8px!important}' +
                '.ir-ov-sort,.ir-ov-starfilter{flex:1 1 calc(50% - 4px)!important;font-size:.72em!important;padding:7px 10px!important;min-width:0!important}' +
                '.ir-ov-search{flex:1 1 100%!important;font-size:.78em!important;min-width:0!important}' +
                '.ir-ov-export,.ir-ov-lb,.ir-ov-close{font-size:.7em!important;padding:7px 10px!important}' +
                '.ir-ov-views{padding:8px 0 4px!important;gap:4px!important;overflow-x:auto!important;flex-wrap:nowrap!important;scrollbar-width:none!important;-webkit-overflow-scrolling:touch!important}' +
                '.ir-ov-views::-webkit-scrollbar{display:none!important}' +
                '.ir-ov-view{font-size:.7em!important;padding:7px 10px!important;flex:0 0 auto!important;white-space:nowrap!important}' +
                '.ir-ov-tabs{padding:10px 0!important;gap:6px!important;overflow-x:auto!important;flex-wrap:nowrap!important;scrollbar-width:none!important}' +
                '.ir-ov-tabs::-webkit-scrollbar{display:none!important}' +
                '.ir-ov-tab{font-size:.68em!important;padding:7px 14px!important;flex:0 0 auto!important}' +
                '.ir-ov-inner{padding:18px 16px!important}' +
                '.ir-ov-favs{padding:14px 0 12px!important;gap:14px!important}' +
                '.ir-ov-favs-grid{grid-template-columns:repeat(2,1fr)!important;gap:10px!important}' +
                '.ir-ov-favs-title{font-size:.7em!important}' +
                '.ir-ov-grid{grid-template-columns:repeat(2,1fr)!important;gap:10px!important}' +
                '.ir-ov-card-name{font-size:.72em!important}' +
                '.ir-ov-card-meta{font-size:.62em!important}' +
                '.ir-ov-card-stars-badge{font-size:.66em!important;padding:2px 7px!important}' +
                '.ir-ov-lb-panel{padding:12px 14px!important;margin:0 0 12px!important;box-sizing:border-box!important;max-width:100%!important;overflow:hidden!important}' +
                '.ir-ov-lb-row{gap:6px!important;margin-bottom:8px!important;flex-direction:column!important;align-items:stretch!important;box-sizing:border-box!important;max-width:100%!important}' +
                '.ir-ov-lb-row *{box-sizing:border-box!important;max-width:100%!important}' +
                '.ir-ov-lb-label{display:block!important;width:100%!important}' +
                '.ir-ov-lb-user{max-width:100%!important;width:100%!important;font-size:.78em!important;box-sizing:border-box!important}' +
                '.ir-ov-lb-check{width:100%!important;font-size:.74em!important}' +
                '.ir-ov-lb-save,.ir-ov-lb-sync,.ir-ov-lb-diag,.ir-ov-lb-clean,.ir-ov-lb-scrapefav{font-size:.72em!important;padding:8px 10px!important;flex:1 1 calc(50% - 3px)!important}' +
                '.ir-ov-lb-upload{font-size:.74em!important;padding:8px 12px!important;width:100%!important;text-align:center!important;display:block!important}' +
                '.ir-ov-lb-hint{font-size:.66em!important}' +
                '.ir-ov-lb-status{font-size:.74em!important}' +
                '.ir-ov-diary-row{padding:10px!important;gap:10px!important}' +
                '.ir-ov-diary-poster{width:50px!important;height:75px!important}' +
                '.ir-ov-diary-title{font-size:.92em!important}' +
                '.ir-ov-diary-meta{font-size:.7em!important;gap:8px!important}' +
                '.ir-ov-diary-review{font-size:.7em!important}' +
                '.ir-ov-review{padding:12px!important;gap:12px!important}' +
                '.ir-ov-review-poster{width:60px!important;height:90px!important}' +
                '.ir-ov-review-title{font-size:.92em!important}' +
                '.ir-ov-review-meta{font-size:.7em!important;gap:6px!important}' +
                '.ir-ov-review-text{font-size:.78em!important;padding-left:10px!important}' +
                '.ir-ov-lists-grid{grid-template-columns:1fr!important}' +
                '.ir-modal{max-width:calc(100vw - 24px)!important;max-height:90vh!important;margin:12px!important}' +
                '.ir-modal-head{padding:14px 16px!important}' +
                '.ir-modal-body{padding:14px 16px!important;gap:10px!important}' +
                '.ir-modal-title{font-size:.95em!important}' +
                '.ir-ov-card-actions{opacity:1!important;transform:none!important}' +
                '.ir-ov-card-act{width:30px!important;height:30px!important}' +
            '}' +
            '@media (max-width: 380px) {' +
                '.ir-ov-favs-grid{grid-template-columns:repeat(2,1fr)!important}' +
                '.ir-ov-grid{grid-template-columns:repeat(2,1fr)!important}' +
                '.ir-ov-view{font-size:.65em!important;padding:6px 8px!important}' +
                '.ir-ov-tab{font-size:.64em!important;padding:6px 11px!important}' +
            '}' +
            // Sidebar link
            '#ir-nav-link{display:flex!important;align-items:center!important;gap:12px!important;padding:10px 20px!important;cursor:pointer!important;background:none!important;border:none!important;width:100%!important;color:inherit!important;text-decoration:none!important;transition:background .15s!important;font-size:inherit!important}',
            '#ir-nav-link:hover{background:rgba(255,255,255,.07)!important}',
            '.ir-nav-icon{color:#f4c430!important;font-size:1.2em!important;width:24px!important;text-align:center!important;flex-shrink:0!important}',
            '.ir-nav-text{font-size:.9em!important}',
            // iOS-style toggle switch used in the My preferences modal. The
            // native checkbox is hidden but still owns the state; the track +
            // thumb pseudo-elements give it a visible, tappable affordance
            // that doesn't disappear against the dark modal background.
            '.ir-toggle{position:relative!important;flex-shrink:0!important;width:46px!important;height:26px!important;display:inline-block!important}',
            '.ir-toggle input{position:absolute!important;opacity:0!important;width:0!important;height:0!important;margin:0!important;pointer-events:none!important}',
            '.ir-toggle-track{position:absolute!important;inset:0!important;background:rgba(255,255,255,.18)!important;border-radius:999px!important;transition:background .18s ease!important;display:block!important}',
            '.ir-toggle-thumb{position:absolute!important;top:3px!important;left:3px!important;width:20px!important;height:20px!important;background:#fff!important;border-radius:50%!important;transition:transform .18s ease!important;box-shadow:0 1px 3px rgba(0,0,0,.4)!important;display:block!important}',
            '.ir-toggle input:checked + .ir-toggle-track{background:#f4c430!important}',
            '.ir-toggle input:checked + .ir-toggle-track .ir-toggle-thumb{transform:translateX(20px)!important}',
            '.ir-toggle input:focus-visible + .ir-toggle-track{box-shadow:0 0 0 3px rgba(244,196,48,.4)!important}',
            '.ir-prefs-row:hover .ir-toggle-track{background:rgba(255,255,255,.28)!important}',
            '.ir-prefs-row:hover .ir-toggle input:checked + .ir-toggle-track{background:#ffce4d!important}',
            // Members tab + per-user profile view ----------------------------
            '.ir-mem-grid{display:grid!important;grid-template-columns:repeat(auto-fill,minmax(180px,1fr))!important;gap:14px!important;padding:12px 0 24px!important}',
            '.ir-mem-card{display:flex!important;flex-direction:column!important;align-items:center!important;gap:10px!important;padding:18px 12px!important;background:#161616!important;border:1px solid rgba(255,255,255,.08)!important;border-radius:12px!important;cursor:pointer!important;transition:all .15s!important;color:inherit!important;font:inherit!important;text-align:center!important}',
            '.ir-mem-card:hover{border-color:rgba(244,196,48,.5)!important;transform:translateY(-2px)!important;box-shadow:0 8px 22px rgba(0,0,0,.4)!important}',
            '.ir-mem-avatar{position:relative!important;width:72px!important;height:72px!important;border-radius:50%!important;overflow:hidden!important;background:linear-gradient(135deg,#cc2020,#f4c430)!important;display:flex!important;align-items:center!important;justify-content:center!important;flex-shrink:0!important}',
            '.ir-mem-avatar-img{width:100%!important;height:100%!important;object-fit:cover!important}',
            '.ir-mem-avatar-fallback{font-size:1.6em!important;font-weight:800!important;color:#fff!important;text-shadow:0 1px 2px rgba(0,0,0,.4)!important}',
            '.ir-mem-name{font-size:.95em!important;font-weight:700!important;color:#fff!important;line-height:1.2!important;word-break:break-word!important;max-width:100%!important;overflow:hidden!important;text-overflow:ellipsis!important;display:-webkit-box!important;-webkit-line-clamp:2!important;-webkit-box-orient:vertical!important}',
            '.ir-mem-stats{display:flex!important;flex-direction:column!important;gap:2px!important;align-items:center!important;font-size:.74em!important;color:rgba(255,255,255,.55)!important}',
            '.ir-mem-stats b{color:#f4c430!important;font-weight:700!important}',
            // Profile detail view
            '.ir-mem-profile{padding:8px 0 32px!important}',
            '.ir-mem-prof-head{display:flex!important;align-items:center!important;gap:14px!important;margin-bottom:18px!important}',
            '.ir-mem-back{background:transparent!important;border:1px solid rgba(255,255,255,.18)!important;color:rgba(255,255,255,.8)!important;padding:6px 14px!important;border-radius:6px!important;cursor:pointer!important;font-size:.82em!important;font-weight:600!important;letter-spacing:.02em!important;flex-shrink:0!important;transition:all .12s!important}',
            '.ir-mem-back:hover{background:rgba(244,196,48,.12)!important;border-color:#f4c430!important;color:#f4c430!important}',
            '.ir-mem-prof-avatar{position:relative!important;width:56px!important;height:56px!important;border-radius:50%!important;overflow:hidden!important;background:linear-gradient(135deg,#cc2020,#f4c430)!important;display:flex!important;align-items:center!important;justify-content:center!important;flex-shrink:0!important}',
            '.ir-mem-prof-img{width:100%!important;height:100%!important;object-fit:cover!important}',
            '.ir-mem-prof-fallback{font-size:1.4em!important;font-weight:800!important;color:#fff!important}',
            '.ir-mem-prof-name{font-size:1.6em!important;font-weight:800!important;color:#fff!important;letter-spacing:-.02em!important}',
            '.ir-mem-stats-row{display:flex!important;flex-wrap:wrap!important;gap:18px!important;padding:14px 0!important;border-top:1px solid rgba(255,255,255,.08)!important;border-bottom:1px solid rgba(255,255,255,.08)!important;margin-bottom:22px!important}',
            '.ir-mem-stat-big{font-size:.8em!important;color:rgba(255,255,255,.55)!important;letter-spacing:.04em!important}',
            '.ir-mem-stat-big b{color:#fff!important;font-weight:800!important;font-size:1.4em!important;display:block!important;margin-bottom:1px!important}',
            '.ir-mem-section{margin:24px 0!important}',
            '.ir-mem-section-title{font-size:.78em!important;font-weight:700!important;letter-spacing:.08em!important;text-transform:uppercase!important;color:rgba(255,255,255,.6)!important;margin:0 0 12px!important;padding-bottom:6px!important;border-bottom:1px solid rgba(255,255,255,.08)!important}',
            '.ir-mem-top4{display:grid!important;grid-template-columns:repeat(4,1fr)!important;gap:14px!important}',
            '.ir-mem-poster{position:relative!important;display:block!important;cursor:pointer!important;border-radius:6px!important;overflow:hidden!important;background:#0e0e0e!important;border:1px solid rgba(255,255,255,.06)!important;aspect-ratio:2/3!important;transition:border-color .12s,box-shadow .12s!important}',
            // Hover via border + ring; no transform → no clipping inside the
            // overflow-x:auto strip.
            '.ir-mem-poster:hover{border-color:rgba(244,196,48,.7)!important;box-shadow:0 0 0 2px rgba(244,196,48,.25)!important}',
            '.ir-mem-poster-img{width:100%!important;height:100%!important;object-fit:cover!important;display:block!important}',
            '.ir-mem-poster-empty{display:flex!important;align-items:center!important;justify-content:center!important;width:100%!important;height:100%!important;font-size:1.6em!important;font-weight:800!important;color:rgba(255,255,255,.3)!important}',
            '.ir-mem-poster-blank{cursor:default!important;border:1px dashed rgba(255,255,255,.12)!important;display:flex!important;align-items:center!important;justify-content:center!important;color:rgba(255,255,255,.18)!important;font-size:2em!important;font-weight:300!important}',
            '.ir-mem-poster-blank:hover{transform:none!important;border-color:rgba(255,255,255,.12)!important}',
            '.ir-mem-poster-title{position:absolute!important;left:0!important;right:0!important;bottom:0!important;padding:10px 8px 8px!important;background:linear-gradient(to top,rgba(0,0,0,.85),rgba(0,0,0,0))!important;color:#fff!important;font-size:.75em!important;font-weight:600!important;line-height:1.2!important;display:-webkit-box!important;-webkit-line-clamp:2!important;-webkit-box-orient:vertical!important;overflow:hidden!important}',
            '.ir-mem-strip{display:flex!important;gap:10px!important;overflow-x:auto!important;padding-bottom:8px!important;scrollbar-width:thin!important}',
            '.ir-mem-strip .ir-mem-poster-s{flex:0 0 110px!important;width:110px!important}',
            '.ir-mem-hist{display:flex!important;align-items:flex-end!important;gap:6px!important;height:120px!important;padding:0 4px!important}',
            '.ir-mem-bar{flex:1 1 0!important;display:flex!important;flex-direction:column!important;justify-content:flex-end!important;align-items:center!important;height:100%!important;gap:6px!important;cursor:default!important}',
            '.ir-mem-bar-fill{display:block!important;width:100%!important;background:linear-gradient(180deg,#f4c430,#cc2020)!important;border-radius:4px 4px 0 0!important;min-height:2px!important;transition:filter .15s!important}',
            '.ir-mem-bar:hover .ir-mem-bar-fill{filter:brightness(1.15)!important}',
            '.ir-mem-bar-label{font-size:.66em!important;color:rgba(255,255,255,.5)!important;font-weight:600!important;letter-spacing:.02em!important}',
            '.ir-mem-diary{list-style:none!important;margin:0!important;padding:0!important;display:flex!important;flex-direction:column!important;gap:10px!important}',
            '.ir-mem-diary-row{display:flex!important;align-items:center!important;gap:12px!important;padding:8px!important;border-radius:8px!important;background:rgba(255,255,255,.03)!important;cursor:pointer!important;transition:background .12s!important}',
            '.ir-mem-diary-row:hover{background:rgba(255,255,255,.07)!important}',
            '.ir-mem-poster-xs{flex:0 0 40px!important;width:40px!important;aspect-ratio:2/3!important}',
            '.ir-mem-diary-meta{display:flex!important;flex-direction:column!important;gap:2px!important;min-width:0!important;flex:1!important}',
            '.ir-mem-diary-title{font-size:.92em!important;font-weight:600!important;color:#fff!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important}',
            '.ir-mem-diary-sub{font-size:.74em!important;color:rgba(255,255,255,.5)!important}',
            '@media (max-width:600px){.ir-mem-grid{grid-template-columns:repeat(auto-fill,minmax(140px,1fr))!important;gap:10px!important}.ir-mem-prof-name{font-size:1.3em!important}.ir-mem-stat-big b{font-size:1.2em!important}}',
            // ── v1.5.1 additions ─────────────────────────────────────────
            '.ir-mem-card-v2{position:relative!important;display:flex!important;flex-direction:column!important;gap:8px!important;padding:0!important;background:#161616!important;border:1px solid rgba(255,255,255,.08)!important;border-radius:12px!important;overflow:hidden!important;transition:all .15s!important}',
            '.ir-mem-card-v2:hover{border-color:rgba(244,196,48,.5)!important;transform:translateY(-2px)!important;box-shadow:0 8px 22px rgba(0,0,0,.4)!important}',
            '.ir-mem-card-body{display:flex!important;flex-direction:column!important;align-items:center!important;gap:10px!important;padding:18px 12px 12px!important;background:transparent!important;border:none!important;cursor:pointer!important;color:inherit!important;font:inherit!important;text-align:center!important;width:100%!important}',
            '.ir-mem-you{font-size:.78em!important;color:rgba(255,255,255,.4)!important;font-weight:500!important}',
            // Follow button — research-driven: Twitter/X primary follow as a
            // bold dark pill with white text. Following state: outline pill
            // with check. Hover Following → "Unfollow" in red, like X/IG.
            '.ir-mem-follow-btn{margin:0!important;background:#fff!important;color:#0e0e0e!important;border:none!important;padding:9px 22px!important;border-radius:999px!important;font-size:.8em!important;font-weight:800!important;letter-spacing:.01em!important;cursor:pointer!important;transition:all .15s!important;display:inline-flex!important;align-items:center!important;justify-content:center!important;gap:6px!important;min-width:108px!important;line-height:1!important}',
            '.ir-mem-follow-btn:hover{background:#e6e6e6!important;transform:translateY(-1px)!important;box-shadow:0 2px 8px rgba(255,255,255,.12)!important}',
            '.ir-mem-follow-btn:active{transform:translateY(0)!important}',
            '.ir-mem-follow-btn.ir-mem-follow-on{background:transparent!important;color:#fff!important;border:1px solid rgba(255,255,255,.3)!important;padding:8px 22px!important}',
            '.ir-mem-follow-btn.ir-mem-follow-on::before{content:"\\2713  Following"!important;letter-spacing:.01em!important}',
            '.ir-mem-follow-btn.ir-mem-follow-on:hover{background:rgba(204,32,32,.12)!important;color:#ff5a5a!important;border-color:rgba(204,32,32,.5)!important;box-shadow:none!important}',
            '.ir-mem-follow-btn.ir-mem-follow-on:hover::before{content:"Unfollow"!important}',
            '.ir-mem-follow-btn.ir-mem-follow-on .ir-follow-text-following{display:none!important}',
            '.ir-mem-follow-btn:disabled{opacity:.55!important;cursor:wait!important}',
            // Profile head — promote follow button to the right side
            '.ir-mem-prof-head{display:flex!important;align-items:center!important;gap:14px!important;margin-bottom:18px!important;flex-wrap:wrap!important}',
            '.ir-mem-prof-follow{margin-left:auto!important;background:transparent!important;border:1px solid rgba(244,196,48,.5)!important;color:#f4c430!important;padding:8px 18px!important;border-radius:8px!important;font-size:.84em!important;font-weight:700!important;text-transform:uppercase!important;letter-spacing:.05em!important;cursor:pointer!important;transition:all .12s!important;flex-shrink:0!important}',
            '.ir-mem-prof-follow:hover{background:rgba(244,196,48,.15)!important}',
            '.ir-mem-prof-follow.ir-mem-follow-on{background:#f4c430!important;color:#111!important;border-color:#f4c430!important}',
            '.ir-mem-prof-follow.ir-mem-follow-on:hover{background:#ffce4d!important;border-color:#ffce4d!important}',
            '.ir-mem-prof-follow:disabled{opacity:.5!important;cursor:wait!important}',
            // Profile section header (title + optional view-all + topcat tabs)
            '.ir-mem-section-head{display:flex!important;align-items:center!important;justify-content:space-between!important;gap:12px!important;flex-wrap:wrap!important;border-bottom:1px solid rgba(255,255,255,.08)!important;margin:0 0 12px!important;padding-bottom:6px!important}',
            '.ir-mem-section-head .ir-mem-section-title{margin:0!important;padding:0!important;border:none!important}',
            // Top 4 — smaller posters than v1.5.0; medium aspect ratio
            '.ir-mem-poster-m{aspect-ratio:2/3!important}',
            '.ir-mem-top4{display:grid!important;grid-template-columns:repeat(4,1fr)!important;gap:12px!important;max-width:560px!important}',
            '@media (max-width:600px){.ir-mem-top4{max-width:100%!important;gap:8px!important}}',
            '.ir-mem-topcats{display:flex!important;gap:4px!important;flex-wrap:wrap!important}',
            '.ir-mem-topcat{background:transparent!important;border:none!important;color:rgba(255,255,255,.55)!important;font-size:.74em!important;font-weight:700!important;text-transform:uppercase!important;letter-spacing:.05em!important;padding:6px 10px!important;border-radius:6px!important;cursor:pointer!important;transition:all .12s!important}',
            '.ir-mem-topcat:hover{color:#fff!important;background:rgba(255,255,255,.05)!important}',
            '.ir-mem-topcat-on{background:rgba(244,196,48,.15)!important;color:#f4c430!important}',
            '.ir-mem-topcat-n{display:inline-block!important;margin-left:4px!important;background:rgba(255,255,255,.1)!important;color:rgba(255,255,255,.7)!important;font-size:.85em!important;padding:1px 6px!important;border-radius:8px!important;min-width:16px!important}',
            '.ir-mem-topcat-on .ir-mem-topcat-n{background:rgba(244,196,48,.3)!important;color:#f4c430!important}',
            // Histogram restored to v1.5.0 layout — calmer single warm tone
            // (#f4c430 with a soft shadow) and no count overlay; tooltip
            // shows count on hover. Replaces the harsh red→gold gradient.
            '.ir-mem-hist{display:flex!important;align-items:flex-end!important;gap:6px!important;height:120px!important;padding:0 4px!important}',
            '.ir-mem-bar{flex:1 1 0!important;display:flex!important;flex-direction:column!important;justify-content:flex-end!important;align-items:center!important;height:100%!important;gap:6px!important;cursor:default!important}',
            '.ir-mem-bar-fill{display:block!important;width:100%!important;background:#f4c430!important;border-radius:3px 3px 0 0!important;min-height:2px!important;transition:filter .15s!important;box-shadow:inset 0 -1px 0 rgba(0,0,0,.18)!important}',
            '.ir-mem-bar:hover .ir-mem-bar-fill{filter:brightness(1.12)!important}',
            '.ir-mem-bar-label{font-size:.66em!important;color:rgba(255,255,255,.5)!important;font-weight:600!important;letter-spacing:.02em!important}',
            // Stats panel
            '.ir-mem-bigstats{display:grid!important;grid-template-columns:repeat(auto-fit,minmax(160px,1fr))!important;gap:12px!important;margin-bottom:18px!important}',
            '.ir-mem-bigstat{background:rgba(255,255,255,.04)!important;border:1px solid rgba(255,255,255,.06)!important;border-radius:10px!important;padding:14px 16px!important;display:flex!important;flex-direction:column!important;gap:4px!important}',
            '.ir-mem-bigstat-v{font-size:1.5em!important;font-weight:800!important;color:#fff!important;letter-spacing:-.02em!important;line-height:1!important}',
            '.ir-mem-bigstat-l{font-size:.72em!important;color:rgba(255,255,255,.5)!important;text-transform:uppercase!important;letter-spacing:.04em!important}',
            '.ir-mem-statgrid{display:grid!important;grid-template-columns:repeat(auto-fit,minmax(220px,1fr))!important;gap:18px!important}',
            '.ir-mem-statcol{background:rgba(255,255,255,.03)!important;border:1px solid rgba(255,255,255,.06)!important;border-radius:10px!important;padding:14px!important}',
            '.ir-mem-statcol-title{margin:0 0 10px!important;font-size:.72em!important;color:rgba(255,255,255,.5)!important;text-transform:uppercase!important;letter-spacing:.05em!important;font-weight:700!important}',
            // Rank lists — taller bars, card backdrop on rows, hover highlight
            '.ir-mem-rank{list-style:none!important;margin:0!important;padding:0!important;display:flex!important;flex-direction:column!important;gap:6px!important}',
            '.ir-mem-rank-row{display:grid!important;grid-template-columns:120px 1fr 36px!important;align-items:center!important;gap:14px!important;font-size:.85em!important;padding:8px 12px!important;background:rgba(255,255,255,.025)!important;border-radius:8px!important;transition:background .12s!important;border:1px solid rgba(255,255,255,.04)!important}',
            '.ir-mem-rank-row:hover{background:rgba(244,196,48,.06)!important;border-color:rgba(244,196,48,.2)!important}',
            '.ir-mem-rank-name{color:#fff!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;font-weight:600!important}',
            '.ir-mem-rank-bar{position:relative!important;height:10px!important;background:rgba(255,255,255,.06)!important;border-radius:5px!important;overflow:hidden!important}',
            '.ir-mem-rank-fill{display:block!important;height:100%!important;background:linear-gradient(90deg,#cc2020,#ff8c1a,#f4c430)!important;border-radius:5px!important;box-shadow:inset 0 1px 0 rgba(255,255,255,.18)!important}',
            '.ir-mem-rank-n{color:#fff!important;font-size:.85em!important;font-weight:800!important;font-variant-numeric:tabular-nums!important;text-align:right!important}',
            '.ir-mem-rank-empty{color:rgba(255,255,255,.3)!important;font-size:.85em!important;padding:6px 0!important}',
            // View-all buttons + bottom CTA row
            '.ir-mem-viewall{background:transparent!important;border:1px solid rgba(244,196,48,.4)!important;color:#f4c430!important;padding:5px 12px!important;border-radius:6px!important;font-size:.74em!important;font-weight:700!important;text-transform:uppercase!important;letter-spacing:.05em!important;cursor:pointer!important;transition:all .12s!important;flex-shrink:0!important}',
            '.ir-mem-viewall:hover{background:rgba(244,196,48,.12)!important}',
            '.ir-mem-cta-row{display:flex!important;gap:10px!important;flex-wrap:wrap!important;margin:18px 0!important}',
            '.ir-mem-cta{flex:1 1 140px!important;background:rgba(255,255,255,.04)!important;border:1px solid rgba(255,255,255,.08)!important;color:#fff!important;padding:14px 18px!important;border-radius:10px!important;font-size:.88em!important;font-weight:600!important;cursor:pointer!important;transition:all .12s!important;text-align:left!important}',
            '.ir-mem-cta:hover{background:rgba(244,196,48,.08)!important;border-color:rgba(244,196,48,.4)!important;color:#f4c430!important}',
            '.ir-mem-cta b{display:block!important;font-size:1.5em!important;line-height:1!important;color:#fff!important;margin-bottom:4px!important;font-weight:800!important}',
            '.ir-mem-cta:hover b{color:#f4c430!important}',
            // Full-grid sub-page (watchlist / liked all-views)
            '.ir-mem-fullgrid{display:grid!important;grid-template-columns:repeat(auto-fill,minmax(140px,1fr))!important;gap:14px!important}',
            '.ir-mem-fullgrid .ir-mem-poster{aspect-ratio:2/3!important}',
            // Followers/Following clickable count + private placeholder
            '.ir-mem-follow-link{cursor:pointer!important;text-decoration:none!important;color:rgba(255,255,255,.55)!important;background:transparent!important}',
            '.ir-mem-follow-link:hover{color:#f4c430!important}',
            '.ir-mem-stat-private{opacity:.5!important;cursor:default!important}',
            // ── v1.5.2 additions ─────────────────────────────────────────
            // Members search header
            '.ir-mem-search-row{position:relative!important;margin:8px 0 14px!important;display:flex!important;align-items:center!important;gap:8px!important}',
            '.ir-mem-search{flex:1!important;background:rgba(255,255,255,.05)!important;border:1px solid rgba(255,255,255,.1)!important;color:#fff!important;font:inherit!important;padding:10px 14px!important;border-radius:8px!important;font-size:.92em!important;outline:none!important;transition:border-color .12s,background .12s!important}',
            '.ir-mem-search:focus{border-color:rgba(244,196,48,.5)!important;background:rgba(255,255,255,.08)!important}',
            '.ir-mem-search::placeholder{color:rgba(255,255,255,.35)!important}',
            '.ir-mem-search-clear{background:rgba(255,255,255,.06)!important;border:1px solid rgba(255,255,255,.1)!important;color:rgba(255,255,255,.7)!important;border-radius:8px!important;padding:8px 12px!important;cursor:pointer!important;font-size:.9em!important}',
            '.ir-mem-search-clear:hover{color:#f4c430!important;border-color:rgba(244,196,48,.4)!important}',
            // Type filter chips for sub-pages
            '.ir-mem-typefilter{display:flex!important;gap:6px!important;flex-wrap:wrap!important;margin:0 0 14px!important}',
            '.ir-mem-typefilter-btn{background:rgba(255,255,255,.05)!important;border:1px solid rgba(255,255,255,.08)!important;color:rgba(255,255,255,.6)!important;font-size:.74em!important;font-weight:700!important;text-transform:uppercase!important;letter-spacing:.05em!important;padding:6px 12px!important;border-radius:6px!important;cursor:pointer!important;transition:all .12s!important;display:inline-flex!important;align-items:center!important;gap:6px!important}',
            '.ir-mem-typefilter-btn:hover{color:#fff!important;background:rgba(255,255,255,.08)!important}',
            '.ir-mem-typefilter-btn span{background:rgba(255,255,255,.1)!important;color:rgba(255,255,255,.6)!important;font-size:.85em!important;font-weight:600!important;padding:1px 6px!important;border-radius:6px!important;min-width:18px!important;text-align:center!important}',
            '.ir-mem-typefilter-on{background:rgba(244,196,48,.15)!important;color:#f4c430!important;border-color:rgba(244,196,48,.4)!important}',
            '.ir-mem-typefilter-on span{background:rgba(244,196,48,.3)!important;color:#f4c430!important}',
            // Stats panel — v1.5.2 redesign
            '.ir-mem-stats-section .ir-mem-section-title{margin-bottom:14px!important}',
            '.ir-mem-stats-loading{padding:24px 12px!important;color:rgba(255,255,255,.45)!important;font-size:.88em!important;text-align:center!important;background:rgba(255,255,255,.02)!important;border-radius:10px!important;border:1px dashed rgba(255,255,255,.06)!important}',
            '.ir-mem-stats-empty{padding:18px 12px!important;color:rgba(255,255,255,.45)!important;font-size:.88em!important;text-align:center!important}',
            '.ir-mem-bigstat-sub{font-size:.66em!important;color:rgba(255,255,255,.4)!important;margin-top:2px!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;display:block!important}',
            // Activity timeline (svg)
            '.ir-mem-timeline-wrap{margin:18px 0 22px!important;background:rgba(255,255,255,.02)!important;border:1px solid rgba(255,255,255,.05)!important;border-radius:10px!important;padding:12px 14px!important}',
            '.ir-mem-timeline-wrap .ir-mem-statcol-title{margin-bottom:10px!important}',
            '.ir-mem-timeline{display:block!important;width:100%!important;height:120px!important;overflow:visible!important}',
            // Hide the old poster-title overlay (no longer rendered, kept for safety)
            '.ir-mem-poster-title{display:none!important}',
            // ── v1.5.3 additions ──────────────────────────────────────────
            // Histogram — Letterboxd-faithful: bars touch (no gap), each
            // bar stretches to fill its column. Solid gold-orange gradient.
            // Hover popover with bold count + star value.
            '.ir-mem-hist{display:flex!important;align-items:flex-end!important;gap:0!important;height:180px!important;padding:24px 0 8px!important;position:relative!important;overflow:visible!important}',
            '.ir-mem-bar{position:relative!important;flex:1 1 0!important;display:flex!important;flex-direction:column!important;justify-content:flex-end!important;align-items:stretch!important;height:100%!important;gap:8px!important;background:transparent!important;border:none!important;cursor:pointer!important;color:inherit!important;font:inherit!important;padding:0!important;transition:transform .1s!important;overflow:visible!important}',
            '.ir-mem-bar:not([disabled]):hover{transform:translateY(-2px)!important}',
            '.ir-mem-bar:not([disabled]):hover .ir-mem-bar-fill{filter:brightness(1.15)!important}',
            '.ir-mem-bar:not([disabled]):hover .ir-mem-bar-pop{opacity:1!important;transform:translateX(-50%) translateY(0)!important}',
            '.ir-mem-bar[disabled]{cursor:default!important}',
            // Stretched, touching bars. Subtle 1px dark separator via inset
            // shadow keeps each column distinguishable without leaving a gap.
            '.ir-mem-bar-fill{display:block!important;width:100%!important;background:linear-gradient(180deg,#ffce4d 0%,#f4c430 50%,#d49d20 100%)!important;border-radius:3px 3px 0 0!important;min-height:2px!important;transition:filter .12s!important;align-self:stretch!important;box-shadow:inset -1px 0 0 rgba(0,0,0,.3),inset 0 -2px 0 rgba(0,0,0,.18)!important}',
            '.ir-mem-bar:last-child .ir-mem-bar-fill{box-shadow:inset 0 -2px 0 rgba(0,0,0,.18)!important}',
            '.ir-mem-bar:not([disabled]) .ir-mem-bar-fill{min-height:4px!important}',
            '.ir-mem-bar-empty .ir-mem-bar-fill{background:rgba(255,255,255,.06)!important}',
            '.ir-mem-bar-label{font-size:.74em!important;color:rgba(255,255,255,.55)!important;font-weight:600!important;letter-spacing:0!important;font-variant-numeric:tabular-nums!important;text-align:center!important}',
            '.ir-mem-bar-pop{position:absolute!important;left:50%!important;bottom:calc(100% + 6px)!important;transform:translateX(-50%) translateY(4px)!important;background:#1f1f23!important;color:#fff!important;font-size:.78em!important;font-weight:700!important;padding:6px 11px!important;border-radius:6px!important;white-space:nowrap!important;opacity:0!important;transition:opacity .12s,transform .12s!important;pointer-events:none!important;border:1px solid rgba(244,196,48,.35)!important;font-variant-numeric:tabular-nums!important;box-shadow:0 4px 14px rgba(0,0,0,.5)!important;z-index:10!important}',
            '.ir-mem-bar-pop b{color:#f4c430!important;font-weight:800!important}',
            '.ir-mem-bar-pop::after{content:""!important;position:absolute!important;top:100%!important;left:50%!important;transform:translateX(-50%)!important;width:0!important;height:0!important;border:5px solid transparent!important;border-top-color:#1f1f23!important;margin-top:-1px!important}',
            // Members cards v3 — horizontal layout
            '.ir-mem-grid-v3{grid-template-columns:repeat(auto-fill,minmax(260px,1fr))!important}',
            '.ir-mem-card-v3{position:relative!important;display:flex!important;flex-direction:column!important;background:#161616!important;border:1px solid rgba(255,255,255,.08)!important;border-radius:10px!important;overflow:hidden!important;transition:all .12s!important}',
            '.ir-mem-card-v3:hover{border-color:rgba(244,196,48,.4)!important;background:#1a1a1a!important}',
            '.ir-mem-card-v3 .ir-mem-card-body{display:grid!important;grid-template-columns:auto 1fr!important;gap:14px!important;align-items:center!important;padding:14px 14px 12px!important;text-align:left!important;background:transparent!important;border:none!important;cursor:pointer!important;color:inherit!important;font:inherit!important}',
            '.ir-mem-card-v3 .ir-mem-avatar{width:54px!important;height:54px!important}',
            '.ir-mem-card-meta{display:flex!important;flex-direction:column!important;gap:3px!important;min-width:0!important}',
            '.ir-mem-card-v3 .ir-mem-name{font-size:.95em!important;font-weight:700!important;color:#fff!important;line-height:1.15!important;display:block!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important}',
            '.ir-mem-card-v3 .ir-mem-you{font-size:.72em!important;color:rgba(244,196,48,.85)!important;font-weight:600!important;text-transform:uppercase!important;letter-spacing:.05em!important;background:rgba(244,196,48,.12)!important;padding:1px 6px!important;border-radius:4px!important;margin-left:6px!important}',
            '.ir-mem-card-tally{font-size:.78em!important;color:#f4c430!important;font-weight:700!important}',
            '.ir-mem-card-foll{display:flex!important;gap:12px!important;font-size:.72em!important;color:rgba(255,255,255,.5)!important}',
            '.ir-mem-card-foll b{color:rgba(255,255,255,.85)!important;font-weight:700!important}',
            '.ir-mem-card-v3 .ir-mem-follow-btn{margin:0 14px 14px!important;align-self:flex-end!important;padding:5px 14px!important;font-size:.72em!important}',
            // Profile head sub label (used by ratings-at-star page)
            '.ir-mem-prof-sub{font-size:.78em!important;color:rgba(255,255,255,.5)!important;margin-left:auto!important}',
            // Media-breakdown panel (Films / TV / Anime tabs in stats card)
            '.ir-mem-mediabreak{margin-top:22px!important;background:rgba(255,255,255,.025)!important;border:1px solid rgba(255,255,255,.05)!important;border-radius:10px!important;padding:14px 16px!important}',
            '.ir-mem-mediatabs{display:flex!important;gap:6px!important;flex-wrap:wrap!important;margin-bottom:14px!important;border-bottom:1px solid rgba(255,255,255,.06)!important;padding-bottom:8px!important}',
            '.ir-mem-mediatab{background:transparent!important;border:none!important;color:rgba(255,255,255,.55)!important;font-size:.78em!important;font-weight:700!important;text-transform:uppercase!important;letter-spacing:.05em!important;padding:6px 12px!important;border-radius:6px!important;cursor:pointer!important;transition:all .12s!important;display:inline-flex!important;align-items:center!important;gap:6px!important}',
            '.ir-mem-mediatab:hover{color:#fff!important;background:rgba(255,255,255,.05)!important}',
            '.ir-mem-mediatab span{background:rgba(255,255,255,.1)!important;color:rgba(255,255,255,.65)!important;font-size:.85em!important;font-weight:700!important;padding:1px 6px!important;border-radius:6px!important;min-width:18px!important;text-align:center!important}',
            '.ir-mem-mediatab-on{background:rgba(244,196,48,.15)!important;color:#f4c430!important}',
            '.ir-mem-mediatab-on span{background:rgba(244,196,48,.3)!important;color:#f4c430!important}',
            '.ir-mem-mediaheadline{display:grid!important;grid-template-columns:repeat(3,1fr)!important;gap:10px!important;margin-bottom:14px!important}',
            '.ir-mem-mediaheadline .ir-mem-bigstat{padding:10px 12px!important}',
            '.ir-mem-mediaheadline .ir-mem-bigstat-v{font-size:1.25em!important}',
            '.ir-mem-mediagenres{margin-top:6px!important}',
            // Following bar/scrollbar fix — overlay stays inset from edges
            '#ir-overlay .ir-ov-grid-wrap{padding-right:6px!important}',
            // Smaller default poster grid for sub-pages so the cards fit
            '.ir-mem-fullgrid{grid-template-columns:repeat(auto-fill,minmax(120px,1fr))!important;gap:10px!important}',
            // ── v1.5.4 additions ──────────────────────────────────────────
            // Top 4 hero — bigger posters, prominent header
            '.ir-mem-top4-section .ir-mem-section-title{font-size:.95em!important;color:#fff!important;letter-spacing:.04em!important}',
            '.ir-mem-top4-hero{display:grid!important;grid-template-columns:repeat(4,1fr)!important;gap:14px!important;max-width:780px!important}',
            '@media (max-width:600px){.ir-mem-top4-hero{max-width:100%!important;gap:8px!important}}',
            '.ir-mem-top4-hero .ir-mem-poster-m{aspect-ratio:2/3!important;border-radius:8px!important}',
            // People (directors/actors with face avatars). The <img> sits on
            // top of the fallback initial chip and hides itself if Jellyfin
            // returns 404 (person has no Primary image), revealing the chip.
            '.ir-mem-people{display:grid!important;grid-template-columns:repeat(auto-fill,minmax(110px,1fr))!important;gap:14px!important}',
            '.ir-mem-pp{display:flex!important;flex-direction:column!important;align-items:center!important;gap:6px!important;text-align:center!important}',
            '.ir-mem-pp-circle{position:relative!important;width:80px!important;height:80px!important;border-radius:50%!important;overflow:hidden!important;border:2px solid rgba(255,255,255,.08)!important;display:block!important;flex-shrink:0!important}',
            '.ir-mem-pp-img{position:absolute!important;inset:0!important;width:100%!important;height:100%!important;object-fit:cover!important;display:block!important}',
            '.ir-mem-pp-img-failed{display:none!important}',
            '.ir-mem-pp-fb{position:absolute!important;inset:0!important;display:flex!important;align-items:center!important;justify-content:center!important;background:linear-gradient(135deg,#cc2020,#f4c430)!important;color:#fff!important;font-weight:800!important;font-size:1.6em!important}',
            '.ir-mem-pp-name{font-size:.78em!important;color:#fff!important;font-weight:600!important;line-height:1.15!important;max-width:100%!important;overflow:hidden!important;text-overflow:ellipsis!important;display:-webkit-box!important;-webkit-line-clamp:2!important;-webkit-box-orient:vertical!important}',
            '.ir-mem-pp-count{font-size:.7em!important;color:rgba(255,255,255,.45)!important;font-weight:500!important}',
            // Year cards
            '.ir-mem-years{display:flex!important;flex-direction:column!important;gap:10px!important}',
            '.ir-mem-yearcard{background:rgba(255,255,255,.03)!important;border:1px solid rgba(255,255,255,.06)!important;border-radius:10px!important;padding:14px 16px!important;display:flex!important;flex-direction:column!important;gap:8px!important;transition:border-color .12s,background .12s!important}',
            '.ir-mem-yearcard:hover{border-color:rgba(244,196,48,.3)!important;background:rgba(255,255,255,.05)!important}',
            '.ir-mem-yearcard-head{display:flex!important;align-items:baseline!important;gap:14px!important;flex-wrap:wrap!important}',
            '.ir-mem-yearcard-year{font-size:1.5em!important;font-weight:800!important;color:#f4c430!important;letter-spacing:-.02em!important;font-variant-numeric:tabular-nums!important}',
            '.ir-mem-yearcard-stats{font-size:.82em!important;color:rgba(255,255,255,.55)!important}',
            '.ir-mem-yearcard-stats b{color:#fff!important;font-weight:700!important}',
            '.ir-mem-yearcard-top{font-size:.85em!important;color:rgba(255,255,255,.7)!important;cursor:pointer!important;display:flex!important;align-items:baseline!important;gap:8px!important;flex-wrap:wrap!important}',
            '.ir-mem-yearcard-top:hover{color:#f4c430!important}',
            '.ir-mem-yearcard-toplabel{font-size:.78em!important;color:rgba(255,255,255,.4)!important;text-transform:uppercase!important;letter-spacing:.05em!important;font-weight:700!important}',
            '.ir-mem-yearcard-toptitle{color:#fff!important;font-weight:600!important}',
            '.ir-mem-yearcard-topstars{margin-left:auto!important;color:#f4c430!important;font-weight:700!important;font-variant-numeric:tabular-nums!important}',
            // All-Ratings sub-page
            '.ir-mem-sortbar{display:flex!important;align-items:center!important;gap:6px!important;flex-wrap:wrap!important;margin:0 0 16px!important}',
            '.ir-mem-sortbar-label{font-size:.74em!important;color:rgba(255,255,255,.45)!important;text-transform:uppercase!important;letter-spacing:.06em!important;font-weight:700!important;margin-right:6px!important}',
            '.ir-mem-sortbtn{background:transparent!important;border:1px solid rgba(255,255,255,.1)!important;color:rgba(255,255,255,.6)!important;padding:5px 12px!important;border-radius:6px!important;font-size:.78em!important;font-weight:600!important;cursor:pointer!important;transition:all .12s!important}',
            '.ir-mem-sortbtn:hover{color:#fff!important;border-color:rgba(255,255,255,.2)!important}',
            '.ir-mem-sortbtn-on{background:rgba(244,196,48,.12)!important;border-color:rgba(244,196,48,.4)!important;color:#f4c430!important}',
            '.ir-mem-allratings{list-style:none!important;margin:0!important;padding:0!important;display:flex!important;flex-direction:column!important;gap:6px!important}',
            '.ir-mem-allratings-row{display:grid!important;grid-template-columns:auto 1fr auto!important;align-items:center!important;gap:14px!important;padding:8px 12px!important;background:rgba(255,255,255,.02)!important;border-radius:8px!important;cursor:pointer!important;transition:background .1s!important}',
            '.ir-mem-allratings-row:hover{background:rgba(244,196,48,.05)!important}',
            '.ir-mem-allratings-row .ir-mem-poster-xs{flex:0 0 40px!important;width:40px!important;aspect-ratio:2/3!important}',
            '.ir-mem-allratings-meta{display:flex!important;flex-direction:column!important;gap:2px!important;min-width:0!important}',
            '.ir-mem-allratings-title{font-size:.92em!important;font-weight:600!important;color:#fff!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important}',
            '.ir-mem-allratings-year{font-size:.85em!important;color:rgba(255,255,255,.4)!important;font-weight:500!important;margin-left:6px!important}',
            '.ir-mem-allratings-sub{font-size:.74em!important;color:rgba(255,255,255,.45)!important}',
            '.ir-mem-stars{display:inline-flex!important;gap:1px!important;font-size:1em!important;color:rgba(255,255,255,.18)!important}',
            '.ir-mem-star{display:inline-block!important;line-height:1!important}',
            '.ir-mem-star-full{color:#f4c430!important}',
            '.ir-mem-star-half{background:linear-gradient(90deg,#f4c430 50%,rgba(255,255,255,.18) 50%)!important;-webkit-background-clip:text!important;-webkit-text-fill-color:transparent!important;background-clip:text!important;color:transparent!important}',
            // Members cards v4
            '.ir-mem-grid-v4{grid-template-columns:repeat(auto-fill,minmax(170px,1fr))!important;gap:14px!important}',
            '.ir-mem-card-v4{position:relative!important;display:flex!important;flex-direction:column!important;background:#161616!important;border:1px solid rgba(255,255,255,.07)!important;border-radius:10px!important;overflow:hidden!important;transition:all .12s!important}',
            '.ir-mem-card-v4:hover{border-color:rgba(244,196,48,.4)!important;transform:translateY(-2px)!important;box-shadow:0 6px 18px rgba(0,0,0,.45)!important}',
            '.ir-mem-card-v4 .ir-mem-card-body{display:flex!important;flex-direction:column!important;align-items:center!important;gap:10px!important;padding:18px 12px 14px!important;background:transparent!important;border:none!important;cursor:pointer!important;color:inherit!important;font:inherit!important;text-align:center!important;width:100%!important}',
            '.ir-mem-card-v4 .ir-mem-avatar{width:64px!important;height:64px!important}',
            '.ir-mem-card-v4 .ir-mem-name{font-size:.95em!important;font-weight:700!important;color:#fff!important;line-height:1.15!important;max-width:100%!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important}',
            '.ir-mem-card-statline{display:inline-flex!important;align-items:center!important;gap:8px!important;font-size:.74em!important;color:rgba(255,255,255,.5)!important}',
            '.ir-mem-card-statline b{color:#f4c430!important;font-weight:700!important}',
            '.ir-mem-card-statsep{color:rgba(255,255,255,.2)!important}',
            '.ir-mem-card-foot{padding:0 12px 12px!important;display:flex!important;justify-content:center!important}',
            '.ir-mem-card-v4 .ir-mem-follow-btn{margin:0!important;width:100%!important;text-align:center!important;justify-content:center!important;padding:8px 0!important;font-size:.74em!important}',
            '.ir-mem-card-self{font-size:.7em!important;color:rgba(255,255,255,.35)!important;text-transform:uppercase!important;letter-spacing:.08em!important;font-weight:700!important;padding:8px 0!important}',
            // ── v1.5.5 additions ──────────────────────────────────────────
            // Members cards v5 — much larger with avatar + meta + Top4 strip.
            // Top4 slots use explicit pixel heights instead of aspect-ratio so
            // they don't collapse when the parent grid hands them an auto height.
            '.ir-mem-grid-v5{grid-template-columns:repeat(auto-fill,minmax(360px,1fr))!important;gap:16px!important}',
            '.ir-mem-card-v5{position:relative!important;display:flex!important;flex-direction:column!important;background:#161616!important;border:1px solid rgba(255,255,255,.08)!important;border-radius:14px!important;overflow:hidden!important;transition:all .12s!important}',
            '.ir-mem-card-v5:hover{border-color:rgba(244,196,48,.45)!important;background:#1c1c1c!important;transform:translateY(-2px)!important;box-shadow:0 10px 28px rgba(0,0,0,.5)!important}',
            '.ir-mem-card-v5 .ir-mem-card-body{display:flex!important;flex-direction:column!important;gap:16px!important;padding:18px 18px 14px!important;background:transparent!important;border:none!important;cursor:pointer!important;color:inherit!important;font:inherit!important;text-align:left!important;width:100%!important}',
            '.ir-mem-card-row1{display:grid!important;grid-template-columns:auto 1fr!important;gap:16px!important;align-items:center!important}',
            '.ir-mem-card-v5 .ir-mem-avatar{width:72px!important;height:72px!important}',
            '.ir-mem-card-name-row{display:flex!important;align-items:center!important;gap:8px!important;flex-wrap:wrap!important;margin-bottom:8px!important}',
            '.ir-mem-card-v5 .ir-mem-name{font-size:1.05em!important;font-weight:800!important;color:#fff!important;line-height:1.1!important;letter-spacing:-.01em!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;max-width:100%!important}',
            '.ir-mem-you-pill{font-size:.6em!important;color:#f4c430!important;font-weight:800!important;text-transform:uppercase!important;letter-spacing:.08em!important;background:rgba(244,196,48,.14)!important;padding:2px 6px!important;border-radius:4px!important;flex-shrink:0!important}',
            '.ir-mem-card-statgrid{display:grid!important;grid-template-columns:1fr 1fr!important;gap:3px 16px!important;font-size:.78em!important;color:rgba(255,255,255,.55)!important}',
            '.ir-mem-card-statgrid b{color:#fff!important;font-weight:700!important}',
            '.ir-mem-card-top4{display:grid!important;grid-template-columns:repeat(4,1fr)!important;gap:8px!important;padding:0!important}',
            '.ir-mem-card-top4-slot{display:block!important;width:100%!important;height:120px!important;background:#0c0c0c!important;border-radius:6px!important;border:1px solid rgba(255,255,255,.08)!important;position:relative!important;overflow:hidden!important}',
            '.ir-mem-card-top4-img{display:block!important;width:100%!important;height:100%!important;object-fit:cover!important;border-radius:5px!important}',
            '.ir-mem-card-top4-img.ir-mem-card-top4-failed{display:none!important}',
            '.ir-mem-card-top4-empty{background:rgba(255,255,255,.025)!important;border:1px dashed rgba(255,255,255,.08)!important}',
            '.ir-mem-card-v5 .ir-mem-card-foot{padding:0 18px 16px!important}',
            '.ir-mem-card-v5 .ir-mem-follow-btn{margin:0!important;width:100%!important;padding:10px 0!important;font-size:.78em!important;text-align:center!important}',
            // Year-card click affordance
            '.ir-mem-yearcard-click{cursor:pointer!important}',
            '.ir-mem-yearcard-click:hover{border-color:rgba(244,196,48,.5)!important;background:rgba(244,196,48,.06)!important}',
            '.ir-mem-yearcard-arrow{margin-left:auto!important;color:rgba(255,255,255,.3)!important;font-weight:700!important;font-size:1.1em!important;transition:color .12s,transform .12s!important}',
            '.ir-mem-yearcard-click:hover .ir-mem-yearcard-arrow{color:#f4c430!important;transform:translateX(2px)!important}',
            // Activity timeline — red gradient, taller bars, hover popover.
            '.ir-mem-tl{display:flex!important;flex-direction:column!important;gap:8px!important;padding:8px 0!important}',
            '.ir-mem-tl-bars{display:flex!important;align-items:flex-end!important;gap:2px!important;height:120px!important;width:100%!important;padding:18px 0 0!important;position:relative!important;overflow:visible!important}',
            '.ir-mem-tl-bar{flex:1 1 0!important;height:100%!important;display:flex!important;align-items:flex-end!important;min-width:3px!important;position:relative!important;cursor:default!important}',
            '.ir-mem-tl-bar-fill{display:block!important;width:100%!important;background:linear-gradient(180deg,#cc2020,#8b1414)!important;border-radius:2px!important;min-height:0!important;transition:filter .1s!important;box-shadow:inset 0 -1px 0 rgba(0,0,0,.25)!important}',
            '.ir-mem-tl-bar:hover .ir-mem-tl-bar-fill{filter:brightness(1.25)!important}',
            '.ir-mem-tl-bar:hover .ir-mem-tl-pop{opacity:1!important;transform:translateX(-50%) translateY(0)!important}',
            '.ir-mem-tl-pop{position:absolute!important;left:50%!important;bottom:calc(100% + 4px)!important;transform:translateX(-50%) translateY(4px)!important;background:#1f1f23!important;color:#fff!important;font-size:.7em!important;font-weight:600!important;padding:5px 9px!important;border-radius:5px!important;white-space:nowrap!important;opacity:0!important;transition:opacity .12s,transform .12s!important;pointer-events:none!important;border:1px solid rgba(255,255,255,.12)!important;box-shadow:0 4px 12px rgba(0,0,0,.4)!important;z-index:10!important;font-variant-numeric:tabular-nums!important}',
            '.ir-mem-tl-pop b{color:#ff6b6b!important;font-weight:800!important}',
            '.ir-mem-tl-pop::after{content:""!important;position:absolute!important;top:100%!important;left:50%!important;transform:translateX(-50%)!important;width:0!important;height:0!important;border:5px solid transparent!important;border-top-color:#1f1f23!important}',
            '.ir-mem-tl-ticks{display:flex!important;align-items:center!important;gap:2px!important;width:100%!important;height:14px!important;border-top:1px solid rgba(255,255,255,.06)!important;padding-top:3px!important}',
            '.ir-mem-tl-tick{flex:1 1 0!important;font-size:.62em!important;color:rgba(255,255,255,.5)!important;text-align:left!important;font-weight:600!important;font-variant-numeric:tabular-nums!important;min-width:3px!important}',
            '.ir-mem-tl-empty{padding:20px 0!important;text-align:center!important;color:rgba(255,255,255,.4)!important;font-size:.85em!important}',
            // Timeline scope filter chips
            '.ir-mem-tl-filter{display:flex!important;gap:6px!important;flex-wrap:wrap!important;margin-bottom:4px!important}',
            '.ir-mem-tl-filter-btn{background:rgba(255,255,255,.04)!important;border:1px solid rgba(255,255,255,.08)!important;color:rgba(255,255,255,.6)!important;font-size:.72em!important;font-weight:600!important;padding:5px 11px!important;border-radius:999px!important;cursor:pointer!important;transition:all .12s!important;letter-spacing:.02em!important}',
            '.ir-mem-tl-filter-btn:hover{color:#fff!important;background:rgba(255,255,255,.08)!important}',
            '.ir-mem-tl-filter-on{background:rgba(204,32,32,.18)!important;border-color:rgba(204,32,32,.45)!important;color:#ff6b6b!important}',
            // Sortbar wraps cleanly even with 9 buttons
            '.ir-mem-sortbar{flex-wrap:wrap!important;row-gap:6px!important}',
            // Scrollbar gutter on the overlay so chips and Follow buttons
            // don't clip into the scrollbar track
            '#ir-overlay .ir-ov-grid-wrap{scrollbar-gutter:stable!important;padding-right:14px!important}',
            // ── v1.5.6 additions ──────────────────────────────────────────
            // Overview tiles row (replaces the old chip strip)
            '.ir-mem-overview{display:grid!important;grid-template-columns:repeat(auto-fit,minmax(110px,1fr))!important;gap:1px!important;background:rgba(255,255,255,.06)!important;border:1px solid rgba(255,255,255,.06)!important;border-radius:12px!important;overflow:hidden!important;margin:18px 0!important}',
            '.ir-mem-overview-tile{display:flex!important;flex-direction:column!important;gap:4px!important;padding:14px 12px!important;background:#161616!important;text-align:center!important;text-decoration:none!important;color:inherit!important;transition:background .12s!important}',
            '.ir-mem-overview-clickable{cursor:pointer!important}',
            '.ir-mem-overview-clickable:hover{background:#1a1a1a!important}',
            '.ir-mem-overview-clickable:hover .ir-mem-overview-v{color:#f4c430!important}',
            '.ir-mem-overview-private{opacity:.5!important;cursor:default!important}',
            '.ir-mem-overview-v{font-size:1.45em!important;font-weight:800!important;color:#fff!important;letter-spacing:-.02em!important;line-height:1!important;font-variant-numeric:tabular-nums!important;transition:color .12s!important}',
            '.ir-mem-overview-l{font-size:.7em!important;color:rgba(255,255,255,.5)!important;text-transform:uppercase!important;letter-spacing:.06em!important;font-weight:700!important}',
            // Year card expanded body
            '.ir-mem-yearcard-expanded{background:rgba(255,255,255,.05)!important;border-color:rgba(244,196,48,.35)!important}',
            '.ir-mem-yearcard-body{display:flex!important;flex-direction:column!important;gap:14px!important;padding:14px 0 0!important;border-top:1px solid rgba(255,255,255,.06)!important;margin-top:8px!important}',
            '.ir-mem-yearcard-row{display:grid!important;grid-template-columns:1fr 1fr!important;gap:18px!important}',
            '@media (max-width:600px){.ir-mem-yearcard-row{grid-template-columns:1fr!important}}',
            '.ir-mem-yearcard-col h6{margin:0 0 8px!important;font-size:.66em!important;color:rgba(255,255,255,.5)!important;text-transform:uppercase!important;letter-spacing:.05em!important;font-weight:700!important}',
            // Year card mini histogram
            '.ir-mem-yearcard-hist{display:flex!important;align-items:flex-end!important;gap:3px!important;height:64px!important;padding:0 2px!important}',
            '.ir-mem-yearcard-mini{flex:1 1 0!important;display:flex!important;flex-direction:column!important;justify-content:flex-end!important;align-items:center!important;height:100%!important;gap:3px!important;cursor:default!important}',
            '.ir-mem-yearcard-mini-fill{display:block!important;width:8px!important;background:rgba(244,196,48,.7)!important;border-radius:1px 1px 0 0!important;min-height:2px!important}',
            '.ir-mem-yearcard-mini:not(.ir-mem-yearcard-mini-empty):hover .ir-mem-yearcard-mini-fill{background:#cc2020!important}',
            '.ir-mem-yearcard-mini-empty .ir-mem-yearcard-mini-fill{background:rgba(255,255,255,.04)!important}',
            '.ir-mem-yearcard-mini-label{font-size:.5em!important;color:rgba(255,255,255,.4)!important;font-weight:600!important;font-variant-numeric:tabular-nums!important}',
            // Year card people row (directors / actors with face avatars)
            '.ir-mem-yearcard-people{display:flex!important;gap:10px!important;flex-wrap:wrap!important}',
            '.ir-mem-yearcard-person{display:flex!important;flex-direction:column!important;align-items:center!important;gap:3px!important;width:60px!important;text-align:center!important}',
            '.ir-mem-yearcard-pp-circle{width:48px!important;height:48px!important}',
            '.ir-mem-yearcard-person-name{font-size:.62em!important;color:#fff!important;font-weight:600!important;line-height:1.1!important;display:-webkit-box!important;-webkit-line-clamp:2!important;-webkit-box-orient:vertical!important;overflow:hidden!important}',
            '.ir-mem-yearcard-person-n{font-size:.6em!important;color:rgba(255,255,255,.45)!important;font-weight:500!important}',
            // Open-year chip on the expanded body
            '.ir-mem-yearcard-open{align-self:flex-start!important;background:rgba(244,196,48,.12)!important;border:1px solid rgba(244,196,48,.4)!important;color:#f4c430!important;padding:6px 14px!important;border-radius:6px!important;font-size:.75em!important;font-weight:700!important;cursor:pointer!important;transition:all .12s!important;text-transform:uppercase!important;letter-spacing:.05em!important;margin-top:4px!important}',
            '.ir-mem-yearcard-open:hover{background:rgba(244,196,48,.22)!important}',
            // ── v1.5.7 additions ──────────────────────────────────────────
            // Diary stars in gold so they pop against the date metadata.
            '.ir-mem-diary-stars{color:#f4c430!important;font-weight:700!important}',
            // Year-detail page rich grid
            '.ir-mem-yearpage-grid{display:grid!important;grid-template-columns:repeat(auto-fit,minmax(260px,1fr))!important;gap:18px!important;margin-top:18px!important}',
            '.ir-mem-yearpage-col{background:rgba(255,255,255,.025)!important;border:1px solid rgba(255,255,255,.05)!important;border-radius:10px!important;padding:14px 16px!important}',
            '.ir-mem-yearpage-col h5{margin-bottom:12px!important}',
            // Three-column layout when the user-card statgrid has 6 cells
            '.ir-mem-card-statgrid{grid-template-columns:1fr 1fr 1fr!important;gap:3px 14px!important}',
            '@media (max-width:600px){.ir-mem-card-statgrid{grid-template-columns:1fr 1fr!important}}',
            // Year-card hover (now navigation, not expand)
            '.ir-mem-yearcard-click:hover{cursor:pointer!important;border-color:rgba(244,196,48,.45)!important;background:rgba(244,196,48,.06)!important}',
            '.ir-mem-yearcard-click:hover .ir-mem-yearcard-arrow{color:#f4c430!important;transform:translateX(3px)!important}',
            // ── v1.5.9 additions ──────────────────────────────────────────
            // Activity feed
            '.ir-act-scope{display:flex!important;gap:8px!important;margin-bottom:14px!important}',
            '.ir-act-scope-btn{background:rgba(255,255,255,.04)!important;border:1px solid rgba(255,255,255,.08)!important;color:rgba(255,255,255,.6)!important;font-size:.8em!important;font-weight:600!important;padding:7px 14px!important;border-radius:999px!important;cursor:pointer!important;transition:all .12s!important}',
            '.ir-act-scope-btn:hover{color:#fff!important;background:rgba(255,255,255,.08)!important}',
            '.ir-act-scope-on{background:rgba(244,196,48,.15)!important;border-color:rgba(244,196,48,.4)!important;color:#f4c430!important}',
            '.ir-act-list{list-style:none!important;margin:0!important;padding:0!important;display:flex!important;flex-direction:column!important;gap:10px!important}',
            '.ir-act-row{display:grid!important;grid-template-columns:auto auto 1fr!important;gap:14px!important;align-items:center!important;padding:12px!important;background:rgba(255,255,255,.03)!important;border-radius:10px!important;border:1px solid rgba(255,255,255,.04)!important;cursor:pointer!important;transition:all .12s!important}',
            '.ir-act-row:hover{background:rgba(244,196,48,.06)!important;border-color:rgba(244,196,48,.2)!important}',
            '.ir-act-user{background:transparent!important;border:none!important;padding:0!important;cursor:pointer!important}',
            '.ir-act-avatar{width:42px!important;height:42px!important;flex-shrink:0!important}',
            '.ir-act-avatar-img{width:100%!important;height:100%!important;object-fit:cover!important;border-radius:50%!important}',
            '.ir-act-poster{display:block!important;width:48px!important;height:72px!important;background:#0c0c0c!important;border-radius:5px!important;border:1px solid rgba(255,255,255,.08)!important;flex-shrink:0!important;cursor:pointer!important;overflow:hidden!important}',
            '.ir-act-poster-img{width:100%!important;height:100%!important;object-fit:cover!important;display:block!important}',
            '.ir-act-meta{display:flex!important;flex-direction:column!important;gap:4px!important;min-width:0!important}',
            '.ir-act-line{font-size:.92em!important;color:rgba(255,255,255,.85)!important;line-height:1.35!important}',
            '.ir-act-line b{color:#fff!important;font-weight:800!important}',
            '.ir-act-title{color:#f4c430!important;font-weight:700!important}',
            '.ir-act-rewatch{display:inline-block!important;font-size:.65em!important;color:rgba(255,255,255,.55)!important;background:rgba(255,255,255,.08)!important;padding:1px 6px!important;border-radius:4px!important;text-transform:uppercase!important;letter-spacing:.05em!important;font-weight:700!important;margin-left:4px!important;vertical-align:middle!important}',
            '.ir-act-review{font-size:.85em!important;color:rgba(255,255,255,.6)!important;line-height:1.4!important;display:-webkit-box!important;-webkit-line-clamp:3!important;-webkit-box-orient:vertical!important;overflow:hidden!important}',
            '.ir-act-when{font-size:.72em!important;color:rgba(255,255,255,.4)!important}',
            // Calendar heatmap
            '.ir-mem-heatmap{display:flex!important;flex-direction:column!important;gap:3px!important;padding:8px 0!important;overflow-x:auto!important}',
            '.ir-mem-heatmap-row{display:flex!important;align-items:center!important;gap:3px!important}',
            '.ir-mem-heatmap-rowlabel{font-size:.6em!important;color:rgba(255,255,255,.4)!important;width:28px!important;flex-shrink:0!important}',
            '.ir-mem-heatmap-cell{display:block!important;width:11px!important;height:11px!important;border-radius:2px!important;background:rgba(255,255,255,.04)!important;flex-shrink:0!important;cursor:default!important;transition:transform .1s!important}',
            '.ir-mem-heatmap-cell:hover{transform:scale(1.4)!important;outline:1px solid rgba(244,196,48,.6)!important}',
            '.ir-mem-heatmap-l1{background:rgba(244,196,48,.18)!important}',
            '.ir-mem-heatmap-l2{background:rgba(244,196,48,.4)!important}',
            '.ir-mem-heatmap-l3{background:rgba(244,196,48,.65)!important}',
            '.ir-mem-heatmap-l4{background:#f4c430!important}',
            // On this day
            '.ir-mem-otd-strip{display:flex!important;gap:10px!important;overflow-x:auto!important;padding-bottom:6px!important}',
            '.ir-mem-otd-card{flex:0 0 110px!important;display:flex!important;flex-direction:column!important;gap:4px!important;cursor:pointer!important}',
            '.ir-mem-otd-when{font-size:.7em!important;color:rgba(255,255,255,.5)!important;text-align:center!important}',
            // Most rewatched
            '.ir-mem-rewatch-strip{display:flex!important;gap:10px!important;overflow-x:auto!important;padding-bottom:6px!important}',
            '.ir-mem-rewatch-card{flex:0 0 110px!important;display:flex!important;flex-direction:column!important;gap:4px!important;cursor:pointer!important;position:relative!important}',
            '.ir-mem-rewatch-count{position:absolute!important;top:6px!important;right:6px!important;background:rgba(0,0,0,.75)!important;color:#f4c430!important;font-size:.78em!important;font-weight:800!important;padding:2px 7px!important;border-radius:4px!important;border:1px solid rgba(244,196,48,.45)!important}',
            // ── v1.5.10 additions ─────────────────────────────────────────
            // Compare button on member cards — small ghost-style next to Follow.
            // Uses neutral border + filled hover so it doesn\'t fight the white
            // primary "Follow" CTA.
            '.ir-mem-card-foot{display:flex!important;gap:8px!important;align-items:stretch!important}',
            '.ir-mem-compare-btn{flex:0 0 auto!important;background:transparent!important;color:rgba(255,255,255,.75)!important;border:1px solid rgba(255,255,255,.18)!important;border-radius:999px!important;padding:0 14px!important;min-width:42px!important;font-size:1.05em!important;font-weight:700!important;line-height:1!important;cursor:pointer!important;transition:all .15s!important;display:inline-flex!important;align-items:center!important;justify-content:center!important}',
            '.ir-mem-compare-btn:hover{background:rgba(244,196,48,.14)!important;border-color:rgba(244,196,48,.55)!important;color:#f4c430!important;transform:translateY(-1px)!important}',
            '.ir-mem-compare-btn:active{transform:translateY(0)!important}',
            // Compare view hero — big % score on left, summary on right.
            '.ir-mem-cmp-hero{display:flex!important;align-items:center!important;gap:24px!important;padding:22px 24px!important;background:linear-gradient(135deg,rgba(255,255,255,.04),rgba(255,255,255,.015))!important;border:1px solid rgba(255,255,255,.08)!important;border-radius:14px!important;margin:18px 0 22px!important;flex-wrap:wrap!important}',
            '.ir-mem-cmp-score{display:flex!important;flex-direction:column!important;align-items:flex-start!important;gap:2px!important;line-height:1!important;flex:0 0 auto!important}',
            '.ir-mem-cmp-score-v{font-size:3.4em!important;font-weight:900!important;letter-spacing:-.04em!important;font-variant-numeric:tabular-nums!important;line-height:1!important}',
            '.ir-mem-cmp-score-v small{font-size:.45em!important;font-weight:700!important;margin-left:2px!important;opacity:.85!important}',
            '.ir-mem-cmp-score-l{font-size:.7em!important;color:rgba(255,255,255,.55)!important;text-transform:uppercase!important;letter-spacing:.08em!important;font-weight:700!important;margin-top:6px!important}',
            '.ir-mem-cmp-summary{display:flex!important;flex-direction:column!important;gap:8px!important;flex:1 1 220px!important;min-width:0!important;font-size:.95em!important;color:rgba(255,255,255,.8)!important}',
            '.ir-mem-cmp-summary b{color:#fff!important;font-weight:800!important;font-size:1.15em!important}',
            '.ir-mem-cmp-tag{display:inline-flex!important;align-self:flex-start!important;padding:5px 12px!important;border-radius:999px!important;border:1px solid!important;font-size:.7em!important;font-weight:800!important;text-transform:uppercase!important;letter-spacing:.1em!important}',
            // Compare disagreement rows.
            '.ir-mem-cmp-rows{list-style:none!important;margin:0!important;padding:0!important;display:flex!important;flex-direction:column!important;gap:8px!important}',
            '.ir-mem-cmp-row{display:grid!important;grid-template-columns:auto 1fr auto!important;align-items:center!important;gap:14px!important;padding:10px!important;border-radius:10px!important;background:rgba(255,255,255,.03)!important;border:1px solid rgba(255,255,255,.05)!important;cursor:pointer!important;transition:all .12s!important}',
            '.ir-mem-cmp-row:hover{background:rgba(255,255,255,.06)!important;border-color:rgba(244,196,48,.3)!important;transform:translateX(2px)!important}',
            '.ir-mem-cmp-row-meta{display:flex!important;flex-direction:column!important;gap:4px!important;min-width:0!important}',
            '.ir-mem-cmp-row-title{font-size:.95em!important;font-weight:700!important;color:#fff!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important}',
            '.ir-mem-cmp-row-stars{display:flex!important;align-items:center!important;gap:8px!important;font-size:.8em!important;color:rgba(255,255,255,.55)!important;flex-wrap:wrap!important}',
            '.ir-mem-cmp-row-stars b{color:#f4c430!important;font-weight:800!important;font-variant-numeric:tabular-nums!important}',
            '.ir-mem-cmp-row-vs{font-size:.7em!important;color:rgba(255,255,255,.3)!important;text-transform:uppercase!important;letter-spacing:.1em!important;font-weight:700!important}',
            '.ir-mem-cmp-row-side{display:inline-flex!important;align-items:center!important;gap:5px!important}',
            '.ir-mem-cmp-row-delta{font-size:1.05em!important;font-weight:900!important;font-variant-numeric:tabular-nums!important;letter-spacing:-.01em!important;flex-shrink:0!important;padding:6px 12px!important;border-radius:8px!important;background:rgba(0,0,0,.35)!important;border:1px solid currentColor!important}',
            '@media (max-width:520px){.ir-mem-cmp-row{grid-template-columns:auto 1fr!important}.ir-mem-cmp-row-delta{grid-column:1 / -1!important;justify-self:start!important}}',
            // Diary row v2 — three columns, stars+badge right-aligned. Overrides
            // the older flex layout above so the empty space gets used.
            '.ir-mem-diary-row{display:grid!important;grid-template-columns:auto 1fr auto!important;align-items:center!important;gap:14px!important;padding:10px 12px!important}',
            '.ir-mem-diary-actions{display:flex!important;flex-direction:column!important;align-items:flex-end!important;gap:6px!important;flex-shrink:0!important}',
            '.ir-mem-diary-rewatch-pill{display:inline-flex!important;align-items:center!important;gap:5px!important;background:rgba(96,165,250,.18)!important;color:#60a5fa!important;border:1px solid rgba(96,165,250,.45)!important;padding:3px 10px 3px 8px!important;border-radius:999px!important;font-size:.66em!important;font-weight:800!important;text-transform:uppercase!important;letter-spacing:.08em!important;line-height:1!important;flex-shrink:0!important;font-variant-numeric:tabular-nums!important}',
            '.ir-mem-diary-rewatch-pill::before{content:"\\21BB"!important;font-size:1.15em!important;font-weight:900!important;letter-spacing:0!important}',
            '.ir-mem-diary-stars-icons{font-size:.92em!important;letter-spacing:.5px!important}',
            '.ir-mem-diary-nostar{color:rgba(255,255,255,.25)!important;font-weight:700!important}',
            // ── v1.5.11 additions: Compare v2 ─────────────────────────────
            // VS header band — both avatars + names + per-side totals.
            '.ir-mem-cmp-vs{display:grid!important;grid-template-columns:1fr auto 1fr!important;align-items:center!important;gap:18px!important;padding:18px 20px!important;background:linear-gradient(135deg,rgba(96,165,250,.06),rgba(192,132,252,.06))!important;border:1px solid rgba(255,255,255,.08)!important;border-radius:14px!important;margin:18px 0 16px!important}',
            '.ir-mem-cmp-side{display:flex!important;flex-direction:column!important;align-items:center!important;gap:6px!important;min-width:0!important;text-align:center!important}',
            '.ir-mem-cmp-side-name{font-size:1.05em!important;font-weight:800!important;color:#fff!important;letter-spacing:-.01em!important;max-width:100%!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important}',
            '.ir-mem-cmp-side-stat{font-size:.78em!important;color:rgba(255,255,255,.55)!important;font-variant-numeric:tabular-nums!important}',
            '.ir-mem-cmp-side-stat b{color:#fff!important;font-weight:700!important}',
            '.ir-mem-cmp-vs-pip{font-size:1.15em!important;font-weight:900!important;letter-spacing:.18em!important;color:rgba(255,255,255,.35)!important;border:1px solid rgba(255,255,255,.18)!important;border-radius:999px!important;padding:7px 14px!important;background:rgba(0,0,0,.25)!important;flex-shrink:0!important}',
            // Avatar — circle with image fallback to initial letter chip.
            '.ir-mem-cmp-avatar{position:relative!important;display:block!important;width:72px!important;height:72px!important;border-radius:50%!important;overflow:hidden!important;background:#1c1c1c!important;border:2px solid rgba(255,255,255,.12)!important;flex-shrink:0!important}',
            '.ir-mem-cmp-avatar img{display:block!important;width:100%!important;height:100%!important;object-fit:cover!important;position:relative!important;z-index:2!important}',
            '.ir-mem-cmp-avatar-letter{position:absolute!important;inset:0!important;display:flex!important;align-items:center!important;justify-content:center!important;font-size:1.6em!important;font-weight:800!important;color:rgba(255,255,255,.85)!important;background:linear-gradient(135deg,#2a2a2a,#1a1a1a)!important;z-index:1!important}',
            '.ir-mem-cmp-avatar-a{box-shadow:0 0 0 3px rgba(96,165,250,.25)!important;border-color:rgba(96,165,250,.6)!important}',
            '.ir-mem-cmp-avatar-b{box-shadow:0 0 0 3px rgba(192,132,252,.25)!important;border-color:rgba(192,132,252,.6)!important}',
            // Quick-stat tiles
            '.ir-mem-cmp-tiles{display:grid!important;grid-template-columns:repeat(auto-fit,minmax(140px,1fr))!important;gap:1px!important;background:rgba(255,255,255,.06)!important;border:1px solid rgba(255,255,255,.06)!important;border-radius:12px!important;overflow:hidden!important;margin:0 0 22px!important}',
            '.ir-mem-cmp-tile{display:flex!important;flex-direction:column!important;align-items:center!important;justify-content:center!important;padding:14px 12px!important;background:#161616!important;text-align:center!important;gap:4px!important}',
            '.ir-mem-cmp-tile-v{font-size:1.7em!important;font-weight:900!important;letter-spacing:-.03em!important;line-height:1!important;font-variant-numeric:tabular-nums!important}',
            '.ir-mem-cmp-tile-l{font-size:.66em!important;color:rgba(255,255,255,.55)!important;text-transform:uppercase!important;letter-spacing:.06em!important;font-weight:700!important}',
            // Mini histograms — side-by-side panels.
            '.ir-mem-cmp-hists{display:grid!important;grid-template-columns:1fr 1fr!important;gap:14px!important}',
            '@media (max-width:640px){.ir-mem-cmp-hists{grid-template-columns:1fr!important}}',
            '.ir-mem-cmp-hist-panel{display:flex!important;flex-direction:column!important;gap:8px!important;padding:14px!important;background:rgba(255,255,255,.025)!important;border:1px solid rgba(255,255,255,.05)!important;border-radius:10px!important}',
            '.ir-mem-cmp-hist-head{display:flex!important;justify-content:space-between!important;align-items:baseline!important;gap:8px!important}',
            '.ir-mem-cmp-hist-name{font-size:.92em!important;font-weight:700!important;color:#fff!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;min-width:0!important;flex:1!important}',
            '.ir-mem-cmp-hist-avg{font-size:.78em!important;color:rgba(255,255,255,.6)!important;font-variant-numeric:tabular-nums!important;flex-shrink:0!important}',
            '.ir-mem-cmp-hist-avg b{color:#f4c430!important;font-weight:800!important}',
            '.ir-mem-cmp-hist{display:flex!important;align-items:flex-end!important;gap:3px!important;height:74px!important;padding:0 2px!important}',
            '.ir-mem-cmp-hist-bar{flex:1 1 0!important;display:flex!important;align-items:flex-end!important;height:100%!important;min-width:4px!important;cursor:default!important}',
            '.ir-mem-cmp-hist-fill{display:block!important;width:100%!important;border-radius:2px 2px 0 0!important;min-height:2px!important;transition:filter .12s!important}',
            '.ir-mem-cmp-hist-fill-a{background:linear-gradient(180deg,#60a5fa,#3b82f6)!important}',
            '.ir-mem-cmp-hist-fill-b{background:linear-gradient(180deg,#c084fc,#9333ea)!important}',
            '.ir-mem-cmp-hist-bar:hover .ir-mem-cmp-hist-fill{filter:brightness(1.25)!important}',
            '.ir-mem-cmp-hist-bar-empty .ir-mem-cmp-hist-fill{background:rgba(255,255,255,.06)!important}',
            '.ir-mem-cmp-hist-axis{display:flex!important;justify-content:space-between!important;font-size:.62em!important;color:rgba(255,255,255,.4)!important;font-weight:600!important;letter-spacing:.04em!important}',
            // Empty / too-few-overlaps state.
            '.ir-mem-cmp-empty{display:flex!important;flex-direction:column!important;align-items:center!important;gap:10px!important;padding:36px 24px!important;text-align:center!important;background:rgba(255,255,255,.02)!important;border:1px dashed rgba(255,255,255,.1)!important;border-radius:14px!important;margin:0 0 18px!important}',
            '.ir-mem-cmp-empty-pip{font-size:2em!important;opacity:.4!important;line-height:1!important}',
            '.ir-mem-cmp-empty-h{font-size:1.1em!important;font-weight:800!important;color:#fff!important}',
            '.ir-mem-cmp-empty-p{font-size:.9em!important;color:rgba(255,255,255,.55)!important;max-width:480px!important;line-height:1.5!important}',
            '.ir-mem-cmp-empty-p b{color:#f4c430!important;font-weight:800!important}',
        ].join('');
        document.head.appendChild(s);
    }

    // ── Star rendering ────────────────────────────────────────────────────

    function setStarDisplay(val) {
        if (!_el) return;
        _el.querySelectorAll('.ir-sw').forEach(function (sw, i) {
            sw.classList.remove('ir-full', 'ir-half');
            if (val >= i + 1)     sw.classList.add('ir-full');
            else if (val >= i + 0.5) sw.classList.add('ir-half');
        });
    }

    // ── Page badge ────────────────────────────────────────────────────────

    function upsertPageBadge(data) {
        var old = document.getElementById('ir-page-badge');
        if (old) old.remove();
        if (!data || data.totalRatings === 0) return;

        var badge = document.createElement('span');
        badge.id = 'ir-page-badge';
        badge.title = 'StarTrack · ' + data.totalRatings + ' rating' + (data.totalRatings !== 1 ? 's' : '') + ' · click to rate';
        badge.textContent = '★ ' + data.averageRating.toFixed(1) + '  StarTrack (' + data.totalRatings + ')';
        badge.addEventListener('click', function () {
            if (_el) { var p = _el.querySelector('.ir-pill'); if (p) p.click(); }
        });

        var placed = false;
        var anchors = ['.itemMiscInfo', '.mediaInfoItems', '.itemTags', '.externalLinks', '.itemExternalLinks', '.ratings'];
        for (var i = 0; i < anchors.length; i++) {
            var anchor = document.querySelector(anchors[i]);
            if (anchor && anchor.parentNode) { anchor.parentNode.insertBefore(badge, anchor); placed = true; break; }
        }
        if (!placed) setTimeout(function () { if (!document.getElementById('ir-page-badge')) upsertPageBadge(data); }, 1000);
    }

    // ── Widget DOM ────────────────────────────────────────────────────────

    var _el = null;

    function buildStarInputHtml() {
        var h = '';
        for (var i = 1; i <= 5; i++) {
            h += '<span class="ir-sw" data-i="' + i + '">' +
                    '<span class="ir-sl" data-v="' + (i - 0.5) + '"></span>' +
                    '<span class="ir-sr" data-v="' + i + '"></span>' +
                 '</span>';
        }
        return h;
    }

    function ensureEl() {
        if (_el && _el.isConnected) return _el;
        injectStyles();
        _el = document.createElement('div');
        _el.id = 'ir-widget';
        _el.innerHTML =
            '<div class="ir-pill">' +
                '<span class="ir-star-icon">\u2606</span>' +
                '<span class="ir-avg-text" style="display:none"></span>' +
                '<span class="ir-label">Rate</span>' +
            '</div>' +
            '<div class="ir-panel">' +
                // Item rating view
                '<div class="ir-item-view">' +
                    '<div class="ir-ph">' +
                        '<span class="ir-big-star">\u2605</span>' +
                        '<span class="ir-big-avg">\u2013</span>' +
                        '<span class="ir-count">(0 ratings)</span>' +
                        // Watchlist + liked + favorite toggles live up here so
                        // they're visible even without clicking Save Rating
                        '<div class="ir-actions">' +
                            '<button class="ir-act ir-act-like" title="Like">\u2661</button>' +
                            '<button class="ir-act ir-act-watch" title="Add to watchlist">\u2606</button>' +
                            '<button class="ir-act ir-act-fav" title="Pin to Top 4 favorites">\u2605</button>' +
                        '</div>' +
                    '</div>' +
                    '<div class="ir-yr">' +
                        '<span class="ir-yl">Your rating:</span>' +
                        '<div class="ir-si">' + buildStarInputHtml() + '</div>' +
                        '<span class="ir-yc"></span>' +
                    '</div>' +
                    '<div class="ir-rev-wrap">' +
                        '<textarea class="ir-rev" placeholder="Add a review (optional)\u2026" maxlength="1000" rows="2"></textarea>' +
                        '<span class="ir-rev-hint">0 / 1000</span>' +
                    '</div>' +
                    '<div class="ir-submit-row">' +
                        '<button class="ir-submit">\u2605 Save Rating</button>' +
                        '<span class="ir-flash"></span>' +
                        '<button class="ir-rb" style="display:none">\u2715 Remove</button>' +
                    '</div>' +
                    '<button class="ir-tb">Show all ratings \u25be</button>' +
                    '<div class="ir-list" style="display:none"></div>' +
                    '<button class="ir-lb-open-btn" title="Connect your Letterboxd account">\u2699 Letterboxd sync</button>' +
                    '<button class="ir-lang-btn" title="Change language" style="background:none;border:none;color:rgba(255,255,255,.45);cursor:pointer;padding:4px 8px;font-size:.8em;margin-left:6px">\ud83c\udf10 <span class="ir-lang-label">EN</span></button>' +
                '</div>' +
                // Recent view
                '<div class="ir-recent-panel" style="display:none">' +
                    '<div class="ir-rec-title">' +
                        '<span>Your recent ratings</span>' +
                        '<button class="ir-rec-open-btn">View all \u2192</button>' +
                    '</div>' +
                    '<div class="ir-rec-list"></div>' +
                    '<button class="ir-lb-open-btn" title="Connect your Letterboxd account">\u2699 Letterboxd sync</button>' +
                    '<button class="ir-lang-btn" title="Change language" style="background:none;border:none;color:rgba(255,255,255,.45);cursor:pointer;padding:4px 8px;font-size:.8em;margin-left:6px">\ud83c\udf10 <span class="ir-lang-label">EN</span></button>' +
                '</div>' +
                // Letterboxd sync view (hidden by default, opens from gear icon)
                '<div class="ir-lb-view" style="display:none">' +
                    '<div class="ir-lb-header">' +
                        '<button class="ir-lb-back">\u2190</button>' +
                        '<span class="ir-lb-title">Letterboxd sync</span>' +
                    '</div>' +
                    '<div class="ir-lb-row">' +
                        '<label class="ir-lb-label">Your Letterboxd username</label>' +
                        '<input type="text" class="ir-lb-user" placeholder="e.g. davidehrlich" maxlength="32" />' +
                    '</div>' +
                    '<div class="ir-lb-row">' +
                        '<label class="ir-lb-check">' +
                            '<input type="checkbox" class="ir-lb-auto" />' +
                            '<span>Auto-sync new ratings hourly</span>' +
                        '</label>' +
                    '</div>' +
                    '<div class="ir-lb-btn-row">' +
                        '<button class="ir-lb-save">Save</button>' +
                        '<button class="ir-lb-sync">Sync now</button>' +
                    '</div>' +
                    '<div class="ir-lb-sep"></div>' +
                    '<div class="ir-lb-csv-title">Import full history</div>' +
                    '<div class="ir-lb-csv-hint">' +
                        'Export your data from letterboxd.com \u2192 Settings \u2192 Import & Export, ' +
                        'then drop the <code>.zip</code> file here (or the <code>ratings.csv</code> from inside).' +
                    '</div>' +
                    '<div class="ir-lb-btn-row">' +
                        '<label class="ir-lb-upload">' +
                            '<input type="file" accept=".zip,.csv,application/zip,text/csv" class="ir-lb-file" />' +
                            '<span>Choose file</span>' +
                        '</label>' +
                    '</div>' +
                    '<div class="ir-lb-status"></div>' +
                '</div>' +
            '</div>';

        bindInteractions(_el);
        document.body.appendChild(_el);
        try { scrubTranslations(_el); } catch (e) {}
        try { applyConfigVisibility(); } catch (e) {}
        return _el;
    }

    // ── Overlay (My Ratings library) ──────────────────────────────────────

    var _overlay = null;
    var _overlayData = null; // merged array after metadata fetch
    var _sortKey = 'ratedAt-desc';
    var _activeTab = 'all'; // 'all' | 'Movie' | 'Series' | 'Episode' | 'Anime'
    var _overlayView = 'films'; // 'films' | 'watchlist' | 'liked' | 'diary' | 'reviews' | 'recs' | 'lists' | 'members' | 'member-profile'
    var _profileUserId = null;
    var _profileUserName = '';
    // Profile sub-state. _profileTab is the section being rendered inside
    // the profile view: 'overview' (default), 'followers', 'following',
    // 'all-watchlist', 'all-liked', 'all-diary'. _profileTopCat toggles the
    // Top 4 category sub-tab: 'all' | 'movies' | 'series' | 'anime'.
    var _profileTab = 'overview';
    var _profileTopCat = 'all';
    var _profileCache = null;       // last-loaded profile DTO
    var _profileMetaCache = null;   // last-built item meta map for that profile
    var _profileStatsCache = null;  // last-loaded heavy stats DTO (lazy)
    var _profileStatsForId = null;  // userId the stats cache was loaded for
    var _profileSubFilter = 'all';  // 'all' | 'movie' | 'series' | 'anime' for sub-page filters
    var _profileStarFilter = null;  // when set, sub-page shows ratings at this star value
    var _profileMediaTab = 'movie'; // active tab in the media-breakdown stats card
    var _profileSortBy = 'date-desc'; // 'date-desc' | 'date-asc' | 'year-desc' | 'year-asc' | 'stars-desc' | 'stars-asc' | 'len-desc' | 'len-asc' | 'title' for All Ratings page
    var _profileYearFilter = null;    // when set, year-detail sub-page shows that year's ratings
    var _profileTimelineScope = 'all'; // 'all' | 'this-year' | 'last-year' | 'this-month'
    var _activityScope = 'following';  // 'following' | 'everyone'
    var _compareWith = null;           // userId we're comparing the viewer against
    var _compareData = null;           // last-loaded compare bundle
    var _profileExpandedYear = null;   // year number whose card is currently expanded
    var _membersSearchQ = '';       // current Members tab search query
    var _membersCache = [];         // last-loaded members list (for client-side search)
    var _searchQuery = '';
    var _starFilter = 'all'; // 'all' | '5' | '4.5' | ... | '0.5'
    var _favItemIds = [];
    var _recsPool = null;
    var _currentListId = null;
    var _watchlistScope = 'mine'; // 'mine' | 'everyone'
    var _watchlistUserFilter = ''; // empty = all users; otherwise the userName to show only
    var _everyoneUsers = []; // populated from /EveryonesWatchlist response

    // Defensive: every comparator returns a real number even for null/NaN
    // inputs, and falls back to a stable secondary sort by name so ties
    // never result in random ordering. The original ratedAt sort returned
    // NaN when ratedAt was null which made the whole sort look random.
    function tsOf(v) {
        if (!v) return 0;
        var d = new Date(v).getTime();
        return isNaN(d) ? 0 : d;
    }
    function nameCmp(a, b) {
        return (a.name || '').localeCompare(b.name || '');
    }
    var _sortFns = {
        'ratedAt-desc':   function (a, b) { return (tsOf(b.ratedAt) - tsOf(a.ratedAt)) || nameCmp(a, b); },
        'ratedAt-asc':    function (a, b) { return (tsOf(a.ratedAt) - tsOf(b.ratedAt)) || nameCmp(a, b); },
        'year-desc':      function (a, b) { return ((b.year || 0) - (a.year || 0)) || nameCmp(a, b); },
        'year-asc':       function (a, b) { return ((a.year || 0) - (b.year || 0)) || nameCmp(a, b); },
        'stars-desc':     function (a, b) { return ((b.stars || 0) - (a.stars || 0)) || nameCmp(a, b); },
        'stars-asc':      function (a, b) { return ((a.stars || 0) - (b.stars || 0)) || nameCmp(a, b); },
        'community-desc': function (a, b) { return ((b.communityRating || 0) - (a.communityRating || 0)) || nameCmp(a, b); },
        'community-asc':  function (a, b) { return ((a.communityRating || 0) - (b.communityRating || 0)) || nameCmp(a, b); },
        'runtime-desc':   function (a, b) { return ((b.runtime || 0) - (a.runtime || 0)) || nameCmp(a, b); },
        'runtime-asc':    function (a, b) { return ((a.runtime || 0) - (b.runtime || 0)) || nameCmp(a, b); }
    };

    function ensureOverlay() {
        if (_overlay && _overlay.isConnected) return _overlay;
        _overlay = document.createElement('div');
        _overlay.id = 'ir-overlay';
        _overlay.innerHTML =
            '<div class="ir-ov-topbar">' +
                '<div class="ir-ov-header">' +
                    '<h2 class="ir-ov-title"><span class="ir-ov-title-star">\u2605</span>My Ratings</h2>' +
                    '<span class="ir-ov-count"></span>' +
                    '<select class="ir-ov-sort">' +
                        '<option value="ratedAt-desc">Newest rated</option>' +
                        '<option value="ratedAt-asc">Oldest rated</option>' +
                        '<option value="year-desc">Film year \u2193</option>' +
                        '<option value="year-asc">Film year \u2191</option>' +
                        '<option value="stars-desc">My rating \u2193</option>' +
                        '<option value="stars-asc">My rating \u2191</option>' +
                        '<option value="community-desc">Avg rating \u2193</option>' +
                        '<option value="community-asc">Avg rating \u2191</option>' +
                        '<option value="runtime-desc">Length \u2193</option>' +
                        '<option value="runtime-asc">Length \u2191</option>' +
                    '</select>' +
                    '<select class="ir-ov-starfilter">' +
                        '<option value="all">All ratings</option>' +
                        '<option value="5">5\u2605 only</option>' +
                        '<option value="4.5">4.5\u2605 only</option>' +
                        '<option value="4">4\u2605 only</option>' +
                        '<option value="3.5">3.5\u2605 only</option>' +
                        '<option value="3">3\u2605 only</option>' +
                        '<option value="2.5">2.5\u2605 only</option>' +
                        '<option value="2">2\u2605 only</option>' +
                        '<option value="1.5">1.5\u2605 only</option>' +
                        '<option value="1">1\u2605 only</option>' +
                        '<option value="0.5">0.5\u2605 only</option>' +
                    '</select>' +
                    '<input type="text" class="ir-ov-search" placeholder="Search titles\u2026" />' +
                    '<button class="ir-ov-export" title="Export ratings as Letterboxd-compatible CSV">\u21E9 Export</button>' +
                    '<button class="ir-ov-lb">\u2699 Letterboxd</button>' +
                    '<button class="ir-ov-prefs" title="User preferences">\u2699 Preferences</button>' +
                    '<button class="ir-ov-close">\u2715 Close</button>' +
                '</div>' +
                // v1.2.0 — view selector (what kind of data to show)
                '<div class="ir-ov-views">' +
                    '<button class="ir-ov-view ir-ov-view-active" data-view="films">\u2605 Media</button>' +
                    '<button class="ir-ov-view" data-view="watchlist">\u2606 Watchlist</button>' +
                    '<button class="ir-ov-view" data-view="liked">\u2661 Liked</button>' +
                    '<button class="ir-ov-view" data-view="diary">\ud83d\udcd6 Diary</button>' +
                    '<button class="ir-ov-view" data-view="reviews">\u270d Reviews</button>' +
                    '<button class="ir-ov-view" data-view="activity">\ud83d\udce1 Activity</button>' +
                    '<button class="ir-ov-view" data-view="recs">\u2728 For you</button>' +
                    '<button class="ir-ov-view" data-view="lists">\ud83d\udcc3 Lists</button>' +
                    '<button class="ir-ov-view" data-view="members">\ud83d\udc65 Members</button>' +
                '</div>' +
                // Existing type tabs — still work as a sub-filter within each view
                '<div class="ir-ov-tabs">' +
                    '<button class="ir-ov-tab ir-ov-tab-active" data-tab="all">All</button>' +
                    '<button class="ir-ov-tab" data-tab="Movie">Movies</button>' +
                    '<button class="ir-ov-tab" data-tab="Series">TV Shows</button>' +
                    '<button class="ir-ov-tab" data-tab="Episode">Episodes</button>' +
                    '<button class="ir-ov-tab" data-tab="Anime">Anime</button>' +
                '</div>' +
                // Collapsible Letterboxd sync panel (inside the overlay topbar,
                // so it has its own styling separate from the small widget panel)
                '<div class="ir-ov-lb-panel" style="display:none">' +
                    '<div class="ir-ov-lb-row">' +
                        '<label class="ir-ov-lb-label">Letterboxd username</label>' +
                        '<input type="text" class="ir-ov-lb-user" placeholder="e.g. davidehrlich" maxlength="32" />' +
                        '<label class="ir-ov-lb-check"><input type="checkbox" class="ir-ov-lb-auto" /> Auto-sync hourly</label>' +
                        '<button class="ir-ov-lb-save">Save</button>' +
                        '<button class="ir-ov-lb-sync">Sync now</button>' +
                        '<button class="ir-ov-lb-diag">\ud83d\udd0d Diagnose</button>' +
                        '<button class="ir-ov-lb-scrapefav" title="Pull your Letterboxd Top 4 from your public profile page">\u2b50 Import Top 4</button>' +
                        '<button class="ir-ov-lb-clean">\ud83d\uddd1 Clean dead ratings</button>' +
                    '</div>' +
                    '<div class="ir-ov-lb-row">' +
                        '<label class="ir-ov-lb-upload">' +
                            '<input type="file" accept=".zip,.csv,application/zip,text/csv" class="ir-ov-lb-file" />' +
                            '<span>\u21E7 Upload Letterboxd export (ZIP or ratings.csv)</span>' +
                        '</label>' +
                        '<span class="ir-ov-lb-hint">Download your data from letterboxd.com \u2192 Settings \u2192 Import &amp; Export, then drop the ZIP here.</span>' +
                    '</div>' +
                    '<div class="ir-ov-lb-status"></div>' +
                '</div>' +
            '</div>' +
            '<div class="ir-ov-inner">' +
                '<div class="ir-ov-body">' +
                    '<div class="ir-ov-favs" style="display:none">' +
                        '<div class="ir-ov-favs-title">\u2605 Your Top 4</div>' +
                        '<div class="ir-ov-favs-grid"></div>' +
                    '</div>' +
                    '<div class="ir-ov-grid-wrap"><div class="ir-ov-loading">Loading\u2026</div></div>' +
                '</div>' +
            '</div>';

        _overlay.querySelector('.ir-ov-close').addEventListener('click', function () {
            _overlay.classList.remove('ir-ov-open');
            document.documentElement.classList.remove('ir-ov-locked');
            document.body.classList.remove('ir-ov-locked');
            document.documentElement.style.overflow = '';
        });
        var prefsBtn = _overlay.querySelector('.ir-ov-prefs');
        if (prefsBtn) prefsBtn.addEventListener('click', openUserPreferences);
        _overlay.querySelector('.ir-ov-sort').addEventListener('change', function (e) {
            _sortKey = e.target.value;
            if (_overlayData) renderOverlayGrid();
        });

        // v1.2.0 — search input
        var searchEl = _overlay.querySelector('.ir-ov-search');
        if (searchEl) {
            var searchDebounce;
            searchEl.addEventListener('input', function (e) {
                clearTimeout(searchDebounce);
                searchDebounce = setTimeout(function () {
                    _searchQuery = (e.target.value || '').trim();
                    if (_overlayData) renderOverlayGrid();
                }, 150);
            });
        }

        // v1.2.0 — view switcher
        _overlay.querySelectorAll('.ir-ov-view').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var v = btn.dataset.view;
                if (v === _overlayView) return;
                _overlayView = v;
                _currentListId = null; // leaving lists detail if we were in it
                _overlay.querySelectorAll('.ir-ov-view').forEach(function (b) { b.classList.remove('ir-ov-view-active'); });
                btn.classList.add('ir-ov-view-active');
                loadOverlayView();
            });
        });

        // v1.3.0 — star filter dropdown
        var starFilterEl = _overlay.querySelector('.ir-ov-starfilter');
        if (starFilterEl) {
            starFilterEl.addEventListener('change', function (e) {
                _starFilter = e.target.value;
                if (_overlayData) renderOverlayGrid();
            });
        }

        // v1.3.0 — export CSV
        var exportEl = _overlay.querySelector('.ir-ov-export');
        if (exportEl) {
            exportEl.addEventListener('click', function () {
                var auth = getAuth(); if (!auth) return;
                // Fetch with auth header, convert to blob, trigger download
                fetch('/Plugins/StarTrack/ExportCsv', { headers: { Authorization: auth } })
                    .then(function (r) { return r.ok ? r.blob() : null; })
                    .then(function (blob) {
                        if (!blob) { alert('Export failed.'); return; }
                        var a = document.createElement('a');
                        var url = URL.createObjectURL(blob);
                        a.href = url;
                        a.download = 'startrack-ratings.csv';
                        document.body.appendChild(a);
                        a.click();
                        setTimeout(function () {
                            document.body.removeChild(a);
                            URL.revokeObjectURL(url);
                        }, 0);
                    });
            });
        }

        // ── Overlay Letterboxd panel wiring ─────────────────────────────
        var ovLbBtn    = _overlay.querySelector('.ir-ov-lb');
        var ovLbPanel  = _overlay.querySelector('.ir-ov-lb-panel');
        var ovLbUser   = _overlay.querySelector('.ir-ov-lb-user');
        var ovLbAuto   = _overlay.querySelector('.ir-ov-lb-auto');
        var ovLbSave   = _overlay.querySelector('.ir-ov-lb-save');
        var ovLbSync   = _overlay.querySelector('.ir-ov-lb-sync');
        var ovLbDiag   = _overlay.querySelector('.ir-ov-lb-diag');
        var ovLbClean  = _overlay.querySelector('.ir-ov-lb-clean');
        var ovLbScrape = _overlay.querySelector('.ir-ov-lb-scrapefav');
        var ovLbFile   = _overlay.querySelector('.ir-ov-lb-file');
        var ovLbStatus = _overlay.querySelector('.ir-ov-lb-status');

        // v1.5.13: Cleanup is admin-only on the server side (returns 403 to
        // non-admins). Hide the button for non-admins so they don't see a
        // broken control. Best-effort — defaults to hidden if the admin
        // query fails, since false-hiding is harmless and false-showing
        // produces a confusing 403.
        if (ovLbClean) {
            ovLbClean.style.display = 'none';
            try {
                if (window.ApiClient && typeof window.ApiClient.getCurrentUser === 'function') {
                    window.ApiClient.getCurrentUser().then(function (u) {
                        if (u && u.Policy && u.Policy.IsAdministrator) ovLbClean.style.display = '';
                    }).catch(function () { /* keep hidden */ });
                }
            } catch (e) { /* keep hidden */ }
        }

        function ovLbShowStatus(text, kind, unmatched) {
            if (!ovLbStatus) return;
            ovLbStatus.classList.remove('ir-ov-lb-ok', 'ir-ov-lb-err');
            var html = esc(text);
            if (unmatched && unmatched.length) {
                html += '<div class="ir-ov-lb-unmatched">' +
                        '<div class="ir-ov-lb-unmatched-title">' + unmatched.length + ' not in library (showing first ' + Math.min(unmatched.length, 100) + '):</div>' +
                        unmatched.slice(0, 100).map(function (t) { return esc(t); }).join('<br>') +
                        '</div>';
            }
            ovLbStatus.innerHTML = html;
            if (kind === 'ok')  ovLbStatus.classList.add('ir-ov-lb-ok');
            if (kind === 'err') ovLbStatus.classList.add('ir-ov-lb-err');
        }

        ovLbBtn.addEventListener('click', function () {
            var opening = ovLbPanel.style.display === 'none';
            ovLbPanel.style.display = opening ? 'block' : 'none';
            ovLbBtn.classList.toggle('ir-ov-lb-active', opening);
            if (opening) {
                ovLbShowStatus('', '', null);
                apiLbGetSettings().then(function (s) {
                    if (!s) return;
                    ovLbUser.value = s.username || '';
                    ovLbAuto.checked = !!s.enableAutoSync;
                    if (s.lastSyncedAt) {
                        ovLbShowStatus('Last synced ' + timeAgo(s.lastSyncedAt) +
                            (s.lastImportedCount ? ' — imported ' + s.lastImportedCount + ' rating' + (s.lastImportedCount !== 1 ? 's' : '') : '') +
                            (s.lastUnmatchedCount ? ', ' + s.lastUnmatchedCount + ' not in library' : ''), '', null);
                    }
                });
            }
        });

        ovLbSave.addEventListener('click', function () {
            var u = (ovLbUser.value || '').trim();
            var auto = !!ovLbAuto.checked;
            if (auto && !u) { ovLbShowStatus('Enter your Letterboxd username first.', 'err', null); return; }
            ovLbSave.disabled = true;
            apiLbSaveSettings(u, auto).then(function (ok) {
                ovLbSave.disabled = false;
                ovLbShowStatus(ok ? 'Saved.' : 'Save failed.', ok ? 'ok' : 'err', null);
            });
        });

        // Defensive accessor: Jellyfin's JSON naming policy may be either
        // camelCase or PascalCase — handle both.
        function lbPick(o, cc, pc) {
            if (!o) return undefined;
            if (o[cc] !== undefined) return o[cc];
            if (o[pc] !== undefined) return o[pc];
            return undefined;
        }

        function lbResultMsg(r, verb) {
            var imported  = lbPick(r, 'imported', 'Imported') || 0;
            var updated   = lbPick(r, 'updated', 'Updated') || 0;
            var unmatched = lbPick(r, 'unmatched', 'Unmatched') || 0;
            var ambiguous = lbPick(r, 'ambiguous', 'Ambiguous') || 0;
            var libCount  = lbPick(r, 'libraryMovieCount', 'LibraryMovieCount');

            var parts = ['Imported ' + imported];
            if (updated)   parts.push('updated ' + updated);
            if (unmatched) parts.push(unmatched + ' not in library');
            if (ambiguous) parts.push(ambiguous + ' ambiguous');
            var libPart = libCount != null
                ? ' (library has ' + libCount + ' movie' + (libCount !== 1 ? 's' : '') + ')'
                : '';
            return parts.join(', ') + '.' + libPart;
        }

        ovLbSync.addEventListener('click', function () {
            if (!(ovLbUser.value || '').trim()) {
                ovLbShowStatus('Save a Letterboxd username first.', 'err', null); return;
            }
            ovLbSync.disabled = true;
            ovLbShowStatus('Syncing from Letterboxd\u2026', '', null);
            apiLbSyncNow().then(function (r) {
                ovLbSync.disabled = false;
                if (!r) { ovLbShowStatus('Sync failed.', 'err', null); return; }
                var err = lbPick(r, 'error', 'Error');
                if (err) { ovLbShowStatus(err, 'err', null); return; }
                var imported = lbPick(r, 'imported', 'Imported') || 0;
                var updated  = lbPick(r, 'updated', 'Updated') || 0;
                var total = imported + updated;
                var msg = total > 0 ? lbResultMsg(r, 'sync') : 'Nothing new on Letterboxd right now.';
                ovLbShowStatus(msg, 'ok', lbPick(r, 'unmatchedTitles', 'UnmatchedTitles'));
                if (_overlay.classList.contains('ir-ov-open')) loadMyRatings();
            });
        });

        if (ovLbScrape) {
            ovLbScrape.addEventListener('click', function () {
                if (!(ovLbUser.value || '').trim()) {
                    ovLbShowStatus('Save a Letterboxd username first.', 'err', null); return;
                }
                ovLbScrape.disabled = true;
                ovLbShowStatus('Fetching your Letterboxd profile\u2026', '', null);
                apiScrapeFavorites().then(function (r) {
                    ovLbScrape.disabled = false;
                    if (!r) { ovLbShowStatus('Could not reach Letterboxd.', 'err', null); return; }
                    if (r.error) { ovLbShowStatus(r.error, 'err', null); return; }
                    var n = r.imported || 0;
                    if (n === 0) {
                        ovLbShowStatus('No favorites found on your profile, or none of them matched your library.', 'err', null);
                    } else {
                        ovLbShowStatus('Set ' + n + ' favorite' + (n !== 1 ? 's' : '') + ' from your Letterboxd profile.', 'ok', null);
                    }
                    if (_overlay.classList.contains('ir-ov-open') && _overlayView === 'films') refreshFavoritesRow();
                });
            });
        }

        if (ovLbClean) {
            ovLbClean.addEventListener('click', function () {
                if (!window.confirm(
                    'Clean dead ratings?\n\n' +
                    'This deletes any StarTrack rating whose underlying library item ' +
                    'no longer has a valid file on disk (zombie entries left over from ' +
                    'dead hard drives). Existing ratings that still resolve to a real ' +
                    'file are untouched. This cannot be undone.')) return;
                ovLbClean.disabled = true;
                ovLbShowStatus('Scanning ratings for dead items\u2026', '', null);
                apiLbCleanup().then(function (r) {
                    ovLbClean.disabled = false;
                    if (!r) { ovLbShowStatus('Cleanup failed.', 'err', null); return; }
                    var err = lbPick(r, 'error', 'Error');
                    if (err) { ovLbShowStatus('Cleanup error: ' + err, 'err', null); return; }
                    var delItems  = lbPick(r, 'deletedItems', 'DeletedItems') || 0;
                    var delRow    = lbPick(r, 'deletedRatings', 'DeletedRatings') || 0;
                    var totalItem = lbPick(r, 'totalItems', 'TotalItems') || 0;
                    var msg = delItems > 0
                        ? 'Cleaned up ' + delRow + ' rating' + (delRow !== 1 ? 's' : '') +
                          ' across ' + delItems + ' dead item' + (delItems !== 1 ? 's' : '') +
                          ' (out of ' + totalItem + ' rated items).'
                        : 'No dead ratings found. All ' + totalItem + ' rated items resolve to living library entries.';
                    ovLbShowStatus(msg, 'ok', null);
                    if (_overlay.classList.contains('ir-ov-open')) loadMyRatings();
                });
            });
        }

        if (ovLbDiag) {
            // Defensive accessor: Jellyfin may serialize our DTO in either
            // camelCase or PascalCase depending on host config. Try both.
            function pick(obj, camel, pascal) {
                if (!obj) return undefined;
                if (obj[camel]  !== undefined) return obj[camel];
                if (obj[pascal] !== undefined) return obj[pascal];
                return undefined;
            }

            ovLbDiag.addEventListener('click', function () {
                ovLbDiag.disabled = true;
                ovLbShowStatus('Running library diagnostic\u2026', '', null);
                apiLbDiagnose().then(function (d) {
                    ovLbDiag.disabled = false;
                    if (!d) { ovLbShowStatus('Diagnose failed — check server logs for StarTrack errors.', 'err', null); return; }
                    var err = pick(d, 'error', 'Error');
                    if (err) { ovLbShowStatus('Diagnose error: ' + err, 'err', null); return; }

                    var count     = pick(d, 'libraryMovieCount', 'LibraryMovieCount') || 0;
                    var unique    = pick(d, 'uniqueNormalizedTitles', 'UniqueNormalizedTitles') || 0;
                    var zombies   = pick(d, 'zombiesFiltered', 'ZombiesFiltered') || 0;
                    var fallback  = pick(d, 'usedFallbackQuery', 'UsedFallbackQuery') || false;
                    var samples   = pick(d, 'sampleMovies', 'SampleMovies') || [];

                    var msg = 'Library query returned ' + count + ' living movie' + (count !== 1 ? 's' : '') + '.';
                    if (zombies > 0) {
                        msg += ' Filtered out ' + zombies + ' zombie item' + (zombies !== 1 ? 's' : '') +
                               ' with missing files (dead drive leftovers).';
                    }
                    if (unique && unique !== count) {
                        var dup = count - unique;
                        msg += ' ' + unique + ' unique normalized titles (' + dup + ' duplicate copies remain).';
                    }
                    if (fallback) msg += ' (Used fallback query path.)';
                    if (count === 0) {
                        msg += ' The library query is returning zero movies — StarTrack cannot see your library.' +
                               ' Check that your library folders have the "Movies" content type and a completed scan.';
                    }

                    var sampleText = samples.map(function (m) {
                        var orig = pick(m, 'originalTitle', 'OriginalTitle') || '';
                        var norm = pick(m, 'normalizedTitle', 'NormalizedTitle') || '(empty)';
                        var yr   = pick(m, 'year', 'Year');
                        return orig + (yr ? ' (' + yr + ')' : '') + '  →  ' + norm;
                    });
                    var html = esc(msg);
                    if (sampleText.length) {
                        html += '<div class="ir-ov-lb-unmatched">' +
                                '<div class="ir-ov-lb-unmatched-title">First ' + sampleText.length + ' library movies (original → normalized):</div>' +
                                sampleText.map(function (t) { return esc(t); }).join('<br>') +
                                '</div>';
                    }
                    ovLbStatus.classList.remove('ir-ov-lb-ok', 'ir-ov-lb-err');
                    if (count > 0) ovLbStatus.classList.add('ir-ov-lb-ok');
                    else           ovLbStatus.classList.add('ir-ov-lb-err');
                    ovLbStatus.innerHTML = html;
                });
            });
        }

        ovLbFile.addEventListener('change', function () {
            var file = ovLbFile.files && ovLbFile.files[0];
            if (!file) return;
            if (file.size > 5 * 1024 * 1024) {
                ovLbShowStatus('File is too large (max 5 MB).', 'err', null);
                ovLbFile.value = ''; return;
            }
            ovLbShowStatus('Importing ' + file.name + '\u2026', '', null);
            var reader = new FileReader();
            reader.onload = function () {
                apiLbImportBytes(reader.result, file.name).then(function (r) {
                    ovLbFile.value = '';
                    if (!r) { ovLbShowStatus('Import failed.', 'err', null); return; }
                    var err = lbPick(r, 'error', 'Error');
                    var unmatched = lbPick(r, 'unmatchedTitles', 'UnmatchedTitles');
                    if (err) { ovLbShowStatus(err, 'err', unmatched); return; }
                    ovLbShowStatus(lbResultMsg(r, 'import'), 'ok', unmatched);
                    if (_overlay.classList.contains('ir-ov-open')) loadMyRatings();
                });
            };
            reader.onerror = function () { ovLbShowStatus('Could not read file.', 'err', null); };
            reader.readAsArrayBuffer(file);
        });
        _overlay.querySelectorAll('.ir-ov-tab').forEach(function (tab) {
            tab.addEventListener('click', function () {
                _activeTab = tab.dataset.tab;
                _overlay.querySelectorAll('.ir-ov-tab').forEach(function (t) { t.classList.remove('ir-ov-tab-active'); });
                tab.classList.add('ir-ov-tab-active');
                if (_overlayData) renderOverlayGrid();
                // Type tab also drives which Top 4 row(s) show
                if (_overlayView === 'films') refreshFavoritesRow();
            });
        });
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && _overlay && _overlay.classList.contains('ir-ov-open')) {
                _overlay.classList.remove('ir-ov-open');
                document.documentElement.style.overflow = '';
            }
        });

        document.body.appendChild(_overlay);
        applyOverlayViewVisibility();
        return _overlay;
    }

    // Admin can hide specific My Ratings views (watchlist, liked, diary,
    // reviews, recs, lists) via PluginConfiguration.HiddenOverlayViews.
    // 'films' / Media is always available.
    function applyOverlayViewVisibility() {
        if (!_overlay) return;
        var hidden = _STARTRACK_CONFIG.hiddenOverlayViews || [];
        var hiddenSet = {};
        (Array.isArray(hidden) ? hidden : []).forEach(function (v) {
            hiddenSet[String(v).toLowerCase()] = true;
        });
        _overlay.querySelectorAll('.ir-ov-view').forEach(function (btn) {
            var v = (btn.getAttribute('data-view') || '').toLowerCase();
            if (v === 'films') { btn.style.display = ''; return; }
            btn.style.display = hiddenSet[v] ? 'none' : '';
        });
        // If the user is currently on a hidden view (admin just toggled
        // mid-session), bounce them back to Media.
        if (_overlayView && _overlayView !== 'films' && hiddenSet[_overlayView]) {
            _overlayView = 'films';
            _overlay.querySelectorAll('.ir-ov-view').forEach(function (b) { b.classList.remove('ir-ov-view-active'); });
            var filmsBtn = _overlay.querySelector('.ir-ov-view[data-view="films"]');
            if (filmsBtn) filmsBtn.classList.add('ir-ov-view-active');
            try { loadOverlayView(); } catch (e) {}
        }
    }

    // Maps a star rating to a CSS tier class so the badge color reflects
    // how strongly the user rated the film. Five distinct tiers feels
    // about right — finer than 3 (good/mid/bad) but not so granular it's
    // 10 colors.
    function starTierClass(stars) {
        if (stars >= 4.5) return 'ir-tier-5';   // gold + glow
        if (stars >= 4)   return 'ir-tier-4';   // gold
        if (stars >= 3)   return 'ir-tier-3';   // silver
        if (stars >= 2)   return 'ir-tier-2';   // bronze
        return 'ir-tier-1';                      // muted red
    }

    function buildOverlayCard(item, opts) {
        opts = opts || {};
        var card = document.createElement('div');
        card.className = 'ir-ov-card';
        card.dataset.id = item.itemId;

        var posterSrc = item.imageTag
            ? '/Items/' + item.itemId + '/Images/Primary?fillHeight=450&fillWidth=300&quality=90&tag=' + item.imageTag
            : '';

        var metaParts = [];
        if (item.year)            metaParts.push(item.year);
        if (item.runtime)         metaParts.push(formatRuntime(item.runtime));
        if (item.communityRating) metaParts.push('\u2605' + item.communityRating.toFixed(1));

        // Badge changes depending on view — stars (with tier color) for films,
        // text label for the other views.
        var badge = '';
        if (opts.showStars && typeof item.stars === 'number' && item.stars > 0) {
            var tier = starTierClass(item.stars);
            badge = '<div class="ir-ov-card-stars-badge ' + tier + '">\u2605 ' + item.stars.toFixed(1) + '</div>';
        } else if (opts.badge) {
            badge = '<div class="ir-ov-card-stars-badge">' + esc(opts.badge) + '</div>';
        }

        // Hover action buttons (top-right of the card). The set of actions
        // depends on the context — favorites slot has just an X to remove,
        // every other card has favorite-pin + add-to-list.
        var actionsHtml = '';
        if (opts.favSlotIndex != null) {
            actionsHtml = '<div class="ir-ov-card-actions">' +
                '<button class="ir-ov-card-act ir-ov-card-act-x" title="Remove from Top 4">\u2715</button>' +
                '</div>';
        } else if (opts.cardActions !== false) {
            actionsHtml = '<div class="ir-ov-card-actions">' +
                '<button class="ir-ov-card-act ir-ov-card-act-fav" title="Pin to Top 4 favorites">\u2605</button>' +
                '<button class="ir-ov-card-act ir-ov-card-act-list" title="Add to a list">\u002b</button>' +
                '</div>';
        }

        // Wanted-by line — only set on "Everyone's watchlist" view
        var wantedByLine = '';
        if (opts.showWantedBy && item.wantedBy && item.wantedBy.length) {
            var n = item.wantedBy.length;
            var label = n + ' user' + (n !== 1 ? 's' : '') + ': ' + item.wantedBy.slice(0, 3).join(', ') + (n > 3 ? ' +' + (n - 3) : '');
            wantedByLine = '<div class="ir-ov-card-wantedby">' + esc(label) + '</div>';
        }

        card.innerHTML =
            (posterSrc
                ? '<img class="ir-ov-poster" src="' + esc(posterSrc) + '" loading="lazy" alt="">'
                : '<div class="ir-ov-poster-ph">\u2605</div>') +
            '<div class="ir-ov-card-gradient"></div>' +
            actionsHtml +
            '<div class="ir-ov-card-overlay"><div class="ir-ov-card-play">\u25b6\ufe0e</div></div>' +
            '<div class="ir-ov-card-info">' +
                badge +
                '<div class="ir-ov-card-name" title="' + esc(item.name || '') + '">' + esc(item.name || item.itemId) + '</div>' +
                (metaParts.length ? '<div class="ir-ov-card-meta">' + esc(metaParts.join(' \u00b7 ')) + '</div>' : '') +
                wantedByLine +
                (item.review && opts.showReview ? '<div class="ir-ov-card-rev">' + esc(item.review) + '</div>' : '') +
            '</div>';

        // Wire the hover action buttons. We stop propagation so clicking an
        // action doesn't also navigate to the item detail page.
        var xBtn = card.querySelector('.ir-ov-card-act-x');
        if (xBtn) xBtn.addEventListener('click', function (e) {
            e.stopPropagation();
            removeFromFavorites(item.itemId);
        });
        var favBtn = card.querySelector('.ir-ov-card-act-fav');
        if (favBtn) favBtn.addEventListener('click', function (e) {
            e.stopPropagation();
            addCurrentToFavorites(item.itemId);
        });
        var listBtn = card.querySelector('.ir-ov-card-act-list');
        if (listBtn) listBtn.addEventListener('click', function (e) {
            e.stopPropagation();
            addToListPrompt(item.itemId);
        });

        card.addEventListener('click', function () { navigateToItem(item.itemId); });
        return card;
    }

    // Navigates to a Jellyfin item detail page in a way that doesn't
    // accidentally toggle the nav drawer open. Setting window.location.hash
    // directly while the overlay is closing seems to confuse Jellyfin's
    // router into popping the side menu, so we:
    //   1. Close the overlay first
    //   2. Wait one tick for the overlay close to settle
    //   3. Use Jellyfin's own router API if available (appRouter.show or
    //      Emby.Page.show), otherwise fall back to a hash assignment
    //   4. Pull focus away from anything that could be a menu trigger
    function navigateToItem(itemId) {
        if (_overlay) {
            _overlay.classList.remove('ir-ov-open');
            document.documentElement.style.overflow = '';
        }
        try { if (document.activeElement && document.activeElement.blur) document.activeElement.blur(); } catch (e) {}

        setTimeout(function () {
            var detailPath = '/details?id=' + itemId;
            // Try Jellyfin / Emby router APIs first — they navigate without
            // touching the nav drawer.
            try {
                if (window.appRouter && typeof window.appRouter.show === 'function') {
                    window.appRouter.show(detailPath);
                    return;
                }
            } catch (e) {}
            try {
                if (window.Emby && window.Emby.Page && typeof window.Emby.Page.show === 'function') {
                    window.Emby.Page.show(detailPath);
                    return;
                }
            } catch (e) {}
            // Fallback — direct hash assignment
            window.location.hash = '#!' + detailPath;
        }, 60);
    }

    // ── Hover action handlers ─────────────────────────────────────────────

    function addCurrentToFavorites(itemId) {
        // Resolve the item type once so we can enforce the per-type cap.
        // The Top 4 is per-type now: 4 movies, 4 series, 4 episodes (max 12 total).
        getItemsMeta([itemId]).then(function (meta) {
            var newType = (meta[itemId] && meta[itemId].type) || 'Movie';
            apiMyFavorites().then(function (favs) {
                favs = favs || [];
                if (favs.indexOf(itemId) >= 0) {
                    flashFeedback('Already pinned');
                    return;
                }
                // Need to know existing favorites' types to enforce the cap
                var existing = favs.filter(function (x) { return !!x; });
                if (existing.length === 0) {
                    favs.push(itemId);
                    apiSetFavorites(favs).then(function (ok) {
                        if (ok) { flashFeedback('Pinned to Top 4'); if (_overlayView === 'films') refreshFavoritesRow(); }
                    });
                    return;
                }
                getItemsMeta(existing).then(function (existingMeta) {
                    var sameType = existing.filter(function (id) {
                        return ((existingMeta[id] && existingMeta[id].type) || 'Movie') === newType;
                    });
                    if (sameType.length >= 4) {
                        var typeName = newType === 'Series' ? 'series' : newType === 'Episode' ? 'episodes' : 'films';
                        if (!window.confirm('You already have 4 ' + typeName + ' pinned. Replace the oldest one?')) return;
                        // Drop the oldest one of that type
                        var dropId = sameType[0];
                        favs = favs.filter(function (x) { return x !== dropId; });
                    }
                    favs.push(itemId);
                    apiSetFavorites(favs).then(function (ok) {
                        if (ok) { flashFeedback('Pinned to Top 4'); if (_overlayView === 'films') refreshFavoritesRow(); }
                    });
                });
            });
        });
    }

    function removeFromFavorites(itemId) {
        apiMyFavorites().then(function (favs) {
            favs = (favs || []).filter(function (x) { return x !== itemId; });
            apiSetFavorites(favs).then(function (ok) {
                if (ok && _overlayView === 'films') refreshFavoritesRow();
            });
        });
    }

    function addToListPrompt(itemId) {
        apiGetLists().then(function (lists) {
            lists = lists || [];
            openListsModal(itemId, lists);
        });
    }

    function openListsModal(itemId, lists) {
        // Remove any existing modal first
        var prev = document.getElementById('ir-list-modal');
        if (prev) prev.remove();

        var modal = document.createElement('div');
        modal.id = 'ir-list-modal';
        modal.className = 'ir-modal-backdrop';
        modal.innerHTML =
            '<div class="ir-modal">' +
                '<div class="ir-modal-head">' +
                    '<h3 class="ir-modal-title">Add to list</h3>' +
                    '<button class="ir-modal-close">\u2715</button>' +
                '</div>' +
                '<div class="ir-modal-body">' +
                    '<div class="ir-modal-listrows"></div>' +
                    '<div class="ir-modal-divider">or create a new list</div>' +
                    '<div class="ir-modal-newrow">' +
                        '<input type="text" class="ir-modal-name" placeholder="List name (e.g. Best horror)" maxlength="80" />' +
                        '<button class="ir-modal-create">+ Create &amp; add</button>' +
                    '</div>' +
                '</div>' +
            '</div>';

        var rowsHost = modal.querySelector('.ir-modal-listrows');
        if (lists.length === 0) {
            rowsHost.innerHTML = '<div class="ir-modal-empty">No lists yet. Create one below.</div>';
        } else {
            lists.forEach(function (l) {
                var row = document.createElement('button');
                row.className = 'ir-modal-listrow';
                row.innerHTML =
                    '<div class="ir-modal-listrow-name">' + esc(l.name) + '</div>' +
                    '<div class="ir-modal-listrow-meta">by ' + esc(l.ownerName) + ' \u00b7 ' +
                        l.items.length + ' film' + (l.items.length !== 1 ? 's' : '') + ' \u00b7 ' +
                        (l.collaborative ? 'collaborative' : 'private') + '</div>';
                row.addEventListener('click', function () {
                    apiAddToList(l.id, itemId).then(function () {
                        flashFeedback('Added to "' + l.name + '"');
                        closeListsModal();
                    });
                });
                rowsHost.appendChild(row);
            });
        }

        var nameInput = modal.querySelector('.ir-modal-name');
        modal.querySelector('.ir-modal-create').addEventListener('click', function () {
            var name = (nameInput.value || '').trim();
            if (!name) { nameInput.focus(); return; }
            apiCreateList(name, '', true).then(function (created) {
                if (!created) return;
                apiAddToList(created.id, itemId).then(function () {
                    flashFeedback('Created "' + name + '" and added film');
                    closeListsModal();
                });
            });
        });
        nameInput.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') modal.querySelector('.ir-modal-create').click();
        });

        modal.querySelector('.ir-modal-close').addEventListener('click', closeListsModal);
        modal.addEventListener('click', function (e) {
            if (e.target === modal) closeListsModal();
        });
        document.body.appendChild(modal);
        setTimeout(function () { nameInput.focus(); }, 50);
    }

    function closeListsModal() {
        var m = document.getElementById('ir-list-modal');
        if (m) m.remove();
    }

    // Standalone "create new list" modal — used by the Lists view's
    // "+ New list" button. Distinct from openListsModal because here
    // there's no item to add, just collect name + description + collab.
    function openCreateListModal(onCreated) {
        var prev = document.getElementById('ir-list-create-modal');
        if (prev) prev.remove();

        var modal = document.createElement('div');
        modal.id = 'ir-list-create-modal';
        modal.className = 'ir-modal-backdrop';
        modal.innerHTML =
            '<div class="ir-modal">' +
                '<div class="ir-modal-head">' +
                    '<h3 class="ir-modal-title">Create a new list</h3>' +
                    '<button class="ir-modal-close">\u2715</button>' +
                '</div>' +
                '<div class="ir-modal-body">' +
                    '<label class="ir-modal-label">List name</label>' +
                    '<input type="text" class="ir-modal-name ir-modal-cl-name" placeholder="e.g. Best horror of the 2010s" maxlength="80" />' +
                    '<label class="ir-modal-label">Description (optional)</label>' +
                    '<textarea class="ir-modal-desc" rows="3" maxlength="300" placeholder="What\u2019s this list about?"></textarea>' +
                    '<label class="ir-modal-check">' +
                        '<input type="checkbox" class="ir-modal-collab" checked /> ' +
                        'Collaborative \u2014 other users on this server can add films to it' +
                    '</label>' +
                    '<div class="ir-modal-newrow" style="margin-top:6px">' +
                        '<button class="ir-modal-cancel">Cancel</button>' +
                        '<button class="ir-modal-create">Create list</button>' +
                    '</div>' +
                '</div>' +
            '</div>';

        var close = function () { modal.remove(); };
        modal.querySelector('.ir-modal-close').addEventListener('click', close);
        modal.querySelector('.ir-modal-cancel').addEventListener('click', close);
        modal.addEventListener('click', function (e) { if (e.target === modal) close(); });

        var nameInput = modal.querySelector('.ir-modal-cl-name');
        var descInput = modal.querySelector('.ir-modal-desc');
        var collabInput = modal.querySelector('.ir-modal-collab');
        var createBtn = modal.querySelector('.ir-modal-create');

        var doCreate = function () {
            var name = (nameInput.value || '').trim();
            if (!name) { nameInput.focus(); return; }
            createBtn.disabled = true;
            apiCreateList(name, (descInput.value || '').trim(), collabInput.checked).then(function (created) {
                createBtn.disabled = false;
                if (!created) { flashFeedback('Failed to create list'); return; }
                close();
                flashFeedback('Created "' + name + '"');
                if (typeof onCreated === 'function') onCreated(created);
            });
        };
        createBtn.addEventListener('click', doCreate);
        nameInput.addEventListener('keydown', function (e) { if (e.key === 'Enter') doCreate(); });

        document.body.appendChild(modal);
        setTimeout(function () { nameInput.focus(); }, 50);
    }

    // Toast-style flash feedback for modal/action confirmations
    function flashFeedback(text) {
        var t = document.createElement('div');
        t.className = 'ir-flash-toast';
        t.textContent = text;
        document.body.appendChild(t);
        setTimeout(function () { t.classList.add('ir-flash-toast-show'); }, 10);
        setTimeout(function () {
            t.classList.remove('ir-flash-toast-show');
            setTimeout(function () { t.remove(); }, 300);
        }, 2200);
    }

    function applyStarFilter(list) {
        if (_starFilter === 'all') return list;
        // Discrete filter — exact star value match. We compare with a 0.05
        // tolerance because the stored value can have floating-point drift.
        var target = parseFloat(_starFilter);
        if (isNaN(target)) return list;
        return list.filter(function (i) {
            return typeof i.stars === 'number' && Math.abs(i.stars - target) < 0.05;
        });
    }

    // Renders a 5-star visual bar where stars are filled left-to-right
    // based on the rating. Half-star values use a half-filled glyph.
    // Returns an HTML string ready to drop into innerHTML.
    function renderStarBar(stars) {
        if (typeof stars !== 'number' || stars <= 0) {
            return '<span class="ir-starbar ir-starbar-empty">' +
                   '\u2606\u2606\u2606\u2606\u2606</span>';
        }
        var html = '<span class="ir-starbar">';
        for (var i = 1; i <= 5; i++) {
            if (stars >= i) {
                html += '<span class="ir-starbar-full">\u2605</span>';
            } else if (stars >= i - 0.5) {
                // Half-filled — render two overlapping glyphs via a span
                html += '<span class="ir-starbar-half">\u2605</span>';
            } else {
                html += '<span class="ir-starbar-empty">\u2606</span>';
            }
        }
        html += '</span>';
        return html;
    }

    function renderDiaryList(gridWrap, items) {
        // Diary list — one row per watch, grouped by month, shows dates
        var container = document.createElement('div');
        container.className = 'ir-ov-diary-list';
        var lastMonth = '';
        items.forEach(function (item) {
            var d = item.ratedAt ? new Date(item.ratedAt) : null;
            var monthKey = d ? d.toLocaleString('en-US', { year: 'numeric', month: 'long' }) : 'Undated';
            if (monthKey !== lastMonth) {
                var hdr = document.createElement('div');
                hdr.className = 'ir-ov-diary-month';
                hdr.textContent = monthKey;
                container.appendChild(hdr);
                lastMonth = monthKey;
            }

            var row = document.createElement('div');
            row.className = 'ir-ov-diary-row';
            var poster = item.imageTag
                ? '<img class="ir-ov-diary-poster" src="' + esc('/Items/' + item.itemId + '/Images/Primary?fillHeight=135&fillWidth=90&quality=90&tag=' + item.imageTag) + '" loading="lazy" alt="">'
                : '<div class="ir-ov-diary-poster"></div>';

            var dateStr = d ? d.toLocaleDateString('en-US', { day: 'numeric', month: 'short' }) : '';
            var hasRating = typeof item.stars === 'number' && item.stars > 0;
            var starBar = renderStarBar(item.stars);

            var metaParts = [];
            if (dateStr) metaParts.push(dateStr);
            if (hasRating) metaParts.push(starBar + ' <span class="ir-ov-diary-stars-num">' + item.stars.toFixed(1) + '</span>');
            else           metaParts.push(starBar + ' <span style="color:rgba(255,255,255,.35)">unrated</span>');

            row.innerHTML =
                poster +
                '<div class="ir-ov-diary-main">' +
                    '<div class="ir-ov-diary-head">' +
                        '<span class="ir-ov-diary-title">' + esc(item.name) + '</span>' +
                        (item.year ? '<span class="ir-ov-diary-year">' + item.year + '</span>' : '') +
                        (item.rewatch ? '<span class="ir-ov-diary-rewatch">Rewatch</span>' : '') +
                    '</div>' +
                    '<div class="ir-ov-diary-meta">' + metaParts.join(' \u00b7 ') + '</div>' +
                    (item.review ? '<div class="ir-ov-diary-review">' + esc(item.review) + '</div>' : '') +
                '</div>';
            row.addEventListener('click', function () { navigateToItem(item.itemId); });
            container.appendChild(row);
        });

        gridWrap.innerHTML = '';
        gridWrap.appendChild(container);
    }

    function renderOverlayGrid() {
        var gridWrap = _overlay.querySelector('.ir-ov-grid-wrap');
        var countEl  = _overlay.querySelector('.ir-ov-count');
        var favsEl   = _overlay.querySelector('.ir-ov-favs');

        // Favorites row is only shown in Films view — always visible even
        // when empty, so users can see the feature and pin their first film.
        // Uses a class toggle because the base rule is `display:flex!important`
        // and a plain inline style won't override it.
        if (favsEl) favsEl.classList.toggle('ir-ov-favs-hidden', _overlayView !== 'films');

        // Lists view has its own renderer
        if (_overlayView === 'lists') { renderListsView(gridWrap); if (countEl) countEl.textContent = ''; return; }
        // Reviews view has its own renderer too
        if (_overlayView === 'reviews') { renderReviewsView(gridWrap); if (countEl) countEl.textContent = ''; return; }

        var source = _overlayData || [];

        // 1. Filter by type tab. Anime isn't a Jellyfin type — it's a
        //    Movie or Series with the 'Anime' genre/tag — so it gets its
        //    own branch that filters on the isAnime flag we computed at
        //    metadata-fetch time.
        var filtered;
        if (_activeTab === 'all') {
            filtered = source;
        } else if (_activeTab === 'Anime') {
            filtered = source.filter(function (i) { return i.isAnime; });
        } else {
            filtered = source.filter(function (i) { return i.type === _activeTab; });
        }

        // 2. Filter by search query
        if (_searchQuery) {
            var q = _searchQuery.toLowerCase();
            filtered = filtered.filter(function (i) {
                return (i.name || '').toLowerCase().indexOf(q) !== -1;
            });
        }

        // 3. Star filter (only applies to views with stars)
        if (_overlayView === 'films' || _overlayView === 'diary') {
            filtered = applyStarFilter(filtered);
        }

        // 4. Per-view empty state text
        var emptyText;
        if (_overlayView === 'watchlist')      emptyText = 'Your watchlist is empty. Add films via the bookmark button on the rating panel, or import your Letterboxd watchlist.csv.';
        else if (_overlayView === 'liked')     emptyText = 'No liked films yet. Tap the heart on the rating panel to like a film.';
        else if (_overlayView === 'diary')     emptyText = 'Your diary is empty. Rate some films or import your Letterboxd diary.csv to see rewatches show up here.';
        else if (_overlayView === 'recs')      emptyText = 'No recommendations yet. Rate a handful of films (especially 4-5 stars) and StarTrack will suggest new titles from genres you love.';
        else                                    emptyText = source.length ? 'No titles match.' : 'You haven\'t rated anything yet.';

        if (!filtered.length) {
            gridWrap.innerHTML = '<div class="ir-ov-empty">' + esc(emptyText) + '</div>';
            if (countEl) countEl.textContent = '';
            return;
        }

        if (countEl) {
            var labelWord = _overlayView === 'watchlist' ? 'on watchlist'
                           : _overlayView === 'liked'     ? 'liked'
                           : _overlayView === 'recs'      ? 'suggested'
                           : _overlayView === 'diary'     ? 'diary entries'
                           : 'rated';
            countEl.textContent = filtered.length + ' ' + (filtered.length !== 1 ? labelWord : labelWord.replace(/s$/, ''));
        }

        // 5. Sort. Diary view forces watchedAt desc. Recs view forces community rating desc.
        var sortFn;
        if (_overlayView === 'diary')      sortFn = _sortFns['ratedAt-desc'];
        else if (_overlayView === 'recs')  sortFn = _sortFns['community-desc'];
        else                               sortFn = _sortFns[_sortKey] || _sortFns['ratedAt-desc'];
        var sorted = filtered.slice().sort(sortFn);

        // 6. Diary view renders as a chronological list, not a grid
        if (_overlayView === 'diary') {
            renderDiaryList(gridWrap, sorted);
            return;
        }

        // 7. Recs view shows a reshuffle button at the top
        gridWrap.innerHTML = '';
        if (_overlayView === 'recs') {
            var btn = document.createElement('button');
            btn.className = 'ir-ov-reshuffle';
            btn.textContent = '\ud83c\udfb2 Reshuffle recommendations';
            btn.addEventListener('click', function () { reshuffleRecs(); });
            gridWrap.appendChild(btn);
        }

        // Watchlist view shows a "mine vs everyone" scope toggle pill row,
        // plus a user dropdown when on the everyone scope so the user can
        // filter the aggregated view down to a specific other user's
        // watchlist (e.g. "show me Adam's watchlist").
        if (_overlayView === 'watchlist') {
            var scope = document.createElement('div');
            scope.className = 'ir-ov-scope';

            var userOptions = '<option value="">All users</option>';
            _everyoneUsers.forEach(function (n) {
                var sel = (n === _watchlistUserFilter) ? ' selected' : '';
                userOptions += '<option value="' + esc(n) + '"' + sel + '>' + esc(n) + '</option>';
            });

            scope.innerHTML =
                '<button class="ir-ov-scope-btn ' + (_watchlistScope === 'mine' ? 'ir-ov-scope-active' : '') + '" data-scope="mine">My watchlist</button>' +
                '<button class="ir-ov-scope-btn ' + (_watchlistScope === 'everyone' ? 'ir-ov-scope-active' : '') + '" data-scope="everyone">\ud83d\udc65 Everyone\u2019s watchlist</button>' +
                (_watchlistScope === 'everyone'
                    ? '<select class="ir-ov-scope-user">' + userOptions + '</select>'
                    : '');

            scope.querySelectorAll('.ir-ov-scope-btn').forEach(function (b) {
                b.addEventListener('click', function () {
                    var s = b.dataset.scope;
                    if (s === _watchlistScope) return;
                    _watchlistScope = s;
                    _watchlistUserFilter = '';
                    loadOverlayView();
                });
            });
            var userSel = scope.querySelector('.ir-ov-scope-user');
            if (userSel) {
                userSel.addEventListener('change', function (e) {
                    _watchlistUserFilter = e.target.value || '';
                    loadOverlayView();
                });
            }
            gridWrap.appendChild(scope);
        }

        var grid = document.createElement('div');
        grid.className = 'ir-ov-grid';
        var opts = {
            showStars:  _overlayView === 'films',
            showReview: _overlayView === 'films'
        };
        if (_overlayView === 'watchlist') {
            opts.badge = (_watchlistScope === 'everyone') ? '\ud83d\udc65 Wanted' : '\u2606 Watchlist';
            // For everyone-scope, the card builder will use item.wantedBy
            // to render the per-item user list as a sub-line.
            opts.showWantedBy = (_watchlistScope === 'everyone');
        }
        if (_overlayView === 'liked')     opts.badge = '\u2665 Liked';
        if (_overlayView === 'recs')      opts.badge = '\u2728 For you';

        sorted.forEach(function (item) { grid.appendChild(buildOverlayCard(item, opts)); });
        gridWrap.appendChild(grid);
    }

    function reshuffleRecs() {
        if (!_recsPool || _recsPool.length === 0) return;
        // Shuffle a copy of the pool and take a fresh 30 items
        var shuffled = _recsPool.slice();
        for (var i = shuffled.length - 1; i > 0; i--) {
            var j = Math.floor(Math.random() * (i + 1));
            var tmp = shuffled[i]; shuffled[i] = shuffled[j]; shuffled[j] = tmp;
        }
        var picks = shuffled.slice(0, 30);
        hydrateAndRender(picks.map(function (r) { return { itemId: r.itemId }; }));
    }

    // Reviews feed — fetches recent ratings server-wide, filters to ones
    // with non-empty review text, renders as a vertical feed.
    function renderReviewsView(gridWrap) {
        gridWrap.innerHTML = '<div class="ir-ov-loading">Loading reviews\u2026</div>';
        apiRecent(100).then(function (recents) {
            var withReviews = (recents || []).filter(function (r) {
                return r.review && r.review.trim().length > 0;
            });
            if (withReviews.length === 0) {
                gridWrap.innerHTML = '<div class="ir-ov-empty">No reviews on this server yet. Add one when you rate a film via the rating pill on a detail page.</div>';
                return;
            }

            var ids = [];
            var seen = {};
            withReviews.forEach(function (r) { if (!seen[r.itemId]) { seen[r.itemId] = 1; ids.push(r.itemId); } });
            getItemsMeta(ids).then(function (meta) {
                // Apply the active filters/sort so this view respects the
                // topbar controls just like every other view does.
                var q = (_searchQuery || '').toLowerCase();
                var filtered = withReviews.filter(function (r) {
                    var m = meta[r.itemId] || {};
                    // Type tab
                    if (_activeTab && _activeTab !== 'all') {
                        if (_activeTab === 'Anime') {
                            if (!m.isAnime) return false;
                        } else if ((m.type || '') !== _activeTab) {
                            return false;
                        }
                    }
                    // Search
                    if (q && (m.name || '').toLowerCase().indexOf(q) === -1) return false;
                    // Star filter
                    if (_starFilter && _starFilter !== 'all') {
                        var target = parseFloat(_starFilter);
                        if (!isNaN(target) && Math.abs((r.stars || 0) - target) > 0.001) return false;
                    }
                    return true;
                });

                // Sort. Most of the sort keys care about item metadata,
                // not the review itself — we look them up in the meta map.
                var sortKey = _sortKey || 'ratedAt-desc';
                filtered.sort(function (a, b) {
                    var ma = meta[a.itemId] || {}, mb = meta[b.itemId] || {};
                    switch (sortKey) {
                        case 'ratedAt-asc':    return new Date(a.ratedAt) - new Date(b.ratedAt);
                        case 'year-desc':      return (mb.year || 0) - (ma.year || 0);
                        case 'year-asc':       return (ma.year || 0) - (mb.year || 0);
                        case 'mine-desc':      return (b.stars || 0) - (a.stars || 0);
                        case 'mine-asc':       return (a.stars || 0) - (b.stars || 0);
                        case 'community-desc': return (mb.communityRating || 0) - (ma.communityRating || 0);
                        case 'community-asc':  return (ma.communityRating || 0) - (mb.communityRating || 0);
                        case 'runtime-desc':   return (mb.runtime || 0) - (ma.runtime || 0);
                        case 'runtime-asc':    return (ma.runtime || 0) - (mb.runtime || 0);
                        case 'ratedAt-desc':
                        default:               return new Date(b.ratedAt) - new Date(a.ratedAt);
                    }
                });

                if (filtered.length === 0) {
                    gridWrap.innerHTML = '<div class="ir-ov-empty">No reviews match your filters.</div>';
                    return;
                }

                // Update the count pill in the topbar so users see how
                // many reviews match.
                var countEl = _overlay.querySelector('.ir-ov-count');
                if (countEl) countEl.textContent = filtered.length + ' review' + (filtered.length !== 1 ? 's' : '');

                var feed = document.createElement('div');
                feed.className = 'ir-ov-reviews-feed';
                filtered.forEach(function (r) {
                    var m = meta[r.itemId] || {};
                    var d = r.ratedAt ? new Date(r.ratedAt) : null;
                    var dateStr = d ? d.toLocaleDateString('en-US', { day: 'numeric', month: 'short', year: 'numeric' }) : '';
                    var posterSrc = m.imageTag
                        ? '/Items/' + r.itemId + '/Images/Primary?fillHeight=180&fillWidth=120&quality=90&tag=' + m.imageTag
                        : '';
                    var stars = renderStarBar(r.stars);

                    var item = document.createElement('div');
                    item.className = 'ir-ov-review';
                    item.innerHTML =
                        (posterSrc
                            ? '<img class="ir-ov-review-poster" src="' + esc(posterSrc) + '" loading="lazy" alt="">'
                            : '<div class="ir-ov-review-poster"></div>') +
                        '<div class="ir-ov-review-body">' +
                            '<div class="ir-ov-review-head">' +
                                '<span class="ir-ov-review-title">' + esc(m.name || r.itemId) + '</span>' +
                                (m.year ? '<span class="ir-ov-review-year">' + m.year + '</span>' : '') +
                            '</div>' +
                            '<div class="ir-ov-review-meta">' +
                                '<span class="ir-ov-review-user">' + esc(r.userName) + '</span> \u00b7 ' +
                                stars + ' ' +
                                '<span class="ir-ov-review-date">' + esc(dateStr) + '</span>' +
                            '</div>' +
                            '<div class="ir-ov-review-text">' + esc(r.review) + '</div>' +
                        '</div>';
                    item.addEventListener('click', function () { navigateToItem(r.itemId); });
                    feed.appendChild(item);
                });
                gridWrap.innerHTML = '';
                gridWrap.appendChild(feed);
            });
        });
    }

    function renderListsView(gridWrap) {
        // If a specific list is selected, show its contents. Otherwise, list index.
        if (_currentListId) {
            apiGetLists().then(function (all) {
                var list = (all || []).find(function (l) { return l.id === _currentListId; });
                if (!list) {
                    _currentListId = null;
                    renderListsView(gridWrap);
                    return;
                }
                // Detect ownership so we can show the Delete button only
                // for lists the current user actually owns.
                var myUserId = (getUserId() || '').replace(/-/g, '').toLowerCase();
                var isOwner = (list.ownerId || '').toLowerCase() === myUserId;

                var detail = document.createElement('div');
                detail.className = 'ir-ov-list-detail';
                detail.innerHTML =
                    '<div class="ir-ov-list-detail-actions">' +
                        '<button class="ir-ov-list-back">\u2190 All lists</button>' +
                        (isOwner ? '<button class="ir-ov-list-delete">\ud83d\uddd1 Delete list</button>' : '') +
                    '</div>' +
                    '<div class="ir-ov-list-title">' + esc(list.name) + '</div>' +
                    '<div class="ir-ov-list-byline">by ' + esc(list.ownerName) + ' \u00b7 ' +
                        (list.collaborative ? 'collaborative' : 'private') + ' \u00b7 ' +
                        list.items.length + ' film' + (list.items.length !== 1 ? 's' : '') + '</div>' +
                    (list.description ? '<div class="ir-ov-list-desc">' + esc(list.description) + '</div>' : '');

                gridWrap.innerHTML = '';
                gridWrap.appendChild(detail);
                detail.querySelector('.ir-ov-list-back').addEventListener('click', function () {
                    _currentListId = null;
                    renderListsView(gridWrap);
                });
                var deleteBtn = detail.querySelector('.ir-ov-list-delete');
                if (deleteBtn) {
                    deleteBtn.addEventListener('click', function () {
                        if (!window.confirm('Delete the list "' + list.name + '"? This cannot be undone.')) return;
                        apiDeleteList(list.id).then(function (ok) {
                            if (ok) {
                                _currentListId = null;
                                flashFeedback('List deleted');
                                renderListsView(gridWrap);
                            } else {
                                flashFeedback('Could not delete list');
                            }
                        });
                    });
                }

                if (list.items.length === 0) {
                    var empty = document.createElement('div');
                    empty.className = 'ir-ov-empty';
                    empty.textContent = 'This list is empty. Click a film on a detail page and use the "Add to list" action to populate it.';
                    gridWrap.appendChild(empty);
                    return;
                }

                var ids = list.items.map(function (i) { return i.itemId; });
                getItemsMeta(ids).then(function (meta) {
                    var grid = document.createElement('div');
                    grid.className = 'ir-ov-grid';
                    list.items.forEach(function (li) {
                        var m = meta[li.itemId] || {};
                        grid.appendChild(buildOverlayCard({
                            itemId: li.itemId,
                            name: m.name || li.itemId,
                            year: m.year || 0,
                            runtime: m.runtime || 0,
                            communityRating: m.communityRating || 0,
                            imageTag: m.imageTag || null,
                            type: m.type || 'Unknown'
                        }, { badge: 'by ' + li.addedByName }));
                    });
                    gridWrap.appendChild(grid);
                });
            });
            return;
        }

        // Lists index
        apiGetLists().then(function (lists) {
            gridWrap.innerHTML = '';
            var header = document.createElement('div');
            header.className = 'ir-ov-lists-header';
            header.innerHTML = '<button class="ir-ov-lists-new">+ New list</button>' +
                               '<span style="color:rgba(255,255,255,.5);font-size:.85em">' +
                               ((lists || []).length) + ' list' + (((lists || []).length) !== 1 ? 's' : '') +
                               ' on this server</span>';
            gridWrap.appendChild(header);
            header.querySelector('.ir-ov-lists-new').addEventListener('click', function () {
                openCreateListModal(function (created) {
                    _currentListId = created.id;
                    renderListsView(gridWrap);
                });
            });

            if (!lists || lists.length === 0) {
                var empty = document.createElement('div');
                empty.className = 'ir-ov-empty';
                empty.textContent = 'No lists on this server yet. Click "+ New list" to create one. Lists are collaborative by default — any user on your server can add films to them.';
                gridWrap.appendChild(empty);
                return;
            }

            var myUserId = (getUserId() || '').replace(/-/g, '').toLowerCase();
            var grid = document.createElement('div');
            grid.className = 'ir-ov-lists-grid';
            lists.forEach(function (l) {
                var isOwner = (l.ownerId || '').toLowerCase() === myUserId;
                var card = document.createElement('div');
                card.className = 'ir-ov-list-card';
                card.innerHTML =
                    (isOwner
                        ? '<div class="ir-ov-list-card-actions"><button class="ir-ov-list-card-del" title="Delete list">\u2715</button></div>'
                        : '') +
                    '<div class="ir-ov-list-name">' + esc(l.name) + '</div>' +
                    '<div class="ir-ov-list-owner">by ' + esc(l.ownerName) + '</div>' +
                    (l.description ? '<div class="ir-ov-list-desc">' + esc(l.description) + '</div>' : '') +
                    '<div class="ir-ov-list-stats">' +
                        '<span>' + l.items.length + ' film' + (l.items.length !== 1 ? 's' : '') + '</span>' +
                        (l.collaborative ? '<span class="ir-ov-list-collab">\u2731 collaborative</span>' : '<span>private</span>') +
                    '</div>';
                card.addEventListener('click', function () {
                    _currentListId = l.id;
                    renderListsView(gridWrap);
                });
                var delBtn = card.querySelector('.ir-ov-list-card-del');
                if (delBtn) {
                    delBtn.addEventListener('click', function (e) {
                        e.stopPropagation();
                        if (!window.confirm('Delete "' + l.name + '"? This cannot be undone.')) return;
                        apiDeleteList(l.id).then(function (ok) {
                            if (ok) { flashFeedback('List deleted'); renderListsView(gridWrap); }
                            else    { flashFeedback('Could not delete list'); }
                        });
                    });
                }
                grid.appendChild(card);
            });
            gridWrap.appendChild(grid);
        });
    }

    // Takes an array of entries with at least { itemId } and fetches metadata
    // for each, building the normalised _overlayData structure used by render.
    // Note: unlike a ratings list, a diary list can have the same itemId
    // multiple times (rewatches). We keep the original entries 1:1 and just
    // look up metadata once per unique id.
    function hydrateAndRender(entries) {
        if (!entries || !entries.length) {
            _overlayData = [];
            renderOverlayGrid();
            return;
        }
        var uniqueIds = [];
        var seen = {};
        entries.forEach(function (e) {
            if (!seen[e.itemId]) { seen[e.itemId] = 1; uniqueIds.push(e.itemId); }
        });
        getItemsMeta(uniqueIds).then(function (meta) {
            _overlayData = entries.map(function (e) {
                var m = meta[e.itemId] || {};
                return {
                    itemId: e.itemId,
                    stars: (typeof e.stars === 'number') ? e.stars : 0,
                    review: e.review || null,
                    ratedAt: e.ratedAt || e.watchedAt || e.addedAt || e.likedAt || null,
                    rewatch: e.rewatch || false,
                    wantedBy: e.wantedBy || null,
                    name: m.name || e.itemId,
                    year: m.year || 0,
                    runtime: m.runtime || 0,
                    communityRating: m.communityRating || 0,
                    imageTag: m.imageTag || null,
                    type: m.type || 'Unknown',
                    isAnime: !!m.isAnime
                };
            });
            renderOverlayGrid();
        });
    }

    function loadOverlayView() {
        var gridWrap = _overlay.querySelector('.ir-ov-grid-wrap');
        gridWrap.innerHTML = '<div class="ir-ov-loading">Loading\u2026</div>';

        // Always refresh the favorites strip when any view loads
        refreshFavoritesRow();

        if (_overlayView === 'films') {
            apiMyRatings().then(function (ratings) { hydrateAndRender(ratings || []); });
        } else if (_overlayView === 'watchlist') {
            // Watchlist view supports a "mine vs everyone" scope toggle.
            // Default is "mine"; the user can flip to see what every other
            // user on the server has on their watchlist.
            if (_watchlistScope === 'everyone') {
                apiEveryonesWatchlist().then(function (rows) {
                    rows = rows || [];
                    // Build a unique sorted list of usernames for the dropdown.
                    var userSet = {};
                    rows.forEach(function (e) {
                        (e.userNames || []).forEach(function (n) { userSet[n] = 1; });
                    });
                    _everyoneUsers = Object.keys(userSet).sort(function (a, b) { return a.localeCompare(b); });

                    // Apply optional per-user filter
                    var filtered = rows;
                    if (_watchlistUserFilter) {
                        filtered = rows.filter(function (e) {
                            return (e.userNames || []).indexOf(_watchlistUserFilter) !== -1;
                        });
                    }

                    hydrateAndRender(filtered.map(function (e) {
                        return {
                            itemId:  e.itemId,
                            ratedAt: e.firstAddedAt,
                            wantedBy: e.userNames || []
                        };
                    }));
                });
            } else {
                apiMyWatchlist().then(function (rows) {
                    hydrateAndRender((rows || []).map(function (w) {
                        return { itemId: w.itemId, ratedAt: w.addedAt };
                    }));
                });
            }
        } else if (_overlayView === 'liked') {
            apiMyLikes().then(function (rows) {
                hydrateAndRender((rows || []).map(function (l) {
                    return { itemId: l.itemId, ratedAt: l.likedAt };
                }));
            });
        } else if (_overlayView === 'diary') {
            // Proper diary source now — allows rewatch duplicates
            apiMyDiary().then(function (rows) {
                hydrateAndRender((rows || []).map(function (d) {
                    return {
                        itemId: d.itemId,
                        stars: d.stars,
                        review: d.review,
                        ratedAt: d.watchedAt,
                        rewatch: d.rewatch
                    };
                }));
            });
        } else if (_overlayView === 'recs') {
            apiRecommendations(60).then(function (rows) {
                _recsPool = rows || [];
                // Initial render picks the first 30 by community rating;
                // the "Reshuffle" button rerolls from the full pool.
                var first = _recsPool.slice(0, 30).map(function (r) { return { itemId: r.itemId }; });
                hydrateAndRender(first);
            });
        } else if (_overlayView === 'lists') {
            _overlayData = [];
            renderOverlayGrid();
        } else if (_overlayView === 'reviews') {
            _overlayData = [];
            renderOverlayGrid();
        } else if (_overlayView === 'members') {
            apiListMembers().then(function (rows) { renderMembersGrid(rows || []); });
        } else if (_overlayView === 'member-profile') {
            if (!_profileUserId) { _overlayView = 'members'; loadOverlayView(); return; }
            apiMemberProfile(_profileUserId).then(function (p) { renderMemberProfile(p); });
        } else if (_overlayView === 'activity') {
            apiActivityFeed(150).then(function (rows) { renderActivityFeed(rows || []); });
        } else if (_overlayView === 'compare') {
            if (!_compareWith) { _overlayView = 'members'; loadOverlayView(); return; }
            apiCompare(getUserId(), _compareWith).then(function (d) { _compareData = d; renderCompareView(d); });
        }
    }

    // ── Members grid + profile view ───────────────────────────────────────
    //
    // Members tab shows a compact card per Jellyfin user (avatar, name,
    // followers/following counts, Follow/Unfollow button, click-through to
    // profile). Profile view shows a Letterboxd-flavored bento: avatar +
    // follow chrome, stats row, Top 4 with category tabs (Movies/Series/
    // Anime/All), recent ratings strip, capped recent diary, watchlist +
    // likes strips, "view all" buttons, redesigned histogram, Letterboxd-
    // style aggregate stats panel. View-all sub-pages and follower/following
    // lists reuse the same profile chrome.

    function renderMembersGrid(members) {
        var gridWrap = _overlay.querySelector('.ir-ov-grid-wrap');
        _membersCache = members || [];
        // Apply search filter client-side; fast and avoids server round-trips
        // every keystroke. The MembersSearch endpoint is still available for
        // future server-side filtering if the user list ever grows large.
        var q = (_membersSearchQ || '').trim().toLowerCase();
        var filtered = q
            ? _membersCache.filter(function (m) { return (m.name || '').toLowerCase().indexOf(q) !== -1; })
            : _membersCache;

        var searchHtml =
            '<div class="ir-mem-search-row">' +
                '<input class="ir-mem-search" type="text" placeholder="Search members…" value="' + esc(_membersSearchQ) + '" />' +
                (q ? '<button class="ir-mem-search-clear" type="button" title="Clear">✕</button>' : '') +
            '</div>';

        if (filtered.length === 0) {
            gridWrap.innerHTML = searchHtml +
                (q
                    ? '<div class="ir-ov-empty">No members match “' + esc(q) + '”.</div>'
                    : '<div class="ir-ov-empty">No members on this server yet.</div>');
            wireMembersSearch(gridWrap);
            return;
        }

        // Members cards v5 — large Letterboxd-style profile cell:
        //   row 1: avatar | name + handle row + stats grid (rated / avg / followers / following)
        //   row 2: Top 4 thumbnails strip
        //   row 3: full-width Follow button
        // Thumbnail posters are hydrated lazily after render via getItemsMeta.
        var html = searchHtml + '<div class="ir-mem-grid ir-mem-grid-v5">';
        // Collect every Top4 itemId across visible cards so we hydrate once.
        var allTopIds = {};
        filtered.forEach(function (m) {
            (m.top4Ids || []).forEach(function (id) { if (id) allTopIds[id] = 1; });
        });

        filtered.forEach(function (m) {
            var img = m.hasImage
                ? '/Users/' + encodeURIComponent(m.id) + '/Images/Primary?fillHeight=200&fillWidth=200&quality=90'
                : null;
            var avatar = img
                ? '<img class="ir-mem-avatar-img" src="' + esc(img) + '" alt="" loading="lazy" />'
                : '<span class="ir-mem-avatar-fallback">' + esc((m.name || '?').charAt(0).toUpperCase()) + '</span>';
            var followBtn;
            if (m.isSelf) {
                followBtn = '<span class="ir-mem-card-self">your profile</span>';
            } else {
                followBtn =
                    '<button class="ir-mem-follow-btn ' + (m.isFollowing ? 'ir-mem-follow-on' : '') + '" data-uid="' + esc(m.id) + '" type="button">' +
                        (m.isFollowing
                            ? '<span class="ir-follow-text-following">Following</span>'
                            : 'Follow') +
                    '</button>' +
                    '<button class="ir-mem-compare-btn" data-uid="' + esc(m.id) + '" type="button" title="Compare taste with ' + esc(m.name || '') + '">⇄</button>';
            }

            // Top 4 thumbnail slots — actual <img> tags so we can react to
            // load failures (Person items without primary images, etc) by
            // swapping to the title-initial fallback. We always set a src
            // even before the meta hydrate so Jellyfin's tagless URL works.
            var topSlots = '';
            for (var ti = 0; ti < 4; ti++) {
                var iid = (m.top4Ids || [])[ti];
                if (iid) {
                    var initialSrc = '/Items/' + iid + '/Images/Primary?fillHeight=180&fillWidth=120&quality=90';
                    topSlots +=
                        '<span class="ir-mem-card-top4-slot" data-iid="' + esc(iid) + '">' +
                            '<img class="ir-mem-card-top4-img" src="' + esc(initialSrc) + '" loading="lazy" alt="" ' +
                            'onerror="this.classList.add(\'ir-mem-card-top4-failed\')" />' +
                        '</span>';
                } else {
                    topSlots += '<span class="ir-mem-card-top4-slot ir-mem-card-top4-empty"></span>';
                }
            }

            var avgStr = m.avgRating > 0 ? m.avgRating.toFixed(2) + '★' : '—';

            html +=
                '<div class="ir-mem-card ir-mem-card-v5">' +
                    '<button class="ir-mem-card-body" data-uid="' + esc(m.id) + '" data-uname="' + esc(m.name) + '" type="button">' +
                        '<div class="ir-mem-card-row1">' +
                            '<span class="ir-mem-avatar">' + avatar + '</span>' +
                            '<div class="ir-mem-card-meta">' +
                                '<div class="ir-mem-card-name-row">' +
                                    '<span class="ir-mem-name">' + esc(m.name) + '</span>' +
                                    (m.isSelf ? '<span class="ir-mem-you-pill">YOU</span>' : '') +
                                '</div>' +
                                '<div class="ir-mem-card-statgrid">' +
                                    '<span><b>' + (m.ratingsCount|0) + '</b> rated</span>' +
                                    '<span><b>' + esc(avgStr) + '</b> avg</span>' +
                                    '<span><b>' + (m.watchlistCount|0) + '</b> watchlist</span>' +
                                    '<span><b>' + (m.likesCount|0) + '</b> liked</span>' +
                                    '<span><b>' + (m.followersCount|0) + '</b> followers</span>' +
                                    '<span><b>' + (m.followingCount|0) + '</b> following</span>' +
                                '</div>' +
                            '</div>' +
                        '</div>' +
                        '<div class="ir-mem-card-top4">' + topSlots + '</div>' +
                    '</button>' +
                    '<div class="ir-mem-card-foot">' + followBtn + '</div>' +
                '</div>';
        });
        html += '</div>';
        gridWrap.innerHTML = html;
        gridWrap.querySelectorAll('.ir-mem-card-body').forEach(function (btn) {
            btn.addEventListener('click', function () {
                _profileUserId = btn.getAttribute('data-uid');
                _profileUserName = btn.getAttribute('data-uname') || '';
                _profileTab = 'overview';
                _profileTopCat = 'all';
                _overlayView = 'member-profile';
                loadOverlayView();
            });
        });
        gridWrap.querySelectorAll('.ir-mem-follow-btn').forEach(function (btn) {
            btn.addEventListener('click', function (ev) {
                ev.stopPropagation();
                var uid = btn.getAttribute('data-uid');
                var on = btn.classList.contains('ir-mem-follow-on');
                btn.disabled = true;
                (on ? apiUnfollow(uid) : apiFollow(uid)).finally(function () {
                    btn.disabled = false;
                    apiListMembers().then(function (rows) { renderMembersGrid(rows || []); });
                });
            });
        });
        // Compare button on each member card
        gridWrap.querySelectorAll('.ir-mem-compare-btn').forEach(function (btn) {
            btn.addEventListener('click', function (ev) {
                ev.stopPropagation();
                _compareWith = btn.getAttribute('data-uid');
                _compareData = null;
                _overlayView = 'compare';
                loadOverlayView();
            });
        });
        wireMembersSearch(gridWrap);
        // Top 4 thumbnails are <img> tags now with src set inline at render
        // time — no hydration pass needed. The onerror handler in the
        // markup tags failures so CSS can show a fallback.
    }

    // Activity feed — chronological list of recent ratings + diary entries +
    // reviews from the users you follow (or everyone on the server).
    function renderActivityFeed(entries) {
        var gridWrap = _overlay.querySelector('.ir-ov-grid-wrap');
        var scopeChips =
            '<div class="ir-act-scope">' +
                '<button class="ir-act-scope-btn ' + (_activityScope === 'following' ? 'ir-act-scope-on' : '') + '" data-scope="following" type="button">From people I follow</button>' +
                '<button class="ir-act-scope-btn ' + (_activityScope === 'everyone'  ? 'ir-act-scope-on' : '') + '" data-scope="everyone" type="button">Everyone</button>' +
            '</div>';
        if (!entries.length) {
            gridWrap.innerHTML = scopeChips + '<div class="ir-ov-empty">No activity yet.</div>';
            wireActivityScope(gridWrap);
            return;
        }
        // Hydrate posters once via getItemsMeta.
        var ids = {};
        entries.forEach(function (e) { if (e.itemId) ids[e.itemId] = 1; });
        getItemsMeta(Object.keys(ids)).then(function (meta) {
            function poster(itemId) {
                var m = meta[itemId];
                if (m && m.imageTag) {
                    return '<img class="ir-act-poster-img" loading="lazy" src="' +
                        esc('/Items/' + itemId + '/Images/Primary?fillHeight=180&fillWidth=120&quality=90&tag=' + m.imageTag) +
                        '" alt="" />';
                }
                return '<span class="ir-mem-poster-empty">' + esc((m && m.name) ? m.name.charAt(0) : '?') + '</span>';
            }
            function titleOf(id) { var m = meta[id]; return m && m.name ? m.name : '—'; }
            function userAvatar(e) {
                if (e.hasImage) {
                    return '<img class="ir-act-avatar-img" src="' + esc('/Users/' + encodeURIComponent(e.userId) + '/Images/Primary?fillHeight=120&fillWidth=120&quality=90') + '" alt="" />';
                }
                return '<span class="ir-mem-avatar-fallback">' + esc((e.userName || '?').charAt(0).toUpperCase()) + '</span>';
            }
            function starRowHtml(stars) {
                if (stars == null) return '';
                var out = '<span class="ir-mem-stars">';
                var s = Math.round(stars * 2) / 2;
                for (var i = 1; i <= 5; i++) {
                    var v = s - (i - 1);
                    if (v >= 1)        out += '<span class="ir-mem-star ir-mem-star-full">★</span>';
                    else if (v >= 0.5) out += '<span class="ir-mem-star ir-mem-star-half">★</span>';
                    else               out += '<span class="ir-mem-star">★</span>';
                }
                return out + '</span>';
            }
            function fmtDate(t) { try { return new Date(t).toLocaleDateString(undefined, { day:'numeric', month:'short' }); } catch (e) { return ''; } }
            function actionLabel(e) {
                if (e.kind === 'review') return 'reviewed';
                if (e.kind === 'rating') return 'rated';
                return e.rewatch ? 'rewatched' : 'watched';
            }
            var html = scopeChips + '<ul class="ir-act-list">' + entries.map(function (e) {
                return '<li class="ir-act-row" data-iid="' + esc(e.itemId) + '">' +
                    '<button class="ir-act-user" data-uid="' + esc(e.userId) + '" type="button">' +
                        '<span class="ir-mem-avatar ir-act-avatar">' + userAvatar(e) + '</span>' +
                    '</button>' +
                    '<span class="ir-act-poster" data-iid="' + esc(e.itemId) + '">' + poster(e.itemId) + '</span>' +
                    '<span class="ir-act-meta">' +
                        '<span class="ir-act-line">' +
                            '<b>' + esc(e.userName || '') + '</b> ' + actionLabel(e) +
                            ' <span class="ir-act-title">' + esc(titleOf(e.itemId)) + '</span>' +
                            (e.stars != null ? ' ' + starRowHtml(e.stars) : '') +
                            (e.rewatch && e.kind !== 'rating' ? ' <span class="ir-act-rewatch">rewatch</span>' : '') +
                        '</span>' +
                        (e.review ? '<span class="ir-act-review">' + esc(e.review) + '</span>' : '') +
                        '<span class="ir-act-when">' + esc(fmtDate(e.when)) + '</span>' +
                    '</span>' +
                '</li>';
            }).join('') + '</ul>';
            gridWrap.innerHTML = html;
            wireActivityScope(gridWrap);
            // Click avatar → open profile; click poster/row → open item.
            gridWrap.querySelectorAll('.ir-act-user').forEach(function (btn) {
                btn.addEventListener('click', function (ev) {
                    ev.stopPropagation();
                    _profileUserId = btn.getAttribute('data-uid');
                    _profileTab = 'overview';
                    _overlayView = 'member-profile';
                    loadOverlayView();
                });
            });
            gridWrap.querySelectorAll('.ir-act-row[data-iid]').forEach(function (row) {
                row.addEventListener('click', function (ev) {
                    if (ev.target.closest('button,a')) return;
                    try { if (_overlay) _overlay.classList.remove('ir-ov-open'); } catch (e) {}
                    location.hash = '#!/details?id=' + encodeURIComponent(row.getAttribute('data-iid'));
                });
            });
        });
    }

    // Compare view — side-by-side: similarity score, biggest disagreements,
    // films one user loved that the other hasn't seen.
    function renderCompareView(d) {
        var gridWrap = _overlay.querySelector('.ir-ov-grid-wrap');
        if (!d) {
            gridWrap.innerHTML =
                '<div class="ir-mem-prof-head"><button class="ir-mem-back" type="button">← Back</button></div>' +
                '<div class="ir-ov-empty">Could not load comparison.</div>';
            wireProfileBack(gridWrap);
            return;
        }
        var ids = {};
        (d.rows || []).forEach(function (r) { ids[r.itemId] = 1; });
        (d.aOnlyTop || []).forEach(function (id) { ids[id] = 1; });
        (d.bOnlyTop || []).forEach(function (id) { ids[id] = 1; });
        getItemsMeta(Object.keys(ids)).then(function (meta) {
            function poster(itemId, h, w) {
                var m = meta[itemId];
                h = h || 180; w = w || 120;
                if (m && m.imageTag) {
                    return '<img class="ir-mem-poster-img" loading="lazy" src="' +
                        esc('/Items/' + itemId + '/Images/Primary?fillHeight=' + h + '&fillWidth=' + w + '&quality=90&tag=' + m.imageTag) +
                        '" alt="" />';
                }
                return '<span class="ir-mem-poster-empty">' + esc((m && m.name) ? m.name.charAt(0) : '?') + '</span>';
            }
            function titleOf(id) { var m = meta[id]; return m && m.name ? m.name : '—'; }

            var simPct = (d.similarity != null ? Math.round(((d.similarity + 1) / 2) * 100) : 0); // map -1..+1 -> 0..100
            var simLabel = d.overlapCount < 3 ? 'too few overlaps' :
                           d.similarity >= 0.7 ? 'twins'    :
                           d.similarity >= 0.4 ? 'aligned'  :
                           d.similarity >= 0.1 ? 'somewhat' :
                           d.similarity >= -0.2 ? 'mixed'   : 'opposites';
            var simHue = d.similarity >= 0.4 ? '#4cd96b' : d.similarity >= 0.1 ? '#f4c430' : '#cc2020';

            // Avatar urls — Jellyfin user image, falls back to initial chip on error.
            function userAvatar(uid, name, side) {
                var initial = (name || '?').charAt(0).toUpperCase();
                return '<span class="ir-mem-cmp-avatar ir-mem-cmp-avatar-' + side + '">' +
                    '<img loading="lazy" src="/Users/' + encodeURIComponent(uid) + '/Images/Primary?fillHeight=120&fillWidth=120&quality=90" alt="" onerror="this.remove()" />' +
                    '<span class="ir-mem-cmp-avatar-letter">' + esc(initial) + '</span>' +
                '</span>';
            }

            var head = '<div class="ir-mem-prof-head">' +
                '<button class="ir-mem-back" type="button">← Back</button>' +
                '<span class="ir-mem-prof-name">Compare</span>' +
            '</div>';

            // VS header — both avatars + names + totals in a nice big band.
            var vsHead =
                '<div class="ir-mem-cmp-vs">' +
                    '<div class="ir-mem-cmp-side ir-mem-cmp-side-a">' +
                        userAvatar(d.a, d.aName, 'a') +
                        '<div class="ir-mem-cmp-side-name">' + esc(d.aName || '') + '</div>' +
                        '<div class="ir-mem-cmp-side-stat"><b>' + (d.aTotalRated|0) + '</b> rated · avg <b>' + (d.aAvg||0).toFixed(2) + '★</b></div>' +
                    '</div>' +
                    '<div class="ir-mem-cmp-vs-pip">VS</div>' +
                    '<div class="ir-mem-cmp-side ir-mem-cmp-side-b">' +
                        userAvatar(d.b, d.bName, 'b') +
                        '<div class="ir-mem-cmp-side-name">' + esc(d.bName || '') + '</div>' +
                        '<div class="ir-mem-cmp-side-stat"><b>' + (d.bTotalRated|0) + '</b> rated · avg <b>' + (d.bAvg||0).toFixed(2) + '★</b></div>' +
                    '</div>' +
                '</div>';

            var simBlock =
                '<div class="ir-mem-cmp-hero" style="border-color:' + simHue + '40">' +
                    '<div class="ir-mem-cmp-score" style="color:' + simHue + '">' +
                        '<span class="ir-mem-cmp-score-v">' + simPct + '<small>%</small></span>' +
                        '<span class="ir-mem-cmp-score-l">taste similarity</span>' +
                    '</div>' +
                    '<div class="ir-mem-cmp-summary">' +
                        '<div><b>' + (d.overlapCount|0) + '</b> films you both rated</div>' +
                        '<div class="ir-mem-cmp-tag" style="background:' + simHue + '22;color:' + simHue + ';border-color:' + simHue + '55">' + esc(simLabel) + '</div>' +
                    '</div>' +
                '</div>';

            // Quick stats tile-grid
            var statTiles =
                '<div class="ir-mem-cmp-tiles">' +
                    tile((d.agree|0),       'agree (within 0.5★)', '#4cd96b') +
                    tile((d.bothLoved|0),   'both loved (≥4★)',    '#f4c430') +
                    tile((d.bothHated|0),   'both meh (≤2.5★)',    '#cc2020') +
                    tile((d.aHigher|0),     esc(d.aName) + ' rates higher', '#60a5fa') +
                    tile((d.bHigher|0),     esc(d.bName) + ' rates higher', '#c084fc') +
                '</div>';
            function tile(v, l, c) {
                return '<div class="ir-mem-cmp-tile">' +
                    '<span class="ir-mem-cmp-tile-v" style="color:' + c + '">' + v + '</span>' +
                    '<span class="ir-mem-cmp-tile-l">' + l + '</span>' +
                '</div>';
            }

            // Side-by-side mini histograms across all ratings, 10 buckets 0.5..5.
            function histPanel(name, hist, avg, side) {
                var max = 0; (hist || []).forEach(function (n) { if (n > max) max = n; });
                if (!max) max = 1;
                var bars = (hist || []).map(function (n, i) {
                    var pct = Math.round((n / max) * 100);
                    var stars = ((i + 1) / 2).toFixed(1);
                    return '<span class="ir-mem-cmp-hist-bar' + (n ? '' : ' ir-mem-cmp-hist-bar-empty') + '" title="' + stars + '★ · ' + n + '">' +
                        '<span class="ir-mem-cmp-hist-fill ir-mem-cmp-hist-fill-' + side + '" style="height:' + pct + '%"></span>' +
                    '</span>';
                }).join('');
                return '<div class="ir-mem-cmp-hist-panel">' +
                    '<div class="ir-mem-cmp-hist-head">' +
                        '<span class="ir-mem-cmp-hist-name">' + esc(name) + '</span>' +
                        '<span class="ir-mem-cmp-hist-avg">avg <b>' + (avg||0).toFixed(2) + '★</b></span>' +
                    '</div>' +
                    '<div class="ir-mem-cmp-hist">' + bars + '</div>' +
                    '<div class="ir-mem-cmp-hist-axis"><span>½★</span><span>2½★</span><span>5★</span></div>' +
                '</div>';
            }
            var histBlock =
                '<div class="ir-mem-section"><h5 class="ir-mem-statcol-title">Rating distributions</h5>' +
                    '<div class="ir-mem-cmp-hists">' +
                        histPanel(d.aName, d.aHist, d.aAvg, 'a') +
                        histPanel(d.bName, d.bHist, d.bAvg, 'b') +
                    '</div>' +
                '</div>';

            // Films you BOTH loved (NEW) — feels positive, balances the disagreement section.
            var agreedHtml = '';
            if (d.agreedTop && d.agreedTop.length) {
                agreedHtml = '<div class="ir-mem-section"><h5 class="ir-mem-statcol-title">Films you both loved</h5>' +
                    '<div class="ir-mem-strip">' + d.agreedTop.map(function (iid) {
                        return '<div class="ir-mem-poster ir-mem-poster-s" data-iid="' + esc(iid) + '" title="' + esc(titleOf(iid)) + '">' + poster(iid) + '</div>';
                    }).join('') + '</div></div>';
            }

            // Biggest disagreements row
            var rowsHtml = '';
            if (d.rows && d.rows.length) {
                rowsHtml = '<div class="ir-mem-section"><h5 class="ir-mem-statcol-title">Biggest disagreements</h5>' +
                    '<ul class="ir-mem-cmp-rows">' + d.rows.slice(0, 12).map(function (r) {
                        return '<li class="ir-mem-cmp-row" data-iid="' + esc(r.itemId) + '">' +
                            '<span class="ir-mem-poster ir-mem-poster-xs" data-iid="' + esc(r.itemId) + '">' + poster(r.itemId) + '</span>' +
                            '<span class="ir-mem-cmp-row-meta">' +
                                '<span class="ir-mem-cmp-row-title">' + esc(titleOf(r.itemId)) + '</span>' +
                                '<span class="ir-mem-cmp-row-stars">' +
                                    '<span class="ir-mem-cmp-row-side">' + esc(d.aName) + ' <b>' + r.aStars.toFixed(1) + '★</b></span>' +
                                    '<span class="ir-mem-cmp-row-vs">vs</span>' +
                                    '<span class="ir-mem-cmp-row-side">' + esc(d.bName) + ' <b>' + r.bStars.toFixed(1) + '★</b></span>' +
                                '</span>' +
                            '</span>' +
                            '<span class="ir-mem-cmp-row-delta" style="color:' + (r.delta > 0 ? '#60a5fa' : '#c084fc') + '">' + (r.delta > 0 ? '+' : '') + r.delta.toFixed(1) + '★</span>' +
                        '</li>';
                    }).join('') + '</ul></div>';
            }

            // A-only and B-only top picks
            function pickStrip(ids, label) {
                if (!ids || !ids.length) return '';
                return '<div class="ir-mem-section"><h5 class="ir-mem-statcol-title">' + label + '</h5>' +
                    '<div class="ir-mem-strip">' + ids.map(function (iid) {
                        return '<div class="ir-mem-poster ir-mem-poster-s" data-iid="' + esc(iid) + '" title="' + esc(titleOf(iid)) + '">' + poster(iid) + '</div>';
                    }).join('') + '</div></div>';
            }
            var aOnly = pickStrip(d.aOnlyTop, esc(d.aName) + ' loved · ' + esc(d.bName) + ' hasn\'t rated');
            var bOnly = pickStrip(d.bOnlyTop, esc(d.bName) + ' loved · ' + esc(d.aName) + ' hasn\'t rated');

            // Empty / too-few-overlaps state
            var body;
            if (d.overlapCount < 3) {
                body = head + vsHead +
                    '<div class="ir-mem-cmp-empty">' +
                        '<div class="ir-mem-cmp-empty-pip">⇄</div>' +
                        '<div class="ir-mem-cmp-empty-h">Not enough overlap yet</div>' +
                        '<div class="ir-mem-cmp-empty-p">You\'ve both rated <b>' + (d.overlapCount|0) + '</b> film' + (d.overlapCount === 1 ? '' : 's') + '. Rate at least 3 of the same titles to unlock similarity, distributions, and disagreement breakdowns.</div>' +
                    '</div>' +
                    aOnly + bOnly;
            } else {
                body = head + vsHead + simBlock + statTiles + histBlock + agreedHtml + rowsHtml + aOnly + bOnly;
            }
            gridWrap.innerHTML = '<div class="ir-mem-profile">' + body + '</div>';
            wireProfileBack(gridWrap);
            wireProfilePosters(gridWrap);
            // Click compare row → film details
            gridWrap.querySelectorAll('.ir-mem-cmp-row[data-iid]').forEach(function (row) {
                row.addEventListener('click', function () {
                    try { if (_overlay) _overlay.classList.remove('ir-ov-open'); } catch (e) {}
                    location.hash = '#!/details?id=' + encodeURIComponent(row.getAttribute('data-iid'));
                });
            });
        });
    }

    function wireActivityScope(scope) {
        scope.querySelectorAll('.ir-act-scope-btn').forEach(function (b) {
            b.addEventListener('click', function () {
                var s = b.getAttribute('data-scope') || 'following';
                if (s === _activityScope) return;
                _activityScope = s;
                loadOverlayView();
            });
        });
    }

    function wireMembersSearch(scope) {
        var input = scope.querySelector('.ir-mem-search');
        var clear = scope.querySelector('.ir-mem-search-clear');
        if (input) {
            // Re-render on every keystroke against the cached members list
            // so search feels instant. We don't refetch from the server.
            input.addEventListener('input', function () {
                _membersSearchQ = input.value || '';
                renderMembersGrid(_membersCache);
                // Restore caret position after re-render.
                var newInput = scope.querySelector('.ir-mem-search');
                if (newInput) { newInput.focus(); newInput.setSelectionRange(_membersSearchQ.length, _membersSearchQ.length); }
            });
        }
        if (clear) {
            clear.addEventListener('click', function () {
                _membersSearchQ = '';
                renderMembersGrid(_membersCache);
            });
        }
    }

    function renderMemberProfile(p) {
        var gridWrap = _overlay.querySelector('.ir-ov-grid-wrap');
        if (!p) {
            gridWrap.innerHTML =
                '<div class="ir-ov-empty">Could not load this profile.</div>' +
                '<div style="margin-top:14px"><button class="ir-mem-back" type="button">← Back to Members</button></div>';
            wireProfileBack(gridWrap);
            return;
        }
        // Collect every itemId we need posters for. Bucketed Top 4s + the
        // mixed Top 4 + recent ratings + diary + watchlist + likes.
        var ids = {};
        function collectIds(arr) { (arr || []).forEach(function (it) { ids[it.itemId || it] = 1; }); }
        collectIds(p.top4); collectIds(p.top4Movies); collectIds(p.top4Series); collectIds(p.top4Anime);
        collectIds(p.recentRatings); collectIds(p.diary); collectIds(p.watchlist); collectIds(p.likes);
        var idList = Object.keys(ids);

        getItemsMeta(idList).then(function (meta) {
            _profileCache = p;
            _profileMetaCache = meta;
            renderProfileScreen();
            // Lazy-load heavy stats. Hits a separate endpoint with server-side
            // caching so repeat visits return instantly. The stats card shows
            // a loading shimmer until this resolves.
            if (_profileStatsForId !== p.id) {
                _profileStatsForId = p.id;
                _profileStatsCache = null;
                apiMemberStats(p.id).then(function (extra) {
                    _profileStatsCache = extra;
                    if (_profileUserId === p.id) {
                        renderStatsBundle(extra, p.stats);
                    }
                });
            } else if (_profileStatsCache) {
                // Already loaded for this profile in this session — paint immediately.
                renderStatsBundle(_profileStatsCache, p.stats);
            }
        });
    }

    // Render the profile DOM based on the current sub-state. Re-callable
    // from any in-profile control without re-fetching the DTO.
    function renderProfileScreen() {
        if (!_profileCache || !_profileMetaCache) return;
        var gridWrap = _overlay.querySelector('.ir-ov-grid-wrap');
        var p = _profileCache, meta = _profileMetaCache;

        if (_profileTab === 'all-watchlist' || _profileTab === 'all-liked' || _profileTab === 'all-diary') {
            gridWrap.innerHTML = buildProfileListPage(p, meta, _profileTab);
        } else if (_profileTab === 'ratings-at-star') {
            gridWrap.innerHTML = buildProfileRatingsAtStarPage(p, meta, _profileStarFilter);
        } else if (_profileTab === 'all-ratings') {
            gridWrap.innerHTML = buildProfileAllRatingsPage(p, meta);
        } else if (_profileTab === 'year-detail') {
            gridWrap.innerHTML = buildProfileYearDetailPage(p, meta, _profileYearFilter);
        } else if (_profileTab === 'followers' || _profileTab === 'following') {
            gridWrap.innerHTML = buildProfileFollowPage(p, _profileTab);
            apiFollowList(p.id, _profileTab).then(function (rows) {
                var holder = gridWrap.querySelector('.ir-mem-followlist');
                if (!holder) return;
                holder.innerHTML = buildFollowCardsHtml(rows || []);
                holder.querySelectorAll('.ir-mem-card-body').forEach(function (btn) {
                    btn.addEventListener('click', function () {
                        _profileUserId = btn.getAttribute('data-uid');
                        _profileTab = 'overview';
                        _profileTopCat = 'all';
                        loadOverlayView();
                    });
                });
                holder.querySelectorAll('.ir-mem-follow-btn').forEach(function (b2) {
                    b2.addEventListener('click', function (ev) {
                        ev.stopPropagation();
                        var uid = b2.getAttribute('data-uid');
                        var on = b2.classList.contains('ir-mem-follow-on');
                        b2.disabled = true;
                        (on ? apiUnfollow(uid) : apiFollow(uid)).finally(function () { renderProfileScreen(); });
                    });
                });
            });
        } else {
            gridWrap.innerHTML = buildMemberProfileHtml(p, meta);
            // Re-paint already-loaded stats if we have them so back-from-
            // sub-page doesn't show "Loading stats…" again.
            if (_profileStatsCache && _profileStatsForId === p.id) {
                renderStatsBundle(_profileStatsCache, p.stats);
            }
        }
        wireProfileBack(gridWrap);
        wireProfilePosters(gridWrap);
        wireProfileNav(gridWrap);
        // Scroll the overlay's scrollable region back to the top whenever a
        // new sub-page (or initial profile view) is rendered. Without this,
        // navigating from the bottom of the profile into "View all liked"
        // dropped you halfway down the new page.
        try {
            var scroller = _overlay && (_overlay.querySelector('.ir-ov-body') || _overlay.querySelector('.ir-ov-grid-wrap') || _overlay);
            if (scroller && typeof scroller.scrollTo === 'function') scroller.scrollTo({ top: 0, behavior: 'auto' });
            else if (scroller) scroller.scrollTop = 0;
        } catch (e) {}
    }

    function wireProfileBack(scope) {
        scope.querySelectorAll('.ir-mem-back').forEach(function (btn) {
            btn.addEventListener('click', function () {
                if (_profileTab !== 'overview') {
                    _profileTab = 'overview';
                    _profileSubFilter = 'all';
                    _profileStarFilter = null;
                    _profileYearFilter = null;
                    renderProfileScreen();
                    return;
                }
                _profileUserId = null;
                _profileUserName = '';
                _profileCache = null;
                _profileMetaCache = null;
                _profileStatsCache = null;
                _profileStatsForId = null;
                _profileSubFilter = 'all';
                _profileStarFilter = null;
                _profileMediaTab = 'movie';
                _overlayView = 'members';
                loadOverlayView();
            });
        });
    }

    function wireProfilePosters(scope) {
        function navigate(iid) {
            if (!iid) return;
            // Close StarTrack overlay so item details renders cleanly.
            try { if (_overlay) _overlay.classList.remove('ir-ov-open'); } catch (e) {}
            // Also collapse the Jellyfin nav drawer if it's open. The web
            // client doesn't expose a direct "close drawer" API, so trigger
            // it via a click on the open backdrop or by stripping the open
            // class from the drawer host.
            try {
                var d = document.querySelector('.mainDrawer.is-open, #mainDrawer.is-open, .mainDrawer-open');
                if (d) d.classList.remove('is-open', 'mainDrawer-open');
                var bd = document.querySelector('.dialogBackdropOpened, .mainDrawer-backdrop.opened, .mainDrawerHandle');
                if (bd && bd.click) bd.click();
            } catch (e) {}
            location.hash = '#!/details?id=' + encodeURIComponent(iid);
        }
        scope.querySelectorAll('.ir-mem-poster[data-iid]').forEach(function (el) {
            el.addEventListener('click', function () { navigate(el.getAttribute('data-iid')); });
        });
        // Diary rows + All-Ratings rows: clickable across the whole row
        scope.querySelectorAll('.ir-mem-diary-row[data-iid], .ir-mem-allratings-row[data-iid]').forEach(function (row) {
            row.addEventListener('click', function (ev) {
                if (ev.target.closest('button,a')) return;
                navigate(row.getAttribute('data-iid'));
            });
        });
    }

    function wireProfileNav(scope) {
        // Top-4 category sub-tabs
        scope.querySelectorAll('.ir-mem-topcat').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var cat = btn.getAttribute('data-cat') || 'all';
                if (cat === _profileTopCat) return;
                _profileTopCat = cat;
                renderProfileScreen();
            });
        });
        // View-all buttons jump into a dedicated sub-page
        scope.querySelectorAll('.ir-mem-viewall').forEach(function (btn) {
            btn.addEventListener('click', function () {
                _profileTab = btn.getAttribute('data-tab') || 'overview';
                renderProfileScreen();
            });
        });
        // Follow button on the profile head
        var followBtn = scope.querySelector('.ir-mem-prof-follow');
        if (followBtn) {
            followBtn.addEventListener('click', function () {
                if (!_profileCache) return;
                var on = followBtn.classList.contains('ir-mem-follow-on');
                followBtn.disabled = true;
                (on ? apiUnfollow(_profileCache.id) : apiFollow(_profileCache.id)).finally(function () {
                    apiMemberProfile(_profileCache.id).then(function (p) {
                        if (p) {
                            _profileCache = p;
                            renderProfileScreen();
                        }
                    });
                });
            });
        }
        // Follower/Following count → list view
        scope.querySelectorAll('.ir-mem-follow-link').forEach(function (a) {
            a.addEventListener('click', function () {
                _profileTab = a.getAttribute('data-which') || 'followers';
                renderProfileScreen();
            });
        });
        // Type-filter chips on view-all sub-pages
        scope.querySelectorAll('.ir-mem-typefilter-btn').forEach(function (b) {
            b.addEventListener('click', function () {
                var f = b.getAttribute('data-f') || 'all';
                if (f === _profileSubFilter) return;
                _profileSubFilter = f;
                renderProfileScreen();
            });
        });
        // Click a histogram bar to see the films rated at that star value.
        scope.querySelectorAll('.ir-mem-bar[data-stars]').forEach(function (b) {
            b.addEventListener('click', function () {
                _profileStarFilter = b.getAttribute('data-stars');
                _profileTab = 'ratings-at-star';
                renderProfileScreen();
            });
        });
        // Media-breakdown tabs (Films / TV / Anime)
        scope.querySelectorAll('.ir-mem-mediatab').forEach(function (b) {
            b.addEventListener('click', function () {
                var t = b.getAttribute('data-mt') || 'movie';
                if (t === _profileMediaTab) return;
                _profileMediaTab = t;
                renderStatsBundle(_profileStatsCache, _profileCache && _profileCache.stats);
            });
        });
        // Sort buttons on the All-Ratings sub-page
        scope.querySelectorAll('.ir-mem-sortbtn').forEach(function (b) {
            b.addEventListener('click', function () {
                var s = b.getAttribute('data-s') || 'date-desc';
                if (s === _profileSortBy) return;
                _profileSortBy = s;
                renderProfileScreen();
            });
        });
        // Year-card click → opens the dedicated year-detail sub-page with
        // every film rated that year (works for any profile, not just self).
        scope.querySelectorAll('.ir-mem-yearcard').forEach(function (card) {
            card.addEventListener('click', function (ev) {
                if (ev.target.closest('.ir-mem-yearcard-top')) return;
                var y = parseInt(card.getAttribute('data-year'), 10) || 0;
                if (!y) return;
                _profileYearFilter = y;
                _profileTab = 'year-detail';
                renderProfileScreen();
            });
        });
        // Activity timeline scope chips (Lifetime / This year / Last year / This month)
        scope.querySelectorAll('.ir-mem-tl-filter-btn').forEach(function (b) {
            b.addEventListener('click', function () {
                var s = b.getAttribute('data-tlscope') || 'all';
                if (s === _profileTimelineScope) return;
                _profileTimelineScope = s;
                renderStatsBundle(_profileStatsCache, _profileCache && _profileCache.stats);
            });
        });
    }

    function buildMemberProfileHtml(p, meta) {
        function poster(itemId, size) {
            var m = meta[itemId];
            if (m && m.imageTag) {
                // Bumped resolutions so thumbnails aren't blurry on retina.
                var s = size || 'm';
                var dims = s === 'l' ? 'fillHeight=540&fillWidth=360' :
                           s === 'm' ? 'fillHeight=360&fillWidth=240' :
                           s === 's' ? 'fillHeight=240&fillWidth=160' :
                                       'fillHeight=180&fillWidth=120'; // xs
                return '<img class="ir-mem-poster-img" loading="lazy" src="' +
                    esc('/Items/' + itemId + '/Images/Primary?' + dims + '&quality=90&tag=' + m.imageTag) +
                    '" alt="" />';
            }
            return '<span class="ir-mem-poster-empty">' + esc((m && m.name) ? m.name.charAt(0) : '?') + '</span>';
        }
        function titleOf(id) { var m = meta[id]; return m && m.name ? m.name : '—'; }

        var avatar = p.hasImage
            ? '<img class="ir-mem-prof-img" src="' + esc('/Users/' + encodeURIComponent(p.id) + '/Images/Primary?fillHeight=240&fillWidth=240&quality=90') + '" alt="" />'
            : '<span class="ir-mem-prof-fallback">' + esc((p.name || '?').charAt(0).toUpperCase()) + '</span>';

        // Follow button on the head — hidden when viewing your own profile.
        var followBtn = '';
        if (!p.isSelf) {
            var on = p.isFollowing;
            followBtn =
                '<button class="ir-mem-prof-follow ' + (on ? 'ir-mem-follow-on' : '') + '" type="button">' +
                    (on ? 'Following' : 'Follow') +
                '</button>';
        }

        // Pick which Top 4 list to show based on the active category tab.
        var top4 = (_profileTopCat === 'movies') ? (p.top4Movies || []) :
                   (_profileTopCat === 'series') ? (p.top4Series || []) :
                   (_profileTopCat === 'anime')  ? (p.top4Anime  || []) :
                                                   (p.top4 || []);
        var cats = [
            ['all',    'All',    (p.top4 || []).length],
            ['movies', 'Movies', (p.top4Movies || []).length],
            ['series', 'Series', (p.top4Series || []).length],
            ['anime',  'Anime',  (p.top4Anime  || []).length]
        ];
        var topCatTabs = cats.map(function (c) {
            return '<button class="ir-mem-topcat ' + (_profileTopCat === c[0] ? 'ir-mem-topcat-on' : '') + '" data-cat="' + c[0] + '" type="button">' +
                esc(c[1]) + ' <span class="ir-mem-topcat-n">' + c[2] + '</span></button>';
        }).join('');
        var top4Html = '';
        for (var i = 0; i < 4; i++) {
            var fid = top4[i];
            top4Html += fid
                ? '<div class="ir-mem-poster ir-mem-poster-m" data-iid="' + esc(fid) + '" title="' + esc(titleOf(fid)) + '">' + poster(fid, 'm') + '</div>'
                : '<div class="ir-mem-poster ir-mem-poster-m ir-mem-poster-blank"><span>+</span></div>';
        }

        // Histogram — back to the simple solid-bar style. No track, no
        // count above. Hover shows a popover with the count. Clickable
        // bars drill into films-rated-at-X.X. Pure neutral colors so the
        // yellow that's been getting flagged is gone.
        var maxBucket = 0;
        var keys = ['0.5','1.0','1.5','2.0','2.5','3.0','3.5','4.0','4.5','5.0'];
        keys.forEach(function (k) { var v = (p.histogram && p.histogram[k]) || 0; if (v > maxBucket) maxBucket = v; });
        var histHtml = '';
        keys.forEach(function (k) {
            var v = (p.histogram && p.histogram[k]) || 0;
            var pct = maxBucket > 0 ? Math.round((v / maxBucket) * 100) : 0;
            var clickable = v > 0;
            histHtml += '<button class="ir-mem-bar' + (v === 0 ? ' ir-mem-bar-empty' : '') + '" type="button" ' +
                (clickable ? 'data-stars="' + esc(k) + '"' : 'disabled') +
                ' title="' + esc(v + ' film' + (v === 1 ? '' : 's') + ' rated ' + k + '★') + '">' +
                '<span class="ir-mem-bar-fill" style="height:' + pct + '%"></span>' +
                '<span class="ir-mem-bar-label">' + esc(k) + '</span>' +
                '<span class="ir-mem-bar-pop"><b>' + v + '</b> rated ' + esc(k) + '★</span>' +
                '</button>';
        });

        var stats = p.stats || {};
        // Header chips: ratings / diary / watchlist / likes / avg + clickable
        // followers + following links.
        var followers = stats.hideFollowerCount && !p.isSelf
            ? '<span class="ir-mem-stat-big ir-mem-stat-private" title="This user keeps their followers private"><b>•</b> followers</span>'
            : '<a class="ir-mem-stat-big ir-mem-follow-link" data-which="followers" href="javascript:void(0)"><b>' + (stats.followersCount|0) + '</b> followers</a>';
        var following = stats.hideFollowing && !p.isSelf
            ? '<span class="ir-mem-stat-big ir-mem-stat-private" title="This user keeps their following list private"><b>•</b> following</span>'
            : '<a class="ir-mem-stat-big ir-mem-follow-link" data-which="following" href="javascript:void(0)"><b>' + (stats.followingCount|0) + '</b> following</a>';
        // Spruced overview tile row — bigger numbers, soft separators,
        // each cell is its own block so the row never collapses to text.
        function tile(value, label, extra) {
            return '<div class="ir-mem-overview-tile' + (extra || '') + '">' +
                '<span class="ir-mem-overview-v">' + esc(String(value)) + '</span>' +
                '<span class="ir-mem-overview-l">' + esc(label) + '</span>' +
            '</div>';
        }
        function tileLink(value, label, which) {
            return '<a class="ir-mem-overview-tile ir-mem-overview-clickable ir-mem-follow-link" data-which="' + which + '" href="javascript:void(0)">' +
                '<span class="ir-mem-overview-v">' + esc(String(value)) + '</span>' +
                '<span class="ir-mem-overview-l">' + esc(label) + '</span>' +
            '</a>';
        }
        function tilePrivate(label) {
            return '<div class="ir-mem-overview-tile ir-mem-overview-private" title="Private">' +
                '<span class="ir-mem-overview-v">•</span>' +
                '<span class="ir-mem-overview-l">' + esc(label) + '</span>' +
            '</div>';
        }
        var statsHtml =
            '<div class="ir-mem-overview">' +
                tile(stats.ratingsCount|0, 'ratings') +
                tile(stats.diaryCount|0, 'diary') +
                tile(stats.watchlistCount|0, 'watchlist') +
                tile(stats.likesCount|0, 'likes') +
                tile(stats.ratingsCount > 0 ? (stats.averageRating || 0).toFixed(2) + '★' : '—', 'avg') +
                (stats.hideFollowerCount && !p.isSelf ? tilePrivate('followers') : tileLink(stats.followersCount|0, 'followers', 'followers')) +
                (stats.hideFollowing && !p.isSelf ? tilePrivate('following') : tileLink(stats.followingCount|0, 'following', 'following')) +
            '</div>';

        function strip(items, label, max, viewAllTab) {
            var list = (items || []).slice(0, max || 12);
            var hasMore = (items || []).length > list.length;
            if (list.length === 0) return '';
            var inner = list.map(function (it) {
                var iid = it.itemId || it;
                return '<div class="ir-mem-poster ir-mem-poster-s" data-iid="' + esc(iid) + '">' + poster(iid, 's') + '</div>';
            }).join('');
            var viewBtn = (viewAllTab && hasMore)
                ? '<button class="ir-mem-viewall" data-tab="' + esc(viewAllTab) + '" type="button">View all →</button>'
                : '';
            return '<div class="ir-mem-section">' +
                '<div class="ir-mem-section-head"><h4 class="ir-mem-section-title">' + esc(label) + '</h4>' + viewBtn + '</div>' +
                '<div class="ir-mem-strip">' + inner + '</div>' +
                '</div>';
        }

        // Recent diary — capped at 5 entries with a View-all → all-diary tab.
        var diaryHtml = '';
        if (p.diary && p.diary.length) {
            diaryHtml = '<div class="ir-mem-section">' +
                '<div class="ir-mem-section-head"><h4 class="ir-mem-section-title">Recent diary</h4>' +
                ((p.diary.length > 5)
                    ? '<button class="ir-mem-viewall" data-tab="all-diary" type="button">View all →</button>'
                    : '') +
                '</div><ul class="ir-mem-diary">';
            p.diary.slice(0, 5).forEach(function (d) {
                var when = '';
                try { when = new Date(d.watchedAt).toLocaleDateString(); } catch (e) {}
                var stars = (d.stars != null) ? Math.round(d.stars * 2) / 2 : null;
                var starsHtml = '';
                if (stars != null) {
                    starsHtml = '<span class="ir-mem-stars ir-mem-diary-stars-icons">';
                    for (var si = 1; si <= 5; si++) {
                        var v = stars - (si - 1);
                        if (v >= 1)        starsHtml += '<span class="ir-mem-star ir-mem-star-full">★</span>';
                        else if (v >= 0.5) starsHtml += '<span class="ir-mem-star ir-mem-star-half">★</span>';
                        else               starsHtml += '<span class="ir-mem-star">★</span>';
                    }
                    starsHtml += '</span>';
                } else {
                    starsHtml = '<span class="ir-mem-diary-nostar">—</span>';
                }
                diaryHtml +=
                    '<li class="ir-mem-diary-row" data-iid="' + esc(d.itemId) + '">' +
                        '<span class="ir-mem-poster ir-mem-poster-xs" data-iid="' + esc(d.itemId) + '">' + poster(d.itemId, 'xs') + '</span>' +
                        '<span class="ir-mem-diary-meta">' +
                            '<span class="ir-mem-diary-title">' + esc(titleOf(d.itemId)) + '</span>' +
                            '<span class="ir-mem-diary-sub">' + esc(when) + '</span>' +
                        '</span>' +
                        '<span class="ir-mem-diary-actions">' +
                            starsHtml +
                            (d.rewatch ? '<span class="ir-mem-diary-rewatch-pill">Rewatch</span>' : '') +
                        '</span>' +
                    '</li>';
            });
            diaryHtml += '</ul></div>';
        }

        // Bottom CTA row — three "View all" buttons for watchlist/liked/diary
        var ctaHtml =
            '<div class="ir-mem-cta-row">' +
                ((p.watchlist && p.watchlist.length)
                    ? '<button class="ir-mem-cta ir-mem-viewall" data-tab="all-watchlist" type="button"><b>' + p.watchlist.length + '</b> Watchlist</button>'
                    : '') +
                ((p.likes && p.likes.length)
                    ? '<button class="ir-mem-cta ir-mem-viewall" data-tab="all-liked" type="button"><b>' + p.likes.length + '</b> Liked</button>'
                    : '') +
                ((p.diary && p.diary.length)
                    ? '<button class="ir-mem-cta ir-mem-viewall" data-tab="all-diary" type="button"><b>' + p.diary.length + '</b> Diary</button>'
                    : '') +
            '</div>';

        // Letterboxd-style stats panel
        var letterboxdStatsHtml = buildLetterboxdStatsHtml(stats);

        // Recent ratings strip with "View all" linking to the full sortable
        // sub-page so the user can drill through every rating.
        var allRatingsCount = (p.recentRatings || []).length;
        var recentStripHtml = strip(p.recentRatings, 'Recent ratings', 12, allRatingsCount > 12 ? 'all-ratings' : null);

        return '<div class="ir-mem-profile">' +
            '<div class="ir-mem-prof-head">' +
                '<button class="ir-mem-back" type="button">← Back</button>' +
                '<span class="ir-mem-prof-avatar">' + avatar + '</span>' +
                '<span class="ir-mem-prof-name">' + esc(p.name || '') + (p.isSelf ? ' <span class="ir-mem-you">(you)</span>' : '') + '</span>' +
                followBtn +
            '</div>' +
            statsHtml +
            // Top 4 hero: full-width banner, bigger posters, eyebrow text
            '<div class="ir-mem-section ir-mem-top4-section">' +
                '<div class="ir-mem-section-head">' +
                    '<h4 class="ir-mem-section-title">Favorite films</h4>' +
                    '<div class="ir-mem-topcats">' + topCatTabs + '</div>' +
                '</div>' +
                '<div class="ir-mem-top4 ir-mem-top4-hero">' + top4Html + '</div>' +
            '</div>' +
            (stats.ratingsCount > 0
                ? '<div class="ir-mem-section">' +
                    '<div class="ir-mem-section-head">' +
                        '<h4 class="ir-mem-section-title">Ratings</h4>' +
                        (allRatingsCount > 12 ? '<button class="ir-mem-viewall" data-tab="all-ratings" type="button">All ratings →</button>' : '') +
                    '</div>' +
                    '<div class="ir-mem-hist">' + histHtml + '</div></div>'
                : '') +
            recentStripHtml +
            diaryHtml +
            ctaHtml +
            letterboxdStatsHtml +
        '</div>';
    }

    // Letterboxd-style stats — placeholder shell that fills in when the
    // lazy /Stats fetch resolves. The profile renders instantly without
    // waiting for the heavy library walk.
    function buildLetterboxdStatsHtml(stats) {
        return '<div class="ir-mem-section ir-mem-stats-section">' +
            '<h4 class="ir-mem-section-title">Stats</h4>' +
            '<div class="ir-mem-stats-host">' +
                '<div class="ir-mem-stats-loading">Loading stats…</div>' +
            '</div>' +
        '</div>';
    }

    // Renders Letterboxd-style stats panel into .ir-mem-stats-host. Two
    // tiers: top "headline" stats (films rated, hours, days journaled,
    // started rating, top decade), middle activity timeline (SVG month
    // bars with year markers), bottom media-breakdown tabs (Films / TV /
    // Anime — each with count, avg rating, hours watched, top genres).
    function renderStatsBundle(extra, baseStats) {
        if (!_overlay) return;
        var host = _overlay.querySelector('.ir-mem-stats-host');
        if (!host) return;
        if (!extra) {
            host.innerHTML = '<div class="ir-mem-stats-empty">No stats yet — rate some films to see your taste profile.</div>';
            return;
        }

        function fmtDate(iso) {
            if (!iso) return '';
            try {
                return new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
            } catch (e) { return ''; }
        }
        function bigStat(value, label, sub) {
            return '<div class="ir-mem-bigstat">' +
                '<span class="ir-mem-bigstat-v">' + esc(String(value)) + '</span>' +
                '<span class="ir-mem-bigstat-l">' + esc(label) + '</span>' +
                (sub ? '<span class="ir-mem-bigstat-sub">' + esc(sub) + '</span>' : '') +
                '</div>';
        }
        function rankList(items) {
            if (!items || !items.length) return '<div class="ir-mem-rank-empty">—</div>';
            var max = items[0].count || 1;
            return '<ul class="ir-mem-rank">' + items.map(function (it) {
                var pct = Math.round((it.count / max) * 100);
                return '<li class="ir-mem-rank-row">' +
                    '<span class="ir-mem-rank-name">' + esc(it.name) + '</span>' +
                    '<span class="ir-mem-rank-bar"><span class="ir-mem-rank-fill" style="width:' + pct + '%"></span></span>' +
                    '<span class="ir-mem-rank-n">' + (it.count|0) + '</span>' +
                    '</li>';
            }).join('') + '</ul>';
        }
        // Activity timeline: ratings per month, rendered as HTML divs in a
        // flex row. Time-range filter (Lifetime / This year / Last year /
        // This month). Hover shows a real popover with month + count.
        var monthFmt = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
        function timelineHtml(months) {
            if (!months || !months.length) return '';
            function ymToIdx(s) { var p = s.split('-'); return parseInt(p[0],10)*12 + parseInt(p[1],10) - 1; }
            function idxToYm(i) { var y = Math.floor(i / 12); var m = (i % 12) + 1; return y + '-' + (m < 10 ? '0'+m : ''+m); }

            // Time-range filter scope.
            var scope = _profileTimelineScope || 'all';
            var now = new Date();
            var thisYear = now.getFullYear();
            var thisMonthIdx = thisYear * 12 + now.getMonth();

            // "This month" gets a special daily breakdown — one bar per day
            // for the current calendar month, derived from the per-rating
            // dates we already have in the profile bundle.
            if (scope === 'this-month') {
                var daysInMonth = new Date(thisYear, now.getMonth() + 1, 0).getDate();
                var byDay = {};
                ((_profileCache && _profileCache.recentRatings) || []).forEach(function (r) {
                    try {
                        var d = new Date(r.ratedAt);
                        if (d.getFullYear() === thisYear && d.getMonth() === now.getMonth()) {
                            var k = d.getDate();
                            byDay[k] = (byDay[k] || 0) + 1;
                        }
                    } catch (e) {}
                });
                var maxD = 1;
                for (var dk in byDay) { if (byDay[dk] > maxD) maxD = byDay[dk]; }
                var dayLabel = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'][now.getMonth()];
                var dayBars = '';
                for (var di = 1; di <= daysInMonth; di++) {
                    var v = byDay[di] || 0;
                    var pct = Math.round((v / maxD) * 100);
                    if (pct < 6 && v > 0) pct = 6;
                    dayBars += '<div class="ir-mem-tl-bar" title="' + esc(dayLabel + ' ' + di + ' · ' + v + ' rating' + (v === 1 ? '' : 's')) + '">' +
                        '<div class="ir-mem-tl-bar-fill" style="height:' + pct + '%"></div>' +
                        '<div class="ir-mem-tl-pop"><b>' + v + '</b> rated · ' + dayLabel + ' ' + di + '</div>' +
                    '</div>';
                }
                // Day-of-month tick row — every 5th day labeled.
                var dayTicks = '';
                for (var di2 = 1; di2 <= daysInMonth; di2++) {
                    dayTicks += '<div class="ir-mem-tl-tick">' + (di2 === 1 || di2 % 5 === 0 || di2 === daysInMonth ? di2 : '') + '</div>';
                }
                return '<div class="ir-mem-tl">' +
                    timelineFilterChips(scope) +
                    '<div class="ir-mem-tl-bars">' + dayBars + '</div>' +
                    '<div class="ir-mem-tl-ticks">' + dayTicks + '</div>' +
                '</div>';
            }

            var filteredSrc = months;
            if (scope === 'this-year')   filteredSrc = months.filter(function (m) { return parseInt(m.name.split('-')[0],10) === thisYear; });
            if (scope === 'last-year')   filteredSrc = months.filter(function (m) { return parseInt(m.name.split('-')[0],10) === (thisYear - 1); });

            if (!filteredSrc.length) {
                return '<div class="ir-mem-tl">' + timelineFilterChips(scope) +
                    '<div class="ir-mem-tl-empty">No ratings in this range.</div></div>';
            }

            var startIdx = ymToIdx(filteredSrc[0].name);
            var endIdx = ymToIdx(filteredSrc[filteredSrc.length - 1].name);
            // Pad to whole years so year labels line up nicely.
            var byKey = {}; filteredSrc.forEach(function (m) { byKey[m.name] = m.count; });
            var filled = [];
            for (var i = startIdx; i <= endIdx; i++) {
                filled.push({ name: idxToYm(i), count: byKey[idxToYm(i)] || 0 });
            }
            var max = 1; filled.forEach(function (f) { if (f.count > max) max = f.count; });

            var bars = filled.map(function (f) {
                var pct = Math.round((f.count / max) * 100);
                if (pct < 6 && f.count > 0) pct = 6;
                var parts = f.name.split('-');
                var mLabel = monthFmt[parseInt(parts[1],10) - 1] + ' ' + parts[0];
                return '<div class="ir-mem-tl-bar"' +
                    ' data-count="' + f.count + '" data-month="' + esc(mLabel) + '"' +
                    ' title="' + esc(mLabel + ' · ' + f.count + ' rating' + (f.count === 1 ? '' : 's')) + '">' +
                    '<div class="ir-mem-tl-bar-fill" style="height:' + pct + '%"></div>' +
                    '<div class="ir-mem-tl-pop"><b>' + f.count + '</b> rated · ' + esc(mLabel) + '</div>' +
                '</div>';
            }).join('');
            var ticks = filled.map(function (f) {
                var parts = f.name.split('-');
                return parts[1] === '01'
                    ? '<div class="ir-mem-tl-tick">' + parts[0] + '</div>'
                    : '<div class="ir-mem-tl-tick"></div>';
            }).join('');
            return '<div class="ir-mem-tl">' +
                timelineFilterChips(scope) +
                '<div class="ir-mem-tl-bars">' + bars + '</div>' +
                '<div class="ir-mem-tl-ticks">' + ticks + '</div></div>';
        }

        function timelineFilterChips(scope) {
            var opts = [
                ['all',        'Lifetime'],
                ['this-year',  'This year'],
                ['last-year',  'Last year'],
                ['this-month', 'This month']
            ];
            return '<div class="ir-mem-tl-filter">' + opts.map(function (e) {
                return '<button class="ir-mem-tl-filter-btn ' + (scope === e[0] ? 'ir-mem-tl-filter-on' : '') + '" data-tlscope="' + e[0] + '" type="button">' + e[1] + '</button>';
            }).join('') + '</div>';
        }

        var firstStr = extra.firstRatingDate ? fmtDate(extra.firstRatingDate) : '—';
        var lastStr  = extra.lastRatingDate  ? fmtDate(extra.lastRatingDate)  : '';

        var bigs = '<div class="ir-mem-bigstats">' +
            ((baseStats && baseStats.ratingsCount > 0) ? bigStat(baseStats.ratingsCount, 'films rated') : '') +
            (extra.ratingsThisYear > 0 ? bigStat(extra.ratingsThisYear, 'this year') : '') +
            (extra.daysJournaled > 0 ? bigStat(extra.daysJournaled, 'days journaled') : '') +
            (extra.hoursWatched > 0 ? bigStat(extra.hoursWatched.toFixed(1) + 'h', 'watched') : '') +
            (firstStr !== '—' ? bigStat(firstStr, 'started rating', lastStr ? 'last: ' + lastStr : '') : '') +
            (extra.longestFilmTitle ? bigStat(extra.longestFilmHours.toFixed(1) + 'h', 'longest film', extra.longestFilmTitle) : '') +
            (extra.highestRatedDecade ? bigStat(extra.highestRatedDecade, 'top decade', extra.highestRatedDecadeAvg.toFixed(1) + '★ avg') : '') +
            '</div>';

        var timelineBlock = (extra.monthTimeline && extra.monthTimeline.length)
            ? '<div class="ir-mem-timeline-wrap"><h5 class="ir-mem-statcol-title">Activity over time</h5>' + timelineHtml(extra.monthTimeline) + '</div>'
            : '';

        // Person-card grid for top directors / top actors with face avatars
        // sourced from Jellyfin's Persons endpoint. Falls back to initial
        // chip when there's no portrait.
        function personCard(p) {
            var initial = esc((p.name || '?').charAt(0).toUpperCase());
            // <img> + onerror swap: when Jellyfin doesn't have a Primary
            // image for this person we hide the img and reveal the
            // fallback initial behind it. Both are stacked in the same
            // circle so layout stays consistent.
            var imgUrl = p.personId
                ? '/Items/' + encodeURIComponent(p.personId) + '/Images/Primary?fillHeight=160&fillWidth=160&quality=90'
                : '';
            var avatar = '<span class="ir-mem-pp-circle">' +
                '<span class="ir-mem-pp-fb">' + initial + '</span>' +
                (imgUrl
                    ? '<img class="ir-mem-pp-img" src="' + esc(imgUrl) + '" loading="lazy" alt="" ' +
                      'onerror="this.classList.add(\'ir-mem-pp-img-failed\')" />'
                    : '') +
            '</span>';
            return '<div class="ir-mem-pp" title="' + esc(p.name + ' — ' + p.count + ' film' + (p.count === 1 ? '' : 's')) + '">' +
                avatar +
                '<span class="ir-mem-pp-name">' + esc(p.name) + '</span>' +
                '<span class="ir-mem-pp-count">' + (p.count|0) + ' film' + (p.count === 1 ? '' : 's') + '</span>' +
            '</div>';
        }

        var directorsHtml = (extra.topDirectors && extra.topDirectors.length)
            ? '<div class="ir-mem-section"><h5 class="ir-mem-statcol-title">Most-watched directors</h5>' +
                '<div class="ir-mem-people">' + extra.topDirectors.map(personCard).join('') + '</div>' +
              '</div>'
            : '';
        var actorsHtml = (extra.topActors && extra.topActors.length)
            ? '<div class="ir-mem-section"><h5 class="ir-mem-statcol-title">Most-watched actors</h5>' +
                '<div class="ir-mem-people">' + extra.topActors.map(personCard).join('') + '</div>' +
              '</div>'
            : '';

        // Year cards — collapsed summary only. Clicking opens the dedicated
        // year-detail sub-page (where the rich per-year breakdown lives,
        // including all directors/actors/genres + full film list).
        var yearsHtml = '';
        if (extra.yearBreakdown && extra.yearBreakdown.length) {
            yearsHtml = '<div class="ir-mem-section"><h5 class="ir-mem-statcol-title">By year</h5>' +
                '<div class="ir-mem-years">' + extra.yearBreakdown.map(function (y) {
                    return '<div class="ir-mem-yearcard ir-mem-yearcard-click" data-year="' + (y.year|0) + '">' +
                        '<div class="ir-mem-yearcard-head">' +
                            '<span class="ir-mem-yearcard-year">' + (y.year|0) + '</span>' +
                            '<span class="ir-mem-yearcard-stats">' +
                                '<b>' + (y.ratingsCount|0) + '</b> rated · ' +
                                (y.avgRating > 0 ? '<b>' + y.avgRating.toFixed(2) + '★</b> avg · ' : '') +
                                (y.hoursWatched > 0 ? '<b>' + y.hoursWatched.toFixed(0) + 'h</b> watched' : '') +
                            '</span>' +
                            '<span class="ir-mem-yearcard-arrow">→</span>' +
                        '</div>' +
                        (y.topFilmId
                            ? '<div class="ir-mem-yearcard-top" data-iid="' + esc(y.topFilmId) + '">' +
                                '<span class="ir-mem-yearcard-toplabel">Best of ' + (y.year|0) + ' ·</span> ' +
                                '<span class="ir-mem-yearcard-toptitle">' + esc(y.topFilmTitle || '—') + '</span>' +
                                '<span class="ir-mem-yearcard-topstars">' + (y.topFilmStars > 0 ? y.topFilmStars.toFixed(1) + '★' : '') + '</span>' +
                            '</div>'
                            : '') +
                    '</div>';
                }).join('') + '</div>' +
            '</div>';
        }

        var genresHtml = (extra.topGenres && extra.topGenres.length)
            ? '<div class="ir-mem-section"><h5 class="ir-mem-statcol-title">Top genres</h5>' + rankList(extra.topGenres) + '</div>'
            : '';
        var decadeHtml = (extra.decadeBreakdown && extra.decadeBreakdown.length)
            ? '<div class="ir-mem-section"><h5 class="ir-mem-statcol-title">By decade</h5>' + rankList(extra.decadeBreakdown) + '</div>'
            : '';

        var grid = directorsHtml + actorsHtml + yearsHtml + genresHtml + decadeHtml;

        // Media breakdown — Letterboxd-style tabbed panel showing per-type
        // count, avg rating, hours watched, and top genres. Renders all
        // three tabs in DOM but only the active one is visible (cheap
        // switch — no re-render of the whole stats card).
        var breakdown = extra.mediaBreakdown || [];
        var mtab = _profileMediaTab || 'movie';
        var mediaHtml = '';
        if (breakdown.length) {
            var labels = { movie: 'Films', series: 'TV', anime: 'Anime' };
            var tabs = breakdown.map(function (b) {
                var on = (b.type === mtab);
                return '<button class="ir-mem-mediatab' + (on ? ' ir-mem-mediatab-on' : '') + '" data-mt="' + esc(b.type) + '" type="button">' +
                    esc(labels[b.type] || b.type) + ' <span>' + (b.ratingsCount|0) + '</span></button>';
            }).join('');
            var active = breakdown.filter(function (b) { return b.type === mtab; })[0] || breakdown[0];
            var av = active && active.ratingsCount > 0 ? active.avgRating.toFixed(2) + '★' : '—';
            var hrs = active && active.hoursWatched > 0 ? active.hoursWatched.toFixed(1) + 'h' : '—';
            var rated = active ? active.ratingsCount : 0;
            var genresList = (active && active.topGenres && active.topGenres.length)
                ? rankList(active.topGenres)
                : '<div class="ir-mem-rank-empty">—</div>';
            mediaHtml =
                '<div class="ir-mem-mediabreak">' +
                    '<div class="ir-mem-mediatabs">' + tabs + '</div>' +
                    '<div class="ir-mem-mediaheadline">' +
                        bigStat(rated, 'rated') +
                        bigStat(av, 'avg rating') +
                        bigStat(hrs, 'watched') +
                    '</div>' +
                    '<div class="ir-mem-mediagenres"><h5 class="ir-mem-statcol-title">Top genres</h5>' + genresList + '</div>' +
                '</div>';
        }

        // Calendar heatmap — last 365 days as a 53×7 grid (column = week,
        // row = day of week). Color intensity scales by count.
        function heatmapHtml(days) {
            if (!days) return '';
            var entries = Object.keys(days);
            if (!entries.length) return '';
            var max = 1; entries.forEach(function (k) { if (days[k] > max) max = days[k]; });
            var today = new Date();
            today.setHours(0,0,0,0);
            // Find the most recent Saturday so weeks line up.
            var endIdx = 364;
            var cells = [];
            for (var i = endIdx; i >= 0; i--) {
                var d = new Date(today); d.setDate(d.getDate() - i);
                var k = d.getFullYear() + '-' +
                        String(d.getMonth()+1).padStart(2,'0') + '-' +
                        String(d.getDate()).padStart(2,'0');
                cells.push({ key: k, day: d.getDay(), date: new Date(d), count: days[k] || 0 });
            }
            // Re-bin into 7 rows x ~53 cols.
            var rows = [[],[],[],[],[],[],[]];
            cells.forEach(function (c) { rows[c.day].push(c); });
            var dayLabels = ['Sun','Mon','Tue','Wed','Thu','Fri','Sat'];
            return '<div class="ir-mem-heatmap">' +
                rows.map(function (r, idx) {
                    return '<div class="ir-mem-heatmap-row">' +
                        '<span class="ir-mem-heatmap-rowlabel">' + dayLabels[idx] + '</span>' +
                        r.map(function (c) {
                            var lvl = c.count === 0 ? 0 :
                                      c.count >= max * 0.75 ? 4 :
                                      c.count >= max * 0.5  ? 3 :
                                      c.count >= max * 0.25 ? 2 : 1;
                            return '<span class="ir-mem-heatmap-cell ir-mem-heatmap-l' + lvl + '" title="' + esc(c.key + ' · ' + c.count + ' film' + (c.count===1?'':'s')) + '"></span>';
                        }).join('') +
                    '</div>';
                }).join('') +
            '</div>';
        }
        var heatmapBlock = (extra.calendarHeatmap && Object.keys(extra.calendarHeatmap).length)
            ? '<div class="ir-mem-section"><h5 class="ir-mem-statcol-title">Activity heatmap (last year)</h5>' + heatmapHtml(extra.calendarHeatmap) + '</div>'
            : '';

        // On this day — small horizontal poster strip of past anniversaries.
        var onThisDayHtml = '';
        if (extra.onThisDay && extra.onThisDay.length) {
            onThisDayHtml = '<div class="ir-mem-section"><h5 class="ir-mem-statcol-title">On this day</h5>' +
                '<div class="ir-mem-otd-strip">' + extra.onThisDay.map(function (o) {
                    var when = '';
                    try { when = new Date(o.watchedAt).toLocaleDateString(undefined, { year: 'numeric' }); } catch (e) {}
                    return '<div class="ir-mem-otd-card" data-iid="' + esc(o.itemId) + '">' +
                        '<span class="ir-mem-poster ir-mem-poster-s" data-iid="' + esc(o.itemId) + '">' +
                            (function(){
                                var src = '/Items/' + o.itemId + '/Images/Primary?fillHeight=240&fillWidth=160&quality=90';
                                return '<img class="ir-mem-poster-img" loading="lazy" src="' + esc(src) + '" alt="" onerror="this.style.display=\'none\'" />';
                            })() +
                        '</span>' +
                        '<span class="ir-mem-otd-when">' + esc(when) + '</span>' +
                    '</div>';
                }).join('') + '</div></div>';
        }

        // Most rewatched
        var rewatchHtml = '';
        if (extra.rewatchTop && extra.rewatchTop.length) {
            rewatchHtml = '<div class="ir-mem-section"><h5 class="ir-mem-statcol-title">Most rewatched</h5>' +
                '<div class="ir-mem-rewatch-strip">' + extra.rewatchTop.map(function (r) {
                    return '<div class="ir-mem-rewatch-card" data-iid="' + esc(r.itemId) + '">' +
                        '<span class="ir-mem-poster ir-mem-poster-s" data-iid="' + esc(r.itemId) + '">' +
                            '<img class="ir-mem-poster-img" loading="lazy" src="' + esc('/Items/' + r.itemId + '/Images/Primary?fillHeight=240&fillWidth=160&quality=90') + '" alt="" onerror="this.style.display=\'none\'" />' +
                        '</span>' +
                        '<span class="ir-mem-rewatch-count">' + (r.rewatchCount|0) + '×</span>' +
                    '</div>';
                }).join('') + '</div></div>';
        }

        host.innerHTML = bigs + timelineBlock + heatmapBlock + onThisDayHtml + rewatchHtml + grid + mediaHtml;
        // Re-wire interactive bits inside the stats card after a re-render
        // (year-card expand, timeline filter chips, year-open buttons).
        wireProfileNav(host);
        wireProfilePosters(host);
    }

    // Full-page list (watchlist / liked / diary). Renders the back button
    // and a poster grid; diary uses the same list-row style as the recent
    // diary section.
    function buildProfileListPage(p, meta, tab) {
        function poster(itemId) {
            var m = meta[itemId];
            if (m && m.imageTag) {
                return '<img class="ir-mem-poster-img" loading="lazy" src="' +
                    esc('/Items/' + itemId + '/Images/Primary?fillHeight=360&fillWidth=240&quality=90&tag=' + m.imageTag) +
                    '" alt="" />';
            }
            return '<span class="ir-mem-poster-empty">' + esc((m && m.name) ? m.name.charAt(0) : '?') + '</span>';
        }
        function titleOf(id) { var m = meta[id]; return m && m.name ? m.name : '—'; }

        var label, items;
        if (tab === 'all-watchlist') { label = 'Watchlist'; items = p.watchlist || []; }
        else if (tab === 'all-liked') { label = 'Liked'; items = p.likes || []; }
        else { label = 'Diary'; items = p.diary || []; }

        var head = '<div class="ir-mem-prof-head">' +
            '<button class="ir-mem-back" type="button">← Back to profile</button>' +
            '<span class="ir-mem-prof-name">' + esc(p.name || '') + ' · ' + esc(label) + '</span>' +
        '</div>';

        if (items.length === 0) {
            return '<div class="ir-mem-profile">' + head + '<div class="ir-ov-empty">Nothing here yet.</div></div>';
        }

        var body;
        // Type filter chips for diary/watchlist/liked sub-pages. Filter
        // happens client-side using p.itemTypes which the server already
        // pre-classified during the profile fetch.
        var filter = _profileSubFilter || 'all';
        var typeMap = p.itemTypes || {};
        var counts = { all: items.length, movie: 0, series: 0, anime: 0 };
        items.forEach(function (it) {
            var iid = it.itemId || it;
            var t = typeMap[iid] || 'other';
            if (counts[t] !== undefined) counts[t] += 1;
        });
        var filterHtml =
            '<div class="ir-mem-typefilter">' +
                '<button class="ir-mem-typefilter-btn ' + (filter === 'all'    ? 'ir-mem-typefilter-on' : '') + '" data-f="all" type="button">All <span>'+counts.all+'</span></button>' +
                '<button class="ir-mem-typefilter-btn ' + (filter === 'movie'  ? 'ir-mem-typefilter-on' : '') + '" data-f="movie" type="button">Movies <span>'+counts.movie+'</span></button>' +
                '<button class="ir-mem-typefilter-btn ' + (filter === 'series' ? 'ir-mem-typefilter-on' : '') + '" data-f="series" type="button">Series <span>'+counts.series+'</span></button>' +
                '<button class="ir-mem-typefilter-btn ' + (filter === 'anime'  ? 'ir-mem-typefilter-on' : '') + '" data-f="anime" type="button">Anime <span>'+counts.anime+'</span></button>' +
            '</div>';

        var visible = items.filter(function (it) {
            if (filter === 'all') return true;
            var iid = it.itemId || it;
            return (typeMap[iid] || 'other') === filter;
        });

        var body;
        if (visible.length === 0) {
            body = '<div class="ir-ov-empty">Nothing matches this filter.</div>';
        } else if (tab === 'all-diary') {
            body = '<ul class="ir-mem-diary">' + visible.map(function (d) {
                var when = '';
                try { when = new Date(d.watchedAt).toLocaleDateString(); } catch (e) {}
                var stars = (d.stars != null) ? Math.round(d.stars * 2) / 2 : null;
                var starsHtml = '';
                if (stars != null) {
                    starsHtml = '<span class="ir-mem-stars ir-mem-diary-stars-icons">';
                    for (var si = 1; si <= 5; si++) {
                        var v = stars - (si - 1);
                        if (v >= 1)        starsHtml += '<span class="ir-mem-star ir-mem-star-full">★</span>';
                        else if (v >= 0.5) starsHtml += '<span class="ir-mem-star ir-mem-star-half">★</span>';
                        else               starsHtml += '<span class="ir-mem-star">★</span>';
                    }
                    starsHtml += '</span>';
                } else {
                    starsHtml = '<span class="ir-mem-diary-nostar">—</span>';
                }
                return '<li class="ir-mem-diary-row" data-iid="' + esc(d.itemId) + '">' +
                    '<span class="ir-mem-poster ir-mem-poster-xs" data-iid="' + esc(d.itemId) + '">' + poster(d.itemId) + '</span>' +
                    '<span class="ir-mem-diary-meta">' +
                        '<span class="ir-mem-diary-title">' + esc(titleOf(d.itemId)) + '</span>' +
                        '<span class="ir-mem-diary-sub">' + esc(when) + '</span>' +
                    '</span>' +
                    '<span class="ir-mem-diary-actions">' +
                        starsHtml +
                        (d.rewatch ? '<span class="ir-mem-diary-rewatch-pill">Rewatch</span>' : '') +
                    '</span></li>';
            }).join('') + '</ul>';
        } else {
            body = '<div class="ir-mem-fullgrid">' + visible.map(function (it) {
                var iid = it.itemId || it;
                return '<div class="ir-mem-poster ir-mem-poster-m" data-iid="' + esc(iid) + '" title="' + esc(titleOf(iid)) + '">' +
                    poster(iid) + '</div>';
            }).join('') + '</div>';
        }
        return '<div class="ir-mem-profile">' + head + filterHtml + body + '</div>';
    }

    // Sub-page showing every film a user has rated at a given star value.
    // Picks from p.recentRatings (top 50) which is sufficient for the
    // common case; server-side full-list endpoint is a follow-up.
    function buildProfileRatingsAtStarPage(p, meta, starVal) {
        function poster(itemId) {
            var m = meta[itemId];
            if (m && m.imageTag) {
                return '<img class="ir-mem-poster-img" loading="lazy" src="' +
                    esc('/Items/' + itemId + '/Images/Primary?fillHeight=360&fillWidth=240&quality=90&tag=' + m.imageTag) +
                    '" alt="" />';
            }
            return '<span class="ir-mem-poster-empty">' + esc((m && m.name) ? m.name.charAt(0) : '?') + '</span>';
        }
        function titleOf(id) { var m = meta[id]; return m && m.name ? m.name : '—'; }

        var target = parseFloat(starVal);
        var items = (p.recentRatings || []).filter(function (r) {
            return Math.round(r.stars * 2) / 2 === target;
        });
        var totalAtStar = (p.histogram && p.histogram[target.toFixed(1)]) || 0;

        var head = '<div class="ir-mem-prof-head">' +
            '<button class="ir-mem-back" type="button">← Back to profile</button>' +
            '<span class="ir-mem-prof-name">' + esc(p.name || '') + ' · ' + esc(target.toFixed(1)) + '★ ratings</span>' +
            '<span class="ir-mem-prof-sub">' + items.length + ' shown · ' + totalAtStar + ' total at this rating</span>' +
        '</div>';

        if (items.length === 0) {
            return '<div class="ir-mem-profile">' + head + '<div class="ir-ov-empty">Nothing recent at ' + esc(target.toFixed(1)) + '★.</div></div>';
        }
        var body = '<div class="ir-mem-fullgrid">' + items.map(function (r) {
            var iid = r.itemId;
            return '<div class="ir-mem-poster ir-mem-poster-m" data-iid="' + esc(iid) + '" title="' + esc(titleOf(iid)) + '">' +
                poster(iid) + '</div>';
        }).join('') + '</div>';
        return '<div class="ir-mem-profile">' + head + body + '</div>';
    }

    // Full sortable + type-filterable rated-films list. Letterboxd-style:
    // each row is poster + title + year + star rating + rated date.
    function buildProfileAllRatingsPage(p, meta) {
        function poster(itemId) {
            var m = meta[itemId];
            if (m && m.imageTag) {
                return '<img class="ir-mem-poster-img" loading="lazy" src="' +
                    esc('/Items/' + itemId + '/Images/Primary?fillHeight=180&fillWidth=120&quality=90&tag=' + m.imageTag) +
                    '" alt="" />';
            }
            return '<span class="ir-mem-poster-empty">' + esc((m && m.name) ? m.name.charAt(0) : '?') + '</span>';
        }
        function titleOf(id) { var m = meta[id]; return m && m.name ? m.name : '—'; }
        function yearOf(id) { var m = meta[id]; return m && m.year ? m.year : ''; }

        var rows = (p.recentRatings || []).slice();
        // Type filter
        var typeMap = p.itemTypes || {};
        if (_profileSubFilter !== 'all') {
            rows = rows.filter(function (r) { return (typeMap[r.itemId] || 'other') === _profileSubFilter; });
        }
        // Sort — Letterboxd parity. Year + runtime come from the server.
        // "User rating" sorts by the profile-owner's star value; "My rating"
        // sorts by the viewer's own rating of the same film (-1 sentinel
        // pushes unrated films to the end).
        function yearVal(r) { return r.productionYear || 0; }
        function runVal(r)  { return r.runtimeMinutes || 0; }
        function myVal(r)   { return (r.myStars != null) ? r.myStars : -1; }
        var sort = _profileSortBy;
        rows.sort(function (a, b) {
            switch (sort) {
                case 'date-desc':   return (new Date(b.ratedAt)) - (new Date(a.ratedAt));
                case 'date-asc':    return (new Date(a.ratedAt)) - (new Date(b.ratedAt));
                case 'year-desc':   return yearVal(b) - yearVal(a) || titleOf(a.itemId).localeCompare(titleOf(b.itemId));
                case 'year-asc':    return yearVal(a) - yearVal(b) || titleOf(a.itemId).localeCompare(titleOf(b.itemId));
                case 'stars-desc':  return b.stars - a.stars || (new Date(b.ratedAt)) - (new Date(a.ratedAt));
                case 'stars-asc':   return a.stars - b.stars || (new Date(b.ratedAt)) - (new Date(a.ratedAt));
                case 'mystars-desc': return myVal(b) - myVal(a) || (new Date(b.ratedAt)) - (new Date(a.ratedAt));
                case 'mystars-asc':  return myVal(a) - myVal(b) || (new Date(b.ratedAt)) - (new Date(a.ratedAt));
                case 'len-desc':    return runVal(b) - runVal(a);
                case 'len-asc':     return runVal(a) - runVal(b);
                case 'title':       return titleOf(a.itemId).localeCompare(titleOf(b.itemId));
            }
            return 0;
        });

        var counts = { all: (p.recentRatings || []).length, movie: 0, series: 0, anime: 0 };
        (p.recentRatings || []).forEach(function (r) {
            var t = typeMap[r.itemId] || 'other';
            if (counts[t] !== undefined) counts[t] += 1;
        });

        var head = '<div class="ir-mem-prof-head">' +
            '<button class="ir-mem-back" type="button">← Back to profile</button>' +
            '<span class="ir-mem-prof-name">' + esc(p.name || '') + ' · All ratings</span>' +
            '<span class="ir-mem-prof-sub">' + rows.length + ' / ' + counts.all + '</span>' +
        '</div>';

        var typeChips =
            '<div class="ir-mem-typefilter">' +
                ['all','movie','series','anime'].map(function (k) {
                    var label = { all:'All', movie:'Movies', series:'Series', anime:'Anime' }[k];
                    return '<button class="ir-mem-typefilter-btn ' + (_profileSubFilter === k ? 'ir-mem-typefilter-on' : '') + '" data-f="' + k + '" type="button">' +
                        label + ' <span>' + counts[k] + '</span></button>';
                }).join('') +
            '</div>';

        // Two rating axes:
        //   "<Name>'s rating" — sort by the profile-owner's stars
        //   "My rating" — sort by the viewer's own stars for those films
        // On your own profile both axes are the same so we hide the second.
        var isSelfProfile = !!(_profileCache && _profileCache.isSelf);
        var theirLabel = isSelfProfile ? 'My rating' : ((p.name || 'User') + "'s rating");
        var sortOptions = [
            ['date-desc',  'Newest rated'],
            ['date-asc',   'Oldest rated'],
            ['year-desc',  'Film year ↓'],
            ['year-asc',   'Film year ↑'],
            ['stars-desc', theirLabel + ' ↓'],
            ['stars-asc',  theirLabel + ' ↑']
        ];
        if (!isSelfProfile) {
            sortOptions.push(['mystars-desc', 'My rating ↓']);
            sortOptions.push(['mystars-asc',  'My rating ↑']);
        }
        sortOptions.push(['len-desc', 'Length ↓']);
        sortOptions.push(['len-asc',  'Length ↑']);
        sortOptions.push(['title',    'A → Z']);

        var sortChips =
            '<div class="ir-mem-sortbar">' +
                '<span class="ir-mem-sortbar-label">Sort by</span>' +
                sortOptions.map(function (e) {
                    var k = e[0], label = e[1];
                    return '<button class="ir-mem-sortbtn ' + (_profileSortBy === k ? 'ir-mem-sortbtn-on' : '') + '" data-s="' + k + '" type="button">' + esc(label) + '</button>';
                }).join('') +
            '</div>';

        if (rows.length === 0) {
            return '<div class="ir-mem-profile">' + head + typeChips + sortChips + '<div class="ir-ov-empty">No ratings to show.</div></div>';
        }

        function starRow(stars) {
            // Render half-stars as filled/empty halves up to 5.
            var html = '<span class="ir-mem-stars">';
            for (var i = 1; i <= 5; i++) {
                var v = stars - (i - 1);
                if (v >= 1)       html += '<span class="ir-mem-star ir-mem-star-full">★</span>';
                else if (v >= 0.5) html += '<span class="ir-mem-star ir-mem-star-half">★</span>';
                else               html += '<span class="ir-mem-star">★</span>';
            }
            html += '</span>';
            return html;
        }

        var body = '<ul class="ir-mem-allratings">' + rows.map(function (r) {
            var when = '';
            try { when = new Date(r.ratedAt).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }); } catch (e) {}
            return '<li class="ir-mem-allratings-row" data-iid="' + esc(r.itemId) + '">' +
                '<span class="ir-mem-poster ir-mem-poster-xs" data-iid="' + esc(r.itemId) + '">' + poster(r.itemId) + '</span>' +
                '<span class="ir-mem-allratings-meta">' +
                    '<span class="ir-mem-allratings-title">' + esc(titleOf(r.itemId)) + (yearOf(r.itemId) ? ' <span class="ir-mem-allratings-year">' + yearOf(r.itemId) + '</span>' : '') + '</span>' +
                    '<span class="ir-mem-allratings-sub">' + esc(when) + '</span>' +
                '</span>' +
                starRow(r.stars) +
            '</li>';
        }).join('') + '</ul>';

        return '<div class="ir-mem-profile">' + head + typeChips + sortChips + body + '</div>';
    }

    // Year-detail sub-page — full per-year breakdown. Top: headline stats
    // + best-of-year. Then a rich grid: top genres, ratings spread mini
    // histogram, top directors with faces, top actors with faces. Below
    // that: every film rated that year as a sortable list.
    function buildProfileYearDetailPage(p, meta, year) {
        function poster(itemId) {
            var m = meta[itemId];
            if (m && m.imageTag) {
                return '<img class="ir-mem-poster-img" loading="lazy" src="' +
                    esc('/Items/' + itemId + '/Images/Primary?fillHeight=180&fillWidth=120&quality=90&tag=' + m.imageTag) +
                    '" alt="" />';
            }
            return '<span class="ir-mem-poster-empty">' + esc((m && m.name) ? m.name.charAt(0) : '?') + '</span>';
        }
        function titleOf(id) { var m = meta[id]; return m && m.name ? m.name : '—'; }
        function yearOf(id) { var m = meta[id]; return m && m.year ? m.year : ''; }

        var y = parseInt(year, 10) || 0;
        // Find the matching year-stat from the cached extra (already loaded
        // when the user navigated to a year card).
        var yStat = null;
        if (_profileStatsCache && Array.isArray(_profileStatsCache.yearBreakdown)) {
            for (var i = 0; i < _profileStatsCache.yearBreakdown.length; i++) {
                if (_profileStatsCache.yearBreakdown[i].year === y) { yStat = _profileStatsCache.yearBreakdown[i]; break; }
            }
        }
        var rows = (p.recentRatings || []).filter(function (r) {
            try { return new Date(r.ratedAt).getFullYear() === y; } catch (e) { return false; }
        });
        rows.sort(function (a, b) { return (new Date(b.ratedAt)) - (new Date(a.ratedAt)); });

        var head = '<div class="ir-mem-prof-head">' +
            '<button class="ir-mem-back" type="button">← Back to profile</button>' +
            '<span class="ir-mem-prof-name">' + esc(p.name || '') + ' · ' + y + '</span>' +
            '<span class="ir-mem-prof-sub">' + rows.length + ' rated</span>' +
        '</div>';

        // Headline tiles + temporal stats (first/last/peak/day-of-week +
        // top director, when present). Rendered as the same overview tile
        // strip used elsewhere on the profile.
        function fmtShortDate(iso) {
            try { return new Date(iso).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }); } catch (e) { return ''; }
        }
        var tilesHtml = '';
        if (yStat) {
            var topDirector = (yStat.topDirectors && yStat.topDirectors.length) ? yStat.topDirectors[0] : null;
            tilesHtml =
                '<div class="ir-mem-overview">' +
                    '<div class="ir-mem-overview-tile"><span class="ir-mem-overview-v">' + (yStat.ratingsCount|0) + '</span><span class="ir-mem-overview-l">rated</span></div>' +
                    (yStat.avgRating > 0 ? '<div class="ir-mem-overview-tile"><span class="ir-mem-overview-v">' + yStat.avgRating.toFixed(2) + '★</span><span class="ir-mem-overview-l">avg</span></div>' : '') +
                    (yStat.hoursWatched > 0 ? '<div class="ir-mem-overview-tile"><span class="ir-mem-overview-v">' + yStat.hoursWatched.toFixed(0) + 'h</span><span class="ir-mem-overview-l">watched</span></div>' : '') +
                    (yStat.firstWatched ? '<div class="ir-mem-overview-tile"><span class="ir-mem-overview-v">' + esc(fmtShortDate(yStat.firstWatched)) + '</span><span class="ir-mem-overview-l">first watched</span></div>' : '') +
                    (yStat.lastWatched  ? '<div class="ir-mem-overview-tile"><span class="ir-mem-overview-v">' + esc(fmtShortDate(yStat.lastWatched))  + '</span><span class="ir-mem-overview-l">last watched</span></div>' : '') +
                    (yStat.mostWatchedDay ? '<div class="ir-mem-overview-tile"><span class="ir-mem-overview-v">' + esc(yStat.mostWatchedDay) + '</span><span class="ir-mem-overview-l">most-watched day · ' + (yStat.mostWatchedDayCount|0) + ' films</span></div>' : '') +
                    (yStat.peakDate    ? '<div class="ir-mem-overview-tile"><span class="ir-mem-overview-v">' + (yStat.peakDateCount|0) + '</span><span class="ir-mem-overview-l">peak day · ' + esc(fmtShortDate(yStat.peakDate)) + '</span></div>' : '') +
                    (topDirector ? '<div class="ir-mem-overview-tile"><span class="ir-mem-overview-v">' + esc(topDirector.name) + '</span><span class="ir-mem-overview-l">top director · ' + (topDirector.count|0) + ' films</span></div>' : '') +
                    (yStat.topFilmTitle ? '<div class="ir-mem-overview-tile ir-mem-overview-clickable" data-iid="' + esc(yStat.topFilmId) + '"><span class="ir-mem-overview-v">' + (yStat.topFilmStars > 0 ? yStat.topFilmStars.toFixed(1) + '★' : '—') + '</span><span class="ir-mem-overview-l">best · ' + esc(yStat.topFilmTitle) + '</span></div>' : '') +
                '</div>';
        }

        // Per-year rank lists + mini histogram + people
        function rankList(items, max) {
            if (!items || !items.length) return '<div class="ir-mem-rank-empty">—</div>';
            var m = Math.max(1, items[0].count);
            return '<ul class="ir-mem-rank">' + items.slice(0, max||5).map(function (it) {
                var pct = Math.round((it.count / m) * 100);
                return '<li class="ir-mem-rank-row"><span class="ir-mem-rank-name">' + esc(it.name) + '</span>' +
                    '<span class="ir-mem-rank-bar"><span class="ir-mem-rank-fill" style="width:' + pct + '%"></span></span>' +
                    '<span class="ir-mem-rank-n">' + (it.count|0) + '</span></li>';
            }).join('') + '</ul>';
        }
        function personGrid(items, max) {
            if (!items || !items.length) return '<div class="ir-mem-rank-empty">—</div>';
            return '<div class="ir-mem-people">' + items.slice(0, max||8).map(function (pp) {
                var initial = esc((pp.name || '?').charAt(0).toUpperCase());
                var imgUrl = pp.personId
                    ? '/Items/' + encodeURIComponent(pp.personId) + '/Images/Primary?fillHeight=160&fillWidth=160&quality=90'
                    : '';
                var av = '<span class="ir-mem-pp-circle">' +
                    '<span class="ir-mem-pp-fb">' + initial + '</span>' +
                    (imgUrl ? '<img class="ir-mem-pp-img" src="' + esc(imgUrl) + '" loading="lazy" alt="" onerror="this.classList.add(\'ir-mem-pp-img-failed\')" />' : '') +
                '</span>';
                return '<div class="ir-mem-pp" title="' + esc(pp.name + ' — ' + pp.count + ' film' + (pp.count===1?'':'s')) + '">' +
                    av + '<span class="ir-mem-pp-name">' + esc(pp.name) + '</span>' +
                    '<span class="ir-mem-pp-count">' + (pp.count|0) + ' film' + (pp.count===1?'':'s') + '</span>' +
                '</div>';
            }).join('') + '</div>';
        }
        // Mini histogram for the year (uses gold gradient + hover popover
        // matching the main histogram).
        var histHtml = '';
        if (yStat && yStat.histogram) {
            var hk = ['0.5','1.0','1.5','2.0','2.5','3.0','3.5','4.0','4.5','5.0'];
            var maxY = 0; hk.forEach(function (k) { if ((yStat.histogram[k]||0) > maxY) maxY = yStat.histogram[k]||0; });
            var bars = hk.map(function (k) {
                var v = yStat.histogram[k] || 0;
                var pct = maxY > 0 ? Math.round((v / maxY) * 100) : 0;
                return '<div class="ir-mem-bar' + (v === 0 ? ' ir-mem-bar-empty' : '') + '" title="' + esc(k+'★ — '+v+' rating'+(v===1?'':'s')) + '">' +
                    '<span class="ir-mem-bar-fill" style="height:' + pct + '%"></span>' +
                    '<span class="ir-mem-bar-label">' + esc(k) + '</span>' +
                    '<span class="ir-mem-bar-pop"><b>' + v + '</b> rated ' + esc(k) + '★</span>' +
                '</div>';
            }).join('');
            histHtml = '<div class="ir-mem-hist" style="height:140px">' + bars + '</div>';
        }

        var richHtml = yStat
            ? '<div class="ir-mem-section ir-mem-yearpage-grid">' +
                '<div class="ir-mem-yearpage-col"><h5 class="ir-mem-statcol-title">Top genres</h5>' + rankList(yStat.topGenres, 6) + '</div>' +
                (histHtml ? '<div class="ir-mem-yearpage-col"><h5 class="ir-mem-statcol-title">Ratings spread</h5>' + histHtml + '</div>' : '') +
                ((yStat.topDirectors && yStat.topDirectors.length) ? '<div class="ir-mem-yearpage-col"><h5 class="ir-mem-statcol-title">Top directors</h5>' + personGrid(yStat.topDirectors, 8) + '</div>' : '') +
                ((yStat.topActors && yStat.topActors.length) ? '<div class="ir-mem-yearpage-col"><h5 class="ir-mem-statcol-title">Top actors</h5>' + personGrid(yStat.topActors, 12) + '</div>' : '') +
            '</div>'
            : '';

        if (rows.length === 0 && !yStat) {
            return '<div class="ir-mem-profile">' + head + '<div class="ir-ov-empty">No ratings recorded in ' + y + '.</div></div>';
        }

        function starRow(stars) {
            var html = '<span class="ir-mem-stars">';
            for (var i = 1; i <= 5; i++) {
                var v = stars - (i - 1);
                if (v >= 1)        html += '<span class="ir-mem-star ir-mem-star-full">★</span>';
                else if (v >= 0.5) html += '<span class="ir-mem-star ir-mem-star-half">★</span>';
                else               html += '<span class="ir-mem-star">★</span>';
            }
            html += '</span>';
            return html;
        }

        var listBody = rows.length === 0 ? '' : '<h5 class="ir-mem-statcol-title" style="margin:24px 0 8px">All films rated in ' + y + '</h5>' +
            '<ul class="ir-mem-allratings">' + rows.map(function (r) {
                var when = '';
                try { when = new Date(r.ratedAt).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }); } catch (e) {}
                return '<li class="ir-mem-allratings-row" data-iid="' + esc(r.itemId) + '">' +
                    '<span class="ir-mem-poster ir-mem-poster-xs" data-iid="' + esc(r.itemId) + '">' + poster(r.itemId) + '</span>' +
                    '<span class="ir-mem-allratings-meta">' +
                        '<span class="ir-mem-allratings-title">' + esc(titleOf(r.itemId)) + (yearOf(r.itemId) ? ' <span class="ir-mem-allratings-year">' + yearOf(r.itemId) + '</span>' : '') + '</span>' +
                        '<span class="ir-mem-allratings-sub">' + esc(when) + '</span>' +
                    '</span>' +
                    starRow(r.stars) +
                '</li>';
            }).join('') + '</ul>';

        return '<div class="ir-mem-profile">' + head + tilesHtml + richHtml + listBody + '</div>';
    }

    function buildProfileFollowPage(p, tab) {
        var label = tab === 'followers' ? 'Followers' : 'Following';
        return '<div class="ir-mem-profile">' +
            '<div class="ir-mem-prof-head">' +
                '<button class="ir-mem-back" type="button">← Back to profile</button>' +
                '<span class="ir-mem-prof-name">' + esc(p.name || '') + ' · ' + esc(label) + '</span>' +
            '</div>' +
            '<div class="ir-mem-followlist"><div class="ir-ov-loading">Loading…</div></div>' +
        '</div>';
    }

    function buildFollowCardsHtml(rows) {
        if (!rows.length) return '<div class="ir-ov-empty">Nobody yet.</div>';
        return '<div class="ir-mem-grid">' + rows.map(function (m) {
            var img = m.hasImage
                ? '/Users/' + encodeURIComponent(m.id) + '/Images/Primary?fillHeight=240&fillWidth=240&quality=90'
                : null;
            var avatar = img
                ? '<img class="ir-mem-avatar-img" src="' + esc(img) + '" alt="" loading="lazy" />'
                : '<span class="ir-mem-avatar-fallback">' + esc((m.name || '?').charAt(0).toUpperCase()) + '</span>';
            var followBtn = m.isSelf ? '' :
                '<button class="ir-mem-follow-btn ' + (m.isFollowing ? 'ir-mem-follow-on' : '') + '" data-uid="' + esc(m.id) + '" type="button">' +
                    (m.isFollowing ? 'Following' : 'Follow') +
                '</button>';
            return '<div class="ir-mem-card ir-mem-card-v2">' +
                '<button class="ir-mem-card-body" data-uid="' + esc(m.id) + '" data-uname="' + esc(m.name) + '" type="button">' +
                    '<span class="ir-mem-avatar">' + avatar + '</span>' +
                    '<span class="ir-mem-name">' + esc(m.name) + '</span>' +
                    '<span class="ir-mem-stats">' +
                        '<span class="ir-mem-stat"><b>' + (m.followersCount|0) + '</b> followers</span>' +
                    '</span>' +
                '</button>' + followBtn +
            '</div>';
        }).join('') + '</div>';
    }

    function refreshFavoritesRow() {
        var favsEl = _overlay.querySelector('.ir-ov-favs');
        if (!favsEl) return;

        // Only show on the Films view — hidden on Watchlist/Liked/Diary/Recs
        if (_overlayView !== 'films') {
            favsEl.classList.add('ir-ov-favs-hidden');
            return;
        }
        favsEl.classList.remove('ir-ov-favs-hidden');

        apiMyFavorites().then(function (favs) {
            _favItemIds = favs || [];
            var fetchIds = _favItemIds.filter(function (x) { return !!x; });

            var renderGroups = function (meta) {
                // Late-callback guard: if the user switched away from the
                // Films view between fetch start and fetch resolve, never
                // re-show the favorites row. Without this check the
                // favorites strip would briefly reappear on Watchlist /
                // Liked / etc whenever the user switched fast enough.
                if (_overlayView !== 'films') {
                    favsEl.classList.add('ir-ov-favs-hidden');
                    return;
                }
                meta = meta || {};
                // Group existing favorites by type. Order within a group
                // follows the original storage order so the user controls
                // the slot positions.
                var groups = { Movie: [], Series: [], Episode: [] };
                _favItemIds.forEach(function (id) {
                    if (!id) return;
                    var m = meta[id] || {};
                    var t = m.type || 'Movie';
                    if (!groups[t]) groups[t] = [];
                    groups[t].push({ id: id, m: m });
                });

                // The favorites row now follows the active type tab so the
                // user only sees the relevant Top 4 for whatever they're
                // browsing. "All" shows whichever rows have content (and
                // always Movies since it's the most common pinning).
                favsEl.innerHTML = '';

                if (_activeTab === 'all') {
                    renderOneFavRow(favsEl, '\u2605 Top 4 Movies',   groups.Movie,   'Movie');
                    if (groups.Series.length > 0)
                        renderOneFavRow(favsEl, '\u2605 Top 4 Series',  groups.Series,  'Series');
                    if (groups.Episode.length > 0)
                        renderOneFavRow(favsEl, '\u2605 Top 4 Episodes', groups.Episode, 'Episode');
                } else if (_activeTab === 'Movie') {
                    renderOneFavRow(favsEl, '\u2605 Top 4 Movies', groups.Movie, 'Movie');
                } else if (_activeTab === 'Series') {
                    renderOneFavRow(favsEl, '\u2605 Top 4 Series', groups.Series, 'Series');
                } else if (_activeTab === 'Episode') {
                    renderOneFavRow(favsEl, '\u2605 Top 4 Episodes', groups.Episode, 'Episode');
                } else if (_activeTab === 'Anime') {
                    // Anime items live inside the Movie/Series/Episode
                    // groups — pull anime out of each, cap at 4 total.
                    var animeItems = [];
                    ['Movie','Series','Episode'].forEach(function (t) {
                        (groups[t] || []).forEach(function (e) {
                            if (e.m && e.m.isAnime) animeItems.push(e);
                        });
                    });
                    renderOneFavRow(favsEl, '\u2605 Top 4 Anime', animeItems.slice(0, 4), 'Anime');
                }
            };

            if (fetchIds.length === 0) { renderGroups({}); return; }
            getItemsMeta(fetchIds).then(renderGroups);
        });
    }

    // Render a single favorites sub-row (Movies / Series / Episodes).
    // Always shows 4 slots; fills with placeholders for empty ones.
    function renderOneFavRow(parent, titleText, items, type, showWhenEmpty) {
        var wrap = document.createElement('div');
        wrap.className = 'ir-ov-favs-section';

        var title = document.createElement('div');
        title.className = 'ir-ov-favs-title';
        title.textContent = titleText;
        wrap.appendChild(title);

        var grid = document.createElement('div');
        grid.className = 'ir-ov-favs-grid';
        for (var i = 0; i < 4; i++) {
            var entry = items[i];
            if (!entry) {
                var ph = document.createElement('div');
                ph.className = 'ir-ov-fav-empty';
                var hint = type === 'Series'  ? 'pin a series'
                         : type === 'Episode' ? 'pin an episode'
                         : type === 'Anime'   ? 'pin an anime'
                         : 'pin a film';
                ph.innerHTML = '<div class="ir-ov-fav-empty-num">#' + (i + 1) + '</div>' +
                               '<div class="ir-ov-fav-empty-plus">+</div>' +
                               '<div class="ir-ov-fav-empty-hint">' + hint + '</div>';
                grid.appendChild(ph);
                continue;
            }
            var card = buildOverlayCard({
                itemId: entry.id,
                name: entry.m.name || entry.id,
                year: entry.m.year || 0,
                runtime: entry.m.runtime || 0,
                communityRating: entry.m.communityRating || 0,
                imageTag: entry.m.imageTag || null,
                type: entry.m.type || type
            }, { badge: '\u2605 #' + (i + 1), favSlotIndex: i });
            grid.appendChild(card);
        }
        wrap.appendChild(grid);
        parent.appendChild(wrap);
    }

    function openMyRatings() {
        var ov = ensureOverlay();
        _overlay.querySelector('.ir-ov-sort').value = _sortKey;
        // Reset filters
        _activeTab = 'all';
        _overlayView = 'films';
        _searchQuery = '';
        _starFilter = 'all';
        _currentListId = null;
        _watchlistScope = 'mine';
        _watchlistUserFilter = '';
        _everyoneUsers = [];
        var searchEl = _overlay.querySelector('.ir-ov-search');
        if (searchEl) searchEl.value = '';
        var starFilterEl = _overlay.querySelector('.ir-ov-starfilter');
        if (starFilterEl) starFilterEl.value = 'all';
        _overlay.querySelectorAll('.ir-ov-tab').forEach(function (t) { t.classList.remove('ir-ov-tab-active'); });
        var allTab = _overlay.querySelector('.ir-ov-tab[data-tab="all"]');
        if (allTab) allTab.classList.add('ir-ov-tab-active');
        _overlay.querySelectorAll('.ir-ov-view').forEach(function (v) { v.classList.remove('ir-ov-view-active'); });
        var filmsView = _overlay.querySelector('.ir-ov-view[data-view="films"]');
        if (filmsView) filmsView.classList.add('ir-ov-view-active');

        ov.classList.add('ir-ov-open');
        document.documentElement.classList.add('ir-ov-locked');
        document.body.classList.add('ir-ov-locked');
        document.documentElement.style.overflow = 'hidden';
        applyOverlayViewVisibility();
        loadOverlayView();
    }

    // Called by sync/import handlers after data changes to refresh the grid
    function loadMyRatings() { loadOverlayView(); }

    // ── Sidebar injection ─────────────────────────────────────────────────

    function injectSidebar() {
        if (document.getElementById('ir-nav-link')) return;

        // Strategy: find the parent of any existing navMenuSection elements.
        // This works regardless of whether the sidebar uses <nav> or a <div>.
        var existingSections = document.querySelectorAll('.navMenuSection');
        var container = null;
        if (existingSections.length) {
            container = existingSections[0].parentElement;
        }
        // Fallback: try known Jellyfin scroll-container selectors
        if (!container) {
            container = document.querySelector('.mainDrawer-scrollContainer')
                     || document.querySelector('.scrollContainer')
                     || document.querySelector('[class*="scrollContainer"]')
                     || document.querySelector('.mainDrawer')
                     || document.querySelector('#mainDrawer');
        }
        if (!container) return;

        var link = document.createElement('a');
        link.id = 'ir-nav-link';
        link.href = 'javascript:void(0)';
        link.className = 'navMenuOption emby-button';
        link.setAttribute('role', 'menuitem');
        link.innerHTML =
            '<span class="ir-nav-icon">\u2605</span>' +
            '<span class="ir-nav-text">My Ratings</span>';
        link.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            openMyRatings();
        });

        var section = document.createElement('div');
        section.id = 'ir-nav-section';
        section.className = 'navMenuSection';
        section.innerHTML = '<div class="sectionTitle" style="padding:16px 20px 4px;font-size:.72em;text-transform:uppercase;letter-spacing:.1em;color:rgba(255,255,255,.4);font-weight:600">StarTrack</div>';
        section.appendChild(link);

        // Insert before the "User" section (which contains Settings / Sign Out)
        var inserted = false;
        var allSections = container.querySelectorAll('.navMenuSection');
        for (var si = 0; si < allSections.length; si++) {
            var header = allSections[si].querySelector('.sectionTitle, .navSection, [class*="header"], [class*="Header"]');
            var htext = (header ? header.textContent : allSections[si].childNodes[0] && allSections[si].childNodes[0].textContent || '').trim().toLowerCase();
            if (htext === 'user' || htext === 'account') {
                container.insertBefore(section, allSections[si]);
                inserted = true; break;
            }
        }
        // Fallback: insert before the section containing "Sign Out"
        if (!inserted) {
            for (var sj = 0; sj < allSections.length; sj++) {
                var links = allSections[sj].querySelectorAll('a, button');
                for (var lk = 0; lk < links.length; lk++) {
                    if (/sign\s*out|log\s*out/i.test(links[lk].textContent)) {
                        container.insertBefore(section, allSections[sj]);
                        inserted = true; break;
                    }
                }
                if (inserted) break;
            }
        }
        if (!inserted) container.appendChild(section);
    }

    // ── Render: item view ─────────────────────────────────────────────────

    var _pendingStars = 0;

    function render(data) {
        var el = _el; if (!el) return;
        var myUid = getUserId();
        var total = (data && data.totalRatings) || 0;
        var avg   = (data && data.averageRating) || 0;

        var icon = el.querySelector('.ir-star-icon'), avgTxt = el.querySelector('.ir-avg-text'), lbl = el.querySelector('.ir-label');
        if (total > 0) {
            icon.textContent = '\u2605'; icon.style.opacity = '1';
            avgTxt.textContent = avg.toFixed(1); avgTxt.style.display = '';
            if (lbl) lbl.style.display = 'none';
        } else {
            icon.textContent = '\u2606'; icon.style.opacity = '0.5';
            avgTxt.style.display = 'none';
            if (lbl) { lbl.textContent = 'Rate'; lbl.style.color = ''; lbl.style.display = ''; }
        }

        el.querySelector('.ir-big-avg').textContent = total > 0 ? avg.toFixed(1) : '\u2013';
        el.querySelector('.ir-count').textContent   = '(' + total + ' rating' + (total !== 1 ? 's' : '') + ')';

        var ratings = (data && data.userRatings) || [];
        var myRat = null;
        if (myUid) {
            var myUidN = myUid.replace(/-/g, '');
            for (var i = 0; i < ratings.length; i++) {
                if (ratings[i].userId === myUid || ratings[i].userId === myUidN) { myRat = ratings[i]; break; }
            }
        }
        var myVal = myRat ? myRat.stars : 0;
        _pendingStars = myVal;
        setStarDisplay(myVal);

        var yc = el.querySelector('.ir-yc'), rb = el.querySelector('.ir-rb'), submit = el.querySelector('.ir-submit'), rev = el.querySelector('.ir-rev');
        yc.textContent     = myVal ? myVal.toFixed(1) + ' \u2605 selected' : '';
        rb.style.display   = myVal ? '' : 'none';
        submit.textContent = myVal ? '\u2605 Update Rating' : '\u2605 Save Rating';
        submit.classList.toggle('ir-ready', myVal > 0);
        if (rev) rev.value = (myRat && myRat.review) ? myRat.review : '';

        var listEl = el.querySelector('.ir-list');
        listEl.innerHTML = ratings.length
            ? ratings.map(function (r) {
                var hasRev = r.review && r.review.trim();
                return '<div class="ir-li">' +
                    '<div class="ir-li-top">' +
                        '<span class="ir-ln">' + esc(r.userName) + (hasRev ? ' \u2026' : '') + '</span>' +
                        '<span class="ir-ls">' + starsText(r.stars) + '</span>' +
                        '<span class="ir-lv">' + timeAgo(r.ratedAt) + '</span>' +
                    '</div>' +
                    (hasRev ? '<div class="ir-li-review">' + esc(r.review) + '</div>' : '') +
                '</div>';
              }).join('')
            : '<p class="ir-empty">No ratings yet \u2013 be the first!</p>';

        listEl.querySelectorAll('.ir-ln').forEach(function (nameEl) {
            nameEl.addEventListener('click', function () {
                var rev2 = nameEl.closest('.ir-li').querySelector('.ir-li-review');
                if (rev2) rev2.classList.toggle('ir-open');
            });
        });

        upsertPageBadge(data);
    }

    // ── Render: recent panel (home screen) ────────────────────────────────

    function renderRecent(items, isCommunity) {
        var el = _el; if (!el) return;
        var container = el.querySelector('.ir-rec-list');
        if (!items || !items.length) {
            container.innerHTML = '<p class="ir-empty">' +
                (isCommunity
                    ? esc(tr('widget.community_recent_empty', null, 'No ratings on this server yet — rate something to get started.'))
                    : esc(tr('widget.recent_empty', null, "You haven't rated anything yet."))) +
                '</p>';
            return;
        }
        var ids = items.map(function (r) { return r.itemId; });
        getItemsMeta(ids).then(function (meta) {
            container.innerHTML = items.map(function (r) {
                var m = meta[r.itemId] || {}, name = m.name || r.itemId;
                var byLine = isCommunity && r.userName
                    ? '<span class="ir-rec-by">' + esc(r.userName) + '</span> \u00b7 '
                    : '';
                return '<div class="ir-rec-item" data-id="' + esc(r.itemId) + '">' +
                    '<div class="ir-rec-info">' +
                        '<div class="ir-rec-name">' + esc(name) + '</div>' +
                        '<div class="ir-rec-meta">' + byLine + (m.year ? m.year + ' \u00b7 ' : '') + timeAgo(r.ratedAt) + '</div>' +
                        (r.review ? '<div class="ir-rec-rev">' + esc(r.review) + '</div>' : '') +
                    '</div>' +
                    '<span class="ir-rec-stars">' + r.stars.toFixed(1) + ' \u2605</span>' +
                '</div>';
            }).join('');
            container.querySelectorAll('.ir-rec-item').forEach(function (row) {
                row.addEventListener('click', function () {
                    var id = row.dataset.id;
                    if (id) { var p = _el && _el.querySelector('.ir-panel'); if (p) p.classList.remove('ir-open'); window.location.hash = '#!/details?id=' + id; }
                });
            });
        });
    }

    // ── Interactions ──────────────────────────────────────────────────────

    function bindInteractions(el) {
        bindLanguagePickers(el);

        var pill = el.querySelector('.ir-pill'), panel = el.querySelector('.ir-panel');
        var tb = el.querySelector('.ir-tb'), listEl = el.querySelector('.ir-list');
        var rb = el.querySelector('.ir-rb'), submit = el.querySelector('.ir-submit');
        var flash = el.querySelector('.ir-flash'), yc = el.querySelector('.ir-yc');
        var rev = el.querySelector('.ir-rev'), revHint = el.querySelector('.ir-rev-hint');
        var actLike  = el.querySelector('.ir-act-like');
        var actWatch = el.querySelector('.ir-act-watch');
        var actFav   = el.querySelector('.ir-act-fav');
        var open = false, listOpen = false;

        pill.addEventListener('click', function () {
            open = !open;
            panel.classList.toggle('ir-open', open);
            if (open) {
                // Always reset to the correct primary view when opening. Prevents
                // a stale Letterboxd sync view from being shown on re-open and
                // prevents an empty black panel if the user navigated away while
                // the lb-view was active.
                var lbEl = el.querySelector('.ir-lb-view');
                if (lbEl) lbEl.style.display = 'none';
                if (_curId) {
                    itemView.style.display   = '';
                    recentView.style.display = 'none';
                    apiGet(_curId).then(function (d) { if (d) render(d); });
                } else {
                    itemView.style.display   = 'none';
                    recentView.style.display = '';
                }
            }
        });
        document.addEventListener('click', function (e) {
            if (open && !el.contains(e.target)) {
                open = false;
                panel.classList.remove('ir-open');
                // Also collapse the Letterboxd view so the next open doesn't
                // show a stale state
                var lbEl = el.querySelector('.ir-lb-view');
                if (lbEl) lbEl.style.display = 'none';
            }
        });
        tb && tb.addEventListener('click', function () {
            listOpen = !listOpen;
            listEl.style.display = listOpen ? 'block' : 'none';
            tb.textContent = listOpen ? 'Hide ratings \u25b4' : 'Show all ratings \u25be';
        });

        if (rev) {
            rev.addEventListener('input', function () { if (revHint) revHint.textContent = rev.value.length + ' / 1000'; });
            rev.addEventListener('click', function (e) { e.stopPropagation(); });
        }

        el.querySelectorAll('.ir-sl, .ir-sr').forEach(function (zone) {
            var v = parseFloat(zone.dataset.v);
            zone.addEventListener('mouseenter', function () { setStarDisplay(v); });
            zone.addEventListener('mouseleave', function () { setStarDisplay(_pendingStars); });
            zone.addEventListener('click', function (e) {
                e.stopPropagation();
                _pendingStars = v;
                setStarDisplay(v);
                yc.textContent = v.toFixed(1) + ' \u2605 selected';
                submit.classList.add('ir-ready');
            });
        });

        submit.addEventListener('click', function () {
            var id = _curId; if (!id || !_pendingStars) return;
            submit.disabled = true;
            apiPost(id, _pendingStars, rev ? rev.value.trim() || null : null).then(function (ok) {
                submit.disabled = false;
                flash.textContent = ok ? '\u2713 Saved!' : '\u2717 Failed to save';
                flash.style.color = ok ? '#52b54b' : '#ff6060';
                flash.classList.add('ir-show');
                setTimeout(function () { flash.classList.remove('ir-show'); }, ok ? 1800 : 2500);
                if (ok) apiGet(id).then(function (d) { if (d) render(d); });
            });
        });

        rb.addEventListener('click', function () {
            var id = _curId; if (!id) return;
            apiDel(id).then(function (ok) {
                if (!ok) return;
                _pendingStars = 0; setStarDisplay(0);
                if (rev) rev.value = '';
                apiGet(id).then(function (d) { if (d) render(d); });
            });
        });

        // ── Action buttons: like, watchlist, favorite ───────────────────
        function toggleAct(btn, apiOn, apiOff) {
            var id = _curId; if (!id) return;
            var isOn = btn.classList.contains('ir-on');
            var fn = isOn ? apiOff : apiOn;
            fn(id).then(function (ok) {
                if (ok) btn.classList.toggle('ir-on', !isOn);
            });
        }

        if (actLike) actLike.addEventListener('click', function (e) {
            e.stopPropagation();
            toggleAct(actLike, apiLikeAdd, apiLikeRemove);
        });
        if (actWatch) actWatch.addEventListener('click', function (e) {
            e.stopPropagation();
            toggleAct(actWatch, apiWatchlistAdd, apiWatchlistRemove);
        });
        if (actFav) actFav.addEventListener('click', function (e) {
            e.stopPropagation();
            var id = _curId; if (!id) return;
            // Favorites are a max-4 list, not a free toggle. Fetch current
            // list, add or remove, push back.
            apiMyFavorites().then(function (favs) {
                favs = favs || [];
                var idx = favs.indexOf(id);
                if (idx >= 0) {
                    favs.splice(idx, 1);
                    apiSetFavorites(favs).then(function (ok) {
                        if (ok) actFav.classList.remove('ir-on');
                    });
                } else {
                    if (favs.length >= 4) {
                        if (!window.confirm('You already have 4 favorites. Replace the oldest one with this film?')) return;
                        favs.shift();
                    }
                    favs.push(id);
                    apiSetFavorites(favs).then(function (ok) {
                        if (ok) actFav.classList.add('ir-on');
                    });
                }
            });
        });

        // "View all →" button in recent panel
        var recOpenBtn = el.querySelector('.ir-rec-open-btn');
        if (recOpenBtn) recOpenBtn.addEventListener('click', function (e) { e.stopPropagation(); openMyRatings(); });

        // ── Letterboxd sync view ────────────────────────────────────────
        var lbView    = el.querySelector('.ir-lb-view');
        var itemView  = el.querySelector('.ir-item-view');
        var recentView= el.querySelector('.ir-recent-panel');
        var lbBack    = el.querySelector('.ir-lb-back');
        var lbUser    = el.querySelector('.ir-lb-user');
        var lbAuto    = el.querySelector('.ir-lb-auto');
        var lbSave    = el.querySelector('.ir-lb-save');
        var lbSync    = el.querySelector('.ir-lb-sync');
        var lbFile    = el.querySelector('.ir-lb-file');
        var lbStatus  = el.querySelector('.ir-lb-status');
        var lbOpenBtns = el.querySelectorAll('.ir-lb-open-btn');

        function showLbStatus(text, kind) {
            if (!lbStatus) return;
            lbStatus.textContent = text;
            lbStatus.classList.remove('ir-lb-ok', 'ir-lb-err');
            if (kind === 'ok')  lbStatus.classList.add('ir-lb-ok');
            if (kind === 'err') lbStatus.classList.add('ir-lb-err');
        }

        // Use _curId as the source of truth for "which view should we go
        // back to". Capturing prevView at openLbView() time was fragile
        // because the panel could close mid-state and lose the flag.
        function openLbView() {
            itemView.style.display   = 'none';
            recentView.style.display = 'none';
            lbView.style.display     = '';
            showLbStatus('', '');
            // Load current settings into the form
            apiLbGetSettings().then(function (s) {
                if (!s) return;
                if (lbUser) lbUser.value = s.username || '';
                if (lbAuto) lbAuto.checked = !!s.enableAutoSync;
                if (s.lastSyncedAt) {
                    showLbStatus('Last synced ' + timeAgo(s.lastSyncedAt) +
                        (s.lastImportedCount ? ' — imported ' + s.lastImportedCount + ' rating' + (s.lastImportedCount !== 1 ? 's' : '') : ''), '');
                }
            });
        }

        function closeLbView() {
            lbView.style.display = 'none';
            if (_curId) {
                itemView.style.display   = '';
                recentView.style.display = 'none';
            } else {
                itemView.style.display   = 'none';
                recentView.style.display = '';
            }
        }

        // Both the item view and the recent view have their own "⚙ Letterboxd sync"
        // footer button. Wire them all to openLbView.
        lbOpenBtns.forEach(function (btn) {
            btn.addEventListener('click', function (e) { e.stopPropagation(); openLbView(); });
        });
        if (lbBack) lbBack.addEventListener('click', function (e) { e.stopPropagation(); closeLbView(); });

        // Stop clicks inside the form from bubbling and closing the whole panel
        lbView && lbView.addEventListener('click', function (e) { e.stopPropagation(); });

        if (lbSave) {
            lbSave.addEventListener('click', function (e) {
                e.stopPropagation();
                var u = (lbUser.value || '').trim();
                var auto = !!lbAuto.checked;
                if (auto && !u) { showLbStatus('Enter your Letterboxd username first.', 'err'); return; }
                lbSave.disabled = true;
                apiLbSaveSettings(u, auto).then(function (ok) {
                    lbSave.disabled = false;
                    showLbStatus(ok ? 'Saved.' : 'Save failed.', ok ? 'ok' : 'err');
                });
            });
        }

        function smallLbResultMsg(r) {
            var parts = ['Imported ' + (r.imported || 0)];
            if (r.updated)   parts.push('updated ' + r.updated);
            if (r.unmatched) parts.push(r.unmatched + ' not in library');
            var lib = r.libraryMovieCount != null
                ? ' · lib: ' + r.libraryMovieCount
                : '';
            return parts.join(', ') + '.' + lib;
        }

        if (lbSync) {
            lbSync.addEventListener('click', function (e) {
                e.stopPropagation();
                if (!(lbUser.value || '').trim()) {
                    showLbStatus('Save a Letterboxd username first.', 'err'); return;
                }
                lbSync.disabled = true;
                showLbStatus('Syncing from Letterboxd\u2026', '');
                apiLbSyncNow().then(function (r) {
                    lbSync.disabled = false;
                    if (!r) { showLbStatus('Sync failed.', 'err'); return; }
                    if (r.error) { showLbStatus(r.error, 'err'); return; }
                    var total = (r.imported || 0) + (r.updated || 0);
                    var msg = total > 0 ? smallLbResultMsg(r) : 'Nothing new on Letterboxd right now.';
                    showLbStatus(msg, 'ok');
                });
            });
        }

        if (lbFile) {
            lbFile.addEventListener('change', function (e) {
                e.stopPropagation();
                var file = lbFile.files && lbFile.files[0];
                if (!file) return;
                if (file.size > 5 * 1024 * 1024) {
                    showLbStatus('File is too large (max 5 MB).', 'err');
                    lbFile.value = '';
                    return;
                }
                showLbStatus('Importing ' + file.name + '\u2026', '');
                var reader = new FileReader();
                reader.onload = function () {
                    apiLbImportBytes(reader.result, file.name).then(function (r) {
                        lbFile.value = '';
                        if (!r) { showLbStatus('Import failed.', 'err'); return; }
                        if (r.error) { showLbStatus(r.error, 'err'); return; }
                        var msg = 'Imported ' + (r.imported || 0) + ' new, ' +
                                  'updated ' + (r.updated || 0) + '. ' +
                                  (r.unmatched || 0) + ' not in library, ' +
                                  (r.ambiguous || 0) + ' ambiguous.';
                        showLbStatus(msg, 'ok');
                    });
                };
                reader.onerror = function () { showLbStatus('Could not read file.', 'err'); };
                // Read as ArrayBuffer so both ZIP and CSV work — the server
                // auto-detects the format from the bytes.
                reader.readAsArrayBuffer(file);
            });
        }
    }

    // ── Letterboxd API helpers ────────────────────────────────────────────

    function apiLbGetSettings() {
        var auth = getAuth(); if (!auth) return Promise.resolve(null);
        return fetch('/Plugins/StarTrack/Letterboxd/Settings', { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .catch(function () { return null; });
    }

    function apiLbSaveSettings(username, enableAutoSync) {
        var auth = getAuth(); if (!auth) return Promise.resolve(false);
        return fetch('/Plugins/StarTrack/Letterboxd/Settings', {
            method: 'POST',
            headers: { Authorization: auth, 'Content-Type': 'application/json' },
            body: JSON.stringify({ username: username, enableAutoSync: enableAutoSync })
        }).then(function (r) { return r.ok; }).catch(function () { return false; });
    }

    function apiLbSyncNow() {
        var auth = getAuth(); if (!auth) return Promise.resolve(null);
        return fetch('/Plugins/StarTrack/Letterboxd/SyncNow', {
            method: 'POST',
            headers: { Authorization: auth }
        }).then(function (r) { return r.ok ? r.json() : null; })
          .catch(function () { return null; });
    }

    function apiLbDiagnose() {
        var auth = getAuth(); if (!auth) return Promise.resolve(null);
        return fetch('/Plugins/StarTrack/Letterboxd/Diagnose', {
            headers: { Authorization: auth }
        }).then(function (r) { return r.ok ? r.json() : null; })
          .catch(function () { return null; });
    }

    function apiLbCleanup() {
        var auth = getAuth(); if (!auth) return Promise.resolve(null);
        return fetch('/Plugins/StarTrack/Letterboxd/Cleanup', {
            method: 'POST',
            headers: { Authorization: auth }
        }).then(function (r) { return r.ok ? r.json() : null; })
          .catch(function () { return null; });
    }

    // ── Interactions API (watchlist / likes / favorites / recommendations) ─

    function apiInteractionStatus(itemId) {
        var auth = getAuth(); if (!auth) return Promise.resolve(null);
        return fetch('/Plugins/StarTrack/Interactions/' + encodeURIComponent(itemId), {
            headers: { Authorization: auth }
        }).then(function (r) { return r.ok ? r.json() : null; })
          .catch(function () { return null; });
    }

    function apiWatchlistAdd(itemId) {
        var auth = getAuth(); if (!auth) return Promise.resolve(false);
        return fetch('/Plugins/StarTrack/Watchlist/' + encodeURIComponent(itemId), {
            method: 'POST', headers: { Authorization: auth }
        }).then(function (r) { return r.ok; }).catch(function () { return false; });
    }

    function apiWatchlistRemove(itemId) {
        var auth = getAuth(); if (!auth) return Promise.resolve(false);
        return fetch('/Plugins/StarTrack/Watchlist/' + encodeURIComponent(itemId), {
            method: 'DELETE', headers: { Authorization: auth }
        }).then(function (r) { return r.ok; }).catch(function () { return false; });
    }

    function apiLikeAdd(itemId) {
        var auth = getAuth(); if (!auth) return Promise.resolve(false);
        return fetch('/Plugins/StarTrack/Likes/' + encodeURIComponent(itemId), {
            method: 'POST', headers: { Authorization: auth }
        }).then(function (r) { return r.ok; }).catch(function () { return false; });
    }

    function apiLikeRemove(itemId) {
        var auth = getAuth(); if (!auth) return Promise.resolve(false);
        return fetch('/Plugins/StarTrack/Likes/' + encodeURIComponent(itemId), {
            method: 'DELETE', headers: { Authorization: auth }
        }).then(function (r) { return r.ok; }).catch(function () { return false; });
    }

    function apiMyWatchlist() {
        var auth = getAuth(); if (!auth) return Promise.resolve([]);
        return fetch('/Plugins/StarTrack/MyWatchlist', { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : []; })
            .catch(function () { return []; });
    }

    function apiEveryonesWatchlist() {
        var auth = getAuth(); if (!auth) return Promise.resolve([]);
        return fetch('/Plugins/StarTrack/EveryonesWatchlist', { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : []; })
            .catch(function () { return []; });
    }

    function apiMyLikes() {
        var auth = getAuth(); if (!auth) return Promise.resolve([]);
        return fetch('/Plugins/StarTrack/MyLikes', { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : []; })
            .catch(function () { return []; });
    }

    function apiMyFavorites() {
        var auth = getAuth(); if (!auth) return Promise.resolve([]);
        return fetch('/Plugins/StarTrack/MyFavorites', { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : []; })
            .catch(function () { return []; });
    }

    function apiSetFavorites(itemIds) {
        var auth = getAuth(); if (!auth) return Promise.resolve(false);
        return fetch('/Plugins/StarTrack/MyFavorites', {
            method: 'POST',
            headers: { Authorization: auth, 'Content-Type': 'application/json' },
            body: JSON.stringify({ itemIds: itemIds })
        }).then(function (r) { return r.ok; }).catch(function () { return false; });
    }

    function apiRecommendations(limit) {
        var auth = getAuth(); if (!auth) return Promise.resolve([]);
        return fetch('/Plugins/StarTrack/Recommendations?limit=' + (limit || 60), {
            headers: { Authorization: auth }
        }).then(function (r) { return r.ok ? r.json() : []; })
          .catch(function () { return []; });
    }

    function apiMyDiary() {
        var auth = getAuth(); if (!auth) return Promise.resolve([]);
        return fetch('/Plugins/StarTrack/MyDiary', { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : []; })
            .catch(function () { return []; });
    }

    function apiListMembers() {
        var auth = getAuth(); if (!auth) return Promise.resolve([]);
        return fetch('/Plugins/StarTrack/Members', { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : []; })
            .catch(function () { return []; });
    }

    function apiMemberProfile(userId) {
        var auth = getAuth(); if (!auth) return Promise.resolve(null);
        return fetch('/Plugins/StarTrack/Members/' + encodeURIComponent(userId) + '/Profile', { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .catch(function () { return null; });
    }

    function apiMemberStats(userId) {
        var auth = getAuth(); if (!auth) return Promise.resolve(null);
        return fetch('/Plugins/StarTrack/Members/' + encodeURIComponent(userId) + '/Stats', { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .catch(function () { return null; });
    }

    function apiMemberReviews(userId) {
        var auth = getAuth(); if (!auth) return Promise.resolve([]);
        return fetch('/Plugins/StarTrack/Members/' + encodeURIComponent(userId) + '/Reviews', { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : []; })
            .catch(function () { return []; });
    }

    function apiActivityFeed(limit) {
        var auth = getAuth(); if (!auth) return Promise.resolve([]);
        return fetch('/Plugins/StarTrack/Activity?limit=' + (limit || 100) + '&scope=' + (_activityScope || 'following'), { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : []; })
            .catch(function () { return []; });
    }

    function apiCompare(aId, bId) {
        var auth = getAuth(); if (!auth) return Promise.resolve(null);
        return fetch('/Plugins/StarTrack/Compare?a=' + encodeURIComponent(aId) + '&b=' + encodeURIComponent(bId), { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .catch(function () { return null; });
    }

    function apiFollow(userId) {
        var auth = getAuth(); if (!auth) return Promise.resolve(null);
        return fetch('/Plugins/StarTrack/Members/' + encodeURIComponent(userId) + '/Follow', {
            method: 'POST', headers: { Authorization: auth }
        }).catch(function () { return null; });
    }

    function apiUnfollow(userId) {
        var auth = getAuth(); if (!auth) return Promise.resolve(null);
        return fetch('/Plugins/StarTrack/Members/' + encodeURIComponent(userId) + '/Unfollow', {
            method: 'POST', headers: { Authorization: auth }
        }).catch(function () { return null; });
    }

    // tab is 'followers' or 'following'.
    function apiFollowList(userId, tab) {
        var auth = getAuth(); if (!auth) return Promise.resolve([]);
        var path = tab === 'followers' ? 'Followers' : 'Following';
        return fetch('/Plugins/StarTrack/Members/' + encodeURIComponent(userId) + '/' + path, { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : []; })
            .catch(function () { return []; });
    }

    function apiGetPrivacy() {
        var auth = getAuth(); if (!auth) return Promise.resolve({ hideFromMembers: false, hideFollowerCount: false });
        return fetch('/Plugins/StarTrack/MyPrivacy', { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : { hideFromMembers: false, hideFollowerCount: false }; })
            .catch(function () { return { hideFromMembers: false, hideFollowerCount: false }; });
    }

    function apiSetPrivacy(hideFromMembers, hideFollowerCount, hideFollowing, hideStats, hideActivity) {
        var auth = getAuth(); if (!auth) return Promise.resolve(null);
        return fetch('/Plugins/StarTrack/MyPrivacy', {
            method: 'POST',
            headers: { Authorization: auth, 'Content-Type': 'application/json' },
            body: JSON.stringify({
                hideFromMembers: !!hideFromMembers,
                hideFollowerCount: !!hideFollowerCount,
                hideFollowing: !!hideFollowing,
                hideStats: !!hideStats,
                hideActivity: !!hideActivity
            })
        }).catch(function () { return null; });
    }

    function apiRecent(limit) {
        var auth = getAuth(); if (!auth) return Promise.resolve([]);
        return fetch('/Plugins/StarTrack/Recent?limit=' + (limit || 100), {
            headers: { Authorization: auth }
        }).then(function (r) { return r.ok ? r.json() : []; })
          .catch(function () { return []; });
    }

    function apiScrapeFavorites() {
        var auth = getAuth(); if (!auth) return Promise.resolve(null);
        return fetch('/Plugins/StarTrack/Letterboxd/ScrapeFavorites', {
            method: 'POST', headers: { Authorization: auth }
        }).then(function (r) { return r.ok ? r.json() : null; })
          .catch(function () { return null; });
    }

    // Lists API
    function apiGetLists() {
        var auth = getAuth(); if (!auth) return Promise.resolve([]);
        return fetch('/Plugins/StarTrack/Lists', { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : []; })
            .catch(function () { return []; });
    }

    function apiCreateList(name, description, collaborative) {
        var auth = getAuth(); if (!auth) return Promise.resolve(null);
        return fetch('/Plugins/StarTrack/Lists', {
            method: 'POST',
            headers: { Authorization: auth, 'Content-Type': 'application/json' },
            body: JSON.stringify({ name: name, description: description, collaborative: collaborative })
        }).then(function (r) { return r.ok ? r.json() : null; })
          .catch(function () { return null; });
    }

    function apiDeleteList(listId) {
        var auth = getAuth(); if (!auth) return Promise.resolve(false);
        return fetch('/Plugins/StarTrack/Lists/' + encodeURIComponent(listId), {
            method: 'DELETE', headers: { Authorization: auth }
        }).then(function (r) { return r.ok; }).catch(function () { return false; });
    }

    function apiAddToList(listId, itemId) {
        var auth = getAuth(); if (!auth) return Promise.resolve(false);
        return fetch('/Plugins/StarTrack/Lists/' + encodeURIComponent(listId) + '/Items', {
            method: 'POST',
            headers: { Authorization: auth, 'Content-Type': 'application/json' },
            body: JSON.stringify({ itemId: itemId })
        }).then(function (r) { return r.ok; }).catch(function () { return false; });
    }

    function apiRemoveFromList(listId, itemId) {
        var auth = getAuth(); if (!auth) return Promise.resolve(false);
        return fetch('/Plugins/StarTrack/Lists/' + encodeURIComponent(listId) + '/Items/' + encodeURIComponent(itemId), {
            method: 'DELETE', headers: { Authorization: auth }
        }).then(function (r) { return r.ok; }).catch(function () { return false; });
    }

    function apiLbImportCsv(csvText) {
        var auth = getAuth(); if (!auth) return Promise.resolve(null);
        return fetch('/Plugins/StarTrack/Letterboxd/Import', {
            method: 'POST',
            headers: { Authorization: auth, 'Content-Type': 'text/csv' },
            body: csvText
        }).then(function (r) { return r.ok ? r.json() : null; })
          .catch(function () { return null; });
    }

    // Uploads raw bytes (ZIP or CSV) — the server detects the format by
    // magic bytes and extracts ratings.csv from the ZIP if needed. Used
    // for the Letterboxd export ZIP which users drop in directly without
    // needing to unzip it first.
    function apiLbImportBytes(bytes, filename) {
        var auth = getAuth(); if (!auth) return Promise.resolve(null);
        var looksZip = /\.zip$/i.test(filename || '');
        return fetch('/Plugins/StarTrack/Letterboxd/Import', {
            method: 'POST',
            headers: {
                Authorization: auth,
                'Content-Type': looksZip ? 'application/zip' : 'text/csv'
            },
            body: bytes
        }).then(function (r) { return r.ok ? r.json() : null; })
          .catch(function () { return null; });
    }

    // ── Show / hide ───────────────────────────────────────────────────────

    var _curId = null;

    function showItem(itemId) {
        var el = ensureEl();
        el.querySelector('.ir-item-view').style.display = '';
        el.querySelector('.ir-recent-panel').style.display = 'none';
        var lbEl = el.querySelector('.ir-lb-view'); if (lbEl) lbEl.style.display = 'none';

        // Load interaction state so the heart/watchlist/fav buttons reflect
        // the real stored state when the panel opens for this item.
        apiInteractionStatus(itemId).then(function (s) {
            if (!s || _curId !== itemId) return;
            var aLike  = el.querySelector('.ir-act-like');
            var aWatch = el.querySelector('.ir-act-watch');
            var aFav   = el.querySelector('.ir-act-fav');
            if (aLike)  aLike .classList.toggle('ir-on', !!s.liked);
            if (aWatch) aWatch.classList.toggle('ir-on', !!s.watchlisted);
            if (aFav)   aFav  .classList.toggle('ir-on', !!s.favorite);
        });

        var lbl = el.querySelector('.ir-label');
        if (lbl) { lbl.textContent = 'Rate'; lbl.style.color = ''; }

        if (_curId !== itemId) {
            _curId = itemId; _pendingStars = 0;
            var panel = el.querySelector('.ir-panel'); if (panel) panel.classList.remove('ir-open');
            var tbEl = el.querySelector('.ir-tb'); if (tbEl) tbEl.textContent = 'Show all ratings \u25be';
            var listEl = el.querySelector('.ir-list'); if (listEl) listEl.style.display = 'none';
            var revEl = el.querySelector('.ir-rev'); if (revEl) revEl.value = '';
            apiGet(itemId).then(function (d) { render(d || { totalRatings: 0, averageRating: 0, userRatings: [] }); });
        }
        el.classList.add('ir-on');
    }

    function showRecent() {
        var el = ensureEl();
        el.querySelector('.ir-item-view').style.display = 'none';
        el.querySelector('.ir-recent-panel').style.display = '';
        var lbEl = el.querySelector('.ir-lb-view'); if (lbEl) lbEl.style.display = 'none';

        var icon = el.querySelector('.ir-star-icon'), avgTxt = el.querySelector('.ir-avg-text'), lbl = el.querySelector('.ir-label');
        if (icon) icon.textContent = '\u2605';
        if (avgTxt) avgTxt.style.display = 'none';
        if (lbl) { lbl.textContent = 'Recent'; lbl.style.color = 'rgba(244,196,48,.8)'; lbl.style.display = ''; }

        _curId = null;
        el.classList.add('ir-on');

        // Community mode: show every user's recent ratings server-wide.
        // Per-user mode (default): only the current user's own ratings.
        var recTitleEl = el.querySelector('.ir-rec-title span');
        if (_STARTRACK_CONFIG.communityRecentMode) {
            if (recTitleEl) recTitleEl.textContent = tr('widget.community_recent_ratings', null, 'Community recent ratings');
            apiRecent(15).then(function (items) { renderRecent(items || [], true); });
        } else {
            if (recTitleEl) recTitleEl.textContent = tr('widget.your_recent_ratings', null, 'Your recent ratings');
            apiMyRatings(15).then(function (items) { renderRecent(items || [], false); });
        }
    }

    function hide() {
        if (_el) {
            _el.classList.remove('ir-on');
            var p = _el.querySelector('.ir-panel'); if (p) p.classList.remove('ir-open');
        }
        _curId = null;
        var b = document.getElementById('ir-page-badge'); if (b) b.remove();
    }

    // ── Navigation ────────────────────────────────────────────────────────

    function isVideoPlayerPage() {
        var hash = window.location.hash;
        if (/videoosd|nowplaying|\/video\b/i.test(hash)) return true;
        if (document.getElementById('videoOsdPage')) return true;
        if (document.querySelector('.videoOsdPage, .videoOsd, .htmlVideoPlayerContainer')) return true;
        // Heuristic: a playing <video> element exists and has a src
        var vid = document.querySelector('video');
        if (vid && vid.readyState >= 2 && !vid.paused) return true;
        return false;
    }

    // Hide the rating pill on admin dashboard, server settings, and
    // per-user preferences pages. Users never need to rate anything
    // from these screens and the pill just clutters them.
    function isAdminOrDashboardPage() {
        var hash = window.location.hash || '';
        // Jellyfin admin dashboard routes all share the /dashboard prefix,
        // and user preferences pages all start with mypreferences*. Metadata
        // editor, scheduled tasks, plugin pages, library settings, API keys,
        // user edit and server activity are all under /dashboard.
        if (/#!?\/dashboard(\.html|\/|$)/i.test(hash)) return true;
        if (/#!?\/mypreferences/i.test(hash)) return true;
        if (/#!?\/(metadata|scheduledtasks|plugins|library|apikeys|useredit|serveractivity|notificationsettings|addserver|wizard)/i.test(hash)) return true;
        // DOM fallback: Jellyfin wraps admin views in .dashboardGeneralForm
        // or pages with class "page type-interior" which is used for all
        // admin/settings views.
        if (document.querySelector('.page.type-interior:not(.hide)')) return true;
        return false;
    }

    // Hide the pill and sidebar entry on any pre-auth page: login,
    // server-picker, wizard, quick-connect. Also hide if we simply
    // don't have a logged-in user yet — covers the gap between
    // page load and ApiClient init on brand-new sessions.
    function isPreAuthPage() {
        var hash = (window.location.hash || '').toLowerCase();
        var path = (window.location.pathname || '').toLowerCase();
        var combined = path + ' ' + hash;
        if (combined.indexOf('/login.html') >= 0) return true;
        if (combined.indexOf('#!/login') >= 0) return true;
        if (combined.indexOf('#/login') >= 0) return true;
        if (combined.indexOf('#!/addserver') >= 0) return true;
        if (combined.indexOf('#!/selectserver') >= 0) return true;
        if (combined.indexOf('#!/wizard') >= 0) return true;
        if (combined.indexOf('/quickconnect') >= 0) return true;
        if (document.body && document.body.classList && document.body.classList.contains('loginPage')) return true;
        // No credentials yet → treat as pre-auth so nothing renders
        if (!getUserId()) return true;
        return false;
    }

    var _lastId = '', _lastHash = '';

    // Sentinel hash used when we're on a filtered page (video player or admin).
    // Setting _lastHash to this forces the next checkNav() to re-evaluate
    // and re-show the pill when the user navigates back to a normal page,
    // instead of early-returning because the hash "matches" a stale cache.
    var FILTERED_SENTINEL = '__ab_filtered__';

    function checkNav() {
        // Pre-auth gate: on the login / server-pick / wizard pages we have
        // no user context, so neither the pill nor the sidebar entry should
        // exist. Remove anything we've already injected and bail early.
        if (isPreAuthPage()) {
            hide();
            _lastHash = FILTERED_SENTINEL;
            _lastId = '';
            var navLink = document.getElementById('ir-nav-link');
            if (navLink && navLink.parentNode) navLink.parentNode.removeChild(navLink);
            var navSection = document.getElementById('ir-nav-section');
            if (navSection && navSection.parentNode) navSection.parentNode.removeChild(navSection);
            return;
        }

        injectSidebar();

        // Never show rating pill while watching video
        if (isVideoPlayerPage()) { hide(); _lastHash = FILTERED_SENTINEL; _lastId = ''; return; }

        // Never show on admin dashboard or user preferences pages
        if (isAdminOrDashboardPage()) { hide(); _lastHash = FILTERED_SENTINEL; _lastId = ''; return; }

        var id    = getItemId();
        var idStr = id || '';
        var hash  = window.location.hash + window.location.search;

        if (idStr === _lastId && hash === _lastHash) return;
        _lastId = idStr; _lastHash = hash;

        if (!id) {
            // Per-user preference takes priority over admin defaults.
            if (_userPrefs().hideRatePill) { hide(); return; }
            // Admin can hide the Recent floating button, and can also require
            // the Rate button to only appear inside media items (i.e. hide it
            // everywhere that no item id is in the URL).
            if (_STARTRACK_CONFIG.hideRecentButton || _STARTRACK_CONFIG.rateButtonOnlyInMediaItem) {
                hide();
                return;
            }
            showRecent();
            return;
        }
        // On detail pages, honour the user's hide-pill preference too
        if (_userPrefs().hideRatePill) { hide(); return; }

        console.log('[StarTrack] item:', id);
        showItem(id);
        getItemType(id).then(function (type) {
            if (id !== _curId) return;
            if (type !== null && type !== 'Movie' && type !== 'Series' && type !== 'Episode') hide();
        });
    }

    function init() {
        var start = function () {
            // Load /public-config + translations in parallel with widget boot
            loadPublicConfig().then(function () {
                var lang = _userLangOverride() || _STARTRACK_CONFIG.language || 'en';
                return loadEnglishThenActive(lang);
            }).then(function () {
                _STARTRACK_READY = true;
                startI18nWatchdog();
                applyConfigVisibility();
                // Replace native ratings on media details
                try { startMediaDetailsReplace(); } catch (e) {}
                try { startPosterBadges(); } catch (e) {}
                try { startMediaBarReplace(); } catch (e) {}
                try { startPostPlaybackPopup(); } catch (e) {}
            });

            setInterval(checkNav, 800);
            window.addEventListener('hashchange', function () { setTimeout(checkNav, 200); });
            window.addEventListener('popstate',   function () { setTimeout(checkNav, 200); });
            checkNav();
        };
        if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
        else start();
    }

    // ── Admin-toggle enforcement ─────────────────────────────────────────
    // applyConfigVisibility() hides pieces of the widget based on server
    // config. It's called on boot + whenever ensureEl mounts.

    function bindLanguagePickers(root) {
        var btns = root.querySelectorAll('.ir-lang-btn');
        if (!btns.length) return;
        var langs = _STARTRACK_CONFIG.supportedLanguages || ['en','fr','es','de','it','pt','zh','ja'];
        var labelMap = { en: 'EN', fr: 'FR', es: 'ES', de: 'DE', it: 'IT', pt: 'PT', zh: '中', ja: '日' };
        var nameMap  = { en: 'English', fr: 'Français', es: 'Español', de: 'Deutsch', it: 'Italiano', pt: 'Português', zh: '简体中文', ja: '日本語' };

        function currentLang() {
            return _userLangOverride() || _STARTRACK_CONFIG.language || 'en';
        }
        function refreshLabel() {
            var c = currentLang();
            root.querySelectorAll('.ir-lang-label').forEach(function (l) {
                l.textContent = labelMap[c] || c.toUpperCase();
            });
        }
        refreshLabel();

        btns.forEach(function (btn) {
            btn.addEventListener('click', function (ev) {
                ev.stopPropagation();
                var existing = document.getElementById('ir-lang-menu');
                if (existing) { existing.remove(); return; }
                // Recompute each time the menu opens so the tick reflects
                // the currently active language, not the one at bind time.
                var cur = currentLang();
                var menu = document.createElement('div');
                menu.id = 'ir-lang-menu';
                menu.style.cssText = 'position:fixed;z-index:2147483647;background:#1a1c23;color:#fff;padding:6px;border-radius:8px;box-shadow:0 6px 20px rgba(0,0,0,.5);border:1px solid rgba(255,255,255,.12);min-width:160px';
                langs.forEach(function (l) {
                    var item = document.createElement('div');
                    var isActive = (l === cur);
                    item.style.cssText = 'padding:7px 12px;border-radius:4px;cursor:pointer;font-size:.9em;display:flex;align-items:center;justify-content:space-between;gap:10px;' +
                        (isActive ? 'background:rgba(244,196,48,.16);color:#f4c430;font-weight:600;' : '');
                    item.innerHTML =
                        '<span>' + (nameMap[l] || l) + '</span>' +
                        (isActive ? '<span style="color:#f4c430">\u2713</span>' : '<span></span>');
                    item.addEventListener('click', function () {
                        menu.remove();
                        window.StarTrackSetLanguage(l);
                        refreshLabel();
                    });
                    item.addEventListener('mouseenter', function () {
                        if (!isActive) item.style.background = 'rgba(255,255,255,.08)';
                    });
                    item.addEventListener('mouseleave', function () {
                        if (!isActive) item.style.background = '';
                    });
                    menu.appendChild(item);
                });
                document.body.appendChild(menu);
                var r = btn.getBoundingClientRect();
                menu.style.left = Math.max(8, Math.min(window.innerWidth - menu.offsetWidth - 8, r.left)) + 'px';
                menu.style.top  = Math.max(8, r.top - menu.offsetHeight - 6) + 'px';
                var off = function (e) {
                    if (!menu.contains(e.target)) { menu.remove(); document.removeEventListener('click', off); }
                };
                setTimeout(function () { document.addEventListener('click', off); }, 0);
            });
        });
    }

    function applyConfigVisibility() {
        var el = document.getElementById('ir-widget');
        if (!el) return;
        // Hide the floating Letterboxd-sync buttons system-wide
        if (_STARTRACK_CONFIG.hideLetterboxdButton) {
            el.querySelectorAll('.ir-lb-open-btn').forEach(function (b) { b.style.display = 'none'; });
        } else {
            el.querySelectorAll('.ir-lb-open-btn').forEach(function (b) { b.style.display = ''; });
        }
    }

    // ── StarTrack rating swap into Media Details page ────────────────────
    // Jellyfin renders the native community rating in the detail page
    // "itemMiscInfo" / ".starRatingContainer" area. Swap it with the
    // StarTrack average when one is available.

    function startMediaDetailsReplace() {
        if (!_STARTRACK_CONFIG.replaceMediaDetailsRating) return;
        var lastId = '';
        setInterval(function () {
            try {
                var id = getItemId();
                if (!id || id === lastId) return;
                // Only when on a details page, not an overlay/play view
                var page = document.querySelector('.itemDetailPage:not(.hide), .detailPage:not(.hide)');
                if (!page) return;
                lastId = id;
                apiGet(id).then(function (d) {
                    if (!d || !d.totalRatings) return;
                    // Find the community rating cluster Jellyfin uses.
                    var hosts = page.querySelectorAll('.starRatingContainer, .itemMiscInfo, .starRating');
                    hosts.forEach(function (host) {
                        // Remove any prior StarTrack badge on this host
                        var prev = host.querySelector('.ir-detail-st'); if (prev) prev.remove();
                        var badge = document.createElement('span');
                        badge.className = 'ir-detail-st';
                        badge.style.cssText = 'display:inline-flex;align-items:center;gap:.3em;margin-left:.6em;padding:.15em .5em;border-radius:999px;background:rgba(244,196,48,.15);color:#f4c430;font-weight:600';
                        badge.textContent = '\u2605 ' + d.averageRating.toFixed(1) + ' StarTrack (' + d.totalRatings + ')';
                        badge.title = 'StarTrack community rating';
                        host.appendChild(badge);
                    });
                });
            } catch (e) {}
        }, 1200);
    }

    // ── Poster-overlay rating badges ─────────────────────────────────────
    // Scan all item cards on the current page, look up StarTrack ratings
    // for each, and stamp a small badge in the corner.

    function startPosterBadges() {
        if (!_STARTRACK_CONFIG.showRatingsOnPosters) return;
        setInterval(scanPostersOnce, 1500);
    }

    function _extractItemId(el) {
        // Jellyfin stores the item id in multiple attributes/locations
        // depending on card kind. Check each, return the first that
        // looks like a valid Jellyfin item id (a 32-char hex string —
        // sometimes with dashes, sometimes without).
        var candidates = [];
        var selectors = ['[data-id]', '[data-itemid]', '[data-item-id]', 'a[href*="id="]'];
        for (var i = 0; i < selectors.length; i++) {
            var nodes = el.matches(selectors[i]) ? [el] : Array.prototype.slice.call(el.querySelectorAll(selectors[i]));
            for (var j = 0; j < nodes.length; j++) {
                var n = nodes[j];
                candidates.push(n.getAttribute('data-id'));
                candidates.push(n.getAttribute('data-itemid'));
                candidates.push(n.getAttribute('data-item-id'));
                var href = n.getAttribute('href') || '';
                var m = href.match(/[?&#]id=([a-f0-9-]{20,})/i);
                if (m) candidates.push(m[1]);
            }
        }
        for (var k = 0; k < candidates.length; k++) {
            var c = candidates[k];
            if (!c) continue;
            var cleaned = c.replace(/-/g, '').toLowerCase();
            if (/^[a-f0-9]{32}$/.test(cleaned)) return cleaned;
        }
        return null;
    }

    function scanPostersOnce() {
        try {
            // Include both .card (classic Jellyfin) and .listItem variants.
            var cards = document.querySelectorAll('.card:not([data-ir-scanned]), .listItem:not([data-ir-scanned])');
            if (!cards.length) return;
            cards.forEach(function (card) {
                card.setAttribute('data-ir-scanned', '1');
                var id = _extractItemId(card);
                if (!id) return;

                apiGet(id).then(function (d) {
                    if (!d || !d.totalRatings) return;
                    // Prefer the image container so the badge overlays the poster
                    var host = card.querySelector('.cardImageContainer') ||
                               card.querySelector('.cardPadder') ||
                               card.querySelector('.listItemImage') ||
                               card;
                    if (host.querySelector('.ir-poster-badge')) return;
                    var b = document.createElement('span');
                    b.className = 'ir-poster-badge';
                    b.style.cssText = 'position:absolute;top:6px;right:6px;padding:3px 8px;border-radius:999px;background:rgba(0,0,0,.82);color:#f4c430;font-size:.75em;font-weight:700;z-index:5;pointer-events:none;line-height:1.25;letter-spacing:.02em;box-shadow:0 2px 6px rgba(0,0,0,.5);backdrop-filter:blur(2px);-webkit-backdrop-filter:blur(2px)';
                    b.textContent = '\u2605 ' + d.averageRating.toFixed(1);
                    if (getComputedStyle(host).position === 'static') host.style.position = 'relative';
                    host.appendChild(b);
                }).catch(function () {});
            });
        } catch (e) {}
    }

    // ── Media Bar plugin rating replacement ──────────────────────────────
    // The Media Bar plugin renders rows with its own rating pill. If present,
    // swap the text to the StarTrack average for that item.

    function startMediaBarReplace() {
        if (!_STARTRACK_CONFIG.replaceMediaBarRating) return;
        setInterval(function () {
            try {
                var pills = document.querySelectorAll('.mediabar-rating[data-id]:not([data-ir-mb])');
                pills.forEach(function (p) {
                    p.setAttribute('data-ir-mb', '1');
                    var id = p.getAttribute('data-id');
                    if (!id) return;
                    apiGet(id).then(function (d) {
                        if (!d || !d.totalRatings) return;
                        p.textContent = '\u2605 ' + d.averageRating.toFixed(1);
                        p.title = 'StarTrack (' + d.totalRatings + ')';
                    });
                });
            } catch (e) {}
        }, 2000);
    }

    // ── Post-playback rating popup ───────────────────────────────────────
    // When a video player closes after playing a Movie or Episode the user
    // is rated, show a quick rating prompt if they haven't already rated it.

    var _lastPlayedId = null, _wasPlaying = false;
    function startPostPlaybackPopup() {
        if (!_STARTRACK_CONFIG.postPlaybackRatingPopup) return;

        // Detection runs at 1s intervals and tracks two signals so we don't
        // miss the playback->stopped transition:
        //   (a) playerActive — current frame has player chrome OR a <video>
        //       element regardless of whether it's currently paused. This is
        //       sticky-true throughout the session, including pauses, scrubs,
        //       and the credits.
        //   (b) playerWasActive — last frame's value of playerActive.
        // We fire on the falling edge (active -> inactive) — i.e. the user
        // closed the player. Item id is captured the first time we see the
        // player so we still know which film to ask about even if the URL has
        // already changed by the time we detect the close.
        setInterval(function () {
            try {
                var active = isPlayerLikelyActive();
                if (active) {
                    var id = currentPlayingId();
                    if (id) _lastPlayedId = id;
                    _wasPlaying = true;
                    return;
                }
                if (_wasPlaying && _lastPlayedId) {
                    _wasPlaying = false;
                    var pid = _lastPlayedId; _lastPlayedId = null;
                    if (_userPrefs().hidePostPlayback) return;
                    apiGet(pid).then(function (d) {
                        var uid = getUserId();
                        var mine = (d && d.userRatings || []).find(function (u) { return u.userId === uid; });
                        if (mine) return; // already rated
                        showPostPlaybackPrompt(pid);
                    }).catch(function () {});
                }
            } catch (e) {}
        }, 1000);
    }

    // Sticky "is the player chrome up at all?" — true while a <video> exists
    // in the DOM or the URL hash points at videoosd/nowplaying, regardless of
    // paused state. The previous version only fired during active playback,
    // which made paused/credits scenarios look like "stopped" prematurely.
    function isPlayerLikelyActive() {
        var hash = window.location.hash || '';
        if (/videoosd|nowplaying|\/video\b/i.test(hash)) return true;
        if (document.getElementById('videoOsdPage')) return true;
        if (document.querySelector('.videoOsdPage, .videoOsd, .htmlVideoPlayerContainer, .videoPlayerContainer, .skinHeader-withBackground.videoOsd')) return true;
        var vid = document.querySelector('video');
        // Treat any <video> with a real source as the player being live —
        // includes paused playback, end-of-credits hold, and seek scrubs.
        if (vid && (vid.currentSrc || vid.src)) return true;
        return false;
    }

    // Best-effort id extraction. Tries the URL first (videoosd?id=..., or
    // ?id= / &id= anywhere in hash+query), then falls back to nowPlayingItem
    // exposed by the Jellyfin web ApiClient if the URL is stale.
    function currentPlayingId() {
        var fromUrl = getItemId();
        if (fromUrl) return fromUrl;
        try {
            if (window.ApiClient && typeof window.ApiClient.getCurrentUser === 'function') {
                // Some Jellyfin builds expose getCurrentSession/nowPlayingItem on the client.
                var sess = window.ApiClient._currentSession || null;
                if (sess && sess.NowPlayingItem && sess.NowPlayingItem.Id) return sess.NowPlayingItem.Id;
            }
        } catch (e) {}
        try {
            // Fallback: scrape the OSD title's containing element if Jellyfin
            // exposes a data-itemid attribute on it (some skins do).
            var el = document.querySelector('[data-itemid]');
            if (el) return el.getAttribute('data-itemid');
        } catch (e) {}
        return null;
    }

    function showPostPlaybackPrompt(itemId) {
        if (document.getElementById('ir-pp-prompt')) return;
        var prompt = document.createElement('div');
        prompt.id = 'ir-pp-prompt';
        prompt.style.cssText = 'position:fixed;bottom:24px;right:24px;z-index:2147483646;background:#1a1c23;color:#fff;padding:14px 18px;border-radius:12px;box-shadow:0 8px 24px rgba(0,0,0,.5);font-family:inherit;max-width:360px;border:1px solid rgba(244,196,48,.3)';
        prompt.innerHTML =
            '<div style="font-weight:600;margin-bottom:8px;color:#f4c430">' + esc(tr('ui.pill.rate_label')) + '?</div>' +
            '<div style="margin-bottom:10px;opacity:.85;font-size:.9em">' + esc(tr('widget.your_rating_legacy') || 'Your rating:') + '</div>' +
            '<div class="ir-pp-stars" style="display:flex;gap:4px;margin-bottom:10px;cursor:pointer;font-size:1.5em">' +
                '<span data-v="1">\u2606</span><span data-v="2">\u2606</span><span data-v="3">\u2606</span><span data-v="4">\u2606</span><span data-v="5">\u2606</span>' +
            '</div>' +
            '<div style="display:flex;gap:8px;justify-content:flex-end">' +
                '<button class="ir-pp-later" style="background:transparent;color:#aaa;border:none;cursor:pointer">' + esc(tr('btn.cancel')) + '</button>' +
                '<button class="ir-pp-save" style="background:#f4c430;color:#111;border:none;padding:6px 12px;border-radius:6px;font-weight:600;cursor:pointer">' + esc(tr('btn.save_rating')) + '</button>' +
            '</div>';
        document.body.appendChild(prompt);

        var selected = 0;
        var stars = prompt.querySelectorAll('.ir-pp-stars span');
        stars.forEach(function (s) {
            s.addEventListener('click', function () {
                selected = parseInt(s.getAttribute('data-v'), 10);
                stars.forEach(function (ss, i) { ss.textContent = (i < selected) ? '\u2605' : '\u2606'; });
            });
        });
        prompt.querySelector('.ir-pp-later').addEventListener('click', function () { prompt.remove(); });
        prompt.querySelector('.ir-pp-save').addEventListener('click', function () {
            if (!selected) { prompt.remove(); return; }
            var auth = getAuth();
            if (!auth) { prompt.remove(); return; }
            fetch('/Plugins/StarTrack/Ratings/' + itemId, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', Authorization: auth },
                body: JSON.stringify({ stars: selected })
            }).finally(function () { prompt.remove(); });
        });
        setTimeout(function () { if (prompt.parentNode) prompt.remove(); }, 30000);
    }

    // ── User Preferences dialog ─────────────────────────────────────
    // Personal overrides a user can toggle from the My Ratings overlay —
    // stored in localStorage, not on the server. Covers the case where
    // a user wants to turn the Rate pill off just for themselves without
    // admin help.
    function openUserPreferences() {
        if (document.getElementById('ir-prefs-modal')) return;
        var prefs = _userPrefs();
        var curLang = _userLangOverride() || _STARTRACK_CONFIG.language || 'en';
        var langs = [
            ['en', 'English'],
            ['fr', 'Français'],
            ['es', 'Español'],
            ['de', 'Deutsch'],
            ['it', 'Italiano'],
            ['pt', 'Português'],
            ['zh', '简体中文'],
            ['ja', '日本語']
        ];
        var nameOf = function (code) {
            for (var i = 0; i < langs.length; i++) if (langs[i][0] === code) return langs[i][1];
            return code;
        };
        // Build a custom language selector — native <select> dropdowns
        // get styled weirdly inside Jellyfin's high-specificity themes
        // and some mobile webviews break them entirely. A plain button
        // + list is predictable everywhere.
        var selectedLang = curLang;
        var langRowsHtml = langs.map(function (l) {
            var isSel = l[0] === selectedLang;
            return '<div class="ir-prefs-lang-item" data-lang="' + l[0] + '"' +
                   ' style="padding:10px 14px;cursor:pointer;display:flex;align-items:center;justify-content:space-between;gap:10px;border-top:1px solid rgba(255,255,255,.05);' +
                   (isSel ? 'background:rgba(244,196,48,.14);color:#f4c430' : 'color:#fff') + '">' +
                        '<span>' + esc(l[1]) + '</span>' +
                        '<span style="font-size:1.1em;color:' + (isSel ? '#f4c430' : 'transparent') + '">\u2713</span>' +
                   '</div>';
        }).join('');

        var modal = document.createElement('div');
        modal.id = 'ir-prefs-modal';
        modal.style.cssText =
            'position:fixed;inset:0;z-index:2147483647;background:rgba(0,0,0,.72);' +
            'display:flex;align-items:center;justify-content:center;font-family:inherit;padding:20px';
        modal.innerHTML =
            '<div style="background:#161821;border:1px solid rgba(255,255,255,.12);border-radius:14px;padding:24px 26px;max-width:480px;width:100%;color:#fff;box-shadow:0 12px 40px rgba(0,0,0,.6);max-height:90vh;overflow-y:auto">' +
                '<div style="display:flex;align-items:center;gap:10px;margin-bottom:6px">' +
                    '<span style="font-size:1.4em;color:#f4c430">\u2699</span>' +
                    '<h3 style="margin:0;font-size:1.15em;font-weight:700">' + esc(tr('prefs.title', null, 'My preferences')) + '</h3>' +
                '</div>' +
                '<p style="margin:0 0 18px;color:rgba(255,255,255,.55);font-size:.85em;line-height:1.45">' +
                    esc(tr('prefs.intro', null, 'These settings apply only to you on this browser.')) +
                '</p>' +

                '<div style="padding:14px 0;border-top:1px solid rgba(255,255,255,.08)">' +
                    '<div style="color:#fff;font-size:.95em;margin-bottom:4px">' +
                        '\ud83c\udf10 ' + esc(tr('prefs.language', null, 'Language')) +
                    '</div>' +
                    '<div style="color:rgba(255,255,255,.5);font-size:.78em;margin-bottom:10px">' +
                        esc(tr('prefs.language_sub', null, 'Applies to the StarTrack widget and overlay on this browser.')) +
                    '</div>' +
                    '<button id="ir-prefs-lang-btn" type="button" style="width:100%;background:rgba(255,255,255,.05);border:1px solid rgba(255,255,255,.15);color:#fff;border-radius:8px;padding:11px 14px;font-size:.95em;outline:none;cursor:pointer;text-align:left;display:flex;align-items:center;justify-content:space-between;gap:8px">' +
                        '<span id="ir-prefs-lang-label">' + esc(nameOf(selectedLang)) + '</span>' +
                        '<span style="color:rgba(255,255,255,.5)">\u25be</span>' +
                    '</button>' +
                    '<div id="ir-prefs-lang-list" style="display:none;margin-top:6px;background:#0f1117;border:1px solid rgba(255,255,255,.12);border-radius:8px;overflow:hidden">' +
                        langRowsHtml +
                    '</div>' +
                '</div>' +

                '<label class="ir-prefs-row" style="display:flex;gap:12px;align-items:center;padding:14px 0;border-top:1px solid rgba(255,255,255,.08);cursor:pointer">' +
                    '<div style="flex:1;min-width:0">' +
                        '<div style="color:#fff;font-size:.95em">' + esc(tr('prefs.hide_pill', null, 'Hide the StarTrack floating Rate pill')) + '</div>' +
                        '<div style="color:rgba(255,255,255,.5);font-size:.78em;margin-top:3px">' + esc(tr('prefs.hide_pill_sub', null, 'You can still open My Ratings from the sidebar at any time.')) + '</div>' +
                    '</div>' +
                    '<span class="ir-toggle"><input type="checkbox" id="ir-prefs-hidepill" /><span class="ir-toggle-track"><span class="ir-toggle-thumb"></span></span></span>' +
                '</label>' +
                '<label class="ir-prefs-row" style="display:flex;gap:12px;align-items:center;padding:14px 0;border-top:1px solid rgba(255,255,255,.08);cursor:pointer">' +
                    '<div style="flex:1;min-width:0">' +
                        '<div style="color:#fff;font-size:.95em">' + esc(tr('prefs.hide_post_playback', null, 'Hide the post-playback rating popup')) + '</div>' +
                        '<div style="color:rgba(255,255,255,.5);font-size:.78em;margin-top:3px">' + esc(tr('prefs.hide_post_playback_sub', null, 'No rating prompt when a movie or episode finishes.')) + '</div>' +
                    '</div>' +
                    '<span class="ir-toggle"><input type="checkbox" id="ir-prefs-hidepostplay" /><span class="ir-toggle-track"><span class="ir-toggle-thumb"></span></span></span>' +
                '</label>' +
                // Server-side privacy toggles. Persist across browsers and gate
                // server-side responses (Members list and profile fetches).
                '<label class="ir-prefs-row" style="display:flex;gap:12px;align-items:center;padding:14px 0;border-top:1px solid rgba(255,255,255,.08);cursor:pointer">' +
                    '<div style="flex:1;min-width:0">' +
                        '<div style="color:#fff;font-size:.95em">Hide me from Members</div>' +
                        '<div style="color:rgba(255,255,255,.5);font-size:.78em;margin-top:3px">Other users won’t see your card or be able to open your profile.</div>' +
                    '</div>' +
                    '<span class="ir-toggle"><input type="checkbox" id="ir-prefs-hidemember" /><span class="ir-toggle-track"><span class="ir-toggle-thumb"></span></span></span>' +
                '</label>' +
                '<label class="ir-prefs-row" style="display:flex;gap:12px;align-items:center;padding:14px 0;border-top:1px solid rgba(255,255,255,.08);cursor:pointer">' +
                    '<div style="flex:1;min-width:0">' +
                        '<div style="color:#fff;font-size:.95em">Hide my followers</div>' +
                        '<div style="color:rgba(255,255,255,.5);font-size:.78em;margin-top:3px">Your follower count and follower list will be hidden from others. You’ll still see them yourself.</div>' +
                    '</div>' +
                    '<span class="ir-toggle"><input type="checkbox" id="ir-prefs-hidefollow" /><span class="ir-toggle-track"><span class="ir-toggle-thumb"></span></span></span>' +
                '</label>' +
                '<label class="ir-prefs-row" style="display:flex;gap:12px;align-items:center;padding:14px 0;border-top:1px solid rgba(255,255,255,.08);cursor:pointer">' +
                    '<div style="flex:1;min-width:0">' +
                        '<div style="color:#fff;font-size:.95em">Hide who I follow</div>' +
                        '<div style="color:rgba(255,255,255,.5);font-size:.78em;margin-top:3px">Your following count and list will be hidden from others. You’ll still see them yourself.</div>' +
                    '</div>' +
                    '<span class="ir-toggle"><input type="checkbox" id="ir-prefs-hidefollowing" /><span class="ir-toggle-track"><span class="ir-toggle-thumb"></span></span></span>' +
                '</label>' +
                '<label class="ir-prefs-row" style="display:flex;gap:12px;align-items:center;padding:14px 0;border-top:1px solid rgba(255,255,255,.08);cursor:pointer">' +
                    '<div style="flex:1;min-width:0">' +
                        '<div style="color:#fff;font-size:.95em">Hide my stats</div>' +
                        '<div style="color:rgba(255,255,255,.5);font-size:.78em;margin-top:3px">Activity timeline, top genres / directors / actors, year breakdown and decade chart will be hidden from others. You still see your own.</div>' +
                    '</div>' +
                    '<span class="ir-toggle"><input type="checkbox" id="ir-prefs-hidestats" /><span class="ir-toggle-track"><span class="ir-toggle-thumb"></span></span></span>' +
                '</label>' +
                '<label class="ir-prefs-row" style="display:flex;gap:12px;align-items:center;padding:14px 0;border-top:1px solid rgba(255,255,255,.08);cursor:pointer">' +
                    '<div style="flex:1;min-width:0">' +
                        '<div style="color:#fff;font-size:.95em">Hide my recent activity</div>' +
                        '<div style="color:rgba(255,255,255,.5);font-size:.78em;margin-top:3px">Your ratings, diary entries and reviews won’t appear in the Activity feed for other users. You still see your own activity.</div>' +
                    '</div>' +
                    '<span class="ir-toggle"><input type="checkbox" id="ir-prefs-hideactivity" /><span class="ir-toggle-track"><span class="ir-toggle-thumb"></span></span></span>' +
                '</label>' +
                '<div style="display:flex;justify-content:flex-end;gap:10px;margin-top:22px;padding-top:16px;border-top:1px solid rgba(255,255,255,.08)">' +
                    '<button id="ir-prefs-cancel" style="background:transparent;color:rgba(255,255,255,.7);border:1px solid rgba(255,255,255,.18);border-radius:8px;padding:8px 18px;cursor:pointer;font-size:.88em">' + esc(tr('btn.cancel', null, 'Cancel')) + '</button>' +
                    '<button id="ir-prefs-save" style="background:#f4c430;color:#111;border:none;border-radius:8px;padding:8px 20px;cursor:pointer;font-weight:700;font-size:.88em">' + esc(tr('admin.save', null, 'Save')) + '</button>' +
                '</div>' +
            '</div>';
        document.body.appendChild(modal);
        modal.querySelector('#ir-prefs-hidepill').checked = !!prefs.hideRatePill;
        modal.querySelector('#ir-prefs-hidepostplay').checked = !!prefs.hidePostPlayback;

        // Privacy toggles persist server-side. Load current state asynchronously
        // and reflect into the checkboxes when the response arrives — they
        // start unchecked until then which is the same as their default.
        apiGetPrivacy().then(function (pr) {
            var a = modal.querySelector('#ir-prefs-hidemember');
            var b = modal.querySelector('#ir-prefs-hidefollow');
            var c = modal.querySelector('#ir-prefs-hidefollowing');
            var d = modal.querySelector('#ir-prefs-hidestats');
            var e = modal.querySelector('#ir-prefs-hideactivity');
            if (a) a.checked = !!(pr && pr.hideFromMembers);
            if (b) b.checked = !!(pr && pr.hideFollowerCount);
            if (c) c.checked = !!(pr && pr.hideFollowing);
            if (d) d.checked = !!(pr && pr.hideStats);
            if (e) e.checked = !!(pr && pr.hideActivity);
        });

        // Custom language dropdown behaviour
        var langBtn = modal.querySelector('#ir-prefs-lang-btn');
        var langList = modal.querySelector('#ir-prefs-lang-list');
        var langLabel = modal.querySelector('#ir-prefs-lang-label');
        langBtn.addEventListener('click', function () {
            langList.style.display = (langList.style.display === 'none') ? 'block' : 'none';
        });
        modal.querySelectorAll('.ir-prefs-lang-item').forEach(function (item) {
            item.addEventListener('click', function () {
                var code = item.getAttribute('data-lang');
                selectedLang = code;
                langLabel.textContent = nameOf(code);
                // Update row highlight
                modal.querySelectorAll('.ir-prefs-lang-item').forEach(function (row) {
                    var isSel = row.getAttribute('data-lang') === code;
                    row.style.background = isSel ? 'rgba(244,196,48,.14)' : '';
                    row.style.color = isSel ? '#f4c430' : '#fff';
                    var tick = row.querySelector('span:last-child');
                    if (tick) tick.style.color = isSel ? '#f4c430' : 'transparent';
                });
                langList.style.display = 'none';
            });
            item.addEventListener('mouseenter', function () {
                if (item.getAttribute('data-lang') !== selectedLang) {
                    item.style.background = 'rgba(255,255,255,.06)';
                }
            });
            item.addEventListener('mouseleave', function () {
                if (item.getAttribute('data-lang') !== selectedLang) {
                    item.style.background = '';
                }
            });
        });

        var close = function () { if (modal.parentNode) modal.remove(); };
        modal.addEventListener('click', function (e) { if (e.target === modal) close(); });
        modal.querySelector('#ir-prefs-cancel').addEventListener('click', close);
        modal.querySelector('#ir-prefs-save').addEventListener('click', function () {
            var p = _userPrefs();
            p.hideRatePill       = modal.querySelector('#ir-prefs-hidepill').checked;
            p.hidePostPlayback   = modal.querySelector('#ir-prefs-hidepostplay').checked;
            _saveUserPrefs(p);
            // Persist privacy server-side so it applies on every browser.
            var hm = !!(modal.querySelector('#ir-prefs-hidemember') && modal.querySelector('#ir-prefs-hidemember').checked);
            var hf = !!(modal.querySelector('#ir-prefs-hidefollow') && modal.querySelector('#ir-prefs-hidefollow').checked);
            var hg = !!(modal.querySelector('#ir-prefs-hidefollowing') && modal.querySelector('#ir-prefs-hidefollowing').checked);
            var hs = !!(modal.querySelector('#ir-prefs-hidestats') && modal.querySelector('#ir-prefs-hidestats').checked);
            var ha = !!(modal.querySelector('#ir-prefs-hideactivity') && modal.querySelector('#ir-prefs-hideactivity').checked);
            apiSetPrivacy(hm, hf, hg, hs, ha);
            if (selectedLang && selectedLang !== curLang) {
                window.StarTrackSetLanguage(selectedLang);
            }
            close();
            try { _lastId = '__force_rerun__'; checkNav(); } catch (e) {}
        });
    }

    // ══════════════════════════════════════════════════════════════════
    // Admin config page wiring (lives here, not in configPage.html,
    // because Jellyfin's SPA loader strips inline <script> tags from
    // plugin config pages. widget.js is injected globally via the
    // ScriptInjectionMiddleware on every page load — admin included —
    // so it's the reliable place for this logic.
    // ══════════════════════════════════════════════════════════════════

    var STARTRACK_PLUGIN_ID = 'a8b5e2f3-4c1d-4e8a-b2f9-6d3c7e1a5b2f';
    var _adminEn = null, _adminTr = null;
    var _adminCachedConfig = null;
    var _adminLocalDirty = false;
    var _adminLastKnownPage = null;

    function _adminLog() {
        try { console.log.apply(console, ['[StarTrack admin]'].concat([].slice.call(arguments))); } catch (e) {}
    }

    function _adminPickKey(c, pascal) {
        if (c == null) return undefined;
        if (pascal in c) return c[pascal];
        var camel = pascal.charAt(0).toLowerCase() + pascal.slice(1);
        if (camel in c) return c[camel];
        return undefined;
    }

    function _adminSetCheckbox(el, val) {
        if (!el) return;
        el.checked = !!val;
    }

    function _adminFillForm(c) {
        if (!c) return;
        _adminLog('fillForm:', c);
        var root = document.querySelector('#StarTrackConfigPage');
        if (!root) return;
        var lang = root.querySelector('#stLanguage');
        if (lang) {
            var override = null;
            try { override = localStorage.getItem('startrack_lang'); } catch (e) {}
            lang.value = override || _adminPickKey(c, 'Language') || 'en';
        }
        _adminSetCheckbox(root.querySelector('#stHideRecentButton'),     _adminPickKey(c, 'HideRecentButton'));
        _adminSetCheckbox(root.querySelector('#stHideLetterboxdButton'), _adminPickKey(c, 'HideLetterboxdButton'));
        _adminSetCheckbox(root.querySelector('#stRateOnlyInMedia'),      _adminPickKey(c, 'RateButtonOnlyInMediaItem'));
        _adminSetCheckbox(root.querySelector('#stReplaceMediaDetails'),  _adminPickKey(c, 'ReplaceMediaDetailsRating'));
        _adminSetCheckbox(root.querySelector('#stReplaceMediaBar'),      _adminPickKey(c, 'ReplaceMediaBarRating'));
        _adminSetCheckbox(root.querySelector('#stShowRatingsOnPosters'), _adminPickKey(c, 'ShowRatingsOnPosters'));
        _adminSetCheckbox(root.querySelector('#stPostPlaybackPopup'),    _adminPickKey(c, 'PostPlaybackRatingPopup'));
        _adminSetCheckbox(root.querySelector('#stCommunityRecentMode'),  _adminPickKey(c, 'CommunityRecentMode'));

        // Hidden views come through as a comma-separated string. Check
        // the corresponding tick boxes (one per view).
        var raw = _adminPickKey(c, 'HiddenOverlayViews') || '';
        var hiddenSet = {};
        String(raw).split(',').forEach(function (v) {
            var t = v.trim().toLowerCase();
            if (t) hiddenSet[t] = true;
        });
        root.querySelectorAll('.st-cb[data-view]').forEach(function (cb) {
            var v = (cb.getAttribute('data-view') || '').toLowerCase();
            cb.checked = !!hiddenSet[v];
        });
    }

    function _adminReadForm(existing) {
        var root = document.querySelector('#StarTrackConfigPage');
        var c = Object.assign({}, existing || {});
        if (!root) return c;
        c.Language                  = root.querySelector('#stLanguage') ? root.querySelector('#stLanguage').value : 'en';
        c.HideRecentButton          = !!(root.querySelector('#stHideRecentButton')     && root.querySelector('#stHideRecentButton').checked);
        c.HideLetterboxdButton      = !!(root.querySelector('#stHideLetterboxdButton') && root.querySelector('#stHideLetterboxdButton').checked);
        c.RateButtonOnlyInMediaItem = !!(root.querySelector('#stRateOnlyInMedia')      && root.querySelector('#stRateOnlyInMedia').checked);
        c.ReplaceMediaDetailsRating = !!(root.querySelector('#stReplaceMediaDetails')  && root.querySelector('#stReplaceMediaDetails').checked);
        c.ReplaceMediaBarRating     = !!(root.querySelector('#stReplaceMediaBar')      && root.querySelector('#stReplaceMediaBar').checked);
        c.ShowRatingsOnPosters      = !!(root.querySelector('#stShowRatingsOnPosters') && root.querySelector('#stShowRatingsOnPosters').checked);
        c.PostPlaybackRatingPopup   = !!(root.querySelector('#stPostPlaybackPopup')    && root.querySelector('#stPostPlaybackPopup').checked);
        c.CommunityRecentMode       = !!(root.querySelector('#stCommunityRecentMode')  && root.querySelector('#stCommunityRecentMode').checked);

        // Hidden views — collect every st-cb[data-view] that's checked and
        // serialize to the comma string format the server expects.
        var hidden = [];
        root.querySelectorAll('.st-cb[data-view]').forEach(function (cb) {
            if (cb.checked) {
                var v = (cb.getAttribute('data-view') || '').toLowerCase();
                if (v) hidden.push(v);
            }
        });
        c.HiddenOverlayViews = hidden.join(',');
        return c;
    }

    function _adminGetClient() {
        return window.ApiClient ||
            (window.connectionManager && window.connectionManager.currentApiClient && window.connectionManager.currentApiClient()) ||
            null;
    }

    function _adminLoadSettings(attempt) {
        attempt = attempt || 0;
        var client = _adminGetClient();
        var cache = function (c) { _adminCachedConfig = c; _adminFillForm(c); return c; };
        if (!client || typeof client.getPluginConfiguration !== 'function') {
            var auth = getAuth();
            if (!auth) {
                if (attempt < 10) return new Promise(function (res) { setTimeout(res, 500); }).then(function () { return _adminLoadSettings(attempt + 1); });
                _adminLog('giving up — ApiClient never initialised');
                return Promise.resolve();
            }
            return fetch('/Plugins/StarTrack/AdminConfig', { headers: { Authorization: auth } })
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(cache)
                .catch(function (e) { _adminLog('AdminConfig fetch failed', e); });
        }
        return client.getPluginConfiguration(STARTRACK_PLUGIN_ID)
            .then(cache)
            .catch(function (e) { _adminLog('getPluginConfiguration failed', e); });
    }

    function _adminSaveSettings(ev) {
        if (ev && ev.preventDefault) ev.preventDefault();
        var root = document.querySelector('#StarTrackConfigPage');
        var s = root && root.querySelector('#stSaveStatus');
        var markOk = function () {
            _adminCachedConfig = _adminReadForm(_adminCachedConfig);
            _adminLocalDirty = false;
            if (s) { s.style.opacity = '1'; s.style.color = '#52b54b'; s.textContent = '\u2713 Saved'; setTimeout(function () { s.style.opacity = '0'; }, 2000); }
        };
        var markErr = function (msg) { if (s) { s.style.opacity = '1'; s.style.color = '#ff8080'; s.textContent = '\u2717 ' + (msg || 'Save failed'); } };

        // Keep the admin's chosen language visible immediately
        try {
            var chosen = root && root.querySelector('#stLanguage') && root.querySelector('#stLanguage').value;
            if (chosen) localStorage.setItem('startrack_lang', chosen);
        } catch (e) {}

        var client = _adminGetClient();
        if (client && typeof client.updatePluginConfiguration === 'function' && typeof client.getPluginConfiguration === 'function') {
            client.getPluginConfiguration(STARTRACK_PLUGIN_ID).then(function (existing) {
                var body = _adminReadForm(existing);
                return client.updatePluginConfiguration(STARTRACK_PLUGIN_ID, body);
            }).then(markOk).catch(function (err) { markErr(err && err.message); });
        } else {
            var auth = getAuth(); if (!auth) { markErr('No auth'); return false; }
            var body = _adminReadForm(null);
            fetch('/Plugins/StarTrack/AdminConfig', {
                method: 'POST',
                headers: { Authorization: auth, 'Content-Type': 'application/json' },
                body: JSON.stringify(body)
            }).then(function (r) { return r.ok ? markOk() : markErr('HTTP ' + r.status); }).catch(function (e) { markErr(e.message); });
        }
        return false;
    }

    function _adminCurrentLang() {
        try { var ls = localStorage.getItem('startrack_lang'); if (ls) return ls; } catch (e) {}
        var root = document.querySelector('#StarTrackConfigPage');
        var sel = root && root.querySelector('#stLanguage');
        return (sel && sel.value) || 'en';
    }

    function _adminLoadTranslations(lang) {
        return Promise.resolve().then(function () {
            if (_adminEn) return null;
            return fetch('/Plugins/StarTrack/Translations/en').then(function (r) { return r.ok ? r.json() : null; }).then(function (j) { _adminEn = j || {}; });
        }).then(function () {
            if (lang === 'en') { _adminTr = _adminEn; return; }
            return fetch('/Plugins/StarTrack/Translations/' + encodeURIComponent(lang)).then(function (r) { return r.ok ? r.json() : null; }).then(function (j) { if (j) _adminTr = j; });
        }).catch(function () { _adminTr = _adminEn; });
    }

    function _adminTranslatePage() {
        if (!_adminEn) { _adminLog('translate skipped: _adminEn not loaded'); return; }
        var active = _adminTr || _adminEn;
        var nodes = document.querySelectorAll('#StarTrackConfigPage [data-tr]');
        var hit = 0, miss = 0;
        nodes.forEach(function (el) {
            var english = el.getAttribute('data-tr');
            if (!english) return;
            var found = false;
            for (var k in _adminEn) {
                if (_adminEn[k] === english) {
                    el.textContent = active[k] || english;
                    found = true; hit++;
                    break;
                }
            }
            if (!found) miss++;
        });
        _adminLog('translated', hit, 'hit,', miss, 'miss, total', nodes.length);
    }

    function _adminLoadStats() {
        var auth = getAuth(); if (!auth) return;
        var el = document.getElementById('irStats');
        if (!el) return;
        fetch('/Plugins/StarTrack/Stats', { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (d) {
                if (!d || !el) return;
                el.innerHTML = '<b>' + d.totalRatings + '</b> total rating' + (d.totalRatings !== 1 ? 's' : '') +
                    ' across <b>' + d.totalItems + '</b> item' + (d.totalItems !== 1 ? 's' : '');
            })
            .catch(function () {});
    }

    function _adminWireInstance(page) {
        if (!page || page.dataset.stWired === '1') return;
        page.dataset.stWired = '1';
        _adminLog('wiring config page instance', page);

        var form = page.querySelector('#starTrackSettingsForm');
        if (form) {
            form.addEventListener('submit', _adminSaveSettings);
            form.addEventListener('change', function () { _adminLocalDirty = true; });
            form.addEventListener('input',  function () { _adminLocalDirty = true; });
        }

        var langSel = page.querySelector('#stLanguage');
        if (langSel) {
            langSel.addEventListener('change', function () {
                try { localStorage.setItem('startrack_lang', langSel.value); } catch (e) {}
                _adminLoadTranslations(langSel.value).then(_adminTranslatePage);
            });
        }
    }

    function _adminTick() {
        var page = document.querySelector('#StarTrackConfigPage');
        if (!page) { _adminLastKnownPage = null; return; }
        if (page !== _adminLastKnownPage) {
            _adminLastKnownPage = page;
            _adminLocalDirty = false;
            _adminLog('new page element detected');
            _adminWireInstance(page);
            _adminLoadTranslations(_adminCurrentLang()).then(_adminTranslatePage);
            _adminLoadSettings();
            _adminLoadStats();
        } else if (_adminCachedConfig && !_adminLocalDirty) {
            // Reassert cached state — protects against Jellyfin or theme
            // scripts that silently clear the checkboxes after render.
            _adminFillForm(_adminCachedConfig);
        }
    }

    if (!window.__starTrackAdminPollerInstalled) {
        window.__starTrackAdminPollerInstalled = true;
        setInterval(_adminTick, 500);
        _adminLog('global poller installed (from widget.js)');
    }
    _adminTick();

})();
