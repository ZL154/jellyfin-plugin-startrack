(function () {
    'use strict';

    // Wait for ApiClient — script loads before SPA boots
    var _boot = 0;
    var _iv = setInterval(function () {
        if (window.ApiClient) { clearInterval(_iv); init(); }
        else if (++_boot > 120) clearInterval(_iv);
    }, 500);

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

    // ── Jellyfin helpers ──────────────────────────────────────────────────

    function getClient() {
        return window.ApiClient ||
            (window.connectionManager && typeof window.connectionManager.currentApiClient === 'function'
                ? window.connectionManager.currentApiClient() : null);
    }

    function getAuth() {
        var c = getClient(); if (!c) return null;
        var t = typeof c.accessToken === 'function' ? c.accessToken() : c._accessToken;
        return t ? 'MediaBrowser Token="' + t + '"' : null;
    }

    function getUserId() {
        var c = getClient(); if (!c) return null;
        if (typeof c.getCurrentUserId === 'function') return c.getCurrentUserId();
        return (c._currentUser && c._currentUser.Id) || c.currentUserId || null;
    }

    // ── URL helpers ───────────────────────────────────────────────────────

    function getItemId() {
        // Try hash first (standard Jellyfin SPA routing), then query string
        var src = decodeURIComponent(window.location.hash + '&' + window.location.search);
        var m = src.match(/[?&#]id=([0-9a-f]{20,})/i);
        return m ? m[1] : null;
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

    function getItemType(id) {
        var c = getClient(), uid = getUserId();
        if (!c || !uid) return Promise.resolve(null);
        return c.getItem(uid, id)
            .then(function (i) { return (i && i.Type) || null; })
            .catch(function () { return null; });
    }

    // ── Styles ────────────────────────────────────────────────────────────

    function injectStyles() {
        if (document.getElementById('ir-auto-styles')) return;
        var s = document.createElement('style');
        s.id = 'ir-auto-styles';
        s.textContent = [
            // Fixed floating pill — always bottom-right, above all theme content
            '#ir-widget{position:fixed!important;bottom:24px!important;right:24px!important;z-index:2147483647!important;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif!important;font-size:14px!important;line-height:1!important;box-sizing:border-box!important;display:none}',
            '#ir-widget.ir-visible{display:block!important}',
            '#ir-widget *{box-sizing:border-box!important;font-family:inherit!important}',
            // Collapsed pill
            '.ir-collapsed{display:flex!important;align-items:center!important;gap:6px!important;cursor:pointer!important;padding:8px 16px!important;border-radius:24px!important;background:rgba(10,10,10,.92)!important;border:1px solid rgba(255,255,255,.22)!important;backdrop-filter:blur(10px)!important;box-shadow:0 4px 20px rgba(0,0,0,.6)!important;transition:background .2s,transform .15s!important;user-select:none!important;white-space:nowrap!important}',
            '.ir-collapsed:hover{background:rgba(25,25,25,.97)!important;transform:scale(1.04)!important}',
            '.ir-star-icon{color:#f4c430!important;font-size:1.1em!important}',
            '.ir-avg-text{color:#fff!important;font-size:.95em!important;font-weight:700!important;letter-spacing:.02em!important}',
            '.ir-label{color:rgba(255,255,255,.55)!important;font-size:.78em!important;letter-spacing:.04em!important}',
            // Panel — opens upward from the pill
            '.ir-panel{position:absolute!important;bottom:calc(100% + 10px)!important;right:0!important;width:300px!important;background:rgba(14,14,14,.98)!important;border:1px solid rgba(255,255,255,.16)!important;border-radius:12px!important;padding:16px!important;backdrop-filter:blur(16px)!important;box-shadow:0 -6px 40px rgba(0,0,0,.8)!important;display:none;color:#fff!important}',
            '.ir-panel.ir-open{display:block!important}',
            '.ir-panel-header{display:flex!important;align-items:baseline!important;gap:8px!important;padding-bottom:12px!important;border-bottom:1px solid rgba(255,255,255,.1)!important;margin-bottom:12px!important}',
            '.ir-big-star{color:#f4c430!important;font-size:2.2em!important}',
            '.ir-big-avg{color:#fff!important;font-size:2.2em!important;font-weight:700!important}',
            '.ir-count{color:rgba(255,255,255,.5)!important;font-size:.82em!important}',
            // Your rating row
            '.ir-your-row{display:flex!important;align-items:center!important;gap:6px!important;margin-bottom:12px!important;flex-wrap:wrap!important}',
            '.ir-your-label{color:rgba(255,255,255,.65)!important;font-size:.85em!important}',
            '.ir-star-input{display:flex!important;gap:2px!important}',
            '.ir-star-btn{cursor:pointer!important;font-size:1.7em!important;color:rgba(255,255,255,.18)!important;transition:color .1s,transform .12s!important;user-select:none!important;background:none!important;border:none!important;padding:0 1px!important;line-height:1!important}',
            '.ir-star-btn:hover,.ir-star-btn.ir-active{color:#f4c430!important;transform:scale(1.15)!important}',
            '.ir-your-current{color:rgba(255,255,255,.4)!important;font-size:.78em!important}',
            '.ir-flash{font-size:.78em!important;color:#52b54b!important;opacity:0!important;transition:opacity .3s!important}',
            '.ir-flash.ir-show{opacity:1!important}',
            '.ir-remove-btn{background:none!important;border:1px solid rgba(255,70,70,.35)!important;color:rgba(255,100,100,.75)!important;border-radius:4px!important;padding:2px 8px!important;font-size:.76em!important;cursor:pointer!important;margin-left:auto!important;transition:all .2s!important}',
            '.ir-remove-btn:hover{background:rgba(255,40,40,.15)!important;color:#ff7070!important}',
            // List toggle
            '.ir-toggle-btn{width:100%!important;background:none!important;border:1px solid rgba(255,255,255,.12)!important;color:rgba(255,255,255,.55)!important;border-radius:6px!important;padding:5px 10px!important;font-size:.8em!important;cursor:pointer!important;text-align:left!important;transition:all .2s!important}',
            '.ir-toggle-btn:hover{background:rgba(255,255,255,.07)!important;color:#fff!important}',
            '.ir-list{margin-top:8px!important;max-height:200px!important;overflow-y:auto!important;scrollbar-width:thin!important}',
            '.ir-list-item{display:flex!important;align-items:center!important;gap:8px!important;padding:5px 2px!important;border-bottom:1px solid rgba(255,255,255,.05)!important;font-size:.86em!important}',
            '.ir-list-item:last-child{border-bottom:none!important}',
            '.ir-list-name{flex:1!important;font-weight:500!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;color:#fff!important}',
            '.ir-list-stars{color:#f4c430!important;letter-spacing:1px!important;font-size:.9em!important}',
            '.ir-list-val{color:rgba(255,255,255,.45)!important;min-width:28px!important;text-align:right!important}',
            '.ir-empty{color:rgba(255,255,255,.38)!important;text-align:center!important;padding:12px 0!important;margin:0!important;font-size:.88em!important}'
        ].join('');
        document.head.appendChild(s);
    }

    // ── Widget HTML ───────────────────────────────────────────────────────

    var _widget = null;

    function ensureWidget() {
        if (_widget && _widget.isConnected) return _widget;
        if (_widget) _widget.remove();

        injectStyles();
        _widget = document.createElement('div');
        _widget.id = 'ir-widget';
        _widget.innerHTML =
            '<div class="ir-collapsed" title="StarTrack – click to rate">' +
                '<span class="ir-star-icon">\u2606</span>' +
                '<span class="ir-avg-text" style="display:none"></span>' +
                '<span class="ir-label">RATE</span>' +
            '</div>' +
            '<div class="ir-panel">' +
                '<div class="ir-panel-header">' +
                    '<span class="ir-big-star">\u2605</span>' +
                    '<span class="ir-big-avg">\u2013</span>' +
                    '<span class="ir-count">(0 ratings)</span>' +
                '</div>' +
                '<div class="ir-your-row">' +
                    '<span class="ir-your-label">Your rating:</span>' +
                    '<div class="ir-star-input">' +
                        [1,2,3,4,5].map(function(n){
                            return '<span class="ir-star-btn" data-v="'+n+'">\u2605</span>';
                        }).join('') +
                    '</div>' +
                    '<span class="ir-your-current"></span>' +
                    '<span class="ir-flash">\u2713 Saved</span>' +
                    '<button class="ir-remove-btn" style="display:none">\u2715 Remove</button>' +
                '</div>' +
                '<button class="ir-toggle-btn">Show individual ratings \u25be</button>' +
                '<div class="ir-list" style="display:none"></div>' +
            '</div>';

        document.body.appendChild(_widget);
        return _widget;
    }

    // ── Render ────────────────────────────────────────────────────────────

    function renderWidget(data) {
        var widget = _widget; if (!widget) return;
        var myUid = getUserId();
        var total = (data && data.totalRatings) || 0;
        var avg   = (data && data.averageRating) || 0;
        var starIcon = widget.querySelector('.ir-star-icon');
        var avgText  = widget.querySelector('.ir-avg-text');
        var label    = widget.querySelector('.ir-label');

        if (total > 0) {
            starIcon.textContent = '\u2605'; starIcon.style.opacity = '1';
            avgText.textContent  = avg.toFixed(1); avgText.style.display = '';
            label.style.display  = 'none';
        } else {
            starIcon.textContent = '\u2606'; starIcon.style.opacity = '0.5';
            avgText.style.display = 'none';
            label.style.display  = '';
        }

        widget.querySelector('.ir-big-avg').textContent = total > 0 ? avg.toFixed(1) : '\u2013';
        widget.querySelector('.ir-count').textContent   = '(' + total + ' rating' + (total !== 1 ? 's' : '') + ')';

        var ratings  = (data && data.userRatings) || [];
        var myRating = null;
        for (var i = 0; i < ratings.length; i++) { if (ratings[i].userId === myUid) { myRating = ratings[i]; break; } }
        var myVal    = myRating ? myRating.stars : 0;

        widget.querySelectorAll('.ir-star-btn').forEach(function (b, idx) {
            b.classList.toggle('ir-active', idx < Math.round(myVal));
        });
        widget.querySelector('.ir-your-current').textContent = myVal ? '(current: ' + myVal.toFixed(1) + ')' : '';
        widget.querySelector('.ir-remove-btn').style.display = myVal ? '' : 'none';

        widget.querySelector('.ir-list').innerHTML = ratings.length
            ? ratings.map(function (r) {
                return '<div class="ir-list-item">' +
                    '<span class="ir-list-name">' + esc(r.userName) + '</span>' +
                    '<span class="ir-list-stars">' + starsHtml(r.stars) + '</span>' +
                    '<span class="ir-list-val">' + r.stars.toFixed(1) + '</span>' +
                '</div>';
              }).join('')
            : '<p class="ir-empty">No ratings yet \u2013 be the first!</p>';
    }

    // ── Interactions (bound once) ─────────────────────────────────────────

    var _bound = false, _boundItemId = null;

    function bindInteractions() {
        if (_bound) return;
        _bound = true;
        var widget    = _widget;
        var collapsed = widget.querySelector('.ir-collapsed');
        var panel     = widget.querySelector('.ir-panel');
        var toggleBtn = widget.querySelector('.ir-toggle-btn');
        var list      = widget.querySelector('.ir-list');
        var removeBtn = widget.querySelector('.ir-remove-btn');
        var starBtns  = widget.querySelectorAll('.ir-star-btn');
        var panelOpen = false, listOpen = false;

        collapsed.addEventListener('click', function () {
            panelOpen = !panelOpen;
            panel.classList.toggle('ir-open', panelOpen);
        });
        document.addEventListener('click', function (e) {
            if (panelOpen && !widget.contains(e.target)) {
                panelOpen = false; panel.classList.remove('ir-open');
            }
        });
        toggleBtn.addEventListener('click', function () {
            listOpen = !listOpen;
            list.style.display = listOpen ? 'block' : 'none';
            toggleBtn.textContent = listOpen ? 'Hide individual ratings \u25b4' : 'Show individual ratings \u25be';
        });
        starBtns.forEach(function (btn, idx) {
            btn.addEventListener('mouseenter', function () {
                starBtns.forEach(function (b, i) { b.classList.toggle('ir-active', i <= idx); });
            });
            btn.addEventListener('mouseleave', function () {
                var m = widget.querySelector('.ir-your-current').textContent.match(/([\d.]+)/);
                var val = m ? parseFloat(m[1]) : 0;
                starBtns.forEach(function (b, i) { b.classList.toggle('ir-active', i < Math.round(val)); });
            });
            btn.addEventListener('click', function () {
                var id = _boundItemId; if (!id) return;
                apiPost(id, idx + 1).then(function (ok) {
                    if (!ok) return;
                    var fl = widget.querySelector('.ir-flash');
                    fl.classList.add('ir-show');
                    setTimeout(function () { fl.classList.remove('ir-show'); }, 1800);
                    apiGet(id).then(function (d) { renderWidget(d); });
                });
            });
        });
        removeBtn.addEventListener('click', function () {
            var id = _boundItemId; if (!id) return;
            apiDel(id).then(function (ok) {
                if (ok) apiGet(id).then(function (d) { renderWidget(d); });
            });
        });
    }

    // ── Show / hide ───────────────────────────────────────────────────────

    function showWidget(itemId) {
        var widget = ensureWidget();
        _boundItemId = itemId;
        bindInteractions();
        widget.classList.add('ir-visible');
        apiGet(itemId).then(function (d) { renderWidget(d); });
    }

    function hideWidget() {
        if (_widget) {
            _widget.classList.remove('ir-visible');
            var panel = _widget.querySelector('.ir-panel');
            if (panel) panel.classList.remove('ir-open');
        }
        _boundItemId = null;
    }

    // ── Navigation polling ────────────────────────────────────────────────

    var _lastItemId = null;

    function checkNav() {
        var itemId = getItemId();

        if (itemId === _lastItemId) return;
        _lastItemId = itemId;

        if (!itemId) { hideWidget(); return; }

        // Check item type — skip episodes, keep Movies + Series + null (unknown)
        getItemType(itemId).then(function (type) {
            if (itemId !== _lastItemId) return; // navigated away while resolving
            if (type !== null && type !== 'Movie' && type !== 'Series') {
                hideWidget(); return;
            }
            showWidget(itemId);
        });
    }

    function init() {
        setInterval(checkNav, 800);
        window.addEventListener('hashchange', function () { setTimeout(checkNav, 300); });
        window.addEventListener('popstate',   function () { setTimeout(checkNav, 300); });
        setTimeout(checkNav, 500);
    }

})();
