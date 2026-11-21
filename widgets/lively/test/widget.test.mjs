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
  const commands = [];
  const bodies = [];
  let commandReply = { status: 200, body: '{"status":"ok"}' };
  await page.route('http://127.0.0.1:8929/api/command', r => {
    const body = JSON.parse(r.request().postData());
    commands.push(body.command);
    bodies.push(body);
    r.fulfill({ status: commandReply.status, contentType: 'text/plain', body: commandReply.body });
  });
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

  await check('hovering the card reveals info and controls that send commands', async () => {
    const push = over => sockets.at(-1).send(JSON.stringify(nowPlaying({ supported_commands: ['play', 'pause', 'next', 'previous', 'shuffle', 'unshuffle'], ...over })));
    const last = () => commands.at(-1);
    const opacity = () => page.evaluate(() => getComputedStyle(document.getElementById('extra')).opacity);

    push({ playback: 'playing', shuffle: false });
    await page.waitForFunction(() => document.getElementById('meta').textContent === '2000 · Track 1 · Living room');
    await page.mouse.move(0, 0);
    await page.waitForFunction(() => getComputedStyle(document.getElementById('extra')).opacity === '0');

    await page.hover('#card');
    await page.waitForFunction(() => getComputedStyle(document.getElementById('extra')).opacity === '1');
    assert.equal(await page.isVisible('#btnPlay'), true);

    await page.click('#btnPlay');                                   // playing -> pause
    assert.equal(last(), 'pause');
    push({ playback: 'paused', shuffle: false });
    await page.waitForFunction(() => !document.getElementById('btnPlay').classList.contains('is-playing'));
    await page.click('#btnPlay');                                   // paused -> play
    assert.equal(last(), 'play');

    await page.click('#btnNext');
    assert.equal(last(), 'next');
    await page.click('#btnPrev');
    assert.equal(last(), 'previous');

    push({ playback: 'playing', shuffle: false });
    await page.waitForFunction(() => document.getElementById('btnPlay').classList.contains('is-playing'));
    await page.click('#btnShuffle');
    assert.equal(last(), 'shuffle');
    assert.equal(await page.getAttribute('#btnShuffle', 'aria-pressed'), 'true');   // optimistic
    await page.click('#btnShuffle');
    assert.equal(last(), 'unshuffle');
    assert.equal(await page.getAttribute('#btnShuffle', 'aria-pressed'), 'false');

    // Heart: hidden on builds that do not report `favorite`, disabled while unknown, a toggle otherwise.
    assert.equal(await page.isVisible('#btnFav'), false);
    push({ playback: 'playing', favorite: null });
    await page.waitForFunction(() => document.getElementById('btnFav').disabled && !document.getElementById('btnFav').classList.contains('hidden'));
    push({ playback: 'playing', favorite: false });
    await page.waitForFunction(() => !document.getElementById('btnFav').disabled);
    await page.click('#btnFav');
    assert.equal(last(), 'favorite');
    assert.equal(await page.getAttribute('#btnFav', 'aria-pressed'), 'true');
    await page.click('#btnFav');
    assert.equal(last(), 'unfavorite');
    assert.equal(await page.getAttribute('#btnFav', 'aria-pressed'), 'false');

    // Commands the player does not support are disabled.
    push({ playback: 'playing', favorite: false, supported_commands: ['play', 'pause'] });
    await page.waitForFunction(() => document.getElementById('btnNext').disabled && !document.getElementById('btnPlay').disabled);

    // A failing command is undone and explained on the status line.
    push({ playback: 'playing', favorite: false });
    await page.waitForFunction(() => !document.getElementById('btnFav').disabled);
    commandReply = { status: 409, body: 'Favourites need the track to be in your Music Assistant queue.' };
    await page.click('#btnFav');
    await page.waitForFunction(() => document.getElementById('status').textContent.includes('failed: Favourites need the track'));
    assert.equal(await page.getAttribute('#btnFav', 'aria-pressed'), 'false');
    commandReply = { status: 200, body: '{"status":"ok"}' };

    await page.mouse.move(0, 0);
    await page.waitForFunction(() => getComputedStyle(document.getElementById('extra')).opacity === '0');
    assert.ok(await opacity() === '0');
  });

  await check('repeat cycles off, all, one and the progress bar seeks by click and drag', async () => {
    const supported = ['play', 'pause', 'next', 'previous', 'shuffle', 'unshuffle', 'seek', 'repeat_off', 'repeat_all', 'repeat_one'];
    const push = over => sockets.at(-1).send(JSON.stringify(nowPlaying({ supported_commands: supported, playback: 'paused', ...over })));
    const mode = () => page.getAttribute('#btnRepeat', 'data-mode');
    const until = async (cond, what) => { for (let i = 0; i < 60 && !cond(); i++) { await page.waitForTimeout(50); } assert.ok(cond(), 'timed out waiting for ' + what); };

    await page.hover('#card');
    await page.waitForTimeout(700); // let the card finish growing so positions are stable

    push({ repeat: 'off' });
    await page.waitForFunction(() => document.getElementById('btnRepeat').dataset.mode === 'off' && !document.getElementById('btnRepeat').disabled);
    await page.click('#btnRepeat');
    await until(() => commands.at(-1) === 'repeat_all', 'repeat_all');
    assert.equal(await mode(), 'all');
    assert.equal(await page.getAttribute('#btnRepeat', 'aria-pressed'), 'true');
    push({ repeat: 'all' });
    await page.waitForFunction(() => document.getElementById('btnRepeat').dataset.mode === 'all');
    await page.click('#btnRepeat');
    await until(() => commands.at(-1) === 'repeat_one', 'repeat_one');
    assert.equal(await mode(), 'one');
    push({ repeat: 'one' });
    await page.waitForFunction(() => document.getElementById('btnRepeat').dataset.mode === 'one');
    await page.click('#btnRepeat');
    await until(() => commands.at(-1) === 'repeat_off', 'repeat_off');
    assert.equal(await page.getAttribute('#btnRepeat', 'aria-pressed'), 'false');

    // Seeking: a click on the bar jumps to that point ...
    push({ repeat: 'off', position_ms: 30000, position_updated_at: Date.now() });
    await page.waitForFunction(() => document.getElementById('progress').classList.contains('seekable'));
    await page.waitForTimeout(700);
    let box = await page.locator('#bar').boundingBox();
    await page.mouse.click(box.x + box.width * 0.5, box.y + box.height / 2);
    await until(() => bodies.at(-1).command === 'seek', 'click seek');
    assert.ok(Math.abs(bodies.at(-1).position_ms - 125500) < 2500, 'click seek ' + bodies.at(-1).position_ms);

    // ... and dragging scrubs: the time follows the pointer and one seek is sent on release.
    push({ position_ms: 30000, position_updated_at: Date.now() });
    await page.waitForTimeout(700);
    box = await page.locator('#bar').boundingBox();
    const y = box.y + box.height / 2;
    const before = bodies.length;
    await page.mouse.move(box.x + box.width * 0.2, y);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width * 0.8, y, { steps: 6 });
    await page.waitForFunction(() => document.getElementById('elapsed').textContent === '3:20');
    assert.equal(bodies.length, before, 'no command while dragging');
    await page.mouse.up();
    await until(() => bodies.length === before + 1, 'drag seek');
    assert.equal(bodies.at(-1).command, 'seek');
    assert.ok(Math.abs(bodies.at(-1).position_ms - 200800) < 2500, 'drag seek ' + bodies.at(-1).position_ms);

    // Without 'seek' in the supported list the bar is not interactive, and repeat is disabled without repeat commands.
    push({ supported_commands: ['play', 'pause'] });
    await page.waitForFunction(() => !document.getElementById('progress').classList.contains('seekable') && document.getElementById('btnRepeat').disabled);
    const n = bodies.length;
    box = await page.locator('#bar').boundingBox();
    await page.mouse.click(box.x + box.width * 0.5, box.y + box.height / 2);
    await page.waitForTimeout(300);
    assert.equal(bodies.length, n);

    await page.mouse.move(0, 0);
    await page.waitForFunction(() => getComputedStyle(document.getElementById('extra')).opacity === '0');
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
