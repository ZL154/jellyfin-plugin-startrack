(function () {
    'use strict';

    console.log('[StarTrack] widget.js loaded — v1.0.12');
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
                var origin = window.location.origin;
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

    function timeAgo(iso) {
        var d = new Date(iso), now = new Date();
        var s = Math.floor((now - d) / 1000);
        if (s < 60)   return 'just now';
        if (s < 3600) return Math.floor(s / 60) + 'm ago';
        if (s < 86400) return Math.floor(s / 3600) + 'h ago';
        return Math.floor(s / 86400) + 'd ago';
    }

    function starsHtml(v) {
        var h = '';
        for (var i = 1; i <= 5; i++) {
            if (v >= i) h += '<span style="color:#f4c430">★</span>';
            else if (v >= i - 0.5) h += '<span style="color:#f4c430;display:inline-block;width:.6em;overflow:hidden">★</span><span style="color:rgba(255,255,255,.2);margin-left:-.6em">★</span>';
            else h += '<span style="color:rgba(255,255,255,.2)">★</span>';
        }
        return h;
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

    function getItemName(id) {
        var auth = getAuth(), uid = getUserId();
        if (!auth || !uid) return Promise.resolve(id);
        return fetch('/Users/' + uid + '/Items/' + id, { headers: { Authorization: auth } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (item) { return item ? (item.Name || id) : id; })
            .catch(function () { return id; });
    }

    // ── API ───────────────────────────────────────────────────────────────

    function apiFetch(path, opts) {
        var auth = getAuth();
        if (!auth) { console.warn('[StarTrack] apiFetch: no auth token'); return Promise.resolve(null); }
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
    function apiRecent(limit)        { return apiFetch('/Plugins/StarTrack/Recent?limit=' + (limit || 15)).then(function (r) { return r ? r.json() : null; }); }

    // ── Styles ────────────────────────────────────────────────────────────

    function injectStyles() {
        if (document.getElementById('ir-styles')) return;
        var s = document.createElement('style');
        s.id = 'ir-styles';
        s.textContent = [
            // Widget container
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
            '.ir-panel{position:absolute!important;bottom:calc(100% + 10px)!important;right:0!important;width:320px!important;background:rgba(12,12,12,.98)!important;border:1px solid rgba(255,255,255,.16)!important;border-radius:12px!important;padding:16px!important;backdrop-filter:blur(16px)!important;box-shadow:0 -6px 40px rgba(0,0,0,.85)!important;display:none!important;color:#fff!important}',
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
            '.ir-yc{font-size:.78em!important;color:#f4c430!important;font-weight:600!important;height:1.2em!important;display:block!important;margin-bottom:6px!important}',
            // Review textarea
            '.ir-rev-wrap{margin-bottom:8px!important}',
            '.ir-rev{width:100%!important;background:rgba(255,255,255,.05)!important;border:1px solid rgba(255,255,255,.14)!important;border-radius:6px!important;color:#fff!important;font-size:.8em!important;padding:6px 8px!important;resize:vertical!important;min-height:52px!important;outline:none!important;transition:border-color .2s!important}',
            '.ir-rev:focus{border-color:rgba(244,196,48,.5)!important}',
            '.ir-rev::placeholder{color:rgba(255,255,255,.25)!important}',
            '.ir-rev-hint{font-size:.72em!important;color:rgba(255,255,255,.3)!important;display:block!important;margin-top:2px!important;text-align:right!important}',
            // Submit row
            '.ir-submit-row{display:flex!important;align-items:center!important;gap:8px!important;margin-bottom:12px!important;flex-wrap:wrap!important}',
            '.ir-submit{background:#f4c430!important;color:#000!important;border:none!important;border-radius:6px!important;padding:6px 16px!important;font-size:.82em!important;font-weight:700!important;cursor:pointer!important;transition:all .15s!important;opacity:.35!important;pointer-events:none!important;letter-spacing:.03em!important}',
            '.ir-submit.ir-ready{opacity:1!important;pointer-events:auto!important}',
            '.ir-submit.ir-ready:hover{background:#ffd84d!important;transform:scale(1.04)!important}',
            '.ir-flash{font-size:.78em!important;opacity:0!important;transition:opacity .3s!important;font-weight:600!important}',
            '.ir-flash.ir-show{opacity:1!important}',
            '.ir-rb{background:none!important;border:1px solid rgba(255,70,70,.35)!important;color:rgba(255,100,100,.75)!important;border-radius:4px!important;padding:3px 8px!important;font-size:.76em!important;cursor:pointer!important;margin-left:auto!important;transition:all .2s!important}',
            '.ir-rb:hover{background:rgba(255,40,40,.15)!important;color:#ff7070!important}',
            // List toggle + list
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
            // Recent panel (home screen)
            '.ir-recent{padding:0!important}',
            '.ir-rec-title{font-size:.72em!important;text-transform:uppercase!important;letter-spacing:.08em!important;color:rgba(255,255,255,.4)!important;margin-bottom:10px!important;font-weight:600!important}',
            '.ir-rec-list{max-height:260px!important;overflow-y:auto!important;scrollbar-width:thin!important}',
            '.ir-rec-item{display:flex!important;align-items:flex-start!important;gap:8px!important;padding:6px 2px!important;border-bottom:1px solid rgba(255,255,255,.05)!important;font-size:.84em!important;cursor:pointer!important;transition:background .15s!important;border-radius:4px!important}',
            '.ir-rec-item:last-child{border-bottom:none!important}',
            '.ir-rec-item:hover{background:rgba(255,255,255,.04)!important}',
            '.ir-rec-info{flex:1!important;overflow:hidden!important}',
            '.ir-rec-name{color:#fff!important;font-weight:600!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;margin-bottom:2px!important}',
            '.ir-rec-meta{color:rgba(255,255,255,.45)!important;font-size:.85em!important}',
            '.ir-rec-rev{font-size:.8em!important;color:rgba(255,255,255,.45)!important;margin-top:3px!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;font-style:italic!important}',
            '.ir-rec-stars{color:#f4c430!important;font-size:.95em!important;white-space:nowrap!important;flex-shrink:0!important}',
            // Page DOM badge
            '#ir-page-badge{display:block!important;margin-bottom:8px!important;background:rgba(10,10,10,.85)!important;border:1px solid rgba(244,196,48,.5)!important;border-radius:4px!important;padding:3px 10px!important;font-size:.82em!important;font-weight:700!important;color:#f4c430!important;cursor:pointer!important;white-space:nowrap!important;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif!important;line-height:1.6!important;width:fit-content!important}',
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

    // ── Page DOM badge ────────────────────────────────────────────────────

    function upsertPageBadge(data) {
        var old = document.getElementById('ir-page-badge');
        if (old) old.remove();
        if (!data || data.totalRatings === 0) return;

        var badge = document.createElement('span');
        badge.id = 'ir-page-badge';
        badge.title = 'StarTrack · ' + data.totalRatings + ' rating' + (data.totalRatings !== 1 ? 's' : '') + ' · click to rate';
        badge.textContent = '★ ' + data.averageRating.toFixed(1) + ' StarTrack (' + data.totalRatings + ')';
        badge.addEventListener('click', function () {
            if (_el) { var p = _el.querySelector('.ir-pill'); if (p) p.click(); }
        });

        // Insert badge on its own row ABOVE the media-info badges row
        var anchors = [
            '.itemMiscInfo', '.mediaInfoItems', '.itemTags',
            '.detailPageContent .itemMiscInfoContainer',
            '.externalLinks', '.itemExternalLinks',
            '.ratings', '.communityRating'
        ];
        var placed = false;
        for (var i = 0; i < anchors.length; i++) {
            var el = document.querySelector(anchors[i]);
            if (el && el.parentNode) {
                el.parentNode.insertBefore(badge, el);
                placed = true;
                break;
            }
        }
        if (!placed) {
            setTimeout(function () { if (!document.getElementById('ir-page-badge')) upsertPageBadge(data); }, 1000);
            setTimeout(function () { if (!document.getElementById('ir-page-badge')) upsertPageBadge(data); }, 2500);
        }
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
                    '<button class="ir-tb">Show ratings \u25be</button>' +
                    '<div class="ir-list" style="display:none"></div>' +
                '</div>' +
                // Recent view (home screen)
                '<div class="ir-recent" style="display:none">' +
                    '<div class="ir-rec-title">Recent Ratings</div>' +
                    '<div class="ir-rec-list"></div>' +
                '</div>' +
            '</div>';

        bindInteractions(_el);
        document.body.appendChild(_el);
        return _el;
    }

    // ── Render item data ──────────────────────────────────────────────────

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

        el.querySelector('.ir-big-avg').textContent = total > 0 ? avg.toFixed(1) : '\u2013';
        el.querySelector('.ir-count').textContent   = '(' + total + ' rating' + (total !== 1 ? 's' : '') + ')';

        // My rating
        var ratings = (data && data.userRatings) || [];
        var myRat = null;
        if (myUid) {
            var myUidN = myUid.replace(/-/g, '');
            for (var i = 0; i < ratings.length; i++) {
                var uid = ratings[i].userId;
                if (uid === myUid || uid === myUidN) { myRat = ratings[i]; break; }
            }
        }
        var myVal = myRat ? myRat.stars : 0;
        _pendingStars = myVal;
        setStarDisplay(myVal);

        var yc     = el.querySelector('.ir-yc');
        var rb     = el.querySelector('.ir-rb');
        var submit = el.querySelector('.ir-submit');
        var rev    = el.querySelector('.ir-rev');
        yc.textContent       = myVal ? myVal.toFixed(1) + ' \u2605 selected' : '';
        rb.style.display     = myVal ? '' : 'none';
        submit.textContent   = myVal ? '\u2605 Update Rating' : '\u2605 Save Rating';
        submit.classList.toggle('ir-ready', myVal > 0);
        if (rev && myRat && myRat.review) rev.value = myRat.review;
        else if (rev && !myRat) rev.value = '';

        // Ratings list
        var listEl = el.querySelector('.ir-list');
        listEl.innerHTML = ratings.length
            ? ratings.map(function (r) {
                var hasReview = r.review && r.review.trim();
                return '<div class="ir-li">' +
                    '<div class="ir-li-top">' +
                        '<span class="ir-ln" data-review="' + (hasReview ? esc(r.review) : '') + '">' + esc(r.userName) + (hasReview ? ' \u2026' : '') + '</span>' +
                        '<span class="ir-ls">' + starsHtml(r.stars) + '</span>' +
                        '<span class="ir-lv">' + timeAgo(r.ratedAt) + '</span>' +
                    '</div>' +
                    (hasReview ? '<div class="ir-li-review">' + esc(r.review) + '</div>' : '') +
                '</div>';
              }).join('')
            : '<p class="ir-empty">No ratings yet \u2013 be the first!</p>';

        // Wire up review expand on name click
        listEl.querySelectorAll('.ir-ln').forEach(function (nameEl) {
            nameEl.addEventListener('click', function () {
                var li = nameEl.closest('.ir-li');
                if (!li) return;
                var revEl = li.querySelector('.ir-li-review');
                if (revEl) revEl.classList.toggle('ir-open');
            });
        });

        upsertPageBadge(data);
    }

    // ── Render recent activity ────────────────────────────────────────────

    function renderRecent(items) {
        var el = _el; if (!el) return;
        var container = el.querySelector('.ir-rec-list');
        if (!items || items.length === 0) {
            container.innerHTML = '<p class="ir-empty">No ratings yet.</p>';
            return;
        }
        // Fetch item names in parallel then render
        var pending = items.length, names = {};
        function done() {
            pending--;
            if (pending > 0) return;
            container.innerHTML = items.map(function (r) {
                var name = names[r.itemId] || r.itemId;
                return '<div class="ir-rec-item" data-id="' + esc(r.itemId) + '">' +
                    '<div class="ir-rec-info">' +
                        '<div class="ir-rec-name">' + esc(name) + '</div>' +
                        '<div class="ir-rec-meta">' + esc(r.userName) + ' · ' + timeAgo(r.ratedAt) + '</div>' +
                        (r.review ? '<div class="ir-rec-rev">' + esc(r.review) + '</div>' : '') +
                    '</div>' +
                    '<span class="ir-rec-stars">' + r.stars.toFixed(1) + ' \u2605</span>' +
                '</div>';
            }).join('');
            // Click → navigate to item
            container.querySelectorAll('.ir-rec-item').forEach(function (row) {
                row.addEventListener('click', function () {
                    var id = row.dataset.id;
                    if (id) window.location.hash = '#!/details?id=' + id;
                });
            });
        }
        items.forEach(function (r) {
            getItemName(r.itemId).then(function (name) { names[r.itemId] = name; done(); });
        });
    }

    // ── Interactions ──────────────────────────────────────────────────────

    function bindInteractions(el) {
        var pill    = el.querySelector('.ir-pill');
        var panel   = el.querySelector('.ir-panel');
        var tb      = el.querySelector('.ir-tb');
        var listEl  = el.querySelector('.ir-list');
        var rb      = el.querySelector('.ir-rb');
        var submit  = el.querySelector('.ir-submit');
        var flash   = el.querySelector('.ir-flash');
        var yc      = el.querySelector('.ir-yc');
        var rev     = el.querySelector('.ir-rev');
        var revHint = el.querySelector('.ir-rev-hint');
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
            listEl.style.display = listOpen ? 'block' : 'none';
            tb.textContent = listOpen ? 'Hide ratings \u25b4' : 'Show ratings \u25be';
        });

        // Review char counter
        if (rev && revHint) {
            rev.addEventListener('input', function () {
                revHint.textContent = rev.value.length + ' / 1000';
            });
            // Prevent panel close when clicking textarea
            rev.addEventListener('click', function (e) { e.stopPropagation(); });
        }

        // Half-star zones
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
            var reviewText = rev ? rev.value.trim() : '';
            apiPost(id, _pendingStars, reviewText || null).then(function (ok) {
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
                if (rev) rev.value = '';
                apiGet(id).then(function (d) { if (d) render(d); });
            });
        });
    }

    // ── Show / hide ───────────────────────────────────────────────────────

    var _curId = null;

    function showItem(itemId) {
        var el = ensureEl();
        var itemView   = el.querySelector('.ir-item-view');
        var recentView = el.querySelector('.ir-recent');
        if (itemView)   itemView.style.display = '';
        if (recentView) recentView.style.display = 'none';

        if (_curId !== itemId) {
            _curId = itemId;
            _pendingStars = 0;
            var panel = el.querySelector('.ir-panel');
            if (panel) panel.classList.remove('ir-open');
            var tbEl = el.querySelector('.ir-tb');
            if (tbEl) tbEl.textContent = 'Show ratings \u25be';
            var listEl2 = el.querySelector('.ir-list');
            if (listEl2) listEl2.style.display = 'none';
            var rev = el.querySelector('.ir-rev');
            if (rev) rev.value = '';
            apiGet(itemId).then(function (d) {
                render(d || { totalRatings: 0, averageRating: 0, userRatings: [] });
            });
        }
        el.classList.add('ir-on');
    }

    function showRecent() {
        var el = ensureEl();
        var itemView   = el.querySelector('.ir-item-view');
        var recentView = el.querySelector('.ir-recent');
        if (itemView)   itemView.style.display = 'none';
        if (recentView) recentView.style.display = '';

        // Update pill to "Recent"
        var icon   = el.querySelector('.ir-star-icon');
        var avgTxt = el.querySelector('.ir-avg-text');
        var lbl    = el.querySelector('.ir-label');
        if (icon)   icon.textContent = '\u2605';
        if (avgTxt) avgTxt.style.display = 'none';
        if (lbl)  { lbl.textContent = 'Recent'; lbl.style.display = ''; lbl.style.color = 'rgba(244,196,48,.8)'; }

        _curId = null;
        el.classList.add('ir-on');

        // Load recent ratings
        var panel = el.querySelector('.ir-panel');
        if (panel) {
            // Wire open/close on pill if not already wired for recent
            // The main pill listener handles open/close already
        }

        apiRecent(15).then(function (items) { renderRecent(items); });
    }

    function hide() {
        if (_el) {
            _el.classList.remove('ir-on');
            var p = _el.querySelector('.ir-panel'); if (p) p.classList.remove('ir-open');
            // Reset label
            var lbl = _el.querySelector('.ir-label');
            if (lbl) { lbl.textContent = 'Rate'; lbl.style.color = ''; }
        }
        _curId = null;
        var b = document.getElementById('ir-page-badge'); if (b) b.remove();
    }

    // ── Navigation ────────────────────────────────────────────────────────

    var _lastId = '', _lastHash = '';

    function checkNav() {
        var id    = getItemId();
        var idStr = id || '';
        var hash  = window.location.hash + window.location.search;

        if (idStr === _lastId && hash === _lastHash) return;
        _lastId   = idStr;
        _lastHash = hash;

        if (!id) {
            // No item in URL — show recent ratings panel on any page
            showRecent();
            return;
        }

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
