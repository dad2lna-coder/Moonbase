import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { JSDOM } = require("jsdom");
const here = dirname(fileURLToPath(import.meta.url));
const pluginRoot = dirname(here);
const jellyfin = dirname(dirname(pluginRoot));
const bridgeSrc = readFileSync(join(jellyfin, "backend/Web/plugin-host-bridge.js"), "utf8");
const formatSrc = readFileSync(join(pluginRoot, "client/format.js"), "utf8");
const appSrc = readFileSync(join(pluginRoot, "client/app.js"), "utf8");
const html = readFileSync(join(pluginRoot, "client/index.html"), "utf8")
  .replace(/<link\b[^>]*>/gi, "")
  .replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, "");
const nodeTimeout = setTimeout;
const HANG = Symbol("hang");

function installClock(win) {
  const realSetTimeout = win.setTimeout.bind(win);
  const realClearTimeout = win.clearTimeout.bind(win);
  let now = 0;
  let seq = 1;
  const timers = new Map();
  win.setTimeout = function (fn, ms) {
    const delay = Math.max(0, Number(ms) || 0);
    const id = seq++;
    const entry = { id, at: now + delay, fn, cleared: false, realId: null };
    timers.set(id, entry);
    if (delay === 0) {
      entry.realId = realSetTimeout(function () {
        if (entry.cleared) return;
        entry.cleared = true;
        timers.delete(id);
        fn();
      }, 0);
    }
    return id;
  };
  win.clearTimeout = function (id) {
    const entry = timers.get(id);
    if (!entry) return;
    entry.cleared = true;
    timers.delete(id);
    if (entry.realId != null) realClearTimeout(entry.realId);
  };
  return {
    advance(ms) {
      const target = now + ms;
      for (let guard = 0; guard < 10000; guard++) {
        let next = null;
        for (const entry of timers.values()) {
          if (entry.cleared) continue;
          if (entry.at <= target && (!next || entry.at < next.at || (entry.at === next.at && entry.id < next.id))) next = entry;
        }
        if (!next) break;
        now = next.at;
        next.cleared = true;
        timers.delete(next.id);
        if (next.realId != null) realClearTimeout(next.realId);
        next.fn();
      }
      now = target;
    }
  };
}

function nonceOf(win) {
  const hash = String(win.location.hash || "");
  const body = hash.charAt(0) === "#" ? hash.slice(1) : hash;
  return new URLSearchParams(body).get("mofinNonce") || "";
}

function coverGames(count) {
  const games = [];
  for (let i = 0; i < count; i++) {
    games.push({
      key: "m:SNES/G" + i,
      title: "Game " + i,
      system: "SNES",
      cover: 1,
      coverPath: "covers/g" + i + ".jpg"
    });
  }
  return games;
}

