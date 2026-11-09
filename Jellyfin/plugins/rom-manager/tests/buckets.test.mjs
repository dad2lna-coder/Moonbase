import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { JSDOM } = require("jsdom");
const root = join(dirname(fileURLToPath(import.meta.url)), "..");

const buckets = [
  { id: "#-D", label: "# – D", games: 2000 },
  { id: "E-H", label: "E – H", games: 900 },
  { id: "I-L", label: "I – L", games: 800 },
  { id: "M-P", label: "M – P", games: 700 },
  { id: "Q-S", label: "Q – S", games: 1200 },
  { id: "T-Z", label: "T – Z", games: 1070 }
];

function boot() {
  const calls = [];
  function call(method, params) {
    calls.push({ method, params });
    const data = (params && params.data) || {};
    if (method !== "callPluginRoute") return Promise.resolve(method === "dataGet" ? null : {});
    if (params.path === "systems") {
      return Promise.resolve({
        ok: true, isAdmin: false, indexing: false,
        systems: [
          { system: "Arcade", games: 6670, roms: 8000, bytes: 1 },
          { system: "NES", games: 12, roms: 12, bytes: 1 },
          { system: "Exact", games: 1000, roms: 1000, bytes: 1 }
        ],
        totals: { games: 7682, roms: 9012, bytes: 3 }
      });
    }
    if (params.path === "developers") return Promise.resolve({ ok: true, developers: [], total: 0 });
    if (params.path === "logos" || params.path === "covers") return Promise.resolve({ ok: true, logos: {}, covers: {} });
    if (params.path === "games") {
      if (data.system === "NES") {
        return Promise.resolve({ ok: true, total: 1, indexing: false, items: [{ key: "p:NES/Mario", title: "Super Mario Bros.", system: "NES", versions: 1 }] });
      }
      if (data.system === "Exact") {
        return Promise.resolve({ ok: true, total: 1, indexing: false, items: [{ key: "p:Exact/a", title: "Exact Game", system: "Exact", versions: 1 }] });
      }
      if (data.q) {
        return Promise.resolve({ ok: true, total: 1, indexing: false, items: [{ key: "p:Arcade/dk", title: "Donkey Kong", system: "Arcade", versions: 1 }] });
      }
      if (data.includeBuckets && !data.bucket) {
        return Promise.resolve({ ok: true, total: 6670, indexing: false, buckets, items: [{ key: "p:Arcade/1942", title: "1942", system: "Arcade", versions: 2 }] });
      }
      if (data.bucket === "Q-S") {
        const offset = Number(data.offset) || 0;
        const items = [];
        for (let i = 0; i < 48; i++) {
          const n = offset + i;
          items.push({ key: "p:Arcade/q" + n, title: n === 0 ? "Q*bert" : "Q Game " + n, system: "Arcade", versions: 1 });
        }
        return Promise.resolve({ ok: true, total: 80, indexing: false, items });
      }
      return Promise.resolve({ ok: true, total: 1, indexing: false, items: [{ key: "p:Arcade/1942", title: "1942", system: "Arcade", versions: 2 }] });
    }
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
    for (let i = 0; i < 80; i++) {
      if (check()) return;
      await new Promise((resolve) => setTimeout(resolve, 15));
    }
    throw new Error(label + "\n" + (main() ? main().textContent.slice(0, 500) : "") + "\n" + errors.join("\n"));
  }
  const games = () => calls.filter((entry) => entry.params && entry.params.path === "games").map((entry) => entry.params.data);
  return { window, document, main, until, games, errors };
}

test("a system over 1000 games opens on six ranges, then pages one range", async () => {
  const { window, document, main, until, games, errors } = boot();
  try {
    await until(() => main().querySelectorAll(".range-tile").length === 6, "bucket tiles");
    assert.equal(main().querySelector(".grid .card"), null);
    assert.match(main().textContent, /Q – S/);
    assert.match(main().querySelector("[data-bucket='Q-S']").textContent, /1200 Games/);
    const probe = games().find((body) => body.includeBuckets === 1);
    assert.equal(probe.system, "Arcade");
    assert.equal(probe.limit, 1);
    assert.equal(probe.bucket, undefined);
    assert.equal(probe.letter, undefined);

    main().querySelector("[data-bucket='Q-S']").click();
    await until(() => /Q\*bert/.test(main().textContent), "bucket page");
    const page = games().filter((body) => body.bucket === "Q-S").pop();
    assert.equal(page.limit, 48);
    assert.equal(page.offset, 0);
    assert.equal(page.includeBuckets, undefined);
    assert.equal(page.system, "Arcade");
    assert.equal(main().querySelector(".sub-range").textContent, "Q – S");
    assert.match(main().querySelector(".sub-count").textContent, /80 Games/);
    assert.equal(main().querySelectorAll(".grid .card").length, 48);

    main().querySelector("[data-more]").click();
    await until(() => games().some((body) => body.bucket === "Q-S" && body.offset === 48), "load more");
    const more = games().filter((body) => body.bucket === "Q-S" && body.offset === 48).pop();
    assert.equal(more.limit, 48);

    main().querySelector("[data-bucket-back]").click();
    await until(() => main().querySelectorAll(".range-tile").length === 6, "back to ranges");
    assert.equal(main().querySelector(".grid .card"), null);

    main().querySelector("[data-console='NES']").click();
    await until(() => /Super Mario Bros\./.test(main().textContent), "small system");
    assert.equal(main().querySelector(".range-tile"), null);
    const nes = games().filter((body) => body.system === "NES").pop();
    assert.equal(nes.limit, 48);
    assert.equal(nes.includeBuckets, undefined);
    assert.equal(nes.bucket, undefined);

    main().querySelector("[data-console='Exact']").click();
    await until(() => /Exact Game/.test(main().textContent), "1000 games skips ranges");
    assert.equal(main().querySelector(".range-tile"), null);
    assert.equal(games().some((body) => body.system === "Exact" && body.includeBuckets), false);

    main().querySelector("[data-console='Arcade']").click();
    await until(() => main().querySelectorAll(".range-tile").length === 6, "arcade ranges again");
    const search = document.getElementById("search");
    search.value = "kong";
    search.dispatchEvent(new window.Event("input", { bubbles: true }));
    await until(() => /Donkey Kong/.test(main().textContent), "search skips ranges");
    const found = games().filter((body) => body.q === "kong").pop();
    assert.equal(found.bucket, undefined);
    assert.equal(found.includeBuckets, undefined);
    assert.equal(main().querySelector(".range-tile"), null);
    assert.deepEqual(errors, []);
  } finally {
    window.close();
  }
});
