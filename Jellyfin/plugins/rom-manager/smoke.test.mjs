import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { JSDOM } = require("jsdom");
const root = dirname(fileURLToPath(import.meta.url));

const card = {
  key: "m:SNES/Chrono",
  title: "Chrono Trigger",
  system: "SNES",
  year: 1995,
  genre: "RPG",
  publisher: "Square",
  versions: 1,
  sizeBytes: 1050,
  cover: 1,
  coverPath: "SNES/Chrono"
};

const game = {
  key: card.key,
  title: card.title,
  system: "SNES",
  year: 1995,
  genre: "RPG",
  publisher: "Square",
  description: "A classic.",
  rating: 4.3,
  ratingCount: 12,
  versionsCount: 1,
  sizeBytes: 1050,
  cover: 1,
  coverPath: "SNES/Chrono",
  versions: [{
    p: "SNES/Chrono",
    file: "Chrono Trigger (USA).sfc",
    ext: "sfc",
    region: "USA",
    sizeBytes: 1050,
    best: true,
    cover: 1
  }, {
    p: "SNES/Unplayable",
    file: "Unplayable (USA).col",
    ext: "col",
    region: "USA",
    sizeBytes: 100,
    best: false,
    cover: 1
  }],
  related: []
};

const calls = [];
function call(method, params) {
  calls.push({ method, params });
  const data = (params && params.data) || {};
  if (method === "callPluginRoute") {
    if (params.path === "systems") {
      return Promise.resolve({
        ok: true,
        isAdmin: true,
        indexing: false,
        systems: [{ system: "SNES", games: 1, roms: 2, bytes: 1050 }],
        totals: { games: 1, roms: 2, bytes: 1050 },
        status: { state: "ready" }
      });
    }
    if (params.path === "games") return Promise.resolve({ ok: true, total: 1, items: [card], indexing: false });
    if (params.path === "game") return Promise.resolve({ ok: true, game, indexing: false });
    if (params.path === "duplicates") {
      return Promise.resolve({
        ok: true,
        total: 1,
        versionGroups: 1,
        groups: [{
          system: "NES",
          members: [
            { p: "NES/Mario", title: "Super Mario Bros.", system: "NES", sizeBytes: 400, ks: true },
            { p: "NES/Mario-copy", title: "Super Mario Bros.", system: "NES", sizeBytes: 400, ks: false }
          ]
        }]
      });
    }
    if (params.path === "trash-list") {
      return Promise.resolve({
        ok: true,
        bytes: 400,
        items: [{ p: "batch/NES/Mario", title: "Super Mario Bros.", system: "NES", sizeBytes: 400 }]
      });
    }
    if (params.path === "covers") {
      const covers = {};
      ((data.items) || []).forEach((item) => {
        covers[item.p] = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==";
      });
      return Promise.resolve({ ok: true, covers });
    }
    if (params.path === "deps") return Promise.resolve({ ok: true, warnings: [] });
    if (params.path === "launch") {
      if (data.p === "SNES/Unplayable") {
        return Promise.resolve({ ok: true, playable: false, reason: "No emulator for this system yet" });
      }
      return Promise.resolve({
        ok: true,
        playable: true,
        libraryId: "lib-1",
        gameId: "game-1",
        core: "snes",
        fileName: "Chrono Trigger (USA).sfc",
        title: "Chrono Trigger",
        system: "SNES"
      });
    }
    if (params.path === "trash" || params.path === "restore" || params.path === "rescan") return Promise.resolve({ ok: true });
    return Promise.resolve({ ok: true, ignored: data });
  }
  if (method === "dataGet") {
    if (params.key === "favorites") return Promise.resolve([card.key]);
    if (params.key === "recent") return Promise.resolve([card.key]);
    if (params.key === "collections") return Promise.resolve([{ name: "RPGs", keys: [card.key] }]);
    return Promise.resolve(null);
  }
  return Promise.resolve({});
}