async function openClient(options) {
  const opts = options || {};
  const hold = new Set(opts.hold || []);
  const games = opts.games || [{ key: "m:SNES/Chrono", title: "Chrono Trigger", system: "SNES" }];
  const dom = new JSDOM("<!DOCTYPE html><iframe id=\"frame\"></iframe>", {
    url: "http://mofin.test/Moonfin/",
    runScripts: "dangerously",
    pretendToBeVisual: true
  });
  const parent = dom.window;
  const iframe = parent.document.getElementById("frame");
  const doc = iframe.contentDocument;
  doc.open();
  doc.write(html);
  doc.close();
  const win = iframe.contentWindow;
  const errors = [];
  win.addEventListener("error", (event) => {
    errors.push(String((event.error && event.error.stack) || event.message));
  });
  win.addEventListener("unhandledrejection", (event) => {
    errors.push(String((event.reason && event.reason.stack) || event.reason));
  });
  win.IntersectionObserver = undefined;
  win.location.hash = "#mofinNonce=" + (opts.nonce || "safari");
  await new Promise((resolve) => nodeTimeout(resolve, 40));

  let loads = 1;
  let dead = false;
  let dropped = 0;
  const calls = [];
  const held = [];
  iframe.addEventListener("load", () => {
    loads += 1;
    if (loads >= 2) dead = true;
  });
  win.addEventListener("hashchange", () => {
    iframe.dispatchEvent(new parent.Event("load"));
  });
  const clock = installClock(win);
  const bootHash = win.location.hash;

  function deliver(callbackId, nonce, result) {
    win.dispatchEvent(new win.MessageEvent("message", {
      data: { mofin: 1, nonce, type: "response", data: { ok: true, result, callbackId } },
      source: win.parent
    }));
  }

  function answer(method, params) {
    if (method === "dataGet") return null;
    if (method !== "callPluginRoute") return {};
    const path = params && params.path;
    const data = (params && params.data) || {};
    if (hold.has(path)) return HANG;
    if (path === "systems") {
      return {
        ok: true,
        isAdmin: true,
        indexing: false,
        systems: [{ system: "SNES", games: games.length, roms: games.length, bytes: 1050 }],
        totals: { games: games.length, roms: games.length, bytes: 1050 },
        status: { state: "ready" }
      };
    }
    if (path === "games") return { ok: true, total: games.length, items: games, indexing: false };
    if (path === "covers") return { ok: true, covers: {} };
    return { ok: true, ignored: data };
  }

  parent.addEventListener("message", (event) => {
    const data = event.data;
    if (!data || data.mofin !== 1 || !data.payload) return;
    const expected = nonceOf(win);
    if (!expected || data.nonce !== expected) {
      dropped += 1;
      return;
    }
    let payload;
    try {
      payload = JSON.parse(data.payload);
    } catch (error) {
      dropped += 1;
      return;
    }
    calls.push({
      method: payload.method,
      path: payload.params && payload.params.path,
      callbackId: payload.callbackId
    });
    if (dead) {
      dropped += 1;
      return;
    }
    const delay = 100 + Math.floor(Math.random() * 201);
    nodeTimeout(() => {
      if (dead) {
        dropped += 1;
        return;
      }
      const result = answer(payload.method, payload.params || {});
      if (result === HANG) {
        held.push({ callbackId: payload.callbackId, nonce: data.nonce, path: payload.params && payload.params.path });
        return;
      }
      deliver(payload.callbackId, data.nonce, result);
    }, delay);
  });

  function run(code) {
    const script = win.document.createElement("script");
    script.textContent = code;
    win.document.body.appendChild(script);
  }
  run(bridgeSrc);
  run(formatSrc);
  run(appSrc);

  function main() {
    return win.document.getElementById("main");
  }
  function heads() {
    return Array.from(main().querySelectorAll(".page-head"));
  }
  function heading() {
    const node = main().querySelector("h1");
    return node ? node.textContent : "";
  }
  async function waitFor(check, label) {
    let last = "";
    for (let i = 0; i < 120; i++) {
      last = (main() && main().textContent) || "";
      if (check()) return last;
      await new Promise((resolve) => nodeTimeout(resolve, 25));
    }
    throw new Error(
      label + ": " + last
      + "\nheads=" + heads().map((node) => node.textContent).join("|")
      + " dropped=" + dropped + " loads=" + loads + " hash=" + win.location.hash
      + " pending=" + Object.keys(win.MoFinHost._callbackMap).length
      + "\n" + errors.join("\n")
    );
  }
  function clickGo(name) {
    const button = win.document.querySelector("#switcher [data-go=\"" + name + "\"]")
      || win.document.querySelector("#sidebar [data-go=\"" + name + "\"]");
    assert.ok(button, name);
    button.click();
  }

  return {
    win, dom, clock, calls, held, errors, bootHash, deliver, clickGo, waitFor, heading, heads, main,
    get loads() { return loads; },
    get dropped() { return dropped; },
    get dead() { return dead; }
  };
}

async function microtasks() {
  await new Promise((resolve) => setImmediate(resolve));
  await new Promise((resolve) => setImmediate(resolve));
}

test("rapid navigation keeps one Home and never reloads the iframe", async () => {
  const session = await openClient();
  try {
    assert.equal(session.win.MoFinHost && typeof session.win.MoFinHost.call, "function");
    await session.waitFor(() => {
      return session.heading() === "Home"
        && !session.main().textContent.includes("Loading")
        && session.main().textContent.includes("Chrono Trigger");
    }, "boot home");
    assert.equal(session.win.location.hash, session.bootHash);
    assert.equal(session.loads, 1);
    assert.equal(session.dropped, 0);

    for (const name of ["settings", "library", "home", "platforms", "library", "favorites", "home"]) {
      session.clickGo(name);
    }

    await session.waitFor(() => {
      const text = session.main().textContent;
      const pending = Object.keys(session.win.MoFinHost._callbackMap).length;
      const pageHeads = session.heads();
      return pageHeads.length === 1
        && pageHeads[0].textContent === "Home"
        && text.includes("Recently Played")
        && text.includes("Favorites")
        && text.includes("Chrono Trigger")
        && !text.includes("Loading")
        && pending === 0;
    }, "settled home");
    assert.equal(session.heads().length, 1);
    assert.equal(session.heads()[0].textContent, "Home");
    assert.equal(session.dropped, 0);
    assert.equal(session.loads, 1);
    assert.equal(session.dead, false);
    assert.equal(session.win.location.hash, session.bootHash);
    assert.equal(session.errors.length, 0, session.errors.join("\n"));
  } finally {
    session.dom.window.close();
  }
});

