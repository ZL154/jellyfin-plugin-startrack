// ==UserScript==
// @name         StarTrack – Jellyfin Ratings
// @namespace    https://github.com/ZL154/jellyfin-plugin-startrack
// @version      1.0.0
// @description  Automatically shows community star ratings on Jellyfin movie and TV show pages. Requires the StarTrack Jellyfin plugin to be installed on the server.
// @author       ZL154
// @match        *://*/*
// @grant        none
// @run-at       document-idle
// ==/UserScript==

(function () {
    'use strict';

    // ── Wait for Jellyfin to boot ─────────────────────────────────────────
    // On slow machines / first load, ApiClient may not exist yet.
    let _bootAttempts = 0;
    const _bootTimer = setInterval(() => {
        if (window.ApiClient) { clearInterval(_bootTimer); init(); return; }
        if (++_bootAttempts > 60) clearInterval(_bootTimer); // give up after 30s
    }, 500);

    // ── Utilities ─────────────────────────────────────────────────────────

    function esc(str) {
        const d = document.createElement('div');
        d.appendChild(document.createTextNode(String(str)));
        return d.innerHTML;
    }

    function starsHtml(val, total = 5) {
        const f = Math.round(Math.max(0, Math.min(total, val)));
        return '★'.repeat(f) + '☆'.repeat(total - f);
    }

    // ── Jellyfin client helpers ───────────────────────────────────────────

    function getClient() {
        return window.ApiClient
            || (window.connectionManager?.currentApiClient?.())
            || null;
    }

    function getAuth() {
        const c = getClient();
        if (!c) return null;
        const t = typeof c.accessToken === 'function' ? c.accessToken() : c._accessToken;
        return t ? `MediaBrowser Token="${t}"` : null;
    }

    function getUserId() {
        const c = getClient();
        if (!c) return null;
        if (typeof c.getCurrentUserId === 'function') return c.getCurrentUserId();
        return c._currentUser?.Id ?? c.currentUserId ?? null;
    }

    // ── URL helpers ───────────────────────────────────────────────────────

    function getItemId() {
        const src = decodeURIComponent(window.location.hash + window.location.search);
        const m = src.match(/[?&#]id=([a-f0-9]{20,})/i);
        return m ? m[1] : null;
    }

    function onDetailPage() {
        const loc = window.location.hash + window.location.pathname + window.location.search;
        if (/details/i.test(loc)) return true;
        return !!(document.querySelector('.btnPlay, [class*="btnPlay"]') && getItemId());
    }

    // ── API ───────────────────────────────────────────────────────────────

    async function apiFetch(path, opts = {}) {
        const auth = getAuth();
        if (!auth) return null;
        try {
            const res = await fetch(path, {
                ...opts,
                headers: { Authorization: auth, 'Content-Type': 'application/json', ...(opts.headers || {}) }
            });
            return res.ok ? res : null;
        } catch { return null; }
    }

    const apiGet    = id      => apiFetch(`/Plugins/StarTrack/Ratings/${id}`).then(r => r ? r.json() : null);
    const apiPost   = (id, s) => apiFetch(`/Plugins/StarTrack/Ratings/${id}`, { method: 'POST',   body: JSON.stringify({ stars: s }) }).then(Boolean);
    const apiDelete = id      => apiFetch(`/Plugins/StarTrack/Ratings/${id}`, { method: 'DELETE' }).then(Boolean);

    async function getItemType(id) {
        const c = getClient(), uid = getUserId();
        if (!c || !uid) return null;
        try { return (await c.getItem(uid, id))?.Type ?? null; }
        catch { return null; }
    }

    // ── CSS ───────────────────────────────────────────────────────────────
    // All rules use !important so no Jellyfin theme can override them.

    function injectStyles() {
        if (document.getElementById('ir-tm-styles')) return;
        const s = document.createElement('style');
        s.id = 'ir-tm-styles';
        s.textContent = `
#ir-widget {
    display: inline-flex !important;
    align-items: center !important;
    position: relative !important;
    z-index: 500 !important;
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif !important;
    margin: 8px 6px !important;
    vertical-align: middle !important;
    box-sizing: border-box !important;
}
#ir-widget * { box-sizing: border-box !important; }

.ir-collapsed {
    display: flex !important;
    align-items: center !important;
    gap: 5px !important;
    cursor: pointer !important;
    padding: 5px 13px !important;
    border-radius: 20px !important;
    background: rgba(255,255,255,.1) !important;
    border: 1px solid rgba(255,255,255,.25) !important;
    transition: background .2s !important;
    user-select: none !important;
    line-height: 1 !important;
}
.ir-collapsed:hover { background: rgba(255,255,255,.2) !important; }

.ir-star-icon {
    color: #f4c430 !important;
    font-size: 1.15em !important;
    line-height: 1 !important;
}
.ir-avg-text {
    color: #fff !important;
    font-size: .95em !important;
    font-weight: 700 !important;
    letter-spacing: .02em !important;
}
.ir-caret { color: rgba(255,255,255,.5) !important; font-size: .7em !important; }

.ir-panel {
    position: absolute !important;
    top: calc(100% + 8px) !important;
    left: 0 !important;
    min-width: 300px !important;
    background: rgba(18,18,18,.97) !important;
    border: 1px solid rgba(255,255,255,.18) !important;
    border-radius: 10px !important;
    padding: 16px !important;
    backdrop-filter: blur(12px) !important;
    box-shadow: 0 10px 40px rgba(0,0,0,.7) !important;
    z-index: 9999 !important;
    color: #fff !important;
}
.ir-panel-header {
    display: flex !important;
    align-items: baseline !important;
    gap: 8px !important;
    padding-bottom: 12px !important;
    border-bottom: 1px solid rgba(255,255,255,.1) !important;
    margin-bottom: 12px !important;
}
.ir-big-star { color: #f4c430 !important; font-size: 2em !important; line-height: 1 !important; }
.ir-big-avg  { color: #fff !important; font-size: 2em !important; font-weight: 700 !important; line-height: 1 !important; }
.ir-count    { color: rgba(255,255,255,.55) !important; font-size: .85em !important; }

.ir-your-row {
    display: flex !important;
    align-items: center !important;
    gap: 8px !important;
    margin-bottom: 12px !important;
    flex-wrap: wrap !important;
}
.ir-your-label { color: rgba(255,255,255,.7) !important; font-size: .88em !important; }
.ir-star-input { display: flex !important; gap: 3px !important; }
.ir-star-btn {
    cursor: pointer !important;
    font-size: 1.6em !important;
    color: rgba(255,255,255,.2) !important;
    transition: color .1s, transform .1s !important;
    line-height: 1 !important;
    user-select: none !important;
    background: none !important;
    border: none !important;
    padding: 0 !important;
}
.ir-star-btn:hover, .ir-star-btn.ir-active { color: #f4c430 !important; transform: scale(1.12) !important; }
.ir-your-current { color: rgba(255,255,255,.45) !important; font-size: .8em !important; }
.ir-flash {
    font-size: .8em !important;
    color: #52b54b !important;
    opacity: 0 !important;
    transition: opacity .3s !important;
}
.ir-flash.ir-show { opacity: 1 !important; }
.ir-remove-btn {
    background: none !important;
    border: 1px solid rgba(255,80,80,.4) !important;
    color: rgba(255,100,100,.8) !important;
    border-radius: 4px !important;
    padding: 2px 8px !important;
    font-size: .78em !important;
    cursor: pointer !important;
    margin-left: auto !important;
    transition: all .2s !important;
}
.ir-remove-btn:hover { background: rgba(255,50,50,.15) !important; color: #ff7070 !important; }
.ir-toggle-btn {
    width: 100% !important;
    background: none !important;
    border: 1px solid rgba(255,255,255,.15) !important;
    color: rgba(255,255,255,.6) !important;
    border-radius: 6px !important;
    padding: 5px 10px !important;
    font-size: .82em !important;
    cursor: pointer !important;
    text-align: left !important;
    transition: all .2s !important;
}
.ir-toggle-btn:hover { background: rgba(255,255,255,.08) !important; color: #fff !important; }
.ir-list {
    margin-top: 8px !important;
    max-height: 220px !important;
    overflow-y: auto !important;
    scrollbar-width: thin !important;
    scrollbar-color: rgba(255,255,255,.2) transparent !important;
}
.ir-list-item {
    display: flex !important;
    align-items: center !important;
    gap: 8px !important;
    padding: 6px 2px !important;
    border-bottom: 1px solid rgba(255,255,255,.06) !important;
    color: rgba(255,255,255,.8) !important;
    font-size: .88em !important;
}
.ir-list-item:last-child { border-bottom: none !important; }
.ir-list-name  { flex: 1 !important; font-weight: 500 !important; overflow: hidden !important; text-overflow: ellipsis !important; white-space: nowrap !important; color: #fff !important; }
.ir-list-stars { color: #f4c430 !important; letter-spacing: 1px !important; }
.ir-list-val   { color: rgba(255,255,255,.5) !important; min-width: 30px !important; text-align: right !important; }
.ir-empty      { color: rgba(255,255,255,.4) !important; text-align: center !important; padding: 14px 0 !important; margin: 0 !important; font-size: .9em !important; }
        `;
        document.head.appendChild(s);
    }

    // ── Widget HTML ───────────────────────────────────────────────────────

    function buildWidget() {
        const w = document.createElement('div');
        w.id = 'ir-widget';
        w.innerHTML = `
            <div class="ir-collapsed" title="StarTrack – click to rate / view ratings">
                <span class="ir-star-icon">☆</span>
                <span class="ir-avg-text" style="display:none"></span>
                <span class="ir-caret">▾</span>
            </div>
            <div class="ir-panel" style="display:none">
                <div class="ir-panel-header">
                    <span class="ir-big-star">★</span>
                    <span class="ir-big-avg">–</span>
                    <span class="ir-count">(0 ratings)</span>
                </div>
                <div class="ir-your-row">
                    <span class="ir-your-label">Your rating:</span>
                    <div class="ir-star-input">
                        ${[1,2,3,4,5].map(n => `<span class="ir-star-btn" data-v="${n}" title="${n} star${n>1?'s':''}">★</span>`).join('')}
                    </div>
                    <span class="ir-your-current"></span>
                    <span class="ir-flash">✓ Saved</span>
                    <button class="ir-remove-btn" style="display:none">✕ Remove</button>
                </div>
                <button class="ir-toggle-btn">Show individual ratings ▾</button>
                <div class="ir-list" style="display:none"></div>
            </div>`;
        return w;
    }

    // ── Render ────────────────────────────────────────────────────────────

    function renderWidget(widget, data) {
        if (!widget?.isConnected) return;
        const myUid = getUserId();
        const total = data?.totalRatings ?? 0;
        const avg   = data?.averageRating ?? 0;

        const starIcon = widget.querySelector('.ir-star-icon');
        const avgText  = widget.querySelector('.ir-avg-text');
        if (total > 0) {
            starIcon.textContent   = '★';
            starIcon.style.opacity = '1';
            avgText.textContent    = avg.toFixed(1);
            avgText.style.display  = '';
        } else {
            starIcon.textContent   = '☆';
            starIcon.style.opacity = '0.45';
            avgText.style.display  = 'none';
        }

        widget.querySelector('.ir-big-avg').textContent = total > 0 ? avg.toFixed(1) : '–';
        widget.querySelector('.ir-count').textContent   = `(${total} rating${total !== 1 ? 's' : ''})`;

        const myRating = (data?.userRatings ?? []).find(r => r.userId === myUid);
        const myVal    = myRating?.stars ?? 0;
        widget.querySelectorAll('.ir-star-btn').forEach((b, i) =>
            b.classList.toggle('ir-active', i < Math.round(myVal)));
        widget.querySelector('.ir-your-current').textContent =
            myVal ? `(current: ${myVal.toFixed(1)})` : '';
        widget.querySelector('.ir-remove-btn').style.display = myVal ? '' : 'none';

        const items = data?.userRatings ?? [];
        widget.querySelector('.ir-list').innerHTML = items.length
            ? items.map(r => `
                <div class="ir-list-item">
                    <span class="ir-list-name">${esc(r.userName)}</span>
                    <span class="ir-list-stars">${starsHtml(r.stars)}</span>
                    <span class="ir-list-val">${r.stars.toFixed(1)}</span>
                </div>`).join('')
            : '<p class="ir-empty">No ratings yet – be the first!</p>';
    }

    // ── Interactions ──────────────────────────────────────────────────────

    function bindInteractions(widget, itemId) {
        const collapsed = widget.querySelector('.ir-collapsed');
        const panel     = widget.querySelector('.ir-panel');
        const toggleBtn = widget.querySelector('.ir-toggle-btn');
        const list      = widget.querySelector('.ir-list');
        const starBtns  = widget.querySelectorAll('.ir-star-btn');
        const removeBtn = widget.querySelector('.ir-remove-btn');
        let panelOpen = false, listOpen = false;

        collapsed.addEventListener('click', () => {
            panelOpen = !panelOpen;
            panel.style.display = panelOpen ? 'block' : 'none';
            widget.querySelector('.ir-caret').textContent = panelOpen ? '▴' : '▾';
        });
        document.addEventListener('click', e => {
            if (panelOpen && !widget.contains(e.target)) {
                panelOpen = false;
                panel.style.display = 'none';
                widget.querySelector('.ir-caret').textContent = '▾';
            }
        });
        toggleBtn.addEventListener('click', () => {
            listOpen = !listOpen;
            list.style.display = listOpen ? 'block' : 'none';
            toggleBtn.textContent = listOpen ? 'Hide individual ratings ▴' : 'Show individual ratings ▾';
        });
        starBtns.forEach((btn, idx) => {
            btn.addEventListener('mouseenter', () =>
                starBtns.forEach((b, i) => b.classList.toggle('ir-active', i <= idx)));
            btn.addEventListener('mouseleave', () => {
                const m   = widget.querySelector('.ir-your-current').textContent.match(/([\d.]+)/);
                const val = m ? parseFloat(m[1]) : 0;
                starBtns.forEach((b, i) => b.classList.toggle('ir-active', i < Math.round(val)));
            });
            btn.addEventListener('click', async () => {
                if (await apiPost(itemId, idx + 1)) {
                    const fl = widget.querySelector('.ir-flash');
                    fl.classList.add('ir-show');
                    setTimeout(() => fl.classList.remove('ir-show'), 1800);
                    renderWidget(widget, await apiGet(itemId));
                }
            });
        });
        removeBtn.addEventListener('click', async () => {
            if (await apiDelete(itemId)) renderWidget(widget, await apiGet(itemId));
        });
    }

    // ── Injection target finder ───────────────────────────────────────────
    // Tries every known selector pattern across all Jellyfin themes.

    function findTarget() {
        // 1. Walk up from play button to find the button row
        const playBtn = document.querySelector(
            '.btnPlay, [class*="btnPlay"], [class*="BtnPlay"], ' +
            'button[title*="Play"], button[aria-label*="Play"]'
        );
        if (playBtn) {
            let el = playBtn.parentElement;
            for (let i = 0; i < 5; i++) {
                if (el && el.children.length >= 2) return el;
                el = el?.parentElement;
            }
            if (playBtn.parentElement) return playBtn.parentElement;
        }

        // 2. Named button containers
        for (const s of [
            '.mainDetailButtons', '.detailButtons', '.itemButtons',
            '[class*="detailButton"]', '[class*="DetailButton"]',
            '[class*="actionButton"]', '[class*="ActionButton"]',
            '[class*="playbackButton"]', '[class*="mediaButton"]',
        ]) {
            const el = document.querySelector(s);
            if (el) return el;
        }

        // 3. Content area fallback
        for (const s of [
            '.itemMiscInfo-secondary', '.itemMiscInfo',
            '.itemOverview', '.overview', '.taglines',
            '.detailPagePrimaryContent', '.detailPageContent',
            '[class*="detailPage"]', 'main',
        ]) {
            const el = document.querySelector(s);
            if (el) return el;
        }
        return null;
    }

    // ── Injection with retry ──────────────────────────────────────────────

    let _injectItemId   = null;
    let _injectAttempts = 0;

    async function startInject(itemId) {
        if (!itemId) return;
        document.getElementById('ir-widget')?.remove();
        _injectItemId   = itemId;
        _injectAttempts = 0;
        injectStyles();
        tryInject();
    }

    async function tryInject() {
        const itemId = _injectItemId;
        if (!itemId || _injectAttempts++ > 40) return;    // give up after ~20s
        if (document.getElementById('ir-widget')) return;   // already done
        if (getItemId() !== itemId) return;                 // user navigated away

        const target = findTarget();
        if (!target) { setTimeout(tryInject, 500); return; }

        // Only filter if we KNOW it's not a Movie or Series — null means "show anyway"
        const type = await getItemType(itemId);
        if (type !== null && type !== 'Movie' && type !== 'Series') return;
        if (getItemId() !== itemId) return; // navigated away during async wait

        const widget = buildWidget();
        target.insertAdjacentElement('afterend', widget);
        renderWidget(widget, await apiGet(itemId));
        bindInteractions(widget, itemId);
    }

    // ── Navigation polling ────────────────────────────────────────────────
    // Polls every 800ms — the only reliable method across all SPA routers
    // and all custom Jellyfin themes, regardless of URL scheme used.

    let _lastUrl = '';

    function checkNav() {
        const url    = window.location.href;
        const itemId = getItemId();
        if (url === _lastUrl) return;
        _lastUrl = url;

        document.getElementById('ir-widget')?.remove();
        _injectItemId = null;

        if (onDetailPage() && itemId) {
            setTimeout(() => startInject(itemId), 700);
        }
    }

    function init() {
        setInterval(checkNav, 800);
        window.addEventListener('hashchange', () => setTimeout(checkNav, 400));
        window.addEventListener('popstate',   () => setTimeout(checkNav, 400));
        if (onDetailPage()) setTimeout(() => startInject(getItemId()), 1000);
    }

})();
