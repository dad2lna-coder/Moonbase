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
  cover: 0
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
  cover: 0,
  versions: [{
    p: "SNES/Chrono",
    file: "Chrono Trigger (USA).sfc",
    ext: "sfc",
    region: "USA",
    sizeBytes: 1050,
    best: true,
    cover: 0
  }],
  related: []
};

function call(method, params) {
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
    if (params.path === "deps") return Promise.resolve({ ok: true, warnings: [] });
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

  function show(hash) {
    window.location.hash = hash;
    window.dispatchEvent(new window.HashChangeEvent("hashchange"));
  }

  await settle("Home");
  assert.match(document.getElementById("main").textContent, /Recently Viewed/);
  assert.match(document.getElementById("main").textContent, /Chrono Trigger/);

  const pages = [
    ["#/library", "Library"],
    ["#/platforms", "Platforms"],
    ["#/platform/SNES", "SNES"],
    ["#/game/" + encodeURIComponent(card.key), "Chrono Trigger"],
    ["#/favorites", "Favorites"],
    ["#/recent", "Recently Viewed"],
    ["#/collections", "Collections"],
    ["#/settings", "Settings"],
    ["#/duplicates", "Identical copies"],
    ["#/trash", "Trash"]
  ];
  for (const [hash, title] of pages) {
    show(hash);
    const text = await settle(title);
    assert.equal(/not defined/i.test(text), false, text);
  }
  assert.equal(errors.length, 0, errors.join("\n"));

  show("#/game/" + encodeURIComponent(card.key));
  const gameText = await settle("Chrono Trigger");
  assert.equal(gameText.includes("Best Experience"), true);
  assert.equal(document.querySelector("[data-trash-best]"), null);
  assert.equal(document.querySelector("[data-trash]"), null);
  assert.equal(Array.from(document.querySelectorAll("#main button")).some((button) => button.textContent === "More"), false);

  show("#/duplicates");
  await settle("Identical copies");
  const box = document.querySelector("[data-dup]");
  assert.ok(box);
  box.click();
  assert.equal(box.checked, true);
  assert.equal(window.location.hash, "#/duplicates");

  show("#/trash");
  await settle("Trash");
  const restore = Array.from(document.querySelectorAll("button")).find((button) => button.textContent === "Restore");
  assert.ok(restore);
  restore.click();
  assert.equal(window.location.hash, "#/trash");
  assert.equal(document.activeElement && document.activeElement.textContent, "Cancel");
  document.dispatchEvent(new window.KeyboardEvent("keydown", { key: "ArrowRight", bubbles: true, cancelable: true }));
  assert.equal(document.activeElement.textContent, "Restore");
  document.dispatchEvent(new window.KeyboardEvent("keydown", { key: "ArrowLeft", bubbles: true, cancelable: true }));
  assert.equal(document.activeElement.textContent, "Cancel");
  document.dispatchEvent(new window.KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
  assert.equal(document.querySelector(".dialog"), null);

  show("#/game/" + encodeURIComponent(card.key));
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

  show("#/library");
  await settle("Library");
  document.querySelector('[data-system="SNES"]').click();
  assert.match(window.location.hash, /platform\/SNES/);
  await settle("SNES");
  await new Promise((resolve) => setTimeout(resolve, 80));
  assert.equal(errors.length, 0, errors.join("\n"));
});
