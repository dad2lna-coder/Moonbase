import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { JSDOM } = require("jsdom");
const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const SVG = "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciLz4=";

const developers = [
  { key: "sega", name: "Sega", games: 12, roms: 30 },
  { key: "dataeast", name: "Data East", games: 10, roms: 11 }
];
const allDevelopers = developers.concat([{ key: "smallco", name: "Small Co", games: 1, roms: 1 }]);

function boot(devLogos) {
  const calls = [];
  function call(method, params) {
    calls.push({ method, params });
    const data = (params && params.data) || {};
    if (method !== "callPluginRoute") {
      return Promise.resolve(method === "dataGet" ? null : {});
    }
    if (params.path === "systems") {
      return Promise.resolve({
        ok: true, isAdmin: false, indexing: false,
        systems: [{ system: "Genesis", games: 8, roms: 9, bytes: 900 }, { system: "Arcade", games: 14, roms: 40, bytes: 9000 }],
        totals: { games: 22, roms: 49, bytes: 9900 }, status: { state: "ready" }
      });
    }
    if (params.path === "developers") {
      const list = Number(data.min) >= 10 ? developers : allDevelopers;
      return Promise.resolve({ ok: true, indexing: false, total: allDevelopers.length, developers: list });
    }
    if (params.path === "logos") {
      const logos = {};
      (data.names || []).forEach((name) => {
        if (data.kind === "consoles" && name === "Genesis") logos[name] = SVG;
        if (data.kind === "developers" && devLogos[name]) logos[name] = devLogos[name];
      });
      return Promise.resolve({ ok: true, kind: data.kind, logos });
    }
    if (params.path === "games") {
      if (data.developer === "Sega") {
        return Promise.resolve({
          ok: true, total: 2, indexing: false,
          items: [
            { key: "p:Genesis/Sonic", title: "Sonic the Hedgehog", system: "Genesis", versions: 1, developer: "Sega" },
            { key: "p:Arcade/OutRun", title: "Out Run", system: "Arcade", versions: 1, developer: "Sega" }
          ]
        });
      }
      return Promise.resolve({ ok: true, total: 1, indexing: false, items: [{ key: "p:Genesis/Gunstar", title: "Gunstar Heroes", system: "Genesis", versions: 1 }] });
    }
    if (params.path === "covers") return Promise.resolve({ ok: true, covers: {} });
    return Promise.resolve({ ok: true });
  }

  const html = readFileSync(join(root, "client/index.html"), "utf8")
    .replace(/<link\b[^>]*>/gi, "")
    .replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, "");
  const dom = new JSDOM(html, { url: "http://127.0.0.1/client/index.html#/library", pretendToBeVisual: true, runScripts: "dangerously" });
  const window = dom.window;
  const errors = [];
  window.addEventListener("error", (event) => errors.push(String((event.error && event.error.stack) || event.message)));
  window.addEventListener("unhandledrejection", (event) => errors.push(String((event.reason && event.reason.stack) || event.reason)));
  window.MoFinHost = { call };
  function run(code) {
    const script = window.document.createElement("script");
    script.textContent = code;
    window.document.body.appendChild(script);
  }
  run(readFileSync(join(root, "client/format.js"), "utf8"));
  run(readFileSync(join(root, "client/app.js"), "utf8"));
  const document = window.document;
  const main = () => document.getElementById("main");
  async function until(check, label) {
    for (let i = 0; i < 100; i++) {
      if (check()) return;
      await new Promise((resolve) => setTimeout(resolve, 15));
    }
    throw new Error(label + "\n" + (main() ? main().innerHTML.slice(0, 1500) : "") + "\n" + errors.join("\n"));
  }
  return { window, document, main, until, calls, errors };
}

