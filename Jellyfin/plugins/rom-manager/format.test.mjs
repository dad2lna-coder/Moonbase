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
  assert.match(app, /api\("launch"/);
  assert.match(app, /hostCall\("playGame"/);
  assert.match(app, /if \(result\.biosUrl\) play\.biosUrl = result\.biosUrl/);
  assert.match(app, /if \(result\.core\) play\.core = result\.core/);
  assert.match(app, /dreamcast:/);
  assert.equal(app.includes("Playing from ROM Manager is coming soon"), false);
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

test("cheat detection: words cheat/trainer and lowercase GoodTools [t] tags only", () => {
  for (const tag of ["[t]", "[t1]", "[t+3]", "[t1+2C]", "(Cheat)", "(Trainer)", "[cheat codes]", "(TRAINER +5)"]) {
    assert.equal(fmt.isCheatTag(tag), true, tag);
  }
  for (const tag of ["[T+Eng]", "[T-Fre]", "[T+Eng1.0]", "(Translated)", "(USA)", "[!]", "[h1]", "[b1]", "", null, undefined]) {
    assert.equal(fmt.isCheatTag(tag), false, String(tag));
  }
});

test("isCheatRom reads tags, tg, or the file name", () => {
  assert.equal(fmt.isCheatRom({ tags: ["(USA)", "[t1]"] }), true);
  assert.equal(fmt.isCheatRom({ tg: ["(Cheat)"] }), true);
  assert.equal(fmt.isCheatRom({ tags: ["(USA)", "[T+Eng]"], file: "Game (USA) [T+Eng].sfc" }), false);
  assert.equal(fmt.isCheatRom({ file: "NES/Game (World) [t+2].nes" }), true);
  assert.equal(fmt.isCheatRom({ file: "Game (Translated).nes" }), false);
  assert.equal(fmt.isCheatRom({ file: "Trainer Quest (USA).nes" }), false, "title words are not tags");
  assert.equal(fmt.isCheatRom({ cheat: true }), true);
  assert.equal(fmt.isCheatRom(null), false);
  assert.deepEqual(fmt.fileTags("a/Game (USA) [t1].sfc"), ["(USA)", "[t1]"]);
});

test("developer routes round-trip and logo URIs are restricted to image data", () => {
  assert.deepEqual(fmt.parseRoute("#/developer/Data%20East"), { name: "library", developer: "Data East" });
  assert.deepEqual(fmt.parseRoute("#/developers"), { name: "developers" });
  assert.equal(fmt.routeHash({ name: "library", developer: "Technōs Japan" }), "#/developer/" + encodeURIComponent("Technōs Japan"));
  assert.equal(fmt.routeHash({ name: "developers" }), "#/developers");
  assert.deepEqual(fmt.parseRoute(fmt.routeHash({ name: "library", developer: "SNK/../x" })), { name: "library", developer: "SNK/../x" });
  assert.equal(fmt.isLogoUri("data:image/svg+xml;base64,PHN2Zz48L3N2Zz4="), true);
  assert.equal(fmt.isLogoUri("data:image/png;base64,iVBORw0KGgo="), true);
  assert.equal(fmt.isLogoUri("data:text/html;base64,PHNjcmlwdD4="), false);
  assert.equal(fmt.isLogoUri("javascript:alert(1)"), false);
  assert.equal(fmt.isLogoUri("data:image/svg+xml,<svg onload=alert(1)>"), false);
  assert.equal(fmt.isLogoUri(null), false);
});
