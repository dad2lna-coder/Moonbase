import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { JSDOM } = require("jsdom");
const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const fmt = require(join(root, "client/format.js"));

test("hack detection: 'hack' in a tag or lowercase GoodTools [h] tags only", () => {
  for (const tag of ["[h]", "[h1]", "[h2C]", "[h1+2]", "(Hack)", "[Hack by Foo]", "(HACK)"]) {
    assert.equal(fmt.isHackTag(tag), true, tag);
  }
  for (const tag of ["[T+Eng]", "[!]", "[b]", "[b1]", "[H1]", "[t1]", "(USA)", "(Translated)", "", null, undefined]) {
    assert.equal(fmt.isHackTag(tag), false, String(tag));
  }
  assert.equal(fmt.isHackRom({ tags: ["(USA)", "[h1]"] }), true);
  assert.equal(fmt.isHackRom({ file: "SNES/Mario (USA) [h2C].sfc" }), true);
  assert.equal(fmt.isHackRom({ file: "Hack Attack (USA) [!].nes" }), false, "title words are not tags");
  assert.equal(fmt.isHackRom({ file: "Game (USA) [T+Eng].sfc" }), false);
  assert.equal(fmt.isHackRom({ hack: true }), true);
  assert.equal(fmt.isHackRom(null), false);
  // Cheat detection is unchanged and independent.
  assert.equal(fmt.isCheatTag("[h1]"), false);
  assert.equal(fmt.isHackTag("[t1]"), false);
});

const hackCard = { key: "m:SNES/Mario", title: "Super Mario World", system: "SNES", versions: 2, hack: "some" };
const bothCard = { key: "m:SNES/Zelda", title: "Zelda", system: "SNES", versions: 1, hack: "best", cheat: "best" };
const cheatCard = { key: "m:SNES/Metroid", title: "Super Metroid", system: "SNES", versions: 1, cheat: "best" };
const plainCard = { key: "m:SNES/Chrono", title: "Chrono Trigger", system: "SNES", versions: 1 };
const game = {
  key: hackCard.key, title: hackCard.title, system: "SNES", versionsCount: 2, sizeBytes: 10,
  versions: [
    { p: "SNES/Mario", file: "SNES/Super Mario World (USA) [!].sfc", tags: ["(USA)", "[!]"], ext: "sfc", region: "USA", sizeBytes: 5, best: true },
    { p: "SNES/Mario-h", file: "SNES/Super Mario World (USA) [h2C].sfc", tags: ["(USA)", "[h2C]"], ext: "sfc", region: "USA", sizeBytes: 5, best: false }
  ],
  related: []
};

function boot() {
  const calls = [];
  function call(method, params) {
    calls.push({ method, params });
    const data = (params && params.data) || {};
    if (method !== "callPluginRoute") return Promise.resolve(method === "dataGet" ? null : {});
    if (params.path === "systems") {
      return Promise.resolve({ ok: true, isAdmin: true, indexing: false, systems: [{ system: "SNES", games: 4, roms: 5, bytes: 10 }, { system: "NES", games: 1, roms: 1, bytes: 1 }], totals: { games: 5, roms: 6, bytes: 11 }, status: { state: "ready" } });
    }
    if (params.path === "developers") return Promise.resolve({ ok: true, total: 0, developers: [] });
    if (params.path === "logos") return Promise.resolve({ ok: true, logos: {} });
    if (params.path === "games") {
      if (data.flag === "hack") return Promise.resolve({ ok: true, total: 2, items: [hackCard, bothCard] });
      if (data.flag === "cheat") return Promise.resolve({ ok: true, total: 2, items: [bothCard, cheatCard] });
      return Promise.resolve({ ok: true, total: 4, items: [hackCard, bothCard, cheatCard, plainCard] });
    }
    if (params.path === "game") return Promise.resolve({ ok: true, game });
    if (params.path === "duplicates") {
      return Promise.resolve({ ok: true, total: 1, versionGroups: 0, groups: [{ system: "NES", members: [
        { p: "NES/Mario", title: "Super Mario Bros.", file: "Super Mario Bros. (World) [!].nes", system: "NES", sizeBytes: 4, ks: true },
        { p: "NES/Mario-h", title: "Super Mario Bros.", file: "Super Mario Bros. (World) [h1].nes", system: "NES", sizeBytes: 4, ks: false },
        { p: "NES/Mario-t", title: "Super Mario Bros.", file: "Super Mario Bros. (World) [T+Eng].nes", system: "NES", sizeBytes: 4, ks: false }
      ] }] });
    }
    if (params.path === "covers") return Promise.resolve({ ok: true, covers: {} });
    return Promise.resolve({ ok: true });
  }
  const html = readFileSync(join(root, "client/index.html"), "utf8").replace(/<link\b[^>]*>/gi, "").replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, "");
  const dom = new JSDOM(html, { url: "http://127.0.0.1/client/index.html#/library", pretendToBeVisual: true, runScripts: "dangerously" });
  const window = dom.window;
  const errors = [];
  window.addEventListener("error", (event) => errors.push(String((event.error && event.error.stack) || event.message)));
  window.addEventListener("unhandledrejection", (event) => errors.push(String((event.reason && event.reason.stack) || event.reason)));
  window.MoFinHost = { call };
  for (const file of ["client/format.js", "client/app.js"]) {
    const script = window.document.createElement("script");
    script.textContent = readFileSync(join(root, file), "utf8");
    window.document.body.appendChild(script);
  }
  const main = () => window.document.getElementById("main");
  async function until(check, label) {
    for (let i = 0; i < 150; i++) {
      if (check()) return;
      await new Promise((resolve) => setTimeout(resolve, 15));
    }
    throw new Error(label + "\n" + (main() ? main().innerHTML.slice(0, 1500) : "") + "\n" + errors.join("\n"));
  }
  return { window, document: window.document, main, until, calls, errors };
}