test("every page renders, and restore or a duplicate tick does not leave the page", async () => {
  const html = readFileSync(join(root, "client/index.html"), "utf8")
    .replace(/<link\b[^>]*>/gi, "")
    .replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, "");
  const errors = [];
  const dom = new JSDOM(html, {
    url: "http://127.0.0.1/client/index.html",
    pretendToBeVisual: true,
    runScripts: "dangerously"
  });
  const window = dom.window;
  try {
  window.addEventListener("error", (event) => {
    errors.push(String((event.error && event.error.stack) || event.message));
  });
  window.addEventListener("unhandledrejection", (event) => {
    errors.push(String((event.reason && event.reason.stack) || event.reason));
  });
  window.MoFinHost = { call };
  function run(code) {
    const script = window.document.createElement("script");
    script.textContent = code;
    window.document.body.appendChild(script);
  }
  run(readFileSync(join(root, "client/format.js"), "utf8"));
  run(readFileSync(join(root, "client/app.js"), "utf8"));
  const document = window.document;
  const style = document.createElement("style");
  style.textContent = readFileSync(join(root, "client/app.css"), "utf8");
  document.head.appendChild(style);

  function heading() {
    const node = document.querySelector("#main h1");
    return node ? node.textContent : "";
  }

  async function settle(expected) {
    let last = "";
    for (let i = 0; i < 80; i++) {
      last = document.getElementById("main").textContent || "";
      if (!last.includes("Loading") && heading() === expected) return last;
      await new Promise((resolve) => setTimeout(resolve, 15));
    }
    throw new Error("timed out waiting for " + expected + ": " + last + "\n" + errors.join("\n"));
  }

  async function assertPlaceholder(selector) {
    let node = null;
    for (let i = 0; i < 40; i++) {
      node = document.querySelector(selector);
      const img = node && node.querySelector("img");
      if (img && img.getAttribute("src")) break;
      await new Promise((resolve) => setTimeout(resolve, 20));
    }
    assert.ok(node, "missing " + selector);
    const img = node.querySelector("img");
    const ph = node.querySelector(".ph");
    assert.ok(img, selector + " img");
    assert.ok(ph, selector + " placeholder");
    img.dispatchEvent(new window.Event("load"));
    assert.equal(window.getComputedStyle(ph).display, "none", selector + " stayed visible after the cover loaded");
    img.dispatchEvent(new window.Event("error"));
    assert.notEqual(window.getComputedStyle(ph).display, "none", selector + " stayed hidden after the cover failed");
  }

  await settle("Home");
  assert.equal(document.querySelectorAll("#main .page-head").length, 1);
  assert.match(document.getElementById("main").textContent, /Recently Played/);
  assert.match(document.getElementById("main").textContent, /Favorites/);
  assert.match(document.getElementById("main").textContent, /Chrono Trigger/);
  assert.match(document.getElementById("side-nav").textContent, /Recently Played/);
  assert.equal(document.getElementById("total-games").textContent, "1");
  assert.match(document.getElementById("clock").textContent, /^\d{2}:\d{2}$/);
  assert.ok(document.querySelector(".art .plat svg"));
  await assertPlaceholder(".art");
  const bootHash = window.location.hash;

  function clickGo(name) {
    const button = document.querySelector(`#sidebar [data-go="${name}"]`)
      || document.querySelector(`#tabbar [data-go="${name}"]`)
      || document.querySelector(`#main [data-go="${name}"]`);
    assert.ok(button, "missing " + name);
    button.click();
  }

  const pages = [
    ["library", "Library"],
    ["platforms", "Platforms"]
  ];
  for (const [name, title] of pages) {
    clickGo(name);
    const text = await settle(title);
    assert.equal(/not defined/i.test(text), false, text);
    assert.equal(window.location.hash, bootHash);
  }

  document.querySelector('#main [data-system="SNES"]').click();
  await settle("SNES");
  assert.equal(window.location.hash, bootHash);
  document.querySelector("#main [data-open]").click();
  const gameText = await settle("Chrono Trigger");
  assert.equal(window.location.hash, bootHash);
  assert.equal(gameText.includes("Best Experience"), true);
  await assertPlaceholder(".hero-bg");
  await assertPlaceholder(".hero-thumb");
  await assertPlaceholder(".version-banner");
  assert.equal(document.querySelector("[data-trash-best]"), null);
  assert.equal(document.querySelector("[data-trash]"), null);
  assert.equal(Array.from(document.querySelectorAll("#main button")).some((button) => button.textContent === "More"), true);
  for (const tab of ["screenshots", "videos", "achievements"]) {
    document.querySelector(`#main [data-tab="${tab}"]`).click();
    assert.match(document.getElementById("main").textContent, /Coming later/);
  }
  document.querySelector('#main [data-tab="versions"]').click();
  assert.match(document.getElementById("main").textContent, /Available Versions \(2\)/);

  async function until(check, label) {
    for (let i = 0; i < 40; i++) {
      if (check()) return;
      await new Promise((resolve) => setTimeout(resolve, 15));
    }
    throw new Error(label + "\n" + errors.join("\n"));
  }

  const unplayable = document.querySelector('#main [data-play="1"]');
  assert.ok(unplayable);
  unplayable.click();
  assert.equal(unplayable.getAttribute("aria-busy"), "true");
  assert.equal(unplayable.textContent, "Starting…");
  await until(() => document.getElementById("toast").textContent.includes("No emulator for this system yet"), "unplayable toast");
  assert.equal(unplayable.textContent, "No emulator for this system yet");
  assert.equal(calls.filter((entry) => entry.method === "playGame").length, 0);

  const hero = document.querySelector('#main [data-play="hero"]');
  assert.ok(hero);
  hero.click();
  assert.equal(hero.getAttribute("aria-busy"), "true");
  assert.equal(hero.textContent, "Starting…");
  await until(() => calls.some((entry) => entry.method === "playGame"), "play handoff");
  const plays = calls.filter((entry) => entry.method === "playGame");
  assert.equal(plays.length, 1);
  assert.equal(plays[0].params.libraryId, "lib-1");
  assert.equal(plays[0].params.gameId, "game-1");
  assert.equal(plays[0].params.title, "Chrono Trigger");
  const remembered = calls.findIndex((entry) => entry.method === "dataPut" && entry.params && entry.params.key === "recent");
  const played = calls.findIndex((entry) => entry.method === "playGame");
  assert.ok(remembered >= 0 && remembered < played);

  for (const [name, title] of [["favorites", "Favorites"], ["recent", "Recently Played"], ["collections", "Collections"], ["settings", "Settings"]]) {
    clickGo(name);
    const text = await settle(title);
    assert.equal(/not defined/i.test(text), false, text);
    assert.equal(window.location.hash, bootHash);
  }

  clickGo("settings");
  await settle("Settings");
  document.querySelector('#main [data-go="duplicates"]').click();
  await settle("Identical copies");
  const box = document.querySelector("[data-dup]");
  assert.ok(box);
  box.click();
  assert.equal(box.checked, true);
  assert.equal(heading(), "Identical copies");
  assert.equal(window.location.hash, bootHash);

  clickGo("settings");
  await settle("Settings");
  document.querySelector('#main [data-go="trash"]').click();
  await settle("Trash");
  const restore = Array.from(document.querySelectorAll("button")).find((button) => button.textContent === "Restore");
  assert.ok(restore);
  restore.click();
  assert.equal(heading(), "Trash");
  assert.equal(window.location.hash, bootHash);
  assert.equal(document.activeElement && document.activeElement.textContent, "Cancel");
  document.dispatchEvent(new window.KeyboardEvent("keydown", { key: "ArrowRight", bubbles: true, cancelable: true }));
  assert.equal(document.activeElement.textContent, "Restore");
  document.dispatchEvent(new window.KeyboardEvent("keydown", { key: "ArrowLeft", bubbles: true, cancelable: true }));
  assert.equal(document.activeElement.textContent, "Cancel");
  document.dispatchEvent(new window.KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
  assert.equal(document.querySelector(".dialog"), null);

  clickGo("library");
  await settle("Library");
  document.querySelector("#main [data-open]").click();
  await settle("Chrono Trigger");
  const collect = document.querySelector("[data-collect]");
  assert.ok(collect);
  collect.click();
  assert.equal(document.activeElement && document.activeElement.textContent, "Close");
  const removeBtn = Array.from(document.querySelectorAll(".dialog button")).find((button) => button.textContent.startsWith("Remove from"));
  assert.ok(removeBtn);
  document.dispatchEvent(new window.KeyboardEvent("keydown", { key: "ArrowRight", bubbles: true, cancelable: true }));
  assert.equal(document.activeElement, removeBtn);
  document.dispatchEvent(new window.KeyboardEvent("keydown", { key: "ArrowRight", bubbles: true, cancelable: true }));
  const name = document.querySelector(".dialog input");
  assert.ok(name);
  assert.equal(document.activeElement, name);
  document.dispatchEvent(new window.KeyboardEvent("keydown", { key: "ArrowRight", bubbles: true, cancelable: true }));
  assert.equal(document.activeElement, name);
  document.dispatchEvent(new window.KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true, cancelable: true }));
  assert.equal(document.activeElement, name);
  document.dispatchEvent(new window.KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true }));
  assert.equal(document.activeElement.textContent, "Create");
  document.dispatchEvent(new window.KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true }));
  assert.equal(document.activeElement.textContent, "Close");
  document.dispatchEvent(new window.KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));

  clickGo("library");
  await settle("Library");
  document.querySelector('[data-system="SNES"]').click();
  await settle("SNES");
  assert.equal(window.location.hash, bootHash);
  await new Promise((resolve) => setTimeout(resolve, 80));
  assert.equal(errors.length, 0, errors.join("\n"));
  } finally {
    window.close();
  }
});
