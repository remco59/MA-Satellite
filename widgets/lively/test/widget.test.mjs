// Browser check for the Lively widget. Run from the repo root:
//   node widgets/lively/test/widget.test.mjs
// Uses the Playwright that ships with the dev container; CI does not run it (no browsers on the runner).
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import assert from 'node:assert/strict';

const { chromium } = createRequire(import.meta.url)('playwright');

const here = path.dirname(fileURLToPath(import.meta.url));
const page_url = 'file://' + path.resolve(here, '..', 'index.html') + '?port=8929';
const art = readFileSync(path.join(here, 'sample-art.png'));

const nowPlaying = (over = {}) => ({
  type: 'state',
  data: {
    connected: true, server: 'Music Assistant', group: 'Living room', playback: 'playing',
    title: 'Everything In Its Right Place', artist: 'Radiohead', album_artist: 'Radiohead', album: 'Kid A',
    year: 2000, track: 1, duration_ms: 251000, position_ms: 30000, position_updated_at: Date.now(),
    playback_speed: 1000, artwork_url: '/api/artwork?v=1', volume: 40, muted: false, in_sync: true,
    shuffle: false, repeat: 'off', supported_commands: ['play', 'pause', 'next'], ...over,
  },
});

const browser = await chromium.launch();
const results = [];
const check = async (name, fn) => { await fn(); results.push(name); console.log('ok  -', name); };

