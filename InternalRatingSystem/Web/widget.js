(function () {
    'use strict';

    // Wait for Jellyfin's ApiClient — script loads before the SPA boots
    var _boot = 0;
    var _iv = setInterval(function () {
        if (window.ApiClient) { clearInterval(_iv); init(); }
        else if (++_boot > 120) clearInterval(_iv); // give up after 60 s
    }, 500);

    // ── Utilities ─────────────────────────────────────────────────────────

    function esc(str) {
        var d = document.createElement('div');
        d.appendChild(document.createTextNode(String(str)));
        return d.innerHTML;
    }

    function starsHtml(val, total) {
        total = total || 5;
        var f = Math.round(Math.max(0, Math.min(total, val)));
        return '\u2605'.repeat(f) + '\u2606'.repeat(total - f);
    }

    // ── Jellyfin helpers ──────────────────────────────────────────────────

    function getClient() {
        return window.ApiClient ||
            (window.connectionManager && window.connectionManager.currentApiClient
                ? window.connectionManager.currentApiClient() : null);
    }

    function getAuth() {
        var c = getClient();
        if (!c) return null;
        var t = typeof c.accessToken === 'function' ? c.accessToken() : c._accessToken;
        return t ? ('MediaBrowser Token="' + t + '"') : null;
    }

    function getUserId() {
        var c = getClient();
        if (!c) return null;
        if (typeof c.getCurrentUserId === 'function') return c.getCurrentUserId();
        return (c._currentUser && c._currentUser.Id) || c.currentUserId || null;
    }

    // ── URL helpers ───────────────────────────────────────────────────────

    function getItemId() {
        var src = decodeURIComponent(window.location.hash + window.location.search);
        var m = src.match(/[?&#]id=([a-f0-9]{20,})/i);
        return m ? m[1] : null;
    }

    function onDetailPage() {
        var loc = window.location.hash + window.location.pathname + window.location.search;
        if (/details/i.test(loc)) return true;
        return !!(document.querySelector('.btnPlay, [class*="btnPlay"]') && getItemId());
    }

    // ── API ───────────────────────────────────────────────────────────────

    function apiFetch(path, opts) {
        var auth = getAuth();
        if (!auth) return Promise.resolve(null);
        opts = opts || {};
        opts.headers = Object.assign({ Authorization: auth, 'Content-Type': 'application/json' }, opts.headers || {});
        return fetch(path, opts).then(function (r) { return r.ok ? r : null; }).catch(function () { return null; });
    }

    function apiGet(id)    { return apiFetch('/Plugins/StarTrack/Ratings/' + id).then(function (r) { return r ? r.json() : null; }); }
    function apiPost(id,s) { return apiFetch('/Plugins/StarTrack/Ratings/' + id, { method:'POST', body: JSON.stringify({ stars: s }) }).then(Boolean); }
    function apiDel(id)    { return apiFetch('/Plugins/StarTrack/Ratings/' + id, { method:'DELETE' }).then(Boolean); }

    function getItemType(id) {
        var c = getClient(), uid = getUserId();
        if (!c || !uid) return Promise.resolve(null);
        return c.getItem(uid, id).then(function (i) { return (i && i.Type) || null; }).catch(function () { return null; });
    }

    // ── Styles ────────────────────────────────────────────────────────────

    function injectStyles() {
        if (document.getElementById('ir-auto-styles')) return;
        var s = document.createElement('style');
        s.id = 'ir-auto-styles';
        s.textContent = [
            '#ir-widget{display:inline-flex!important;align-items:center!important;position:relative!important;z-index:500!important;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif!important;margin:8px 6px!important;vertical-align:middle!important;box-sizing:border-box!important}',
            '#ir-widget *{box-sizing:border-box!important}',
            '.ir-collapsed{display:flex!important;align-items:center!important;gap:5px!important;cursor:pointer!important;padding:5px 13px!important;border-radius:20px!important;background:rgba(255,255,255,.1)!important;border:1px solid rgba(255,255,255,.25)!important;transition:background .2s!important;user-select:none!important;line-height:1!important}',
            '.ir-collapsed:hover{background:rgba(255,255,255,.2)!important}',
            '.ir-star-icon{color:#f4c430!important;font-size:1.15em!important;line-height:1!important}',
            '.ir-avg-text{color:#fff!important;font-size:.95em!important;font-weight:700!important;letter-spacing:.02em!important}',
            '.ir-caret{color:rgba(255,255,255,.5)!important;font-size:.7em!important}',
            '.ir-panel{position:absolute!important;top:calc(100% + 8px)!important;left:0!important;min-width:300px!important;background:rgba(18,18,18,.97)!important;border:1px solid rgba(255,255,255,.18)!important;border-radius:10px!important;padding:16px!important;backdrop-filter:blur(12px)!important;box-shadow:0 10px 40px rgba(0,0,0,.7)!important;z-index:9999!important;color:#fff!important}',
            '.ir-panel-header{display:flex!important;align-items:baseline!important;gap:8px!important;padding-bottom:12px!important;border-bottom:1px solid rgba(255,255,255,.1)!important;margin-bottom:12px!important}',
            '.ir-big-star{color:#f4c430!important;font-size:2em!important;line-height:1!important}',
            '.ir-big-avg{color:#fff!important;font-size:2em!important;font-weight:700!important;line-height:1!important}',
            '.ir-count{color:rgba(255,255,255,.55)!important;font-size:.85em!important}',
            '.ir-your-row{display:flex!important;align-items:center!important;gap:8px!important;margin-bottom:12px!important;flex-wrap:wrap!important}',
            '.ir-your-label{color:rgba(255,255,255,.7)!important;font-size:.88em!important}',
            '.ir-star-input{display:flex!important;gap:3px!important}',
            '.ir-star-btn{cursor:pointer!important;font-size:1.6em!important;color:rgba(255,255,255,.2)!important;transition:color .1s,transform .1s!important;line-height:1!important;user-select:none!important;background:none!important;border:none!important;padding:0!important}',
            '.ir-star-btn:hover,.ir-star-btn.ir-active{color:#f4c430!important;transform:scale(1.12)!important}',
            '.ir-your-current{color:rgba(255,255,255,.45)!important;font-size:.8em!important}',
            '.ir-flash{font-size:.8em!important;color:#52b54b!important;opacity:0!important;transition:opacity .3s!important}',
            '.ir-flash.ir-show{opacity:1!important}',
            '.ir-remove-btn{background:none!important;border:1px solid rgba(255,80,80,.4)!important;color:rgba(255,100,100,.8)!important;border-radius:4px!important;padding:2px 8px!important;font-size:.78em!important;cursor:pointer!important;margin-left:auto!important;transition:all .2s!important}',
            '.ir-remove-btn:hover{background:rgba(255,50,50,.15)!important;color:#ff7070!important}',
            '.ir-toggle-btn{width:100%!important;background:none!important;border:1px solid rgba(255,255,255,.15)!important;color:rgba(255,255,255,.6)!important;border-radius:6px!important;padding:5px 10px!important;font-size:.82em!important;cursor:pointer!important;text-align:left!important;transition:all .2s!important}',
            '.ir-toggle-btn:hover{background:rgba(255,255,255,.08)!important;color:#fff!important}',
            '.ir-list{margin-top:8px!important;max-height:220px!important;overflow-y:auto!important;scrollbar-width:thin!important}',
            '.ir-list-item{display:flex!important;align-items:center!important;gap:8px!important;padding:6px 2px!important;border-bottom:1px solid rgba(255,255,255,.06)!important;font-size:.88em!important}',
            '.ir-list-item:last-child{border-bottom:none!important}',
            '.ir-list-name{flex:1!important;font-weight:500!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;color:#fff!important}',
            '.ir-list-stars{color:#f4c430!important;letter-spacing:1px!important}',
            '.ir-list-val{color:rgba(255,255,255,.5)!important;min-width:30px!important;text-align:right!important}',
            '.ir-empty{color:rgba(255,255,255,.4)!important;text-align:center!important;padding:14px 0!important;margin:0!important;font-size:.9em!important}'
        ].join('\n');
        document.head.appendChild(s);
    }

    // ── Widget HTML ───────────────────────────────────────────────────────

    function buildWidget() {
        var w = document.createElement('div');
        w.id = 'ir-widget';
        w.innerHTML =
            '<div class="ir-collapsed" title="StarTrack \u2013 click to rate">' +
                '<span class="ir-star-icon">\u2606</span>' +
                '<span class="ir-avg-text" style="display:none"></span>' +
                '<span class="ir-caret">\u25be</span>' +
            '</div>' +
            '<div class="ir-panel" style="display:none">' +
                '<div class="ir-panel-header">' +
                    '<span class="ir-big-star">\u2605</span>' +
                    '<span class="ir-big-avg">\u2013</span>' +
                    '<span class="ir-count">(0 ratings)</span>' +
                '</div>' +
                '<div class="ir-your-row">' +
                    '<span class="ir-your-label">Your rating:</span>' +
                    '<div class="ir-star-input">' +
                        [1,2,3,4,5].map(function(n){
                            return '<span class="ir-star-btn" data-v="'+n+'" title="'+n+' star'+(n>1?'s':'')+'">\u2605</span>';
                        }).join('') +
                    '</div>' +
                    '<span class="ir-your-current"></span>' +
                    '<span class="ir-flash">\u2713 Saved</span>' +
                    '<button class="ir-remove-btn" style="display:none">\u2715 Remove</button>' +
                '</div>' +
                '<button class="ir-toggle-btn">Show individual ratings \u25be</button>' +
                '<div class="ir-list" style="display:none"></div>' +
            '</div>';
        return w;
    }

    // ── Render ────────────────────────────────────────────────────────────

    function renderWidget(widget, data) {
        if (!widget || !widget.isConnected) return;
        var myUid = getUserId();
        var total = (data && data.totalRatings) || 0;
        var avg   = (data && data.averageRating) || 0;
        var starIcon = widget.querySelector('.ir-star-icon');
        var avgText  = widget.querySelector('.ir-avg-text');

        if (total > 0) {
            starIcon.textContent  = '\u2605'; starIcon.style.opacity = '1';
            avgText.textContent   = avg.toFixed(1); avgText.style.display = '';
        } else {
            starIcon.textContent  = '\u2606'; starIcon.style.opacity = '0.45';
            avgText.style.display = 'none';
        }

        widget.querySelector('.ir-big-avg').textContent = total > 0 ? avg.toFixed(1) : '\u2013';
        widget.querySelector('.ir-count').textContent   = '(' + total + ' rating' + (total !== 1 ? 's' : '') + ')';

        var ratings = (data && data.userRatings) || [];
        var myRating = ratings.filter(function(r){ return r.userId === myUid; })[0];
        var myVal = myRating ? myRating.stars : 0;
        widget.querySelectorAll('.ir-star-btn').forEach(function(b, i){
            b.classList.toggle('ir-active', i < Math.round(myVal));
        });
        widget.querySelector('.ir-your-current').textContent = myVal ? '(current: ' + myVal.toFixed(1) + ')' : '';
        widget.querySelector('.ir-remove-btn').style.display = myVal ? '' : 'none';

        var list = widget.querySelector('.ir-list');
        list.innerHTML = ratings.length
            ? ratings.map(function(r){
                return '<div class="ir-list-item">' +
                    '<span class="ir-list-name">' + esc(r.userName) + '</span>' +
                    '<span class="ir-list-stars">' + starsHtml(r.stars) + '</span>' +
                    '<span class="ir-list-val">' + r.stars.toFixed(1) + '</span>' +
                '</div>';
              }).join('')
            : '<p class="ir-empty">No ratings yet \u2013 be the first!</p>';
    }

    // ── Interactions ──────────────────────────────────────────────────────

    function bindInteractions(widget, itemId) {
        var collapsed = widget.querySelector('.ir-collapsed');
        var panel     = widget.querySelector('.ir-panel');
        var toggleBtn = widget.querySelector('.ir-toggle-btn');
        var list      = widget.querySelector('.ir-list');
        var starBtns  = widget.querySelectorAll('.ir-star-btn');
        var removeBtn = widget.querySelector('.ir-remove-btn');
        var panelOpen = false, listOpen = false;

        collapsed.addEventListener('click', function () {
            panelOpen = !panelOpen;
            panel.style.display = panelOpen ? 'block' : 'none';
            widget.querySelector('.ir-caret').textContent = panelOpen ? '\u25b4' : '\u25be';
        });
        document.addEventListener('click', function (e) {
            if (panelOpen && !widget.contains(e.target)) {
                panelOpen = false; panel.style.display = 'none';
                widget.querySelector('.ir-caret').textContent = '\u25be';
            }
        });
        toggleBtn.addEventListener('click', function () {
            listOpen = !listOpen;
            list.style.display = listOpen ? 'block' : 'none';
            toggleBtn.textContent = listOpen ? 'Hide individual ratings \u25b4' : 'Show individual ratings \u25be';
        });
        starBtns.forEach(function (btn, idx) {
            btn.addEventListener('mouseenter', function () {
                starBtns.forEach(function(b,i){ b.classList.toggle('ir-active', i <= idx); });
            });
            btn.addEventListener('mouseleave', function () {
                var m = widget.querySelector('.ir-your-current').textContent.match(/([\d.]+)/);
                var val = m ? parseFloat(m[1]) : 0;
                starBtns.forEach(function(b,i){ b.classList.toggle('ir-active', i < Math.round(val)); });
            });
            btn.addEventListener('click', function () {
                apiPost(itemId, idx + 1).then(function (ok) {
                    if (!ok) return;
                    var fl = widget.querySelector('.ir-flash');
                    fl.classList.add('ir-show');
                    setTimeout(function(){ fl.classList.remove('ir-show'); }, 1800);
                    apiGet(itemId).then(function(d){ renderWidget(widget, d); });
                });
            });
        });
        removeBtn.addEventListener('click', function () {
            apiDel(itemId).then(function (ok) {
                if (ok) apiGet(itemId).then(function(d){ renderWidget(widget, d); });
            });
        });
    }

    // ── Injection target ──────────────────────────────────────────────────

    function findTarget() {
        // Walk up from the play button — works on every theme
        var playBtn = document.querySelector(
            '.btnPlay,[class*="btnPlay"],[class*="BtnPlay"],button[title*="Play"],button[aria-label*="Play"]'
        );
        if (playBtn) {
            var el = playBtn.parentElement;
            for (var i = 0; i < 5; i++) {
                if (el && el.children.length >= 2) return el;
                el = el && el.parentElement;
            }
            if (playBtn.parentElement) return playBtn.parentElement;
        }
        var named = [
            '.mainDetailButtons','.detailButtons','.itemButtons',
            '[class*="detailButton"],[class*="DetailButton"]',
            '[class*="actionButton"],[class*="ActionButton"]',
            '.itemMiscInfo-secondary','.itemMiscInfo','.itemOverview',
            '.detailPagePrimaryContent','.detailPageContent',
            '[class*="detailPage"]','main'
        ];
        for (var j = 0; j < named.length; j++) {
            var found = document.querySelector(named[j]);
            if (found) return found;
        }
        return null;
    }

    // ── Injection with retry ──────────────────────────────────────────────

    var _injectItemId = null, _injectAttempts = 0;

    function startInject(itemId) {
        if (!itemId) return;
        var old = document.getElementById('ir-widget');
        if (old) old.remove();
        _injectItemId = itemId; _injectAttempts = 0;
        injectStyles();
        tryInject();
    }

    function tryInject() {
        var itemId = _injectItemId;
        if (!itemId || _injectAttempts++ > 40) return;
        if (document.getElementById('ir-widget')) return;
        if (getItemId() !== itemId) return;

        var target = findTarget();
        if (!target) { setTimeout(tryInject, 500); return; }

        getItemType(itemId).then(function (type) {
            if (type !== null && type !== 'Movie' && type !== 'Series') return;
            if (getItemId() !== itemId) return;
            var widget = buildWidget();
            target.insertAdjacentElement('afterend', widget);
            apiGet(itemId).then(function (d) {
                renderWidget(widget, d);
                bindInteractions(widget, itemId);
            });
        });
    }

    // ── Navigation polling ────────────────────────────────────────────────

    var _lastUrl = '';

    function checkNav() {
        var url    = window.location.href;
        var itemId = getItemId();
        if (url === _lastUrl) return;
        _lastUrl = url;
        var old = document.getElementById('ir-widget');
        if (old) old.remove();
        _injectItemId = null;
        if (onDetailPage() && itemId) setTimeout(function(){ startInject(itemId); }, 700);
    }

    function init() {
        setInterval(checkNav, 800);
        window.addEventListener('hashchange', function(){ setTimeout(checkNav, 400); });
        window.addEventListener('popstate',   function(){ setTimeout(checkNav, 400); });
        if (onDetailPage()) setTimeout(function(){ startInject(getItemId()); }, 1000);
    }

})();
