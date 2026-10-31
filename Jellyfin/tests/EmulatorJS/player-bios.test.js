'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const players = [
  path.resolve(__dirname, '../../backend/EmulatorJS/player.html'),
  path.resolve(__dirname, '../../../Emby/Emby.Plugins.Moonfin/EmulatorJS/player.html')
];

function inlineScripts(html) {
  const scripts = [];
  const re = /<script>([\s\S]*?)<\/script>/gi;
  let match;
  while ((match = re.exec(html))) scripts.push(match[1]);
  return scripts.join('\n');
}

function boot(search, fetchImpl) {
  const errors = [];
  const appended = [];
  const windowObject = {};
  const document = {
    getElementById() {
      const node = { style: {} };
      Object.defineProperty(node, 'textContent', {
        set(value) { errors.push(value); },
        get() { return errors.at(-1) || ''; }
      });
      return node;
    },
    createElement() { return {}; },
    body: { appendChild(el) { appended.push(el); } }
  };
  const location = {
    origin: 'http://jellyfin.local',
    href: 'http://jellyfin.local/Moonfin/EmulatorJS/player.html' + search,
    search
  };
  return { errors, appended, window: windowObject, document, location, fetch: fetchImpl };
}

function run(html, search, fetchImpl, extras) {
  const env = boot(search, fetchImpl || (async () => { throw new Error('unexpected fetch'); }));
  const sandbox = {
    window: env.window,
    document: env.document,
    location: env.location,
    self: env.window,
    fetch: env.fetch,
    URL,
    URLSearchParams,
    Promise,
    decodeURIComponent,
    ArrayBuffer,
    Uint8Array,
    Uint16Array,
    DataView,
    requestAnimationFrame() {},
    navigator: { getGamepads: () => [] }
  };
  if (extras) {
    Object.keys(extras).forEach((key) => { sandbox[key] = extras[key]; });
  }
  vm.runInNewContext(inlineScripts(html), sandbox, { filename: 'player.html' });
  return env;
}

function filesOf(windowObject) {
  return JSON.parse(JSON.stringify(windowObject.EJS_externalFiles));
}

async function settle() {
  await new Promise((resolve) => setImmediate(resolve));
}

