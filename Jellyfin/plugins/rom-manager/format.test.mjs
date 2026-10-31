import test from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const loaded = require("./client/format.js");
const fmt = loaded && typeof loaded.formatBytes === "function" ? loaded : globalThis.RomFormat;
const root = dirname(fileURLToPath(import.meta.url));

test("formatBytes uses binary units", () => {
  assert.equal(fmt.formatBytes(0), "0 B");
  assert.equal(fmt.formatBytes(1024), "1 KB");
  assert.equal(fmt.formatBytes(1536), "1.5 KB");
  assert.equal(fmt.formatBytes(1048576), "1 MB");
});

test("joinParts skips blanks", () => {
  assert.equal(fmt.joinParts(["SNES", "", null, "1995", "Square"]), "SNES • 1995 • Square");
});

test("versionName drops the extension", () => {
  assert.equal(fmt.versionName("Chrono Trigger (USA).sfc"), "Chrono Trigger (USA)");
  assert.equal(fmt.versionName("folder/Sonic (USA).md"), "Sonic (USA)");
});

test("routes round-trip game keys", () => {
  assert.deepEqual(fmt.parseRoute(""), { name: "home" });
  assert.deepEqual(fmt.parseRoute("#/library"), { name: "library" });
  assert.deepEqual(fmt.parseRoute("#/platform/SNES"), { name: "library", system: "SNES" });
  const hash = fmt.routeHash({ name: "game", key: "m:SNES/Chrono" });
  assert.equal(hash, "#/game/m%3ASNES%2FChrono");
  assert.deepEqual(fmt.parseRoute(hash), { name: "game", key: "m:SNES/Chrono" });
});

test("the page has one play path and does not call forbidden routes", () => {
  const app = readFileSync(join(root, "client/app.js"), "utf8");
  const css = readFileSync(join(root, "client/app.css"), "utf8");
  const html = readFileSync(join(root, "client/index.html"), "utf8");
  assert.match(app, /function playGame\(/);
  assert.equal(app.split("function playGame(").length, 2);
  assert.match(app, /Playing from ROM Manager is coming soon/);
  assert.equal(app.includes("fetch("), false);
  assert.equal(app.includes("XMLHttpRequest"), false);
  assert.equal(/\blocation\.hash\s*=/.test(app), false);
  assert.equal(app.includes("pushState"), false);
  assert.equal(app.includes("replaceState"), false);
  assert.equal(app.includes("hashchange"), false);
  const bannedA = ["romman", "empty"].join("/");
  const bannedB = ["arcade", "refresh"].join("-");
  for (const source of [app, css, html]) {
    assert.equal(source.toLowerCase().includes(bannedA), false);
    assert.equal(source.toLowerCase().includes(bannedB), false);
    assert.equal(source.includes("http://") || source.includes("https://"), false);
  }
});
