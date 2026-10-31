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

function run(html, search, fetchImpl) {
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
    requestAnimationFrame() {},
    navigator: { getGamepads: () => [] }
  };
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
}
