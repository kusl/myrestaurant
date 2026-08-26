(function () {
    'use strict';

    var SURFACE_ELEMENT_ID = 'table-display-surface';
    var STALE_CLASS = 'is-stale';
    var TICK_MILLISECONDS = 1000;
    var FALLBACK_FRESH_FOR_MILLISECONDS = 90000;

    var observedToken = null;
    var observedAtMilliseconds = 0;
    var wakeLockSentinel = null;
    var wakeLockPending = false;

    function surfaceElement() {
        return document.getElementById(SURFACE_ELEMENT_ID);
    }

    function wakeLockSupported() {
        return typeof navigator !== 'undefined'
            && navigator.wakeLock
            && typeof navigator.wakeLock.request === 'function';
    }

    function requestWakeLock() {
        if (!wakeLockSupported() || wakeLockPending || wakeLockSentinel !== null || document.hidden) {
            return;
        }

        wakeLockPending = true;
        navigator.wakeLock.request('screen').then(function (sentinel) {
            wakeLockPending = false;
            wakeLockSentinel = sentinel;
            sentinel.addEventListener('release', function () {
                wakeLockSentinel = null;
            });
        }).catch(function () {

            wakeLockPending = false;
        });
    }

    function releaseWakeLock() {
        if (wakeLockSentinel === null) {
            return;
        }

        var sentinel = wakeLockSentinel;
        wakeLockSentinel = null;
        if (typeof sentinel.release === 'function') {
            sentinel.release().catch(function () {  });
        }
    }

    function freshForMilliseconds(element) {
        var declared = parseInt(element.getAttribute('data-fresh-for-ms') || '', 10);
        return isNaN(declared) || declared <= 0 ? FALLBACK_FRESH_FOR_MILLISECONDS : declared;
    }

    function tick() {
        var element = surfaceElement();
        if (!element) {

            observedToken = null;
            observedAtMilliseconds = 0;
            releaseWakeLock();
            return;
        }

        requestWakeLock();

        var token = element.getAttribute('data-refresh-token');
        if (token !== observedToken) {
            observedToken = token;
            observedAtMilliseconds = Date.now();
        }

        var stale = (Date.now() - observedAtMilliseconds) > freshForMilliseconds(element);
        if (stale) {
            element.classList.add(STALE_CLASS);
        } else {
            element.classList.remove(STALE_CLASS);
        }
    }

    document.addEventListener('visibilitychange', function () {
        if (document.hidden) {
            return;
        }

        requestWakeLock();
    });

    setInterval(tick, TICK_MILLISECONDS);
    tick();
})();
