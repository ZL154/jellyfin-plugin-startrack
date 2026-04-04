(function () {
    'use strict';

    console.log('[StarTrack] widget.js loaded — v1.0.5');

    // Start immediately — don't wait for window.ApiClient.
    // Auth is resolved lazily from multiple sources.
    init();

    // ── Auth (multiple strategies) ────────────────────────────────────────

    function getCredentials() {
        // 1. window.ApiClient (standard Jellyfin global)
        var c = window.ApiClient;
        if (c) {
            var t = typeof c.accessToken === 'function' ? c.accessToken() : c._accessToken;
            var u = typeof c.getCurrentUserId === 'function' ? c.getCurrentUserId()
                    : (c._currentUser && c._currentUser.Id) || c.currentUserId;
            if (t) return { token: t, userId: u || null };
        }

        // 2. connectionManager
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

        // 3. localStorage (Jellyfin stores creds here — works even before ApiClient is ready)
        try {
            var raw = localStorage.getItem('jellyfin_credentials');
            if (raw) {
                var obj = JSON.parse(raw);
                var servers = obj.Servers || obj.servers || [];
                var origin  = window.location.origin;
                // Prefer a server whose address matches the current origin
                for (var i = 0; i < servers.length; i++) {
                    var s = servers[i];
                    var addr = s.LocalAddress || s.ManualAddress || s.RemoteAddress || '';
                    if (addr && addr.indexOf(origin) !== -1 && (s.AccessToken || s.accessToken)) {
                        return { token: s.AccessToken || s.accessToken, userId: s.UserId || s.userId || null };
                    }
                }
                // Fallback: first server with a token
                for (var j = 0; j < servers.length; j++) {
                    var sv = servers[j];
                    var tk = sv.AccessToken || sv.accessToken;
                    if (tk) return { token: tk, userId: sv.UserId || sv.userId || null };
                }
            }
        } catch (e) { /* ignore */ }

        return null;
    }

    function getAuth() {
        var cred = getCredentials(); return cred ? 'MediaBrowser Token="' + cred.token + '"' : null;
    }

    function getUserId() {
        var cred = getCredentials();
        if (cred && cred.userId) return cred.userId;
        // Also check ApiClient directly
        var c = window.ApiClient;
        if (c && typeof c.getCurrentUserId === 'function') return c.getCurrentUserId();
        return null;
    }

    // ── Utilities ─────────────────────────────────────────────────────────

    function esc(s) {
        var d = document.createElement('div');
        d.appendChild(document.createTextNode(String(s)));
        return d.innerHTML;
    }

    function starsHtml(val) {
        var f = Math.round(Math.max(0, Math.min(5, val)));
        return '\u2605'.repeat(f) + '\u2606'.repeat(5 - f);
    }

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

    // ── API ───────────────────────────────────────────────────────────────

    function apiFetch(path, opts) {
        var auth = getAuth(); if (!auth) return Promise.resolve(null);
        opts = opts || {};
        opts.headers = { Authorization: auth, 'Content-Type': 'application/json' };
        return fetch(path, opts).then(function (r) { return r.ok ? r : null; }).catch(function () { return null; });
    }

    function apiGet(id)     { return apiFetch('/Plugins/StarTrack/Ratings/' + id).then(function (r) { return r ? r.json() : null; }); }
    function apiPost(id, s) { return apiFetch('/Plugins/StarTrack/Ratings/' + id, { method: 'POST', body: JSON.stringify({ stars: s }) }).then(Boolean); }
    function apiDel(id)     { return apiFetch('/Plugins/StarTrack/Ratings/' + id, { method: 'DELETE' }).then(Boolean); }

    // ── Styles ────────────────────────────────────────────────────────────

    function injectStyles() {
        if (document.getElementById('ir-auto-styles')) return;
        var s = document.createElement('style');
        s.id  = 'ir-auto-styles';
        s.textContent = [
            '#ir-widget{position:fixed!important;bottom:24px!important;right:24px!important;z-index:2147483647!important;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif!important;font-size:14px!important;line-height:1!important;box-sizing:border-box!important;display:none!important}',
            '#ir-widget.ir-on{display:block!important}',
            '#ir-widget *{box-sizing:border-box!important;font-family:inherit!important}',
            '.ir-pill{display:flex!important;align-items:center!important;gap:6px!important;cursor:pointer!important;padding:8px 16px!important;border-radius:24px!important;background:rgba(10,10,10,.93)!important;border:1px solid rgba(255,255,255,.22)!important;backdrop-filter:blur(10px)!important;box-shadow:0 4px 24px rgba(0,0,0,.65)!important;transition:background .2s,transform .15s!important;user-select:none!important;white-space:nowrap!important;color:#fff!important}',
            '.ir-pill:hover{background:rgba(30,30,30,.98)!important;transform:scale(1.05)!important}',
            '.ir-star-icon{color:#f4c430!important;font-size:1.1em!important}',
            '.ir-avg-text{font-size:.95em!important;font-weight:700!important;letter-spacing:.02em!important}',
            '.ir-label{color:rgba(255,255,255,.5)!important;font-size:.75em!important;letter-spacing:.05em!important;text-transform:uppercase!important}',
            '.ir-panel{position:absolute!important;bottom:calc(100% + 10px)!important;right:0!important;width:300px!important;background:rgba(12,12,12,.98)!important;border:1px solid rgba(255,255,255,.16)!important;border-radius:12px!important;padding:16px!important;backdrop-filter:blur(16px)!important;box-shadow:0 -6px 40px rgba(0,0,0,.85)!important;display:none!important;color:#fff!important}',
            '.ir-panel.ir-open{display:block!important}',
            '.ir-ph{display:flex!important;align-items:baseline!important;gap:8px!important;padding-bottom:12px!important;border-bottom:1px solid rgba(255,255,255,.1)!important;margin-bottom:12px!important}',
            '.ir-big-star{color:#f4c430!important;font-size:2.2em!important}',
            '.ir-big-avg{color:#fff!important;font-size:2.2em!important;font-weight:700!important}',
            '.ir-count{color:rgba(255,255,255,.5)!important;font-size:.82em!important}',
            '.ir-yr{display:flex!important;align-items:center!important;gap:6px!important;margin-bottom:12px!important;flex-wrap:wrap!important}',
            '.ir-yl{color:rgba(255,255,255,.65)!important;font-size:.85em!important}',
            '.ir-si{display:flex!important;gap:2px!important}',
            '.ir-sb{cursor:pointer!important;font-size:1.7em!important;color:rgba(255,255,255,.18)!important;transition:color .1s,transform .12s!important;user-select:none!important;background:none!important;border:none!important;padding:0 1px!important;line-height:1!important}',
            '.ir-sb:hover,.ir-sb.ir-on2{color:#f4c430!important;transform:scale(1.15)!important}',
            '.ir-yc{color:rgba(255,255,255,.4)!important;font-size:.78em!important}',
            '.ir-flash{font-size:.78em!important;color:#52b54b!important;opacity:0!important;transition:opacity .3s!important}',
            '.ir-flash.ir-show{opacity:1!important}',
            '.ir-rb{background:none!important;border:1px solid rgba(255,70,70,.35)!important;color:rgba(255,100,100,.75)!important;border-radius:4px!important;padding:2px 8px!important;font-size:.76em!important;cursor:pointer!important;margin-left:auto!important;transition:all .2s!important}',
            '.ir-rb:hover{background:rgba(255,40,40,.15)!important;color:#ff7070!important}',
            '.ir-tb{width:100%!important;background:none!important;border:1px solid rgba(255,255,255,.12)!important;color:rgba(255,255,255,.55)!important;border-radius:6px!important;padding:5px 10px!important;font-size:.8em!important;cursor:pointer!important;text-align:left!important;transition:all .2s!important}',
            '.ir-tb:hover{background:rgba(255,255,255,.07)!important;color:#fff!important}',
            '.ir-list{margin-top:8px!important;max-height:200px!important;overflow-y:auto!important;scrollbar-width:thin!important}',
            '.ir-li{display:flex!important;align-items:center!important;gap:8px!important;padding:5px 2px!important;border-bottom:1px solid rgba(255,255,255,.05)!important;font-size:.86em!important}',
            '.ir-li:last-child{border-bottom:none!important}',
            '.ir-ln{flex:1!important;font-weight:500!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;color:#fff!important}',
            '.ir-ls{color:#f4c430!important;letter-spacing:1px!important;font-size:.9em!important}',
            '.ir-lv{color:rgba(255,255,255,.45)!important;min-width:28px!important;text-align:right!important}',
            '.ir-empty{color:rgba(255,255,255,.38)!important;text-align:center!important;padding:12px 0!important;margin:0!important;font-size:.88em!important}'
        ].join('');
        document.head.appendChild(s);
    }

    // ── Widget DOM (singleton, lives in body) ─────────────────────────────

    var _el = null;

    function ensureEl() {
        if (_el && _el.isConnected) return _el;
        injectStyles();
        _el = document.createElement('div');
        _el.id = 'ir-widget';
        _el.innerHTML =
            '<div class="ir-pill" title="StarTrack \u2013 click to rate">' +
                '<span class="ir-star-icon">\u2606</span>' +
                '<span class="ir-avg-text" style="display:none"></span>' +
                '<span class="ir-label">Rate</span>' +
            '</div>' +
            '<div class="ir-panel">' +
                '<div class="ir-ph">' +
                    '<span class="ir-big-star">\u2605</span>' +
                    '<span class="ir-big-avg">\u2013</span>' +
                    '<span class="ir-count">(0 ratings)</span>' +
                '</div>' +
                '<div class="ir-yr">' +
                    '<span class="ir-yl">Your rating:</span>' +
                    '<div class="ir-si">' +
                    [1,2,3,4,5].map(function (n) {
                        return '<span class="ir-sb" data-v="' + n + '">\u2605</span>';
                    }).join('') +
                    '</div>' +
                    '<span class="ir-yc"></span>' +
                    '<span class="ir-flash">\u2713 Saved</span>' +
                    '<button class="ir-rb" style="display:none">\u2715 Remove</button>' +
                '</div>' +
                '<button class="ir-tb">Show individual ratings \u25be</button>' +
                '<div class="ir-list" style="display:none"></div>' +
            '</div>';

        // Bind interactions once
        _bindInteractions(_el);
        document.body.appendChild(_el);
        return _el;
    }

    // ── Render ────────────────────────────────────────────────────────────

    function render(data) {
        var el = _el; if (!el) return;
        var myUid  = getUserId();
        var total  = (data && data.totalRatings) || 0;
        var avg    = (data && data.averageRating) || 0;
        var icon   = el.querySelector('.ir-star-icon');
        var avgTxt = el.querySelector('.ir-avg-text');
        var lbl    = el.querySelector('.ir-label');

        if (total > 0) {
            icon.textContent = '\u2605'; icon.style.opacity = '1';
            avgTxt.textContent = avg.toFixed(1); avgTxt.style.display = '';
            if (lbl) lbl.style.display = 'none';
        } else {
            icon.textContent = '\u2606'; icon.style.opacity = '0.5';
            avgTxt.style.display = 'none';
            if (lbl) lbl.style.display = '';
        }

        el.querySelector('.ir-big-avg').textContent = total > 0 ? avg.toFixed(1) : '\u2013';
        el.querySelector('.ir-count').textContent   = '(' + total + ' rating' + (total !== 1 ? 's' : '') + ')';

        var ratings = (data && data.userRatings) || [];
        var myRat   = null;
        for (var i = 0; i < ratings.length; i++) { if (ratings[i].userId === myUid) { myRat = ratings[i]; break; } }
        var myVal   = myRat ? myRat.stars : 0;
        el.querySelectorAll('.ir-sb').forEach(function (b, idx) { b.classList.toggle('ir-on2', idx < Math.round(myVal)); });
        el.querySelector('.ir-yc').textContent      = myVal ? '(current: ' + myVal.toFixed(1) + ')' : '';
        el.querySelector('.ir-rb').style.display    = myVal ? '' : 'none';

        el.querySelector('.ir-list').innerHTML = ratings.length
            ? ratings.map(function (r) {
                return '<div class="ir-li">' +
                    '<span class="ir-ln">' + esc(r.userName) + '</span>' +
                    '<span class="ir-ls">' + starsHtml(r.stars) + '</span>' +
                    '<span class="ir-lv">' + r.stars.toFixed(1) + '</span>' +
                '</div>';
              }).join('')
            : '<p class="ir-empty">No ratings yet \u2013 be the first!</p>';
    }

    // ── Interactions (bound once on creation) ─────────────────────────────

    function _bindInteractions(el) {
        var pill     = el.querySelector('.ir-pill');
        var panel    = el.querySelector('.ir-panel');
        var tb       = el.querySelector('.ir-tb');
        var list     = el.querySelector('.ir-list');
        var rb       = el.querySelector('.ir-rb');
        var sbs      = el.querySelectorAll('.ir-sb');
        var open = false, listOpen = false;

        pill.addEventListener('click', function () {
            open = !open;
            panel.classList.toggle('ir-open', open);
            // Load/refresh data when opening
            if (open && _curId) apiGet(_curId).then(function (d) { if (d) render(d); });
        });
        document.addEventListener('click', function (e) {
            if (open && !el.contains(e.target)) { open = false; panel.classList.remove('ir-open'); }
        });
        tb.addEventListener('click', function () {
            listOpen = !listOpen;
            list.style.display = listOpen ? 'block' : 'none';
            tb.textContent = listOpen ? 'Hide individual ratings \u25b4' : 'Show individual ratings \u25be';
        });
        sbs.forEach(function (btn, idx) {
            btn.addEventListener('mouseenter', function () { sbs.forEach(function (b, i) { b.classList.toggle('ir-on2', i <= idx); }); });
            btn.addEventListener('mouseleave', function () {
                var m = el.querySelector('.ir-yc').textContent.match(/([\d.]+)/);
                var v = m ? parseFloat(m[1]) : 0;
                sbs.forEach(function (b, i) { b.classList.toggle('ir-on2', i < Math.round(v)); });
            });
            btn.addEventListener('click', function () {
                var id = _curId; if (!id) return;
                apiPost(id, idx + 1).then(function (ok) {
                    if (!ok) return;
                    var fl = el.querySelector('.ir-flash');
                    fl.classList.add('ir-show');
                    setTimeout(function () { fl.classList.remove('ir-show'); }, 1800);
                    apiGet(id).then(function (d) { if (d) render(d); });
                });
            });
        });
        rb.addEventListener('click', function () {
            var id = _curId; if (!id) return;
            apiDel(id).then(function (ok) { if (ok) apiGet(id).then(function (d) { if (d) render(d); }); });
        });
    }

    // ── Show / hide / update ──────────────────────────────────────────────

    var _curId = null;

    function showFor(itemId) {
        var el = ensureEl();
        if (_curId !== itemId) {
            _curId = itemId;
            // Reset state
            var panel = el.querySelector('.ir-panel');
            if (panel) panel.classList.remove('ir-open');
            var tb = el.querySelector('.ir-tb');
            if (tb) { tb.textContent = 'Show individual ratings \u25be'; }
            var list = el.querySelector('.ir-list');
            if (list) list.style.display = 'none';
            // Load data
            apiGet(itemId).then(function (d) { render(d || null); });
        }
        el.classList.add('ir-on');
    }

    function hide() {
        if (_el) { _el.classList.remove('ir-on'); var p = _el.querySelector('.ir-panel'); if (p) p.classList.remove('ir-open'); }
        _curId = null;
    }

    // ── Navigation detection ──────────────────────────────────────────────

    var _lastId = '';

    function checkNav() {
        var id = getItemId();
        var idStr = id || '';
        if (idStr === _lastId) return;
        _lastId = idStr;

        if (!id) { hide(); return; }

        console.log('[StarTrack] item detected:', id, '| hash:', window.location.hash, '| search:', window.location.search);

        // Show pill immediately, then filter out episodes
        showFor(id);
        getItemType(id).then(function (type) {
            if (id !== _curId) return; // navigated away
            console.log('[StarTrack] item type:', type);
            if (type !== null && type !== 'Movie' && type !== 'Series') hide();
        });
    }

    function init() {
        // Wait for DOM to be ready
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', function () {
                setInterval(checkNav, 800);
                window.addEventListener('hashchange', function () { setTimeout(checkNav, 200); });
                window.addEventListener('popstate',   function () { setTimeout(checkNav, 200); });
                checkNav();
            });
        } else {
            setInterval(checkNav, 800);
            window.addEventListener('hashchange', function () { setTimeout(checkNav, 200); });
            window.addEventListener('popstate',   function () { setTimeout(checkNav, 200); });
            checkNav();
        }
    }

})();