test("Browse by Developer shows logo developers as cards and the rest under Other", async () => {
  // Data East's "logo" is not an image data URI, so it is treated as missing.
  const { window, document, main, until, calls, errors } = boot({ Sega: SVG, "Data East": "javascript:alert(1)" });
  try {
    await until(() => main().querySelector(".dev-row"), "developer carousel");
    const heads = Array.from(main().querySelectorAll("h1")).map((h) => h.textContent);
    assert.deepEqual(heads, ["Browse by Console", "Browse by Developer"]);
    const devTiles = Array.from(main().querySelectorAll(".dev-row [data-developer]"));
    assert.deepEqual(devTiles.map((t) => t.getAttribute("data-developer")), ["Sega"]);
    assert.match(devTiles[0].textContent, /12 Games/);
    // Data East (bad logo) and Small Co (no logo) share one Other card at the end.
    const scrollerKids = Array.from(main().querySelector(".dev-row .console-scroller, .dev-row [class*='scroll']") ? main().querySelector(".dev-row .console-scroller, .dev-row [class*='scroll']").children : []);
    const other = main().querySelector(".dev-row .other-tile[data-go='developers-other']");
    assert.ok(other, "Other card");
    if (scrollerKids.length) assert.equal(scrollerKids[scrollerKids.length - 1], other);
    assert.match(other.textContent, /Other/);
    assert.match(other.textContent, /11 Games/);
    assert.equal(main().querySelector(".dev-row [data-developer='Data East']"), null);
    assert.equal(main().querySelector(".dev-row [data-go='developers']"), null, "no see-all card");
    assert.ok(main().querySelector(".dev-head [data-go='developers']"), "All developers link in the header");
    const devCalls = calls.filter((c) => c.params && c.params.path === "developers");
    assert.equal(Number(devCalls[0].params.data.min), 1);

    // Logos arrive as <img> only; a non-image URI is ignored and the styled name stays.
    await until(() => main().querySelector(".dev-row [data-developer='Sega'] img"), "Sega logo");
    await until(() => main().querySelector("[data-console='Genesis'] img.console-logo-img"), "Genesis logo");
    assert.equal(main().querySelector("[data-developer='Sega'] img").getAttribute("src"), SVG);
    assert.ok(main().querySelector("[data-developer='Sega']").classList.contains("has-logo"));
    assert.equal(main().querySelector("[data-console='Arcade'] img.console-logo-img"), null);
    assert.equal(main().querySelector("[data-console='Arcade'] .console-name").textContent, "Arcade");
    assert.equal(main().querySelectorAll("svg image, foreignObject").length, 0);
    const logoCalls = calls.filter((c) => c.params && c.params.path === "logos");
    assert.ok(logoCalls.every((c) => c.params.method === "POST"));

    // Selecting a developer opens their games across consoles with the same tabs and sort.
    devTiles[0].click();
    await until(() => /Sonic the Hedgehog/.test(main().textContent), "developer grid");
    const gamesCall = calls.filter((c) => c.params && c.params.path === "games").pop();
    assert.equal(gamesCall.params.data.developer, "Sega");
    assert.equal(gamesCall.params.data.system, "");
    assert.match(main().textContent, /Out Run/);
    assert.equal(main().querySelector(".library-sub .sub-name").textContent, "Sega");
    assert.match(main().querySelector(".library-sub .sub-count").textContent, /12 Games/);
    assert.ok(main().querySelector(".library-sub img"), "developer logo in the section header");
    assert.ok(main().querySelector("[data-lib-view='favorites']"), "same tabs");
    assert.ok(main().querySelector("[data-sort]"), "same sort");
    assert.equal(main().querySelector(".dev-row [aria-pressed='true']").getAttribute("data-developer"), "Sega");
    assert.equal(main().querySelector(".console-row:not(.dev-row) [aria-pressed='true']"), null);

    // Sorting keeps the developer filter.
    const sort = main().querySelector("[data-sort]");
    sort.value = sort.options[sort.options.length - 1].value;
    sort.dispatchEvent(new window.Event("change", { bubbles: true }));
    await until(() => {
      const last = calls.filter((c) => c.params && c.params.path === "games").pop();
      return last !== gamesCall && last.params.data.developer === "Sega";
    }, "sort keeps developer");

    // The Other card opens #/developers/other listing the logo-less developers.
    await until(() => main().querySelector(".dev-row .other-tile") && /Sonic/.test(main().textContent), "carousel after sort");
    main().querySelector(".dev-row .other-tile").click();
    await until(() => main().querySelector(".dev-list"), "other developers page");
    const fmt = window.RomFormat;
    assert.equal(JSON.stringify(fmt.parseRoute("#/developers/other")), JSON.stringify({ name: "developers-other" }));
    assert.equal(fmt.routeHash({ name: "developers-other" }), "#/developers/other");
    assert.match(main().querySelector("h1").textContent, /Other developers/);
    assert.deepEqual(Array.from(main().querySelectorAll(".dev-list [data-developer]")).map((t) => t.getAttribute("data-developer")), ["Data East", "Small Co"]);
    assert.match(main().textContent, /2 developers without a logo/);
    main().querySelector(".dev-list [data-developer='Small Co']").click();
    await until(() => main().querySelector(".library-sub .sub-name") && main().querySelector(".library-sub .sub-name").textContent === "Small Co", "small developer grid");
    assert.equal(main().querySelector(".dev-row .other-tile").getAttribute("aria-pressed"), "true");

    // All developers page still lists everyone.
    main().querySelector(".dev-head [data-go='developers']").click();
    await until(() => main().querySelector(".dev-list") && !/without a logo/.test(main().textContent), "all developers page");
    assert.deepEqual(Array.from(main().querySelectorAll(".dev-list [data-developer]")).map((t) => t.getAttribute("data-developer")), ["Sega", "Data East", "Small Co"]);
    assert.match(main().textContent, /1 Game · 1 ROMs/);
    assert.deepEqual(errors, []);
  } finally {
    window.close();
  }
});