test("Hack badge on cards, versions and duplicates; Hacks/Cheats filters", async () => {
  const { window, document, main, until, calls, errors } = boot();
  try {
    await until(() => main().querySelector(".card[data-open='m:SNES/Mario']"), "library grid");
    const badge = (key, cls) => main().querySelector(".card[data-open='" + key + "'] ." + cls);
    assert.equal(badge("m:SNES/Mario", "hack-badge").textContent, "Hack");
    assert.match(badge("m:SNES/Mario", "hack-badge").getAttribute("title"), /Some versions/);
    assert.equal(badge("m:SNES/Mario", "cheat-badge"), null);
    assert.match(badge("m:SNES/Zelda", "hack-badge").getAttribute("title"), /best file/);
    assert.ok(badge("m:SNES/Zelda", "cheat-badge"), "cheat and hack badges coexist");
    assert.ok(badge("m:SNES/Zelda", "hack-badge").closest(".art").classList.contains("has-cheat"));
    assert.equal(badge("m:SNES/Metroid", "hack-badge"), null);
    assert.equal(badge("m:SNES/Metroid", "cheat-badge").textContent, "Cheat");
    assert.equal(badge("m:SNES/Chrono", "hack-badge"), null);

    // Filter button: Hacks, then Cheats, then Clear.
    const lastGames = () => calls.filter((c) => c.params && c.params.path === "games").pop();
    async function pickFilter(flag) {
      main().querySelector("[data-filter]").click();
      const dialog = document.querySelector("#modal .dialog[aria-label='Filter']");
      if (!dialog) throw new Error("no filter dialog: " + document.getElementById("modal").innerHTML.slice(0, 800));
      assert.deepEqual(Array.from(dialog.querySelectorAll("[data-flag]")).map((n) => n.textContent), ["All ROMs", "Hacks", "Cheats"]);
      if (flag === null) {
        Array.from(dialog.querySelectorAll("button")).find((b) => b.textContent === "Clear").click();
      } else {
        dialog.querySelector("[data-flag='" + flag + "']").click();
        assert.equal(dialog.querySelector("[data-flag='" + flag + "']").getAttribute("aria-pressed"), "true");
        Array.from(dialog.querySelectorAll("button")).find((b) => b.textContent === "Apply").click();
      }
    }
    await pickFilter("hack");
    await until(() => lastGames().params.data.flag === "hack" && main().querySelector(".sub-flag"), "hacks list");
    assert.equal(lastGames().params.data.system, "", "hack review spans all consoles");
    assert.match(main().querySelector(".sub-flag").textContent, /Hacks/);
    assert.match(main().querySelector(".sub-count").textContent, /2 Games/);
    assert.equal(main().querySelectorAll(".grid .card").length, 2);
    assert.ok(main().querySelector(".filter-btn.on"));

    await pickFilter("cheat");
    await until(() => lastGames().params.data.flag === "cheat" && /Cheats/.test((main().querySelector(".sub-flag") || {}).textContent || ""), "cheats list");
    assert.ok(main().querySelector(".card[data-open='m:SNES/Metroid']"));
    assert.equal(main().querySelector(".card[data-open='m:SNES/Mario']"), null);

    await pickFilter(null);
    await until(() => !("flag" in lastGames().params.data) && !main().querySelector(".sub-flag") && main().querySelector(".card[data-open='m:SNES/Chrono']"), "filter cleared");
    assert.equal(main().querySelector(".filter-btn.on"), null);

    // Game detail: Hack pill on the [h2C] version only, with full file name and tags.
    main().querySelector(".card[data-open='m:SNES/Mario']").click();
    await until(() => main().querySelectorAll(".version").length === 2, "game detail");
    const versions = Array.from(main().querySelectorAll(".version"));
    assert.equal(versions[0].querySelector(".pill.hack"), null, "[!] is not a hack");
    assert.equal(versions[1].querySelector(".pill.hack").textContent, "Hack");
    assert.equal(versions[1].querySelector(".pill.cheat"), null);
    assert.equal(versions[1].querySelector(".version-file").textContent, "Super Mario World (USA) [h2C].sfc");
    assert.equal(versions[1].querySelector(".version-tags").textContent, "Tags: (USA) [h2C]");

    // Duplicates: only the [h1] copy is a hack; [T+Eng] is not.
    window.document.querySelector("[data-go='duplicates']") ? window.document.querySelector("[data-go='duplicates']").click() : null;
    if (!main().querySelector(".group")) {
      const go = document.createElement("button");
      go.setAttribute("data-go", "duplicates");
      main().appendChild(go);
      go.click();
    }
    await until(() => main().querySelectorAll(".group label.check").length === 3, "duplicates");
    const rows = Array.from(main().querySelectorAll(".group label.check"));
    assert.equal(rows[0].querySelector(".pill.hack"), null);
    assert.ok(rows[1].querySelector(".pill.hack"));
    assert.equal(rows[2].querySelector(".pill.hack"), null);
    assert.deepEqual(errors, []);
  } finally {
    window.close();
  }
});
