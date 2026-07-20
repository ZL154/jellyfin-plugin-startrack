'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const widgetPath = path.join(__dirname, '..', 'InternalRatingSystem', 'Web', 'widget.js');
const source = fs.readFileSync(widgetPath, 'utf8');

function extractFunction(name) {
    const marker = `function ${name}(`;
    const start = source.indexOf(marker);
    assert.notEqual(start, -1, `${name} must exist`);

    const bodyStart = source.indexOf('{', start);
    let depth = 0;
    for (let i = bodyStart; i < source.length; i++) {
        if (source[i] === '{') depth++;
        if (source[i] === '}') {
            depth--;
            if (depth === 0) return source.slice(start, i + 1);
        }
    }
    throw new Error(`Could not find the end of ${name}`);
}

function fakeNode() {
    const classes = new Set();
    return {
        classes,
        classList: {
            toggle(name, enabled) {
                if (enabled) classes.add(name);
                else classes.delete(name);
            }
        }
    };
}

const syncNativeDetailRating = vm.runInNewContext(
    `(${extractFunction('_syncNativeDetailRating')})`
);

const nodes = [fakeNode(), fakeNode(), fakeNode()];
const scope = {
    querySelectorAll(selector) {
        assert.equal(selector, '.starRatingContainer');
        return nodes;
    }
};

syncNativeDetailRating(scope, true);
for (const node of nodes) assert.equal(node.classes.has('ir-native-rating-hidden'), true);

syncNativeDetailRating(scope, false);
for (const node of nodes) assert.equal(node.classes.has('ir-native-rating-hidden'), false);

// Jellyfin mounts its native rating asynchronously, sometimes well after the
// StarTrack badge. The navigation heartbeat must hide that late node too.
let badgeEmpty = false;
const badge = {
    classList: {
        contains(name) { return name === 'ir-page-badge-empty' && badgeEmpty; }
    }
};
const lateNodes = [];
const lateScope = {
    querySelector(selector) {
        assert.equal(selector, '#ir-page-badge, .ir-page-badge, [data-st-page-badge]');
        return badge;
    },
    querySelectorAll(selector) {
        assert.equal(selector, '.starRatingContainer');
        return lateNodes;
    }
};
const runtimeConfig = { replaceMediaDetailsRating: true };
const refreshNativeDetailRating = vm.runInNewContext(
    `(${extractFunction('_refreshNativeDetailRating')})`,
    {
        _visiblePageScope: () => lateScope,
        _syncNativeDetailRating: syncNativeDetailRating,
        _STARTRACK_CONFIG: runtimeConfig
    }
);

refreshNativeDetailRating();
const lateNode = fakeNode();
lateNodes.push(lateNode);
refreshNativeDetailRating();
assert.equal(lateNode.classes.has('ir-native-rating-hidden'), true,
    'a native rating inserted after the badge must still be hidden');

runtimeConfig.replaceMediaDetailsRating = false;
refreshNativeDetailRating();
assert.equal(lateNode.classes.has('ir-native-rating-hidden'), false,
    'disabling replacement must restore Jellyfin\'s native rating');

runtimeConfig.replaceMediaDetailsRating = true;
badgeEmpty = true;
refreshNativeDetailRating();
assert.equal(lateNode.classes.has('ir-native-rating-hidden'), false,
    'an unrated StarTrack badge must not hide Jellyfin\'s native rating');

assert.equal(source.includes('function startMediaDetailsReplace'), false,
    'the retired duplicate renderer must not return');
assert.equal(source.includes("className = 'ir-detail-st'"), false,
    'no production path may create the legacy badge');
assert.match(source,
    /_syncNativeDetailRating\(scope, hasRatings && _STARTRACK_CONFIG\.replaceMediaDetailsRating\)/,
    'the canonical badge owner must apply the replacement setting');
const refreshCall = source.indexOf('if (id) _refreshNativeDetailRating();');
const unchangedRouteReturn = source.indexOf('if (idStr === _lastId && hash === _lastHash) return;');
assert.ok(refreshCall !== -1 && refreshCall < unchangedRouteReturn,
    'the native-rating refresh must run before the unchanged-route fast path');

console.log('widget media-details rating regression: passed');