test("a hash change reloads the iframe and the host drops later calls", async () => {
  const session = await openClient();
  try {
    await session.waitFor(() => session.heading() === "Home" && !session.main().textContent.includes("Loading"), "boot");
    assert.equal(session.loads, 1);
    session.win.location.hash = "#/library";
    await new Promise((resolve) => nodeTimeout(resolve, 40));
    assert.notEqual(session.win.location.hash, session.bootHash);
    assert.equal(session.loads, 2);
    assert.equal(session.dead, true);
    const before = session.dropped;
    session.clickGo("library");
    await new Promise((resolve) => nodeTimeout(resolve, 400));
    assert.ok(session.dropped > before);
    assert.ok(Object.keys(session.win.MoFinHost._callbackMap).length > 0);
  } finally {
    session.dom.window.close();
  }
});

test("a silent games call shows the timeout banner only on the current page", async () => {
  const hung = await openClient({ hold: ["games"] });
  try {
    await hung.waitFor(() => hung.main().textContent.includes("Loading"), "loading");
    await new Promise((resolve) => nodeTimeout(resolve, 400));
    assert.equal(hung.main().textContent.includes("MoFin stopped answering"), false);
    hung.clock.advance(14999);
    await microtasks();
    assert.equal(hung.main().textContent.includes("MoFin stopped answering"), false);
    hung.clock.advance(1);
    await microtasks();
    const banner = Array.from(hung.main().querySelectorAll(".banner")).find((node) => node.textContent.includes("MoFin stopped answering"));
    assert.ok(banner, hung.main().textContent);
    assert.match(banner.textContent, /MoFin stopped answering\. Tap reload to try again\./);
    assert.equal(hung.errors.length, 0, hung.errors.join("\n"));
  } finally {
    hung.dom.window.close();
  }

  const left = await openClient({ hold: ["games"] });
  try {
    await left.waitFor(() => left.main().textContent.includes("Loading"), "loading");
    left.clickGo("settings");
    assert.equal(left.heading(), "Settings");
    left.clock.advance(16000);
    await microtasks();
    assert.equal(left.heading(), "Settings");
    assert.equal(left.main().textContent.includes("MoFin stopped answering"), false);
    assert.equal(left.win.document.getElementById("toast").textContent.includes("MoFin stopped answering"), false);
    assert.equal(left.errors.length, 0, left.errors.join("\n"));
  } finally {
    left.dom.window.close();
  }
});

test("cover timeouts reset coverBusy and a stale cover does not paint the next page", async () => {
  const games = coverGames(5);
  const session = await openClient({ hold: ["covers"], games });
  try {
    await session.waitFor(() => {
      return session.heading() === "Home"
        && !session.main().textContent.includes("Loading")
        && session.main().querySelectorAll("[data-cover]").length === 5;
    }, "cover cards");
    assert.equal(session.calls.filter((call) => call.path === "covers").length, 0);
    session.clock.advance(40);
    await session.waitFor(() => session.calls.filter((call) => call.path === "covers").length === 1, "first covers");
    session.clock.advance(15000);
    await microtasks();
    assert.equal(session.calls.filter((call) => call.path === "covers").length, 1);
    session.clock.advance(40);
    await session.waitFor(() => session.calls.filter((call) => call.path === "covers").length === 2, "second covers");
    assert.equal(session.win.location.hash, session.bootHash);
    assert.equal(session.dropped, 0);
  } finally {
    session.dom.window.close();
  }

  const stale = await openClient({ hold: ["covers"], games });
  try {
    await stale.waitFor(() => stale.main().querySelectorAll("[data-cover]").length === 5 && stale.heading() === "Home", "home cards");
    stale.clock.advance(40);
    await stale.waitFor(() => stale.held.length === 1, "held cover");
    stale.clickGo("library");
    await stale.waitFor(() => {
      return stale.heading() === "Browse by Console"
        && !stale.main().textContent.includes("Loading")
        && stale.main().querySelectorAll("[data-cover]").length === 5;
    }, "library cards");
    const pending = stale.held[0];
    const covers = {};
    games.forEach((game) => { covers[game.coverPath] = "data:image/jpeg;base64,AAAA"; });
    stale.deliver(pending.callbackId, pending.nonce, { ok: true, covers });
    await microtasks();
    const imgs = Array.from(stale.main().querySelectorAll("[data-cover] img"));
    assert.equal(imgs.length, 5);
    imgs.forEach((img) => {
      assert.equal(img.hidden, true);
      assert.equal(img.getAttribute("src"), null);
    });
    assert.equal(stale.heads().length, 1);
    assert.equal(stale.heading(), "Browse by Console");
    assert.equal(stale.win.location.hash, stale.bootHash);
  } finally {
    stale.dom.window.close();
  }
});