for (const playerPath of players) {
  const html = fs.readFileSync(playerPath, 'utf8');
  const label = playerPath.includes(`${path.sep}Emby${path.sep}`) ? 'emby' : 'jellyfin';

  test(`${label} keeps EJS_biosUrl for non-arcade cores`, async () => {
    const bios = '/Moonfin/Games/lib/Bios/tok/fds.zip?ApiKey=k';
    const env = run(html, `?core=snes&rom=/rom.sfc&bios=${encodeURIComponent(bios)}`);
    assert.equal(env.window.EJS_biosUrl, bios);
    assert.equal(env.window.EJS_externalFiles, undefined);
    assert.equal(env.window.moonfinBeforeLoader, undefined);
    assert.equal(env.appended.length, 1);
    assert.equal(env.appended[0].src, '__EJS_PATHTODATA__loader.js');
  });

  test(`${label} arcade zip URL is an external file and is not extracted`, async () => {
    const bios = '/Moonfin/Games/lib/Bios/tok/neogeo.zip?ApiKey=k';
    const env = run(html, `?core=arcade&rom=${encodeURIComponent('/Moonfin/Games/lib/Rom/g/svc.zip')}&bios=${encodeURIComponent(bios)}`);
    assert.equal(env.appended.length, 0);
    assert.equal(env.window.EJS_biosUrl, undefined);
    await env.window.moonfinBeforeLoader;
    await settle();
    assert.deepEqual(filesOf(env.window), { '/neogeo.zip': bios });
    assert.equal(env.appended.length, 1);
    assert.equal(env.appended[0].src, '__EJS_PATHTODATA__loader.js');
  });

  test(`${label} mame uses the same external zip path`, async () => {
    const bios = 'http://jellyfin.local/Moonfin/Games/lib/Bios/tok/pgm.zip';
    const env = run(html, `?core=mame&rom=/r.zip&bios=${encodeURIComponent(bios)}`);
    await env.window.moonfinBeforeLoader;
    await settle();
    assert.deepEqual(filesOf(env.window), { '/pgm.zip': bios });
    assert.equal(env.window.EJS_biosUrl, undefined);
  });

  test(`${label} biosName wins over the URL and skips HEAD`, async () => {
    let fetched = 0;
    const bios = '/Moonfin/Games/lib/Bios/tok?ApiKey=k';
    const env = run(
      html,
      `?core=arcade&rom=/r.zip&biosName=neogeo.zip&bios=${encodeURIComponent(bios)}`,
      async () => { fetched += 1; throw new Error('HEAD not allowed'); }
    );
    await env.window.moonfinBeforeLoader;
    await settle();
    assert.equal(fetched, 0);
    assert.deepEqual(filesOf(env.window), { '/neogeo.zip': bios });
  });

  test(`${label} old token BIOS URL is named from Content-Disposition`, async () => {
    const bios = '/Moonfin/Games/lib/Bios/tok?ApiKey=k';
    let resolveHead;
    const calls = [];
    const env = run(
      html,
      `?core=arcade&rom=/r.zip&bios=${encodeURIComponent(bios)}`,
      (url, init) => {
        calls.push({ url, init });
        return new Promise((resolve) => { resolveHead = resolve; });
      }
    );
    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, bios);
    assert.equal(calls[0].init.method, 'HEAD');
    assert.equal(calls[0].init.credentials, 'same-origin');
    assert.equal(env.appended.length, 0);
    assert.equal(env.window.EJS_biosUrl, undefined);
    resolveHead({
      ok: true,
      headers: {
        get(name) {
          assert.equal(name, 'Content-Disposition');
          return 'attachment; filename="ignored.zip"; filename*=UTF-8\'\'neogeo.zip';
        }
      }
    });
    const ok = await env.window.moonfinBeforeLoader;
    await settle();
    assert.equal(ok, true);
    assert.deepEqual(filesOf(env.window), { '/neogeo.zip': bios });
    assert.equal(env.appended.length, 1);
  });

  test(`${label} quoted Content-Disposition filename is accepted`, async () => {
    const bios = 'http://jellyfin.local/Moonfin/Games/lib/Bios/tok';
    const env = run(
      html,
      `?core=Arcade&rom=/r.zip&bios=${encodeURIComponent(bios)}`,
      async () => ({
        ok: true,
        headers: { get: () => 'attachment; filename="neogeo.zip"' }
      })
    );
    await env.window.moonfinBeforeLoader;
    await settle();
    assert.deepEqual(filesOf(env.window), { '/neogeo.zip': bios });
  });

  test(`${label} a missing BIOS name does not start the loader or set EJS_biosUrl`, async () => {
    const env = run(
      html,
      '?core=arcade&rom=/r.zip&bios=' + encodeURIComponent('https://cdn.example/bios/token'),
    );
    const ok = await env.window.moonfinBeforeLoader;
    await settle();
    assert.equal(ok, false);
    assert.equal(env.window.EJS_biosUrl, undefined);
    assert.equal(env.window.EJS_externalFiles, undefined);
    assert.equal(env.appended.length, 0);
    assert.equal(env.errors[0], 'Could not determine the arcade BIOS file name.');
  });

  test(`${label} parent query param is a second raw zip`, async () => {
    const bios = '/Moonfin/Games/lib/Bios/tok/neogeo.zip';
    const parent = '/Moonfin/Games/lib/Rom/p/svc.zip?ApiKey=k';
    const env = run(
      html,
      `?core=arcade&rom=/svcplus.zip&bios=${encodeURIComponent(bios)}&parent=${encodeURIComponent(parent)}`
    );
    await env.window.moonfinBeforeLoader;
    await settle();
    assert.deepEqual(filesOf(env.window), {
      '/neogeo.zip': bios,
      '/svc.zip': parent
    });
  });

  test(`${label} a parent without a file name does not drop the BIOS zip`, async () => {
    const bios = '/Moonfin/Games/lib/Bios/tok/neogeo.zip';
    const parent = '/Moonfin/Games/lib/Rom/p';
    let heads = 0;
    const env = run(
      html,
      `?core=arcade&rom=/svcplus.zip&bios=${encodeURIComponent(bios)}&parent=${encodeURIComponent(parent)}`,
      async () => { heads += 1; return { ok: false, headers: { get: () => null } }; }
    );
    const ok = await env.window.moonfinBeforeLoader;
    await settle();
    assert.equal(ok, true);
    assert.equal(heads, 1);
    assert.deepEqual(filesOf(env.window), { '/neogeo.zip': bios });
  });

  test(`${label} fbneo and mame2003_plus keep the BIOS zip`, async () => {
    const bios = '/Moonfin/Games/lib/Bios/tok/neogeo.zip';
    for (const core of ['fbneo', 'mame2003', 'mame2003_plus']) {
      const env = run(html, `?core=${core}&rom=/svc.zip&bios=${encodeURIComponent(bios)}`);
      await env.window.moonfinBeforeLoader;
      await settle();
      assert.equal(env.window.EJS_biosUrl, undefined);
      assert.deepEqual(filesOf(env.window), { '/neogeo.zip': bios });
    }
  });

  test(`${label} writeFile converts an ArrayBuffer and views to Uint8Array`, () => {
    const env = run(html, '?core=snes&rom=/r.sfc');
    const seen = [];
    function GameManager() {}
    GameManager.prototype.writeFile = function (filePath, data) { seen.push(data); };
    env.window.EJS_GameManager = GameManager;
    const bytes = new Uint8Array([1, 2, 3, 4]);
    const gm = new env.window.EJS_GameManager();
    gm.writeFile('/neogeo.zip', bytes.buffer);
    assert.ok(seen[0] instanceof Uint8Array);
    assert.equal(seen[0].byteLength, 4);
    assert.deepEqual(Array.from(seen[0]), [1, 2, 3, 4]);
    const view = new Uint16Array([0x201]);
    gm.writeFile('/pgm.zip', view);
    assert.ok(seen[1] instanceof Uint8Array);
    assert.deepEqual(Array.from(seen[1]), Array.from(new Uint8Array(view.buffer, view.byteOffset, view.byteLength)));
  });

  test(`${label} a failed external BIOS download is shown`, async () => {
    function NativeXhr() {
      this.status = 0;
      this.listeners = {};
    }
    NativeXhr.prototype.open = function (method, url) { this.url = url; };
    NativeXhr.prototype.send = function () {};
    NativeXhr.prototype.addEventListener = function (name, fn) {
      this.listeners[name] = fn;
    };
    const bios = '/Moonfin/Games/lib/Bios/tok/neogeo.zip?ApiKey=k';
    const env = run(
      html,
      `?core=arcade&rom=/svc.zip&bios=${encodeURIComponent(bios)}`,
      undefined,
      { XMLHttpRequest: NativeXhr }
    );
    await env.window.moonfinBeforeLoader;
    await settle();
    const xhr = new env.window.XMLHttpRequest();
    xhr.open('GET', bios);
    xhr.status = 404;
    xhr.listeners.load();
    assert.equal(env.errors.at(-1), 'Could not download /neogeo.zip.');
  });

  test(`${label} does not keep a second copy of a large ROM`, () => {
    const env = run(html, '?core=arcade&rom=/svc.zip');
    assert.equal(env.window.EJS_CacheLimit, 0);
    assert.equal(env.window.moonfinIos, false);
    assert.equal(env.window.EJS_defaultOptions, undefined);
  });

  test(`${label} frees a tracked download ArrayBuffer after writeFile`, () => {
    const env = run(html, '?core=arcade&rom=/svc.zip');
    const seen = [];
    function GameManager() {}
    GameManager.prototype.writeFile = function (filePath, data) { seen.push(data); };
    env.window.EJS_GameManager = GameManager;
    const raw = new Uint8Array([9, 8, 7, 6]).buffer;
    env.window.moonfinDownloadBuffers.add(raw);
    const gm = new env.window.EJS_GameManager();
    gm.writeFile('/svc.zip', raw);
    assert.ok(seen[0] instanceof Uint8Array);
    assert.deepEqual(Array.from(seen[0]), [9, 8, 7, 6]);
    assert.equal(raw.byteLength, 0);
  });

  test(`${label} iOS turns off the 60s autosave and the extra buffers`, () => {
    const env = run(html, '?core=arcade&rom=/svc.zip', undefined, {
      navigator: {
        userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_7 like Mac OS X) AppleWebKit/605.1.15',
        platform: 'iPhone',
        maxTouchPoints: 5,
        getGamepads: () => []
      }
    });
    assert.equal(env.window.moonfinIos, true);
    assert.equal(env.window.devicePixelRatio, 1);
    assert.equal(env.window.EJS_forceLegacyCores, true);
    assert.equal(env.window.EJS_defaultOptions.rewindEnabled, 'disabled');
    assert.equal(env.window.EJS_defaultOptions.shader, 'disabled');
    assert.equal(env.window.EJS_defaultOptions.fastForward, 'disabled');
    assert.equal(env.window.EJS_defaultOptions['ff-ratio'], '1.0');
    assert.equal(env.window.EJS_defaultOptions['save-save-interval'], '0');
    assert.equal(env.window.EJS_defaultOptions.webgl2Enabled, 'disabled');
    let written = '';
    function GameManager() {}
    GameManager.prototype.writeFile = function (filePath, data) { written = data; };
    env.window.EJS_GameManager = GameManager;
    const gm = new env.window.EJS_GameManager();
    gm.writeFile(
      '/home/web_user/.config/retroarch/retroarch.cfg',
      'autosave_interval = 60\naudio_latency = 64\nfastforward_ratio = 3.0\nrewind_enable = true\nvideo_smooth = false\n'
    );
    assert.match(written, /autosave_interval = 0/);
    assert.doesNotMatch(written, /autosave_interval = 60/);
    assert.match(written, /audio_latency = 32/);
    assert.match(written, /rewind_enable = false/);
    assert.match(written, /fastforward_ratio = 1.0/);
    assert.match(written, /video_shader_enable = false/);
    assert.match(written, /run_ahead_enabled = false/);
  });

  test(`${label} debug=1 logs the JS heap and the WASM heap`, () => {
    const logs = [];
    const timer = {};
    const env = run(html, '?core=snes&rom=/r.sfc&debug=1', undefined, {
      console: { log: (...args) => logs.push(args.join(' ')) },
      performance: { memory: { usedJSHeapSize: 1234 } },
      setInterval(fn, ms) {
        timer.fn = fn;
        timer.ms = ms;
        return 7;
      }
    });
    assert.equal(timer.ms, 5000);
    env.window.EJS_emulator = { Module: { HEAPU8: { byteLength: 4096 } } };
    timer.fn();
    assert.match(logs.at(-1), /jsHeap=1234/);
    assert.match(logs.at(-1), /wasmHeap=4096/);
  });
}
