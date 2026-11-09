/* ma-satellite Now Playing widget.
 *
 * Data comes from the local ma-satellite API (WebSocket push, no polling):
 *   ws://127.0.0.1:<port>/api/events   -> {type:"state"|"progress", data:{...}}
 * If the app is not reachable the widget falls back to Lively's own media display
 * (livelyCurrentTrack), so it still shows what Windows is playing.
 */
(function () {
  'use strict';

  var query = new URLSearchParams(location.search);
  var settings = {
    host: query.get('host') || '127.0.0.1',
    port: parseInt(query.get('port') || '8929', 10),
  };

  var el = {
    card: document.getElementById('card'),
    artImg: document.getElementById('artImg'),
    placeholder: document.getElementById('artPlaceholder'),
    title: document.getElementById('title'),
    artist: document.getElementById('artist'),
    album: document.getElementById('album'),
    progress: document.getElementById('progress'),
    elapsed: document.getElementById('elapsed'),
    total: document.getElementById('total'),
    fill: document.getElementById('fill'),
    status: document.getElementById('status'),
  };

  var socket = null;
  var retryMs = 1000;
  var retryTimer = null;
  var connected = false;
  var state = null;          // last NowPlaying payload from the API
  var fallback = null;       // last livelyCurrentTrack payload
  var lastArtUrl = null;
  var diag = ' (v6)';           // why the last connection attempt failed, shown while disconnected

  function formatTime(ms) {
    var total = Math.max(0, Math.floor(ms / 1000));
    var h = Math.floor(total / 3600);
    var m = Math.floor((total % 3600) / 60);
    var s = total % 60;
    var ss = (s < 10 ? '0' : '') + s;
    return h > 0 ? h + ':' + (m < 10 ? '0' : '') + m + ':' + ss : m + ':' + ss;
  }

  function setArt(url) {
    if (url === lastArtUrl) { return; }
    lastArtUrl = url;
    if (!url) { applyPalette({ neutral: true }); }
    if (url) {
      el.artImg.crossOrigin = 'anonymous';
      el.artImg.src = url;
      el.artImg.hidden = false;
      el.placeholder.style.display = 'none';
    } else {
      el.artImg.removeAttribute('src');
      el.artImg.hidden = true;
      el.placeholder.style.display = '';
    }
  }

  function apiUrl(path) {
    return 'http://' + settings.host + ':' + settings.port + path;
  }

  /** Current position, extrapolated from the last server report. */
  function currentPosition() {
    if (!state || state.position_ms == null) { return null; }
    var elapsed = state.playback === 'playing' ? (Date.now() - state.position_updated_at) * (state.playback_speed || 1000) / 1000 : 0;
    var pos = state.position_ms + Math.max(0, elapsed);
    if (state.duration_ms) { pos = Math.min(pos, state.duration_ms); }
    return pos;
  }

  function render() {
    var source = null;
    if (connected && state && state.connected) {
      source = {
        title: state.title, artist: state.artist, album: state.album,
        art: state.artwork_url ? apiUrl(state.artwork_url) : null,
        status: state.playback === 'playing' ? '' : (state.playback === 'paused' ? 'Paused' : ''),
      };
    } else if (fallback && fallback.Title) {
      source = {
        title: fallback.Title, artist: fallback.Artist, album: fallback.AlbumTitle,
        art: fallback.Thumbnail ? 'data:image/png;base64,' + fallback.Thumbnail : null,
        status: connected ? '' : 'via Windows media (ma-satellite not running)',
      };
    }

    if (!source || !source.title) {
      el.card.classList.add('idle');
      el.title.textContent = 'Nothing playing';
      el.artist.textContent = '';
      el.album.textContent = '';
      el.status.textContent = connected ? (state && state.connected ? '' : 'Waiting for Music Assistant') : 'Connecting to ma-satellite…' + diag;
      setArt(null);
      el.progress.classList.add('hidden');
      return;
    }

    el.card.classList.remove('idle');
    el.title.textContent = source.title;
    el.artist.textContent = source.artist || '';
    el.album.textContent = source.album || '';
    el.status.textContent = source.status || '';
    setArt(source.art);
    renderProgress();
  }

  function renderProgress() {
    var pos = connected && state && state.connected ? currentPosition() : null;
    var dur = state && state.duration_ms;
    var visible = pos != null && dur > 0 && settings.showProgress !== false;
    el.progress.classList.toggle('hidden', !visible);
    if (!visible) { return; }
    el.elapsed.textContent = formatTime(pos);
    el.total.textContent = formatTime(dur);
    el.fill.style.width = Math.min(100, (pos / dur) * 100) + '%';
  }

  function onMessage(event) {
    var msg;
    try { msg = JSON.parse(event.data); } catch (e) { return; }
    if (!msg || !msg.data) { return; }
    if (msg.type === 'state') {
      state = msg.data;
      render();
    } else if (msg.type === 'progress' && state) {
      state.position_ms = msg.data.position_ms;
      state.position_updated_at = msg.data.position_updated_at;
      state.duration_ms = msg.data.duration_ms;
      state.playback = msg.data.playback;
      state.playback_speed = msg.data.playback_speed;
    }
  }

  function connect() {
    clearTimeout(retryTimer);
    if (socket) { try { socket.close(); } catch (e) { /* ignore */ } }
    var ws;
    try {
      ws = new WebSocket('ws://' + settings.host + ':' + settings.port + '/api/events');
    } catch (e) {
      diag = ' (' + e.name + ': ' + e.message + ')';
      render();
      scheduleRetry();
      return;
    }
    socket = ws;
    // If the handshake neither succeeds nor fails, say so and probe plain HTTP to tell the cases apart.
    setTimeout(function () {
      if (socket !== ws || connected) { return; }
      diag = ' [v6: handshake pending, readyState ' + ws.readyState + ', origin ' + location.origin + ']';
      render();
      try {
        fetch(apiUrl('/api/health')).then(function (r) {
          if (!connected) { diag += ' [health HTTP ' + r.status + ']'; render(); }
        }, function (err) {
          if (!connected) { diag += ' [health fetch failed: ' + err.message + ']'; render(); }
        });
      } catch (err) { /* ignore */ }
    }, 3000);
    ws.onopen = function () { connected = true; diag = ''; retryMs = 1000; render(); };
    ws.onmessage = onMessage;
    ws.onclose = function (e) {
      if (socket !== ws) { return; }
      connected = false;
      state = null;
      diag = ' (WebSocket closed, code ' + (e && e.code) + ', page origin ' + location.origin + ')';
      render();
      scheduleRetry();
    };
    ws.onerror = function () { if (!connected) { diag = ' [v6: socket error, origin ' + location.origin + ']'; render(); } };
  }

  function scheduleRetry() {
    clearTimeout(retryTimer);
    retryTimer = setTimeout(connect, retryMs);
    retryMs = Math.min(retryMs * 2, 10000);
  }

  function frame() {
    renderProgress();
    tweenBackground();
    requestAnimationFrame(frame);
  }

  /** Lively calls this with the system "now playing" info (SMTC, which ma-satellite feeds too). */
  window.livelyCurrentTrack = function (data) {
    try { fallback = typeof data === 'string' ? JSON.parse(data) : data; } catch (e) { fallback = null; }
    render();
  };

  /** Lively calls this for every customisation property. */
  window.livelyPropertyListener = function (name, val) {
    var root = document.documentElement;
    switch (name) {
      case 'apiPort':
        var p = parseInt(val, 10);
        if (p > 0 && p < 65536 && p !== settings.port) { settings.port = p; retryMs = 500; connect(); }
        break;
      case 'accent': root.style.setProperty('--accent', val); break;
      case 'opacity': root.style.setProperty('--card-opacity', String(val / 100)); break;
      case 'showAlbum': el.album.classList.toggle('hidden', !val); break;
      case 'fluidBackground': document.getElementById('bg').classList.toggle('off', !val); break;
      case 'showProgress': settings.showProgress = !!val; renderProgress(); break;
    }
  };


  // ---- Fluid background ------------------------------------------------------------------
  // The album art is only used to pick a colour *family*. The colours shown always come from this
  // fixed, hand-picked table (one row per 30 degrees of hue), so a muddy or clashing cover can
  // never turn the background ugly. Each row is [deep, mid, light].
  var PALETTES = [
    ['#b3123f', '#e8315b', '#ff7a93'], // 0   rose red
    ['#c2410c', '#f97316', '#ffb067'], // 30  orange
    ['#b45309', '#f59e0b', '#fcd34d'], // 60  amber (also catches olive/mustard)
    ['#4d7c0f', '#7cc43a', '#c6f08a'], // 90  lime
    ['#046c4e', '#10b981', '#6ee7b7'], // 120 emerald
    ['#0f766e', '#14b8a6', '#5eead4'], // 150 teal
    ['#0e7490', '#06b6d4', '#67e8f9'], // 180 cyan
    ['#1d4ed8', '#3b82f6', '#93c5fd'], // 210 blue
    ['#3730a3', '#6366f1', '#a5b4fc'], // 240 indigo
    ['#6d28d9', '#8b5cf6', '#c4b5fd'], // 270 violet
    ['#a21caf', '#d946ef', '#f0abfc'], // 300 fuchsia
    ['#be185d', '#ec4899', '#f9a8d4'], // 330 pink
  ];
  var NEUTRAL = ['#1f2937', '#475569', '#94a3b8']; // slate, for black/white/grey covers

  function hexToRgb(hex) {
    return [parseInt(hex.substr(1, 2), 16), parseInt(hex.substr(3, 2), 16), parseInt(hex.substr(5, 2), 16)];
  }

  /** Choose palette rows for an RGBA pixel array: {primary, secondary} (row indexes) or {neutral:true}. */
  function pickPalette(px) {
    var hist = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    var total = 0;
    var count = px.length / 4;
    for (var i = 0; i < px.length; i += 4) {
      if (px[i + 3] < 128) { continue; }
      var r = px[i] / 255, g = px[i + 1] / 255, b = px[i + 2] / 255;
      var max = Math.max(r, g, b), d = max - Math.min(r, g, b);
      var s = max === 0 ? 0 : d / max;
      if (s < 0.25 || max < 0.2) { continue; } // grey / near-black / washed-out pixels carry no hue
      var hue;
      if (max === r) { hue = ((g - b) / d) % 6; } else if (max === g) { hue = (b - r) / d + 2; } else { hue = (r - g) / d + 4; }
      hue = (hue * 60 + 360) % 360;
      var weight = s * max; // vivid pixels count more than dull ones
      hist[Math.round(hue / 30) % 12] += weight;
      total += weight;
    }
    if (total < count * 0.05) { return { neutral: true }; }

    var sm = hist.map(function (v, k) { return v + 0.5 * (hist[(k + 11) % 12] + hist[(k + 1) % 12]); });
    var primary = 0;
    for (var k = 1; k < 12; k++) { if (sm[k] > sm[primary]) { primary = k; } }
    var secondary = -1, best = 0;
    for (k = 0; k < 12; k++) {
      var dist = Math.min((k - primary + 12) % 12, (primary - k + 12) % 12);
      if (dist >= 2 && sm[k] > best) { best = sm[k]; secondary = k; }
    }
    if (best < sm[primary] * 0.3) { secondary = -1; } // accent colour too faint to matter
    return { primary: primary, secondary: secondary };
  }

  var bgCurrent = [NEUTRAL[0], NEUTRAL[1], NEUTRAL[1], NEUTRAL[2], NEUTRAL[0]].map(hexToRgb);
  var bgTarget = bgCurrent.slice();

  function applyPalette(pick) {
    var a = pick.neutral ? NEUTRAL : PALETTES[pick.primary];
    var b = pick.neutral || pick.secondary < 0 ? a : PALETTES[pick.secondary];
    bgTarget = [a[0], a[1], b[1], a[2], b[0]].map(hexToRgb);
  }

  function sampleArt(img) {
    try {
      var canvas = document.createElement('canvas');
      canvas.width = canvas.height = 32;
      var ctx = canvas.getContext('2d');
      ctx.drawImage(img, 0, 0, 32, 32);
      applyPalette(pickPalette(ctx.getImageData(0, 0, 32, 32).data));
    } catch (e) {
      applyPalette({ neutral: true }); // e.g. a cross-origin image we may not read
    }
  }

  /** Ease the displayed colours towards the target so a track change fades instead of jumping. */
  function tweenBackground() {
    var root = document.documentElement;
    for (var i = 0; i < bgCurrent.length; i++) {
      var cur = bgCurrent[i], tgt = bgTarget[i], moved = false;
      for (var k = 0; k < 3; k++) {
        var diff = tgt[k] - cur[k];
        if (Math.abs(diff) > 0.4) { cur[k] += diff * 0.04; moved = true; } else if (diff !== 0) { cur[k] = tgt[k]; moved = true; }
      }
      if (moved) {
        root.style.setProperty('--bg' + (i + 1), 'rgb(' + Math.round(cur[0]) + ',' + Math.round(cur[1]) + ',' + Math.round(cur[2]) + ')');
      }
    }
  }

  el.artImg.crossOrigin = 'anonymous'; // lets us read the pixels (the API sends CORS headers for allowed origins)
  el.artImg.onload = function () { sampleArt(el.artImg); };
  el.artImg.onerror = function () {
    // If the CORS-enabled request failed, still show the cover (just without colour sampling).
    if (el.artImg.crossOrigin && lastArtUrl) {
      el.artImg.removeAttribute('crossorigin');
      el.artImg.src = lastArtUrl;
      applyPalette({ neutral: true });
    }
  };
  window.__maWidget = { pickPalette: pickPalette, palettes: PALETTES }; // test hook

  render();
  connect();
  requestAnimationFrame(frame);
})();
