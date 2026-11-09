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
    extra: document.getElementById('extra'),
    meta: document.getElementById('meta'),
    shuffle: document.getElementById('btnShuffle'),
    prev: document.getElementById('btnPrev'),
    play: document.getElementById('btnPlay'),
    next: document.getElementById('btnNext'),
    fav: document.getElementById('btnFav'),
  };

  var socket = null;
  var retryMs = 1000;
  var retryTimer = null;
  var connected = false;
  var state = null;          // last NowPlaying payload from the API
  var fallback = null;       // last livelyCurrentTrack payload
  var lastArtUrl = null;
  var notice = '';           // short message such as a failed command, shown on the status line for a few seconds
  var noticeTimer = null;
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
      setStatus(connected ? (state && state.connected ? '' : 'Waiting for Music Assistant') : 'Connecting to ma-satellite…' + diag);
      setArt(null);
      el.progress.classList.add('hidden');
      renderControls();
      return;
    }

    el.card.classList.remove('idle');
    el.title.textContent = source.title;
    el.artist.textContent = source.artist || '';
    el.album.textContent = source.album || '';
    setStatus(source.status || '');
    setArt(source.art);
    renderProgress();
    renderControls();
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

  function frame(now) {
    renderProgress();
    tweenBackground();
    drawGL(now || 0);
    requestAnimationFrame(frame);
  }

  function setStatus(text) { el.status.textContent = notice || text; }

  function showNotice(text) {
    notice = text;
    el.status.textContent = text;
    clearTimeout(noticeTimer);
    noticeTimer = setTimeout(function () { notice = ''; render(); }, 4000);
  }

  // ---- Hover controls --------------------------------------------------------------------
  function apiLive() { return connected && state && state.connected; }

  function sendCommand(name) {
    return fetch(apiUrl('/api/command'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ command: name }),
    }).then(function (r) {
      if (r.ok) { return; }
      return r.text().then(function (t) { throw new Error(t || ('HTTP ' + r.status)); });
    });
  }

  /** Runs a command; `optimistic` flips the UI at once and `undo` puts it back if the command fails. */
  function runCommand(name, optimistic, undo) {
    if (optimistic) { optimistic(); renderControls(); }
    sendCommand(name).catch(function (err) {
      if (undo) { undo(); renderControls(); }
      showNotice(name + ' failed: ' + (err && err.message ? err.message : 'unknown error'));
    });
  }

  function renderControls() {
    var live = apiLive();
    el.extra.classList.toggle('offline', !live);
    if (!live) { return; }

    var supported = state.supported_commands || [];
    function can(names) {
      if (!supported.length) { return true; } // unknown list: let the server decide
      return names.some(function (n) { return supported.indexOf(n) >= 0; });
    }

    var playing = state.playback === 'playing';
    el.play.classList.toggle('is-playing', playing);
    el.play.title = playing ? 'Pause' : 'Play';
    el.play.setAttribute('aria-label', el.play.title);
    el.play.disabled = !can(['play', 'pause']);
    el.prev.disabled = !can(['previous']);
    el.next.disabled = !can(['next']);
    el.shuffle.disabled = !can(['shuffle', 'unshuffle']);
    el.shuffle.setAttribute('aria-pressed', state.shuffle ? 'true' : 'false');
    el.shuffle.title = state.shuffle ? 'Shuffle on' : 'Shuffle off';

    // `favorite` is absent on older ma-satellite builds (hide the heart) and null while unknown (show it disabled).
    var hasFav = Object.prototype.hasOwnProperty.call(state, 'favorite');
    el.fav.classList.toggle('hidden', !hasFav);
    el.fav.disabled = state.favorite == null;
    el.fav.setAttribute('aria-pressed', state.favorite === true ? 'true' : 'false');
    el.fav.title = state.favorite == null ? 'Favourites need the track to be in your Music Assistant queue'
      : (state.favorite ? 'Remove from favourites' : 'Add to favourites');

    var parts = [];
    if (state.year) { parts.push(state.year); }
    if (state.track) { parts.push('Track ' + state.track); }
    if (state.group) { parts.push(state.group); }
    el.meta.textContent = parts.join(' · ');
  }

  // A mouse click must not leave the button focused, or :focus-within would keep the panel open after the pointer leaves.
  [el.shuffle, el.prev, el.play, el.next, el.fav].forEach(function (b) {
    b.addEventListener('click', function (e) { if (e.detail > 0) { b.blur(); } });
  });

  el.play.addEventListener('click', function () {
    var was = state.playback;
    runCommand(was === 'playing' ? 'pause' : 'play',
      function () { state.playback = was === 'playing' ? 'paused' : 'playing'; },
      function () { state.playback = was; });
  });
  el.next.addEventListener('click', function () { runCommand('next'); });
  el.prev.addEventListener('click', function () { runCommand('previous'); });
  el.shuffle.addEventListener('click', function () {
    var was = !!state.shuffle;
    runCommand(was ? 'unshuffle' : 'shuffle', function () { state.shuffle = !was; }, function () { state.shuffle = was; });
  });
  el.fav.addEventListener('click', function () {
    var was = state.favorite === true;
    runCommand(was ? 'unfavorite' : 'favorite', function () { state.favorite = !was; }, function () { state.favorite = was; });
  });

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
      case 'flowSpeed': glFlowSpeed = Math.max(0, Math.min(2, Number(val) / 100)); break;
      case 'colorIntensity': glSat = Math.max(0, Math.min(1, Number(val) / 100)); break;
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
  var NEUTRAL = ['#1e293b', '#3f5278', '#8fa3c4']; // slate, for black/white/grey covers

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
      if (s < 0.14 || max < 0.15) { continue; } // grey / near-black pixels carry no hue
      var hue;
      if (max === r) { hue = ((g - b) / d) % 6; } else if (max === g) { hue = (b - r) / d + 2; } else { hue = (r - g) / d + 4; }
      hue = (hue * 60 + 360) % 360;
      var weight = s * max; // vivid pixels count more than dull ones
      hist[Math.round(hue / 30) % 12] += weight;
      total += weight;
    }
    if (total < count * 0.03) { return { neutral: true }; }

    var sm = hist.map(function (v, k) { return v + 0.5 * (hist[(k + 11) % 12] + hist[(k + 1) % 12]); });
    var primary = 0;
    for (var k = 1; k < 12; k++) { if (sm[k] > sm[primary]) { primary = k; } }
    var secondary = -1, best = 0;
    for (k = 0; k < 12; k++) {
      var dist = Math.min((k - primary + 12) % 12, (primary - k + 12) % 12);
      if (dist >= 2 && sm[k] > best) { best = sm[k]; secondary = k; }
    }
    if (best < sm[primary] * 0.3) { secondary = -1; } // accent colour too faint to matter
    // Neighbouring family (towards whichever side the cover leans), used when there is no real second colour.
    var analogous = sm[(primary + 1) % 12] >= sm[(primary + 11) % 12] ? (primary + 1) % 12 : (primary + 11) % 12;
    return { primary: primary, secondary: secondary, analogous: analogous };
  }

  var bgCurrent = [NEUTRAL[0], NEUTRAL[1], NEUTRAL[1], NEUTRAL[2], NEUTRAL[0]].map(hexToRgb);
  var bgTarget = bgCurrent.slice();

  function applyPalette(pick) {
    var a = pick.neutral ? NEUTRAL : PALETTES[pick.primary];
    var accent = pick.neutral ? -1 : (pick.secondary >= 0 ? pick.secondary : pick.analogous); // a single-colour cover still gets a neighbouring accent
    var b = accent < 0 ? a : PALETTES[accent];
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


  // ---- WebGL liquid ---------------------------------------------------------------------
  // A domain-warped noise field, stretched into diagonal streaks and mapped through the five palette
  // colours: it reads like flowing paint. The CSS blobs stay as the fallback if WebGL is missing.
  var VERT = 'attribute vec2 p; void main(){ gl_Position = vec4(p, 0.0, 1.0); }';
  var FRAG = [
    'precision highp float;',
    'uniform vec2 u_res; uniform float u_time; uniform float u_sat;',
    'uniform vec3 u_c0; uniform vec3 u_c1; uniform vec3 u_c2; uniform vec3 u_c3; uniform vec3 u_c4;',
    'float hash(vec2 p){ p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }',
    'float noise(vec2 p){ vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);',
    '  return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y); }',
    'float fbm(vec2 p){ float v = 0.0; float a = 0.5; mat2 m = mat2(1.6, 1.2, -1.2, 1.6);',
    '  for (int i = 0; i < 5; i++) { v += a * noise(p); p = m * p; a *= 0.5; } return v; }',
    'vec3 ramp(float t){ t = clamp(t, 0.0, 1.0) * 4.0;',
    '  vec3 c = mix(u_c0, u_c1, smoothstep(0.0, 1.0, t));',
    '  c = mix(c, u_c2, smoothstep(1.0, 2.0, t));',
    '  c = mix(c, u_c3, smoothstep(2.0, 3.0, t));',
    '  return mix(c, u_c4, smoothstep(3.0, 4.0, t)); }',
    'void main(){',
    '  vec2 uv = gl_FragCoord.xy / u_res;',
    '  vec2 p = (uv - 0.5) * vec2(u_res.x / u_res.y, 1.0);',
    '  float a = -0.62; p = mat2(cos(a), -sin(a), sin(a), cos(a)) * p;',
    '  p *= vec2(0.75, 1.9);',                       // stretch into diagonal streaks
    '  float t = u_time * 0.05;',
    '  vec2 q = vec2(fbm(p + vec2(0.0, t)), fbm(p + vec2(5.2, 1.3) - vec2(t * 0.8, 0.0)));',
    '  vec2 r = vec2(fbm(p + 2.4 * q + vec2(1.7, 9.2) + t * 0.7), fbm(p + 2.4 * q + vec2(8.3, 2.8) - t * 0.55));',
    '  float f = fbm(p + 2.4 * r);',
    '  float k = smoothstep(0.12, 0.88, f * 0.85 + (q.x - 0.5) * 0.7 + 0.1);',
    '  vec3 col = ramp(k);',
    '  float sheen = smoothstep(0.5, 0.0, abs(fbm(p * 2.2 + r * 2.0 + t) - 0.5));',   // thin bright oily lines
    '  col += sheen * 0.10 * u_c3;',
    '  col *= mix(0.62, 1.08, smoothstep(1.15, 0.15, length(uv - 0.5)));',
    '  float luma = dot(col, vec3(0.299, 0.587, 0.114));',
    '  col = mix(vec3(luma), col, u_sat) * 0.94;',               // mute: pull the colour towards grey
    '  gl_FragColor = vec4(col, 1.0);',
    '}',
  ].join('\n');

  var gl = null, glUniforms = null;
  var glFlowSpeed = 0.2; // 1 = base speed; Lively slider 'flowSpeed' (percent)
  var glFlowTime = 0, glLastNow = 0;
  var glSat = 0.55; // colour intensity: 1 = full palette colours, 0 = grey (Lively slider 'colorIntensity')

  function initGL() {
    var canvas = document.getElementById('bgCanvas');
    try { gl = canvas.getContext('webgl', { antialias: false, alpha: false }); } catch (e) { gl = null; }
    if (!gl) { return; }
    function shader(type, src) {
      var s = gl.createShader(type);
      gl.shaderSource(s, src);
      gl.compileShader(s);
      return gl.getShaderParameter(s, gl.COMPILE_STATUS) ? s : null;
    }
    var vs = shader(gl.VERTEX_SHADER, VERT), fs = shader(gl.FRAGMENT_SHADER, FRAG);
    var prog = vs && fs ? gl.createProgram() : null;
    if (prog) {
      gl.attachShader(prog, vs); gl.attachShader(prog, fs); gl.linkProgram(prog);
      if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) { prog = null; }
    }
    if (!prog) { gl = null; return; }
    gl.useProgram(prog);
    var buf = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW); // one big triangle
    var loc = gl.getAttribLocation(prog, 'p');
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
    glUniforms = {
      res: gl.getUniformLocation(prog, 'u_res'), time: gl.getUniformLocation(prog, 'u_time'), sat: gl.getUniformLocation(prog, 'u_sat'),
      c: [0, 1, 2, 3, 4].map(function (i) { return gl.getUniformLocation(prog, 'u_c' + i); }),
    };
    canvas.addEventListener('webglcontextlost', function (e) { e.preventDefault(); gl = null; document.getElementById('bg').classList.remove('gl'); });
    document.getElementById('bg').classList.add('gl');
  }

  var RAMP_ORDER = [0, 1, 3, 2, 4]; // deep, mid, light, secondary mid, secondary deep
  var reducedMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  function drawGL(now) {
    if (!gl) { return; }
    var canvas = gl.canvas;
    var w = Math.max(2, Math.round(window.innerWidth * 0.5)), h = Math.max(2, Math.round(window.innerHeight * 0.5)); // half res is plenty for soft colour
    if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; gl.viewport(0, 0, w, h); }
    gl.uniform2f(glUniforms.res, w, h);
    gl.uniform1f(glUniforms.sat, glSat);
    var dt = glLastNow ? Math.min(now - glLastNow, 100) : 0; // cap so a pause/resume doesn't jump the flow
    glLastNow = now;
    glFlowTime += dt / 1000 * glFlowSpeed * (reducedMotion ? 0.15 : 1);
    gl.uniform1f(glUniforms.time, glFlowTime);
    for (var i = 0; i < 5; i++) {
      var c = bgCurrent[RAMP_ORDER[i]];
      gl.uniform3f(glUniforms.c[i], c[0] / 255, c[1] / 255, c[2] / 255);
    }
    gl.drawArrays(gl.TRIANGLES, 0, 3);
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
  window.__maWidget = { pickPalette: pickPalette, palettes: PALETTES, flowTime: function () { return glFlowTime; } }; // test hook

  initGL();
  render();
  connect();
  requestAnimationFrame(frame);
})();