try {
  const context = await browser.newContext({ viewport: { width: 480, height: 190 }, deviceScaleFactor: 2 });
  const page = await context.newPage();
  const polled = [];
  let sockets = [];
  await page.route('http://127.0.0.1:8929/api/artwork*', r => r.fulfill({ contentType: 'image/png', body: art }));
  await page.route('http://127.0.0.1:8929/api/now-playing*', r => { polled.push(r.request().url()); r.fulfill({ status: 500 }); });
  await page.routeWebSocket('ws://127.0.0.1:8929/api/events', ws => { sockets.push(ws); ws.send(JSON.stringify(nowPlaying())); });

  await page.goto(page_url);

  await check('renders the initial state pushed over the socket', async () => {
    await page.waitForFunction(() => document.getElementById('title').textContent.includes('Everything'));
    assert.equal(await page.textContent('#artist'), 'Radiohead');
    assert.equal(await page.textContent('#album'), 'Kid A');
    assert.equal(await page.textContent('#total'), '4:11');
    await page.waitForFunction(() => document.getElementById('artImg').complete && document.getElementById('artImg').naturalWidth > 0);
    assert.equal(await page.isVisible('#artImg'), true);
  });

  await check('progress advances locally between server reports', async () => {
    const before = await page.textContent('#elapsed');
    await page.waitForTimeout(2200);
    const after = await page.textContent('#elapsed');
    const secs = s => s.split(':').reduce((a, b) => a * 60 + Number(b), 0);
    assert.ok(secs(after) - secs(before) >= 2, `elapsed ${before} -> ${after}`);
  });

  await check('a pushed state replaces the track without reloading', async () => {
    sockets[0].send(JSON.stringify(nowPlaying({ title: 'The National Anthem', position_ms: 0, artwork_url: '/api/artwork?v=2' })));
    await page.waitForFunction(() => document.getElementById('title').textContent === 'The National Anthem');
    assert.equal(await page.textContent('#elapsed'), '0:00');
  });

  await check('a progress event corrects the position', async () => {
    sockets[0].send(JSON.stringify({ type: 'progress', data: { position_ms: 120000, position_updated_at: Date.now(), duration_ms: 251000, playback: 'playing', playback_speed: 1000 } }));
    await page.waitForFunction(() => document.getElementById('elapsed').textContent === '2:00' || document.getElementById('elapsed').textContent === '2:01');
  });

  await check('pause freezes the progress bar and says Paused', async () => {
    sockets[0].send(JSON.stringify(nowPlaying({ playback: 'paused', position_ms: 60000 })));
    await page.waitForFunction(() => document.getElementById('status').textContent === 'Paused');
    const a = await page.textContent('#elapsed');
    await page.waitForTimeout(1500);
    assert.equal(await page.textContent('#elapsed'), a);
  });

  await check('never polls the REST endpoint', async () => {
    assert.equal(polled.length, 0, 'unexpected polling: ' + polled.join(','));
  });

  await check('shows waiting state when the server is not connected', async () => {
    sockets[0].send(JSON.stringify(nowPlaying({ connected: false, title: null, artist: null, album: null, playback: 'idle', artwork_url: null })));
    await page.waitForFunction(() => document.getElementById('title').textContent === 'Nothing playing');
    assert.equal(await page.textContent('#status'), 'Waiting for Music Assistant');
  });

  await check('reconnects after the socket drops', async () => {
    const count = sockets.length;
    await sockets[0].close();
    await page.waitForFunction(() => document.getElementById('status').textContent.startsWith('Connecting'));
    await page.waitForFunction(() => document.getElementById('title').textContent.includes('Everything'), null, { timeout: 8000 });
    assert.ok(sockets.length > count);
  });

  await check('snaps album colours to the fixed palette', async () => {
    const pick = rgbs => page.evaluate(list => {
      const px = new Uint8ClampedArray(4 * 200);
      for (let i = 0; i < 200; i++) { const c = list[i % list.length]; px.set([c[0], c[1], c[2], 255], i * 4); }
      return window.__maWidget.pickPalette(px);
    }, rgbs);
    assert.deepEqual(await pick([[30, 95, 214]]), { primary: 7, secondary: -1, analogous: 8 });            // blue -> blue
    assert.deepEqual(await pick([[107, 106, 42]]), { primary: 2, secondary: -1, analogous: 3 });           // muddy olive -> amber, not mud
    assert.deepEqual(await pick([[230, 99, 10]]), { primary: 1, secondary: -1, analogous: 2 });            // orange
    assert.equal((await pick([[143, 127, 166]])).primary, 9);                                 // washed-out purple still -> violet
    assert.deepEqual(await pick([[128, 128, 128]]), { neutral: true });                      // grey cover -> neutral slate
    assert.deepEqual(await pick([[10, 10, 10]]), { neutral: true });                         // near-black -> neutral
    const duo = await pick([[30, 95, 214], [30, 95, 214], [30, 95, 214], [220, 40, 70]]);   // dominant blue, red accent
    assert.equal(duo.primary, 7);
    assert.equal(duo.secondary, 0);
  });

  await check('the background colours come from the palette table', async () => {
    const allowed = await page.evaluate(() => window.__maWidget.palettes.flat().map(h => {
      const n = parseInt(h.slice(1), 16); return `rgb(${n >> 16},${(n >> 8) & 255},${n & 255})`;
    }));
    await page.waitForFunction(() => document.documentElement.style.getPropertyValue('--bg2') !== '', null, { timeout: 8000 });
    await page.waitForTimeout(3500); // let the fade settle
    for (const v of ['--bg1', '--bg2', '--bg3', '--bg4', '--bg5']) {
      const val = await page.evaluate(n => document.documentElement.style.getPropertyValue(n), v);
      assert.ok(allowed.includes(val), `${v}=${val} is not a palette colour`);
    }
  });

  await check('colour intensity slider is accepted', async () => {
    await page.evaluate(() => { window.livelyPropertyListener('colorIntensity', 30); window.livelyPropertyListener('colorIntensity', 55); });
  });

  await check('the flow speed slider scales the flow clock', async () => {
    if (!(await page.evaluate(() => document.getElementById('bg').classList.contains('gl')))) { return; } // no WebGL here: CSS fallback
    const rate = async pct => {
      await page.evaluate(v => window.livelyPropertyListener('flowSpeed', v), pct);
      const a = await page.evaluate(() => window.__maWidget.flowTime());
      await page.waitForTimeout(1500);
      return (await page.evaluate(() => window.__maWidget.flowTime()) - a) / 1.5;
    };
    const fast = await rate(100), slow = await rate(20), still = await rate(0);
    assert.ok(slow < fast * 0.35 && slow > fast * 0.1, `fast ${fast} slow ${slow}`);
    assert.ok(still < 0.001, `still ${still}`);
    await page.evaluate(() => window.livelyPropertyListener('flowSpeed', 20));
  });

  await check('the fluid background can be switched off', async () => {
    await page.evaluate(() => window.livelyPropertyListener('fluidBackground', false));
    assert.equal(await page.isVisible('#bg'), false);
    await page.evaluate(() => window.livelyPropertyListener('fluidBackground', true));
    assert.equal(await page.isVisible('#bg'), true);
  });

  await check('livelyPropertyListener restyles the card', async () => {
    await page.evaluate(() => { window.livelyPropertyListener('accent', '#ff8800'); window.livelyPropertyListener('showAlbum', false); });
    assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--accent').trim()), '#ff8800');
    assert.equal(await page.isVisible('#album'), false);
    await page.evaluate(() => { window.livelyPropertyListener('accent', '#40d6be'); window.livelyPropertyListener('showAlbum', true); });
  });

  // Preview screenshot with a stable, good-looking state.
  sockets.at(-1).send(JSON.stringify(nowPlaying({ position_ms: 96000, position_updated_at: Date.now() + 3_600_000 })));
  await page.waitForFunction(() => document.getElementById('title').textContent.includes('Everything'));
  await page.waitForTimeout(300);
  await page.locator('#card').screenshot({ path: path.resolve(here, '..', 'preview.png') });

  // Fallback to Lively's own media info when ma-satellite is not running.
  const offline = await context.newPage();
  await offline.routeWebSocket('ws://127.0.0.1:8929/api/events', ws => ws.close());
  await offline.goto(page_url);
  await check('falls back to livelyCurrentTrack when the API is unreachable', async () => {
    await offline.evaluate(() => window.livelyCurrentTrack(JSON.stringify({ Title: 'Windows Track', Artist: 'Someone', AlbumTitle: 'Disc', Thumbnail: null })));
    await offline.waitForFunction(() => document.getElementById('title').textContent === 'Windows Track');
    assert.match(await offline.textContent('#status'), /ma-satellite not running/);
  });
} finally {
  await browser.close();
}

console.log(`\n${results.length} checks passed`);
