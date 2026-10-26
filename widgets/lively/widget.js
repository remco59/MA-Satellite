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
    if (url) {
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
      el.status.textContent = connected ? (state && state.connected ? '' : 'Waiting for Music Assistant') : 'Connecting to ma-satellite…';
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
      scheduleRetry();
      return;
    }
    socket = ws;
    ws.onopen = function () { connected = true; retryMs = 1000; render(); };
    ws.onmessage = onMessage;
    ws.onclose = function () {
      if (socket !== ws) { return; }
      connected = false;
      state = null;
      render();
      scheduleRetry();
    };
    ws.onerror = function () { /* onclose follows */ };
  }

  function scheduleRetry() {
    clearTimeout(retryTimer);
    retryTimer = setTimeout(connect, retryMs);
    retryMs = Math.min(retryMs * 2, 10000);
  }

  function frame() {
    renderProgress();
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
      case 'showProgress': settings.showProgress = !!val; renderProgress(); break;
    }
  };

  render();
  connect();
  requestAnimationFrame(frame);
})();
