(function () {
    'use strict';

    console.log('[StarTrack] widget.js loaded — v1.0.9');
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
                var obj = JSON.parse(raw);
                var servers = obj.Servers || obj.servers || [];
                var origin  = window.location.origin;
                for (var i = 0; i < servers.length; i++) {
                    var s = servers[i];
                    var addr = s.LocalAddress || s.ManualAddress || s.RemoteAddress || '';
                    if (addr && addr.indexOf(origin) !== -1 && (s.AccessToken || s.accessToken))
                        return { token: s.AccessToken || s.accessToken, userId: s.UserId || s.userId || null };
                }
                for (var j = 0; j < servers.length; j++) {
                    var sv = servers[j];
                    var tk = sv.AccessToken || sv.accessToken;
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
        var auth = getAuth();
        if (!auth) {
            console.warn('[StarTrack] apiFetch: no auth token found');
            return Promise.resolve(null);
        }
        opts = opts || {};
        opts.headers = { Authorization: auth, 'Content-Type': 'application/json' };
        return fetch(path, opts).then(function (r) {
            if (!r.ok) console.warn('[StarTrack] apiFetch', opts.method || 'GET', path, '→', r.status);
            return r.ok ? r : null;
        }).catch(function (e) { console.error('[StarTrack] apiFetch error:', e); return null; });
    }

    function apiGet(id)         { return apiFetch('/Plugins/StarTrack/Ratings/' + id).then(function (r) { return r ? r.json() : null; }); }
    function apiPost(id, stars) { return apiFetch('/Plugins/StarTrack/Ratings/' + id, { method: 'POST',   body: JSON.stringify({ stars: stars }) }).then(function (r) { return r !== null; }); }
    function apiDel(id)         { return apiFetch('/Plugins/StarTrack/Ratings/' + id, { method: 'DELETE' }).then(function (r) { return r !== null; }); }

    // ── Styles ────────────────────────────────────────────────────────────

    function injectStyles() {
        if (document.getElementById('ir-styles')) return;
        var s = document.createElement('style');
        s.id = 'ir-styles';
        s.textContent = [
            '#ir-widget{position:fixed!important;bottom:24px!important;right:24px!important;z-index:2147483647!important;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif!important;font-size:14px!important;line-height:1!important;box-sizing:border-box!important;display:none!important}',
            '#ir-widget.ir-on{display:block!important}',
            '#ir-widget *{box-sizing:border-box!important;font-family:inherit!important}',
            // Pill
            '.ir-pill{display:flex!important;align-items:center!important;gap:6px!important;cursor:pointer!important;padding:8px 16px!important;border-radius:24px!important;background:rgba(10,10,10,.93)!important;border:1px solid rgba(255,255,255,.22)!important;backdrop-filter:blur(10px)!important;box-shadow:0 4px 24px rgba(0,0,0,.65)!important;transition:background .2s,transform .15s!important;user-select:none!important;white-space:nowrap!important;color:#fff!important}',
            '.ir-pill:hover{background:rgba(30,30,30,.98)!important;transform:scale(1.05)!important}',
            '.ir-star-icon{color:#f4c430!important;font-size:1.1em!important}',
            '.ir-avg-text{font-size:.95em!important;font-weight:700!important}',
            '.ir-label{color:rgba(255,255,255,.5)!important;font-size:.75em!important;text-transform:uppercase!important;letter-spacing:.05em!important}',
            // Panel
            '.ir-panel{position:absolute!important;bottom:calc(100% + 10px)!important;right:0!important;width:300px!important;background:rgba(12,12,12,.98)!important;border:1px solid rgba(255,255,255,.16)!important;border-radius:12px!important;padding:16px!important;backdrop-filter:blur(16px)!important;box-shadow:0 -6px 40px rgba(0,0,0,.85)!important;display:none!important;color:#fff!important}',
            '.ir-panel.ir-open{display:block!important}',
            '.ir-ph{display:flex!important;align-items:baseline!important;gap:8px!important;padding-bottom:12px!important;border-bottom:1px solid rgba(255,255,255,.1)!important;margin-bottom:12px!important}',
            '.ir-big-star{color:#f4c430!important;font-size:2.2em!important}',
            '.ir-big-avg{color:#fff!important;font-size:2.2em!important;font-weight:700!important}',
            '.ir-count{color:rgba(255,255,255,.5)!important;font-size:.82em!important}',
            // Star input (half-star)
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
            '.ir-yc{font-size:.78em!important;color:#f4c430!important;font-weight:600!important;height:1.2em!important;display:block!important;margin-bottom:8px!important}',
            // Submit row
            '.ir-submit-row{display:flex!important;align-items:center!important;gap:8px!important;margin-bottom:12px!important;flex-wrap:wrap!important}',
            '.ir-submit{background:#f4c430!important;color:#000!important;border:none!important;border-radius:6px!important;padding:6px 16px!important;font-size:.82em!important;font-weight:700!important;cursor:pointer!important;transition:all .15s!important;opacity:.35!important;pointer-events:none!important;letter-spacing:.03em!important}',
            '.ir-submit.ir-ready{opacity:1!important;pointer-events:auto!important}',
            '.ir-submit.ir-ready:hover{background:#ffd84d!important;transform:scale(1.04)!important}',
            '.ir-flash{font-size:.78em!important;opacity:0!important;transition:opacity .3s!important;font-weight:600!important}',
            '.ir-flash.ir-show{opacity:1!important}',
            '.ir-rb{background:none!important;border:1px solid rgba(255,70,70,.35)!important;color:rgba(255,100,100,.75)!important;border-radius:4px!important;padding:3px 8px!important;font-size:.76em!important;cursor:pointer!important;margin-left:auto!important;transition:all .2s!important}',
            '.ir-rb:hover{background:rgba(255,40,40,.15)!important;color:#ff7070!important}',
            '.ir-tb{width:100%!important;background:none!important;border:1px solid rgba(255,255,255,.12)!important;color:rgba(255,255,255,.55)!important;border-radius:6px!important;padding:5px 10px!important;font-size:.8em!important;cursor:pointer!important;text-align:left!important;transition:all .2s!important}',
            '.ir-tb:hover{background:rgba(255,255,255,.07)!important;color:#fff!important}',
            '.ir-list{margin-top:8px!important;max-height:200px!important;overflow-y:auto!important;scrollbar-width:thin!important}',
            '.ir-li{display:flex!important;align-items:center!important;gap:8px!important;padding:5px 2px!important;border-bottom:1px solid rgba(255,255,255,.05)!important;font-size:.86em!important}',
            '.ir-li:last-child{border-bottom:none!important}',
            '.ir-ln{flex:1!important;font-weight:500!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;color:#fff!important}',
            '.ir-ls{color:#f4c430!important;font-size:.9em!important}',
            '.ir-lv{color:rgba(255,255,255,.45)!important;min-width:32px!important;text-align:right!important}',
            '.ir-empty{color:rgba(255,255,255,.38)!important;text-align:center!important;padding:12px 0!important;margin:0!important;font-size:.88em!important}',
            // Page DOM badge (next to IMDb)
            '#ir-page-badge{display:inline-flex!important;align-items:center!important;gap:4px!important;background:rgba(10,10,10,.85)!important;border:1px solid rgba(244,196,48,.5)!important;border-radius:4px!important;padding:2px 8px!important;font-size:.8em!important;font-weight:700!important;color:#f4c430!important;cursor:pointer!important;margin-right:6px!important;vertical-align:middle!important;white-space:nowrap!important;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif!important;line-height:1.6!important}',
            '#ir-page-badge:hover{background:rgba(30,30,30,.95)!important}',
        ].join('');
        document.head.appendChild(s);
    }

    // ── Star rendering ────────────────────────────────────────────────────

    function setStarDisplay(val) {
        if (!_el) return;
        _el.querySelectorAll('.ir-sw').forEach(function (sw, i) {
            var full = i + 1, half = i + 0.5;
            sw.classList.remove('ir-full', 'ir-half');
            if (val >= full)      sw.classList.add('ir-full');
            else if (val >= half) sw.classList.add('ir-half');
        });
    }

    // ── DOM badge (page) ──────────────────────────────────────────────────

    var _badgeTargetSelectors = [
        '.imdbBadge', '[class*="imdbBadge"]', '[class*="ImdbBadge"]',
        '.externalRatingBadge', '.ratingBadge',
        '.itemMiscInfo .itemMiscInfoItem', '.itemMiscInfoItem',
        '.itemMiscInfo', '.mediaInfoItem', '.mediaInfoItems'
    ];

    function upsertPageBadge(data) {
        var old = document.getElementById('ir-page-badge');
        if (old) old.remove();
        if (!data || data.totalRatings === 0) return;

        var badge = document.createElement('span');
        badge.id = 'ir-page-badge';
        badge.title = 'StarTrack · ' + data.totalRatings + ' rating' + (data.totalRatings !== 1 ? 's' : '') + ' · click to rate';
        badge.innerHTML = '&#9733; ' + data.averageRating.toFixed(1);
        badge.addEventListener('click', function () {
            if (_el) { var p = _el.querySelector('.ir-pill'); if (p) p.click(); }
        });

        if (!tryPlaceBadge(badge)) {
            setTimeout(function () { if (!document.getElementById('ir-page-badge')) tryPlaceBadge(badge); }, 1000);
            setTimeout(function () { if (!document.getElementById('ir-page-badge')) tryPlaceBadge(badge); }, 2500);
        }
    }

    function tryPlaceBadge(badge) {
        for (var i = 0; i < _badgeTargetSelectors.length; i++) {
            var anchor = document.querySelector(_badgeTargetSelectors[i]);
            if (anchor && anchor.parentNode) {
                anchor.parentNode.insertBefore(badge, anchor);
                return true;
            }
        }
        return false;
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
                    '<div class="ir-si">' + buildStarInputHtml() + '</div>' +
                    '<span class="ir-yc"></span>' +
                '</div>' +
                '<div class="ir-submit-row">' +
                    '<button class="ir-submit">\u2605 Save Rating</button>' +
                    '<span class="ir-flash"></span>' +
                    '<button class="ir-rb" style="display:none">\u2715 Remove</button>' +
                '</div>' +
                '<button class="ir-tb">Show individual ratings \u25be</button>' +
                '<div class="ir-list" style="display:none"></div>' +
            '</div>';

        bindInteractions(_el);
        document.body.appendChild(_el);
        return _el;
    }

    // ── Render data into widget ───────────────────────────────────────────

    var _pendingStars = 0;

    function render(data) {
        var el = _el; if (!el) return;
        var myUid  = getUserId();
        var total  = (data && data.totalRatings)  || 0;
        var avg    = (data && data.averageRating) || 0;

        // Pill
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

        // Panel header
        el.querySelector('.ir-big-avg').textContent = total > 0 ? avg.toFixed(1) : '\u2013';
        el.querySelector('.ir-count').textContent   = '(' + total + ' rating' + (total !== 1 ? 's' : '') + ')';

        // My rating
        var ratings = (data && data.userRatings) || [];
        var myRat = null;
        for (var i = 0; i < ratings.length; i++) {
            if (ratings[i].userId === myUid) { myRat = ratings[i]; break; }
        }
        var myVal = myRat ? myRat.stars : 0;
        _pendingStars = myVal;
        setStarDisplay(myVal);

        var yc     = el.querySelector('.ir-yc');
        var rb     = el.querySelector('.ir-rb');
        var submit = el.querySelector('.ir-submit');
        yc.textContent       = myVal ? myVal.toFixed(1) + ' \u2605 selected' : '';
        rb.style.display     = myVal ? '' : 'none';
        submit.textContent   = myVal ? '\u2605 Update Rating' : '\u2605 Save Rating';
        submit.classList.toggle('ir-ready', myVal > 0);

        // Ratings list
        el.querySelector('.ir-list').innerHTML = ratings.length
            ? ratings.map(function (r) {
                return '<div class="ir-li">' +
                    '<span class="ir-ln">' + esc(r.userName) + '</span>' +
                    '<span class="ir-ls">' + r.stars.toFixed(1) + ' \u2605</span>' +
                    '<span class="ir-lv">' + r.stars.toFixed(1) + '</span>' +
                '</div>';
              }).join('')
            : '<p class="ir-empty">No ratings yet \u2013 be the first!</p>';

        upsertPageBadge(data);
    }

    // ── Interactions ──────────────────────────────────────────────────────

    function bindInteractions(el) {
        var pill    = el.querySelector('.ir-pill');
        var panel   = el.querySelector('.ir-panel');
        var tb      = el.querySelector('.ir-tb');
        var list    = el.querySelector('.ir-list');
        var rb      = el.querySelector('.ir-rb');
        var submit  = el.querySelector('.ir-submit');
        var flash   = el.querySelector('.ir-flash');
        var yc      = el.querySelector('.ir-yc');
        var open = false, listOpen = false;

        pill.addEventListener('click', function () {
            open = !open;
            panel.classList.toggle('ir-open', open);
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

        // Half-star hover/click on each half-zone
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

        // Submit
        submit.addEventListener('click', function () {
            var id = _curId;
            if (!id || !_pendingStars) return;
            submit.disabled = true;
            apiPost(id, _pendingStars).then(function (ok) {
                submit.disabled = false;
                if (!ok) {
                    flash.textContent = '\u2717 Failed to save';
                    flash.style.color = '#ff6060';
                    flash.classList.add('ir-show');
                    setTimeout(function () { flash.classList.remove('ir-show'); }, 2500);
                    return;
                }
                flash.textContent = '\u2713 Saved!';
                flash.style.color = '#52b54b';
                flash.classList.add('ir-show');
                setTimeout(function () { flash.classList.remove('ir-show'); }, 1800);
                apiGet(id).then(function (d) { if (d) render(d); });
            });
        });

        // Remove
        rb.addEventListener('click', function () {
            var id = _curId; if (!id) return;
            apiDel(id).then(function (ok) {
                if (!ok) return;
                _pendingStars = 0;
                setStarDisplay(0);
                apiGet(id).then(function (d) { if (d) render(d); });
            });
        });
    }

    // ── Show / hide ───────────────────────────────────────────────────────

    var _curId = null;

    function showFor(itemId) {
        var el = ensureEl();
        if (_curId !== itemId) {
            _curId = itemId;
            _pendingStars = 0;
            var panel = el.querySelector('.ir-panel');
            if (panel) panel.classList.remove('ir-open');
            var tbEl = el.querySelector('.ir-tb');
            if (tbEl) tbEl.textContent = 'Show individual ratings \u25be';
            var listEl = el.querySelector('.ir-list');
            if (listEl) listEl.style.display = 'none';
            apiGet(itemId).then(function (d) {
                render(d || { totalRatings: 0, averageRating: 0, userRatings: [] });
            });
        }
        el.classList.add('ir-on');
    }

    function hide() {
        if (_el) { _el.classList.remove('ir-on'); var p = _el.querySelector('.ir-panel'); if (p) p.classList.remove('ir-open'); }
        _curId = null;
        var b = document.getElementById('ir-page-badge'); if (b) b.remove();
    }

    // ── Navigation ────────────────────────────────────────────────────────

    var _lastId = '';

    function checkNav() {
        var id = getItemId(), idStr = id || '';
        if (idStr === _lastId) return;
        _lastId = idStr;
        if (!id) { hide(); return; }
        console.log('[StarTrack] item:', id);
        showFor(id);
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
