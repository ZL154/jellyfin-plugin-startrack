(function () {
    'use strict';

    console.log('[StarTrack] widget.js loaded — v1.1.0');
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

    function getItemsMeta(ids) {
        var auth = getAuth(), uid = getUserId();
        if (!auth || !uid || !ids.length) return Promise.resolve({});
        var batches = [], bs = 100;
        for (var i = 0; i < ids.length; i += bs) batches.push(ids.slice(i, i + bs));
        return Promise.all(batches.map(function (batch) {
            return fetch(
                '/Users/' + uid + '/Items?Ids=' + batch.join(',') +
                '&Fields=RunTimeTicks,ProductionYear,CommunityRating&Limit=' + batch.length,
                { headers: { Authorization: auth } }
            ).then(function (r) { return r.ok ? r.json() : null; }).catch(function () { return null; });
        })).then(function (results) {
            var meta = {};
            results.forEach(function (res) {
                if (!res || !res.Items) return;
                res.Items.forEach(function (item) {
                    meta[item.Id] = {
                        name: item.Name || 'Unknown',
                        year: item.ProductionYear || 0,
                        runtime: item.RunTimeTicks || 0,
                        communityRating: item.CommunityRating || 0,
                        imageTag: item.ImageTags && item.ImageTags.Primary ? item.ImageTags.Primary : null,
                        type: item.Type || 'Unknown'
                    };
                });
            });
            return meta;
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
            '.ir-ph{display:flex!important;align-items:baseline!important;gap:8px!important;padding-bottom:12px!important;border-bottom:1px solid rgba(255,255,255,.1)!important;margin-bottom:12px!important}',
            '.ir-big-star{color:#f4c430!important;font-size:2.2em!important}',
            '.ir-big-avg{color:#fff!important;font-size:2.2em!important;font-weight:700!important}',
            '.ir-count{color:rgba(255,255,255,.5)!important;font-size:.82em!important}',
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
            '.ir-rec-rev{font-size:.78em!important;color:rgba(255,255,255,.4)!important;margin-top:3px!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;font-style:italic!important}',
            '.ir-rec-stars{color:#f4c430!important;font-size:.92em!important;white-space:nowrap!important;font-weight:700!important;flex-shrink:0!important}',
            // Page badge
            '#ir-page-badge{display:block!important;margin-bottom:8px!important;background:rgba(10,10,10,.85)!important;border:1px solid rgba(244,196,48,.5)!important;border-radius:4px!important;padding:3px 10px!important;font-size:.82em!important;font-weight:700!important;color:#f4c430!important;cursor:pointer!important;white-space:nowrap!important;line-height:1.6!important;width:fit-content!important}',
            '#ir-page-badge:hover{background:rgba(30,30,30,.95)!important}',
            // ── My Ratings overlay — red/black theme ──────────────────────
            '#ir-overlay{position:fixed!important;inset:0!important;z-index:2147483646!important;background:#080808!important;display:none!important;flex-direction:column!important;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif!important;color:#fff!important}',
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
            '.ir-ov-card-name{font-weight:700!important;font-size:.8em!important;color:#fff!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;line-height:1.3!important;text-shadow:0 1px 8px rgba(0,0,0,1),0 0 20px rgba(0,0,0,.9)!important}',
            '.ir-ov-card-meta{font-size:.68em!important;color:rgba(255,255,255,.7)!important;margin-top:3px!important;white-space:nowrap!important;overflow:hidden!important;text-overflow:ellipsis!important;text-shadow:0 1px 4px rgba(0,0,0,1)!important}',
            '.ir-ov-card-rev{font-size:.67em!important;color:rgba(255,255,255,.38)!important;margin-top:3px!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;font-style:italic!important}',
            // ── Letterboxd sync view (inside the main panel) ───────────────
            '.ir-lb-gear{position:absolute!important;top:10px!important;right:10px!important;background:none!important;border:none!important;color:rgba(255,255,255,.5)!important;font-size:1.15em!important;cursor:pointer!important;padding:2px 6px!important;border-radius:4px!important;transition:all .15s!important;z-index:2!important}',
            '.ir-lb-gear:hover{color:#f4c430!important;background:rgba(255,255,255,.08)!important;transform:rotate(30deg)!important}',
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
            // Sidebar link
            '#ir-nav-link{display:flex!important;align-items:center!important;gap:12px!important;padding:10px 20px!important;cursor:pointer!important;background:none!important;border:none!important;width:100%!important;color:inherit!important;text-decoration:none!important;transition:background .15s!important;font-size:inherit!important}',
            '#ir-nav-link:hover{background:rgba(255,255,255,.07)!important}',
            '.ir-nav-icon{color:#f4c430!important;font-size:1.2em!important;width:24px!important;text-align:center!important;flex-shrink:0!important}',
            '.ir-nav-text{font-size:.9em!important}',
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
                // Letterboxd gear icon (top-right of every panel view)
                '<button class="ir-lb-gear" title="Letterboxd sync">\u2699</button>' +
                // Item rating view
                '<div class="ir-item-view">' +
                    '<div class="ir-ph">' +
                        '<span class="ir-big-star">\u2605</span>' +
                        '<span class="ir-big-avg">\u2013</span>' +
                        '<span class="ir-count">(0 ratings)</span>' +
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
                '</div>' +
                // Recent view
                '<div class="ir-recent-panel" style="display:none">' +
                    '<div class="ir-rec-title">' +
                        '<span>Your recent ratings</span>' +
                        '<button class="ir-rec-open-btn">View all \u2192</button>' +
                    '</div>' +
                    '<div class="ir-rec-list"></div>' +
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
                    '<div class="ir-lb-csv-title">Import history from CSV</div>' +
                    '<div class="ir-lb-csv-hint">' +
                        'Export your data from letterboxd.com \u2192 Settings \u2192 Import & Export, ' +
                        'then upload the <code>ratings.csv</code> from inside the ZIP.' +
                    '</div>' +
                    '<div class="ir-lb-btn-row">' +
                        '<label class="ir-lb-upload">' +
                            '<input type="file" accept=".csv,text/csv" class="ir-lb-file" />' +
                            '<span>Choose CSV file</span>' +
                        '</label>' +
                    '</div>' +
                    '<div class="ir-lb-status"></div>' +
                '</div>' +
            '</div>';

        bindInteractions(_el);
        document.body.appendChild(_el);
        return _el;
    }

    // ── Overlay (My Ratings library) ──────────────────────────────────────

    var _overlay = null;
    var _overlayData = null; // merged array after metadata fetch
    var _sortKey = 'ratedAt-desc';
    var _activeTab = 'all'; // 'all' | 'Movie' | 'Series' | 'Episode'

    var _sortFns = {
        'ratedAt-desc':   function (a, b) { return new Date(b.ratedAt) - new Date(a.ratedAt); },
        'ratedAt-asc':    function (a, b) { return new Date(a.ratedAt) - new Date(b.ratedAt); },
        'year-desc':      function (a, b) { return (b.year || 0) - (a.year || 0); },
        'year-asc':       function (a, b) { return (a.year || 0) - (b.year || 0); },
        'stars-desc':     function (a, b) { return b.stars - a.stars; },
        'stars-asc':      function (a, b) { return a.stars - b.stars; },
        'community-desc': function (a, b) { return (b.communityRating || 0) - (a.communityRating || 0); },
        'community-asc':  function (a, b) { return (a.communityRating || 0) - (b.communityRating || 0); },
        'runtime-desc':   function (a, b) { return (b.runtime || 0) - (a.runtime || 0); },
        'runtime-asc':    function (a, b) { return (a.runtime || 0) - (b.runtime || 0); }
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
                    '<button class="ir-ov-close">\u2715 Close</button>' +
                '</div>' +
                '<div class="ir-ov-tabs">' +
                    '<button class="ir-ov-tab ir-ov-tab-active" data-tab="all">All</button>' +
                    '<button class="ir-ov-tab" data-tab="Movie">Movies</button>' +
                    '<button class="ir-ov-tab" data-tab="Series">TV Shows</button>' +
                    '<button class="ir-ov-tab" data-tab="Episode">Episodes</button>' +
                '</div>' +
            '</div>' +
            '<div class="ir-ov-inner">' +
                '<div class="ir-ov-body"><div class="ir-ov-loading">Loading\u2026</div></div>' +
            '</div>';

        _overlay.querySelector('.ir-ov-close').addEventListener('click', function () {
            _overlay.classList.remove('ir-ov-open');
            document.documentElement.style.overflow = '';
        });
        _overlay.querySelector('.ir-ov-sort').addEventListener('change', function (e) {
            _sortKey = e.target.value;
            if (_overlayData) renderOverlayGrid();
        });
        _overlay.querySelectorAll('.ir-ov-tab').forEach(function (tab) {
            tab.addEventListener('click', function () {
                _activeTab = tab.dataset.tab;
                _overlay.querySelectorAll('.ir-ov-tab').forEach(function (t) { t.classList.remove('ir-ov-tab-active'); });
                tab.classList.add('ir-ov-tab-active');
                if (_overlayData) renderOverlayGrid();
            });
        });
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && _overlay && _overlay.classList.contains('ir-ov-open')) {
                _overlay.classList.remove('ir-ov-open');
                document.documentElement.style.overflow = '';
            }
        });

        document.body.appendChild(_overlay);
        return _overlay;
    }

    function renderOverlayGrid() {
        var body = _overlay.querySelector('.ir-ov-body');
        var countEl = _overlay.querySelector('.ir-ov-count');

        var source = _overlayData || [];
        var filtered = _activeTab === 'all'
            ? source
            : source.filter(function (i) { return i.type === _activeTab; });

        if (!filtered.length) {
            body.innerHTML = '<div class="ir-ov-empty">' +
                (source.length ? 'No ratings in this category yet.' : 'You haven\'t rated anything yet.') +
                '</div>';
            if (countEl) countEl.textContent = '';
            return;
        }

        if (countEl) countEl.textContent = filtered.length + ' title' + (filtered.length !== 1 ? 's' : '') + ' rated';

        var sorted = filtered.slice().sort(_sortFns[_sortKey] || _sortFns['ratedAt-desc']);
        var grid = document.createElement('div');
        grid.className = 'ir-ov-grid';

        sorted.forEach(function (item) {
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

            card.innerHTML =
                (posterSrc
                    ? '<img class="ir-ov-poster" src="' + esc(posterSrc) + '" loading="lazy" alt="">'
                    : '<div class="ir-ov-poster-ph">\u2605</div>') +
                '<div class="ir-ov-card-gradient"></div>' +
                '<div class="ir-ov-card-overlay"><div class="ir-ov-card-play">\u25b6\ufe0e</div></div>' +
                '<div class="ir-ov-card-info">' +
                    '<div class="ir-ov-card-stars-badge">\u2605 ' + item.stars.toFixed(1) + '</div>' +
                    '<div class="ir-ov-card-name" title="' + esc(item.name) + '">' + esc(item.name) + '</div>' +
                    (metaParts.length ? '<div class="ir-ov-card-meta">' + esc(metaParts.join(' \u00b7 ')) + '</div>' : '') +
                    (item.review ? '<div class="ir-ov-card-rev">' + esc(item.review) + '</div>' : '') +
                '</div>';

            card.addEventListener('click', function () {
                _overlay.classList.remove('ir-ov-open');
                document.documentElement.style.overflow = '';
                window.location.hash = '#!/details?id=' + item.itemId;
            });

            grid.appendChild(card);
        });

        body.innerHTML = '';
        body.appendChild(grid);
    }

    function openMyRatings() {
        var ov = ensureOverlay();
        _overlay.querySelector('.ir-ov-sort').value = _sortKey;
        // Reset tabs to 'all'
        _activeTab = 'all';
        _overlay.querySelectorAll('.ir-ov-tab').forEach(function (t) { t.classList.remove('ir-ov-tab-active'); });
        var allTab = _overlay.querySelector('.ir-ov-tab[data-tab="all"]');
        if (allTab) allTab.classList.add('ir-ov-tab-active');

        ov.classList.add('ir-ov-open');
        document.documentElement.style.overflow = 'hidden';

        // Refresh data each open
        var body = ov.querySelector('.ir-ov-body');
        body.innerHTML = '<div class="ir-ov-loading">Loading your ratings\u2026</div>';

        apiMyRatings().then(function (ratings) {
            if (!ratings || !ratings.length) { _overlayData = []; renderOverlayGrid(); return; }
            var ids = ratings.map(function (r) { return r.itemId; });
            getItemsMeta(ids).then(function (meta) {
                _overlayData = ratings.map(function (r) {
                    var m = meta[r.itemId] || {};
                    return {
                        itemId: r.itemId,
                        stars: r.stars,
                        review: r.review || null,
                        ratedAt: r.ratedAt,
                        name: m.name || r.itemId,
                        year: m.year || 0,
                        runtime: m.runtime || 0,
                        communityRating: m.communityRating || 0,
                        imageTag: m.imageTag || null,
                        type: m.type || 'Unknown'
                    };
                });
                renderOverlayGrid();
            });
        });
    }

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

    function renderRecent(items) {
        var el = _el; if (!el) return;
        var container = el.querySelector('.ir-rec-list');
        if (!items || !items.length) {
            container.innerHTML = '<p class="ir-empty">You haven\'t rated anything yet.</p>';
            return;
        }
        var ids = items.map(function (r) { return r.itemId; });
        getItemsMeta(ids).then(function (meta) {
            container.innerHTML = items.map(function (r) {
                var m = meta[r.itemId] || {}, name = m.name || r.itemId;
                return '<div class="ir-rec-item" data-id="' + esc(r.itemId) + '">' +
                    '<div class="ir-rec-info">' +
                        '<div class="ir-rec-name">' + esc(name) + '</div>' +
                        '<div class="ir-rec-meta">' + (m.year ? m.year + ' \u00b7 ' : '') + timeAgo(r.ratedAt) + '</div>' +
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
        var pill = el.querySelector('.ir-pill'), panel = el.querySelector('.ir-panel');
        var tb = el.querySelector('.ir-tb'), listEl = el.querySelector('.ir-list');
        var rb = el.querySelector('.ir-rb'), submit = el.querySelector('.ir-submit');
        var flash = el.querySelector('.ir-flash'), yc = el.querySelector('.ir-yc');
        var rev = el.querySelector('.ir-rev'), revHint = el.querySelector('.ir-rev-hint');
        var open = false, listOpen = false;

        pill.addEventListener('click', function () {
            open = !open;
            panel.classList.toggle('ir-open', open);
            if (open && _curId) apiGet(_curId).then(function (d) { if (d) render(d); });
        });
        document.addEventListener('click', function (e) {
            if (open && !el.contains(e.target)) { open = false; panel.classList.remove('ir-open'); }
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

        // "View all →" button in recent panel
        var recOpenBtn = el.querySelector('.ir-rec-open-btn');
        if (recOpenBtn) recOpenBtn.addEventListener('click', function (e) { e.stopPropagation(); openMyRatings(); });

        // ── Letterboxd sync view ────────────────────────────────────────
        var lbGear    = el.querySelector('.ir-lb-gear');
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
        var prevView  = { item: false, recent: false };

        function showLbStatus(text, kind) {
            if (!lbStatus) return;
            lbStatus.textContent = text;
            lbStatus.classList.remove('ir-lb-ok', 'ir-lb-err');
            if (kind === 'ok')  lbStatus.classList.add('ir-lb-ok');
            if (kind === 'err') lbStatus.classList.add('ir-lb-err');
        }

        function openLbView() {
            prevView.item   = itemView.style.display !== 'none';
            prevView.recent = recentView.style.display !== 'none';
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
            if (prevView.item)   itemView.style.display = '';
            if (prevView.recent) recentView.style.display = '';
        }

        if (lbGear) lbGear.addEventListener('click', function (e) { e.stopPropagation(); openLbView(); });
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
                    var msg = total > 0
                        ? 'Imported ' + (r.imported || 0) + ', updated ' + (r.updated || 0) +
                          (r.unmatched ? ', ' + r.unmatched + ' not in library' : '') + '.'
                        : 'Nothing new on Letterboxd right now.';
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
                    showLbStatus('CSV is too large (max 5 MB).', 'err');
                    lbFile.value = '';
                    return;
                }
                showLbStatus('Importing ' + file.name + '\u2026', '');
                var reader = new FileReader();
                reader.onload = function () {
                    apiLbImportCsv(reader.result).then(function (r) {
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
                reader.readAsText(file);
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

    function apiLbImportCsv(csvText) {
        var auth = getAuth(); if (!auth) return Promise.resolve(null);
        return fetch('/Plugins/StarTrack/Letterboxd/Import', {
            method: 'POST',
            headers: { Authorization: auth, 'Content-Type': 'text/csv' },
            body: csvText
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

        // Load this user's recent ratings (limit 15)
        apiMyRatings(15).then(function (items) { renderRecent(items || []); });
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

    var _lastId = '', _lastHash = '';

    function checkNav() {
        injectSidebar();

        // Never show rating pill while watching video
        if (isVideoPlayerPage()) { hide(); return; }

        // Never show on admin dashboard or user preferences pages
        if (isAdminOrDashboardPage()) { hide(); return; }

        var id    = getItemId();
        var idStr = id || '';
        var hash  = window.location.hash + window.location.search;

        if (idStr === _lastId && hash === _lastHash) return;
        _lastId = idStr; _lastHash = hash;

        if (!id) { showRecent(); return; }

        console.log('[StarTrack] item:', id);
        showItem(id);
        getItemType(id).then(function (type) {
            if (id !== _curId) return;
            if (type !== null && type !== 'Movie' && type !== 'Series' && type !== 'Episode') hide();
        });
    }

    function init() {
        var start = function () {
            setInterval(checkNav, 800);
            window.addEventListener('hashchange', function () { setTimeout(checkNav, 200); });
            window.addEventListener('popstate',   function () { setTimeout(checkNav, 200); });
            checkNav();
        };
        if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
        else start();
    }

})();
