// Helpers for the /trust (Install the HomeYarp root CA) page.
window.homeyarpTrust = {
    platform: function () {
        var ua = navigator.userAgent || '';
        if (/CrOS/.test(ua)) return 'chromeos';
        if (/Android/.test(ua)) return 'android';
        if (/iPhone|iPad|iPod/.test(ua)) return 'ios';
        if (/Windows/.test(ua)) return 'windows';
        if (/Mac OS X|Macintosh/.test(ua)) return 'macos';
        if (/Linux|X11/.test(ua)) return 'linux';
        return 'unknown';
    },
    // navigator.clipboard needs a secure context, and this page is plain http on the LAN — use execCommand.
    copy: function (text) {
        try {
            var t = document.createElement('textarea');
            t.value = text;
            t.setAttribute('readonly', '');
            t.style.position = 'fixed';
            t.style.opacity = '0';
            document.body.appendChild(t);
            t.select();
            var ok = document.execCommand('copy');
            document.body.removeChild(t);
            if (ok) return true;
        } catch (e) { }
        if (navigator.clipboard) {
            return navigator.clipboard.writeText(text).then(function () { return true; }, function () { return false; });
        }
        return false;
    },
    // A no-cors fetch resolves iff the TLS handshake (incl. certificate validation) succeeds; it rejects otherwise.
    probe: function (url) {
        var ctl = new AbortController();
        var timer = setTimeout(function () { ctl.abort(); }, 7000);
        return fetch(url, { mode: 'no-cors', cache: 'no-store', signal: ctl.signal })
            .then(function () { clearTimeout(timer); return 'trusted'; })
            .catch(function (e) { clearTimeout(timer); return e && e.name === 'AbortError' ? 'timeout' : 'untrusted'; });
    }
};
