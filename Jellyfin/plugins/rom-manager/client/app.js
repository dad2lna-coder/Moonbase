(function () {
  var fmt = window.RomFormat;
  var state = {
    route: fmt.parseRoute(location.hash),
    query: "",
    sort: "title",
    libraryView: "all",
    consoleName: "",
    isAdmin: false,
    totals: { games: 0, roms: 0, bytes: 0 },
    systems: [],
    indexing: false,
    status: null,
    favorites: [],
    recent: [],
    collections: [],
    list: [],
    total: 0,
    token: 0,
    current: null,
    tab: "versions",
    coverQueue: [],
    coverTimer: 0,
    coverBusy: false,
    observer: null,
    consoleArt: {},
    artQueue: [],
    artBusy: false,
    artObserver: null
  };

  var NAV = [
    ["home", "Home", "M3 11l9-7 9 7v9a2 2 0 0 1-2 2h-4v-6H9v6H5a2 2 0 0 1-2-2z"],
    ["library", "Library", "M4 5h7v14H4zM13 5h7v14h-7z"],
    ["favorites", "Favorites", "M12 20s-7-4.4-7-9a4 4 0 0 1 7-2 4 4 0 0 1 7 2c0 4.6-7 9-7 9z"],
    ["recent", "Recently Played", "M12 7v6l4 2M12 21a9 9 0 1 0-9-9"],
    ["collections", "Collections", "M4 7h16v12H4zM8 7V5h8v2"],
    ["platforms", "Platforms", "M4 8h16v10H4zM8 8V6h8v2"],
    ["achievements", "Achievements", "M8 4h8v3a4 4 0 0 1-8 0zM8 7H6a2 2 0 0 0 2 2M16 7h2a2 2 0 0 1-2 2M12 14v3M9 20h6"],
    ["settings", "Settings", "M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8z"]
  ];

  var PLATFORM_ICONS = {
    nes: "M4 8h16v8H4zM8 16v2M16 16v2M9 11h2v2H9zm4 0h2v2h-2z",
    snes: "M3 9h18v7H3zM7 16v2h2v-2M15 16v2h2v-2M8 12h8",
    genesis: "M5 7h14v10H5zM8 10h3v4H8zm5 0h3v4h-3z",
    gb: "M7 3h10v18H7zM9 6h6v6H9zM10 16h1M13 16h1",
    gba: "M2 9h20v6H2zM6 11h4v2H6zM16 12h.01",
    n64: "M4 8h10v8H4zM14 10h6v4h-6z",
    psx: "M6 5h12v14H6zM9 9h6v6H9z",
    psp: "M3 7h18v10H3zM7 10h6v4H7z",
    dreamcast: "M8 4h8l2 4v8a4 4 0 0 1-4 4h-4a4 4 0 0 1-4-4V8zM10 8h4",
    arcade: "M4 4h16v12H4zM8 20h8M9 16v4M15 16v4"
  };
  var GENERIC_ICON = "M7 6h10a3 3 0 0 1 3 3v2a2 2 0 0 1-2 2h-1v2H7v-2H6a2 2 0 0 1-2-2V9a3 3 0 0 1 3-3zM8 8h2M14 8h2";
  var GAMEPAD_ICON = "M6 9h12a3 3 0 0 1 3 3v2a3 3 0 0 1-3 3H6a3 3 0 0 1-3-3v-2a3 3 0 0 1 3-3zM8 13v3M6.5 14.5h3M16 14h.01M18 16h.01";
  // Brand-ish gradient per console: top colour, bottom colour, glow colour.
  var CONSOLE_COLORS = {
    nes: "#d8373a,#3b0c12,#ff5a5f",
    snes: "#6f58e8,#1c1250,#9d8bff",
    n64: "#1f8f4e,#0b2a4a,#ffcc33",
    gamecube: "#6a4cc9,#160f3c,#a58bff",
    wii: "#8fa2b8,#1d2633,#d8e6f5",
    gb: "#8bac0f,#1d3511,#c4e24a",
    gbc: "#7b3fc4,#22113f,#c084fc",
    gba: "#5b3fb8,#170f3d,#a78bfa",
    nds: "#a9b6c6,#262f3c,#e2ecf7",
    n3ds: "#d4343a,#3a0c10,#ff6b70",
    virtualboy: "#d11c1c,#1a0303,#ff4040",
    pokemini: "#e2c33a,#3a2e08,#ffe066",
    genesis: "#2c2c34,#0a0a0f,#e8343a",
    mastersystem: "#c62828,#1a1a1f,#ff5252",
    gamegear: "#2f3540,#0b1020,#3d8bff",
    segacd: "#2c6fd6,#0b1a3a,#7fb2ff",
    sega32x: "#d4a017,#2a1c03,#ffd24a",
    saturn: "#4b5d82,#0f1526,#9fb4e0",
    dreamcast: "#f26b1d,#132a52,#ff9a4d",
    psx: "#3b6fd8,#0b1840,#7ea6ff",
    ps2: "#1f4fd1,#050f33,#4f8cff",
    psp: "#4a5568,#0d1117,#a0aec0",
    arcade: "#ff4d2e,#3a0a3f,#ffb02e",
    neogeo: "#c9a227,#141414,#ffd84a",
    colecovision: "#e2862b,#1c1208,#ffae55",
    intellivision: "#b38a4f,#21170a,#e5c389",
    atari2600: "#c8742c,#24140a,#ffa45c",
    atari5200: "#5d6b7a,#12161c,#9fb0c2",
    atari7800: "#9b2c2c,#1d0a0a,#e05a5a",
    lynx: "#e07b24,#21120a,#ffa552",
    jaguar: "#c41e1e,#140404,#ff4b4b",
    pcengine: "#ff6a00,#1a1209,#ff9a40",
    turbografx16: "#ff6a00,#1a1209,#ff9a40",
    wonderswan: "#3a8ad8,#0b1c33,#7cc0ff",
    ngp: "#2d7d9a,#0b2229,#6cc8e8",
    msx: "#2f62c9,#0a1430,#76a1ff",
    zxspectrum: "#d6272d,#111111,#ffd23f",
    c64: "#7a6ec8,#221d48,#b5abff",
    amiga: "#d35c2a,#1a0e08,#ff8c55",
    channelf: "#b57a3c,#1e1309,#e8ad6c",
    vectrex: "#3fbf6f,#06180d,#7dffb0",
    threedo: "#c9a227,#191307,#ffd84a",
    dos: "#4a6b8a,#0b141d,#8fb7dc"
  };
  var CONSOLE_ALIASES = {
    famicom: "nes", nintendo: "nes", nintendoentertainmentsystem: "nes",
    supernintendo: "snes", superfamicom: "snes", supernes: "snes", sfc: "snes",
    nintendo64: "n64", gc: "gamecube", ngc: "gamecube", nintendogamecube: "gamecube",
    gameboy: "gb", gameboycolor: "gbc", gameboyadvance: "gba",
    nintendods: "nds", ds: "nds", "3ds": "n3ds", nintendo3ds: "n3ds",
    megadrive: "genesis", segagenesis: "genesis", segamegadrive: "genesis", md: "genesis",
    sms: "mastersystem", segamastersystem: "mastersystem", gg: "gamegear", segagamegear: "gamegear",
    megacd: "segacd", "32x": "sega32x", segasaturn: "saturn",
    dc: "dreamcast", segadreamcast: "dreamcast",
    playstation: "psx", ps1: "psx", ps: "psx", sonyplaystation: "psx",
    playstation2: "ps2", playstationportable: "psp",
    mame: "arcade", fbneo: "arcade", fba: "arcade", cps1: "arcade", cps2: "arcade", cps3: "arcade",
    neogeoaes: "neogeo", neogeomvs: "neogeo", neogeocd: "neogeo",
    coleco: "colecovision", atari: "atari2600", a2600: "atari2600", a7800: "atari7800",
    atarilynx: "lynx", atarijaguar: "jaguar",
    pce: "pcengine", tg16: "turbografx16", turbografx: "turbografx16",
    ws: "wonderswan", wsc: "wonderswan", wonderswancolor: "wonderswan",
    neogeopocket: "ngp", neogeopocketcolor: "ngp", ngpc: "ngp",
    zx: "zxspectrum", spectrum: "zxspectrum", commodore64: "c64",
    fairchildchannelf: "channelf", "3do": "threedo", msdos: "dos", pc: "dos"
  };

  function platformIcon(system) {
    var key = String(system || "").toLowerCase().replace(/[^a-z0-9]/g, "");
    var aliases = {
      supernintendo: "snes",
      superfamicom: "snes",
      famicom: "nes",
      megadrive: "genesis",
      segagenesis: "genesis",
      playstation: "psx",
      dreamcast: "dreamcast",
      dc: "dreamcast",
      segadreamcast: "dreamcast",
      nintendo64: "n64",
      gameboy: "gb",
      gameboycolor: "gb",
      gameboyadvance: "gba",
      mame: "arcade",
      fbneo: "arcade",
      neogeo: "arcade",
      ps1: "psx",
      ps2: "psx",
      playstation2: "psx",
      mastersystem: "genesis",
      sms: "genesis",
      gamegear: "gba",
      gg: "gba",
      gbc: "gb",
      nds: "gba",
      colecovision: "snes",
      gamecube: "n64"
    };
    var icon = el("span", { class: "plat", "aria-hidden": "true" });
    icon.appendChild(svg(PLATFORM_ICONS[aliases[key] || key] || GENERIC_ICON));
    return icon;
  }

  function el(tag, attrs, kids) {
    var node = document.createElement(tag);
    Object.keys(attrs || {}).forEach(function (key) {
      if (key === "text") node.textContent = attrs[key] == null ? "" : String(attrs[key]);
      else if (attrs[key] != null) node.setAttribute(key, String(attrs[key]));
    });
    (kids || []).forEach(function (kid) { if (kid) node.appendChild(kid); });
    return node;
  }

  function svg(d) {
    var html = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="' + d + '"/></svg>';
    var wrap = el("span", { class: "ico" });
    wrap.innerHTML = html;
    return wrap;
  }

  function hostCall(method, params) {
    if (!window.MoFinHost || typeof window.MoFinHost.call !== "function") {
      return Promise.reject(new Error("Open ROM Manager inside MoFin."));
    }
    var pending = window.MoFinHost.call(method, params || {});
    return new Promise(function (resolve, reject) {
      var settled = false;
      var timer = setTimeout(function () {
        if (settled) return;
        settled = true;
        reject(new Error("MoFin stopped answering. Tap reload to try again."));
      }, 15000);
      pending.then(function (result) {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        resolve(result);
      }, function (error) {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        reject(error);
      });
    });
  }

  function asObject(value) {
    if (typeof value === "string") {
      try { return JSON.parse(value); } catch (e) { return null; }
    }
    return value;
  }

  function showToast(message) {
    var toast = document.getElementById("toast");
    toast.textContent = message;
    toast.classList.add("show");
    clearTimeout(showToast._t);
    showToast._t = setTimeout(function () { toast.classList.remove("show"); }, 2800);
    hostCall("showToast", { message: message }).catch(function () {});
  }

  function playGame(game, version, button) {
    if (!game || !version || !version.p) {
      showToast("This version cannot be played.");
      return;
    }
    var label = button ? button.textContent : "Play";
    if (button) {
      button.disabled = true;
      button.setAttribute("aria-busy", "true");
      button.textContent = "Starting…";
    }
    api("launch", { key: game.key, p: version.p }).then(function (result) {
      if (!result || result.playable !== true) {
        var why = (result && result.reason) || "This version cannot be played.";
        showToast(why);
        if (button) {
          button.textContent = why;
          button.classList.remove("primary");
          button.classList.add("text-btn");
        }
        return;
      }
      remember(game.key);
      var play = {
        libraryId: result.libraryId,
        gameId: result.gameId,
        title: result.title || game.title || ""
      };
      if (result.core) play.core = result.core;
      if (result.biosUrl) play.biosUrl = result.biosUrl;
      return hostCall("playGame", play).then(function () {
        if (button) button.textContent = label;
      });
    }).catch(function (error) {
      showToast(error.message || "Could not start the game.");
      if (button) button.textContent = label;
    }).then(function () {
      if (button) {
        button.disabled = false;
        button.removeAttribute("aria-busy");
      }
    });
  }

  async function apiRaw(path, data) {
    return asObject(await hostCall("callPluginRoute", { path: path, method: "POST", data: data || {} }));
  }

  async function api(path, data) {
    var result = await apiRaw(path, data);
    if (!result || result.ok === false) {
      var error = new Error((result && result.error) || "Request failed");
      error.payload = result;
      throw error;
    }
    return result;
  }

  function isFav(key) { return state.favorites.indexOf(key) >= 0; }

  function save(key, value) {
    return hostCall("dataPut", { key: key, value: value }).catch(function () {});
  }

  function remember(key) {
    state.recent = [key].concat(state.recent.filter(function (item) { return item !== key; })).slice(0, 30);
    save("recent", state.recent);
  }

  function toggleFav(key) {
    if (isFav(key)) state.favorites = state.favorites.filter(function (item) { return item !== key; });
    else state.favorites = [key].concat(state.favorites);
    save("favorites", state.favorites);
    render();
  }

  function go(route) {
    state.route = route || { name: "home" };
    if (state.route.name !== "game") state.tab = "versions";
    render();
  }

  function updateNav() {
    var name = state.route.name === "collection" ? "collections" : state.route.name;
    document.querySelectorAll("#side-nav [data-go], #switcher [data-go]").forEach(function (button) {
      button.classList.toggle("active", button.getAttribute("data-go") === name);
    });
    var total = document.getElementById("total-games");
    if (total) total.textContent = String(state.totals.games || 0);
    var used = document.getElementById("storage-used");
    var fill = document.getElementById("storage-fill");
    var bytes = Number(state.totals && state.totals.bytes) || 0;
    if (used) used.textContent = fmt.formatBytes(bytes);
    if (fill) fill.style.width = storagePercent(bytes) + "%";
  }

  function banner() {
    if (!state.indexing) return null;
    return el("div", { class: "banner", text: "Library is being indexed" });
  }

  function clearMain(main) {
    var note = banner();
    main.replaceChildren();
    if (note) main.appendChild(note);
    return main;
  }

  function showPageError(main, token, message) {
    if (token !== state.token) return;
    clearMain(main);
    main.appendChild(el("div", { class: "banner", text: message || "Could not load the library." }));
  }

  function cheatPill() {
    return el("span", { class: "pill cheat", title: "Cheat or trainer ROM", text: "Cheat" });
  }

  function cardEl(item, opts) {
    var library = opts && opts.library;
    var card = el("div", { class: "card" + (library ? " library" : ""), tabindex: "0", "data-nav": "1", "data-open": item.key || "" });
    var art = el("div", { class: "art" });
    if (item.cover && item.coverPath) {
      art.setAttribute("data-cover", "1");
      art.setAttribute("data-p", item.coverPath);
      art.setAttribute("data-w", "200");
      if (item.lb) art.setAttribute("data-lb", item.lb);
    }
    art.appendChild(el("span", { class: "ph", text: item.system || "ROM" }));
    var img = el("img", { alt: "" });
    img.hidden = true;
    art.appendChild(img);
    art.appendChild(platformIcon(item.system));
    if (!library) art.appendChild(el("span", { class: "badge", text: item.system || "" }));
    if (library && Number(item.versions) > 1) {
      art.classList.add("has-vers");
      art.appendChild(el("span", { class: "ver-badge", text: item.versions + " Versions" }));
    }
    if (item.cheat) {
      art.classList.add("has-cheat");
      art.appendChild(el("span", {
        class: "cheat-badge",
        title: item.cheat === "best" ? "The best file is a cheat or trainer ROM" : "Some versions are cheat or trainer ROMs",
        text: "Cheat"
      }));
    }
    var star = el("button", {
      class: "star" + (isFav(item.key) ? " on" : ""),
      type: "button",
      "data-nav": "1",
      "data-fav": item.key || "",
      "aria-label": isFav(item.key) ? "Remove favorite" : "Add favorite",
      "aria-pressed": isFav(item.key) ? "true" : "false",
      text: "★"
    });
    art.appendChild(star);
    card.appendChild(art);
    var meta = el("div", { class: "meta" });
    meta.appendChild(el("strong", { class: "clamp", text: item.title || "Untitled" }));
    if (!library) meta.appendChild(el("div", { class: "tiny", text: item.system || "" }));
    card.appendChild(meta);
    return card;
  }

  function row(title, items, empty) {
    var section = el("section", { class: "row" });
    section.appendChild(el("h2", { text: title }));
    if (!items.length) {
      section.appendChild(el("div", { class: "empty", text: empty }));
      return section;
    }
    var scroller = el("div", { class: "hscroll" });
    items.forEach(function (item) { scroller.appendChild(cardEl(item)); });
    section.appendChild(scroller);
    return section;
  }

  async function renderHome(main) {
    clearMain(main);
    main.appendChild(el("div", { class: "page-head" }, [el("h1", { text: "Home" })]));
    main.appendChild(el("p", { class: "muted", text: "Loading rows…" }));
    var token = state.token;
    try {
      var platforms = state.systems.slice(0, 4);
      var jobs = [
        state.recent.length ? api("games", { keys: state.recent, limit: 18 }) : Promise.resolve({ items: [] }),
        state.favorites.length ? api("games", { keys: state.favorites, limit: 18 }) : Promise.resolve({ items: [] })
      ];
      platforms.forEach(function (entry) {
        jobs.push(api("games", { system: entry.system, sort: "title", offset: 0, limit: 12 }));
      });
      var results = await Promise.all(jobs);
      if (token !== state.token) return;
      state.indexing = results.some(function (result) { return result && result.indexing; });
      clearMain(main);
      main.appendChild(el("div", { class: "page-head" }, [el("h1", { text: "Home" })]));
      main.appendChild(row("Recently Played", orderBy((results[0] && results[0].items) || [], state.recent), "Games you play will show up here."));
      main.appendChild(row("Favorites", orderBy((results[1] && results[1].items) || [], state.favorites), "Star a game to keep it here."));
      platforms.forEach(function (entry, index) {
        main.appendChild(row(entry.system, (results[index + 2] && results[index + 2].items) || [], "No games for this platform yet."));
      });
      if (!platforms.length) {
        main.appendChild(el("div", { class: "empty", text: state.indexing ? "Library is being indexed" : "No platforms yet." }));
      }
      watchCovers(main);
    } catch (error) {
      showPageError(main, token, error.message || "Could not load the library.");
    }
  }

  function orderBy(items, keys) {
    var rank = {};
    (keys || []).forEach(function (key, index) { rank[key] = index; });
    return (items || []).slice().sort(function (a, b) {
      var left = rank[a.key] == null ? 99 : rank[a.key];
      var right = rank[b.key] == null ? 99 : rank[b.key];
      return left - right;
    });
  }

  function paintList(main, title, items, empty) {
    clearMain(main);
    main.appendChild(el("div", { class: "page-head" }, [el("h1", { text: title })]));
    if (!items.length) {
      main.appendChild(el("div", { class: "empty", text: empty }));
      return;
    }
    var grid = el("div", { class: "grid" });
    items.forEach(function (item) { grid.appendChild(cardEl(item)); });
    main.appendChild(grid);
    watchCovers(main);
  }

  async function renderKeyed(main, title, keys, empty) {
    clearMain(main);
    if (!keys.length) {
      paintList(main, title, [], empty);
      return;
    }
    main.appendChild(el("div", { class: "page-head" }, [el("h1", { text: title })]));
    main.appendChild(el("p", { class: "muted", text: "Loading…" }));
    var token = state.token;
    try {
      var result = await api("games", { keys: keys, limit: 60 });
      if (token !== state.token) return;
      state.indexing = !!result.indexing;
      paintList(main, title, result.items || [], empty);
    } catch (error) {
      showPageError(main, token, error.message);
    }
  }

  function consoleKey(system) {
    return String(system || "").toLowerCase().replace(/[^a-z0-9]/g, "");
  }

  function consoleColors(system) {
    var key = consoleKey(system);
    var pair = (CONSOLE_COLORS[CONSOLE_ALIASES[key] || key] || "#2a5fae,#0b1a33,#6ea8ff").split(",");
    return { top: pair[0], bottom: pair[1], glow: pair[2] || pair[0] };
  }

  function consoleGradient(system) {
    var c = consoleColors(system);
    return "radial-gradient(120% 70% at 50% 0%, " + c.top + " 0%, transparent 70%), linear-gradient(170deg, " + c.top + " 0%, " + c.bottom + " 78%)";
  }

  function consoleBadge(system, cls) {
    var badge = el("span", { class: cls || "console-badge", "aria-hidden": "true" });
    badge.style.background = consoleGradient(system);
    badge.style.setProperty("--glow", consoleColors(system).glow);
    badge.appendChild(platformIcon(system).firstChild);
    return badge;
  }

  // A size factor for the console name, in container-inline units, so the full
  // name fits the card without truncating. Long names wrap on spaces first.
  function nameFit(name) {
    var text = String(name || "ROM");
    var words = text.split(/[\s]+/).filter(Boolean);
    var longest = words.reduce(function (n, w) { return Math.max(n, w.length); }, 1);
    var len = text.length <= 11 ? text.length : longest;
    return Math.max(6, Math.min(30, 150 / Math.max(len, 1)));
  }

  function gamesLabel(count) {
    var n = Number(count) || 0;
    return n === 1 ? "1 Game" : n + " Games";
  }

  function storagePercent(bytes) {
    var n = Number(bytes) || 0;
    if (n <= 0) return 0;
    var cap = Math.log10(100 * 1024 * 1024 * 1024);
    var pct = Math.round((Math.log10(n) / cap) * 100);
    if (pct < 8) pct = 8;
    if (pct > 100) pct = 100;
    return pct;
  }

  function sortLocal(items, mode) {
    var list = (items || []).slice();
    function title(item) { return String(item.title || "").toLowerCase(); }
    function key(item) { return String(item.key || ""); }
    list.sort(function (a, b) {
      if (mode === "title-desc") return title(b).localeCompare(title(a)) || key(a).localeCompare(key(b));
      if (mode === "added") return (Number(b.added) || 0) - (Number(a.added) || 0) || title(a).localeCompare(title(b));
      if (mode === "size") return (Number(b.sizeBytes) || 0) - (Number(a.sizeBytes) || 0) || title(a).localeCompare(title(b));
      return title(a).localeCompare(title(b)) || key(a).localeCompare(key(b));
    });
    return list;
  }

  function chosenConsole() {
    if (state.route.system) state.consoleName = state.route.system;
    if (!state.consoleName && state.systems[0]) state.consoleName = state.systems[0].system;
    return state.consoleName || "";
  }

  function consoleRow(system) {
    for (var i = 0; i < state.systems.length; i++) {
      if (state.systems[i].system === system) return state.systems[i];
    }
    return null;
  }

  function browseHead() {
    var head = el("div", { class: "page-head browse-head" });
    var title = el("h1");
    title.appendChild(svg(GAMEPAD_ICON));
    title.appendChild(document.createTextNode("Browse by Console"));
    head.appendChild(title);
    return head;
  }

  function libraryBody(offset) {
    var body = {
      system: state.consoleName || "",
      q: state.query || "",
      sort: state.sort || "title",
      offset: offset || 0,
      limit: 48
    };
    if (state.libraryView === "favorites") body.keys = state.favorites.slice();
    if (state.libraryView === "recent") body.keys = state.recent.slice();
    return body;
  }

  function viewKeysEmpty() {
    if (state.libraryView === "favorites") return state.favorites.length === 0;
    if (state.libraryView === "recent") return state.recent.length === 0;
    return false;
  }

  async function renderLibrary(main) {
    chosenConsole();
    clearMain(main);
    main.appendChild(browseHead());
    main.appendChild(el("p", { class: "muted", text: "Loading…" }));
    var token = state.token;
    if (!state.systems.length) {
      if (token !== state.token) return;
      drawLibrary(main);
      return;
    }
    if (viewKeysEmpty()) {
      state.list = [];
      state.total = 0;
      drawLibrary(main);
      return;
    }
    try {
      var result = await api("games", libraryBody(0));
      if (token !== state.token) return;
      var items = result.items || [];
      if (state.libraryView !== "all") items = sortLocal(items, state.sort);
      state.list = items;
      state.total = result.total || 0;
      state.indexing = !!result.indexing;
      drawLibrary(main);
    } catch (error) {
      showPageError(main, token, error.message);
    }
  }

  function drawLibrary(main) {
    clearMain(main);
    var system = chosenConsole();
    var rowInfo = consoleRow(system);
    main.appendChild(browseHead());
    main.appendChild(consoleCarousel(system));
    watchConsoleArt(main);
    var sub = el("div", { class: "library-sub" });
    if (system) sub.appendChild(consoleBadge(system, "console-badge"));
    var subTitle = el("h2");
    subTitle.appendChild(el("span", { class: "sub-name", text: system || "Library" }));
    subTitle.appendChild(el("span", { class: "sub-sep", "aria-hidden": "true", text: "|" }));
    subTitle.appendChild(el("span", { class: "sub-count", text: gamesLabel(rowInfo ? rowInfo.games : state.total) }));
    sub.appendChild(subTitle);
    main.appendChild(sub);
    main.appendChild(libraryTools());
    if (!state.systems.length) {
      main.appendChild(el("div", { class: "empty", text: state.indexing ? "Library is being indexed" : "No platforms yet." }));
      return;
    }
    if (!state.list.length) {
      var empty = "No games for this console yet.";
      if (state.query) empty = "No games match that search.";
      else if (state.libraryView === "favorites") empty = "No favorites for this console yet.";
      else if (state.libraryView === "recent") empty = "No recently played games for this console yet.";
      main.appendChild(el("div", { class: "empty", text: empty }));
      return;
    }
    var grid = el("div", { class: "grid" });
    state.list.forEach(function (item) { grid.appendChild(cardEl(item, { library: true })); });
    main.appendChild(grid);
    if (state.list.length < state.total) {
      main.appendChild(el("button", { type: "button", class: "text-btn", "data-nav": "1", "data-more": "1", text: "Load more" }));
    }
    watchCovers(main);
    var selected = main.querySelector(".console[aria-pressed='true']");
    if (selected && selected.scrollIntoView) selected.scrollIntoView({ inline: "nearest", block: "nearest" });
  }

  function consoleCarousel(system) {
    var row = el("div", { class: "console-row" });
    row.appendChild(el("button", { type: "button", class: "console-arrow", "data-console-nav": "prev", "aria-label": "Previous consoles", text: "‹" }));
    var scroller = el("div", { class: "console-scroll" });
    state.systems.forEach(function (entry) {
      var selected = entry.system === system;
      var tile = el("button", {
        type: "button",
        class: "console" + (selected ? " selected" : ""),
        "data-nav": "1",
        "data-console": entry.system,
        "aria-pressed": selected ? "true" : "false",
        "aria-label": (entry.system || "Console") + ", " + gamesLabel(entry.games)
      });
      var colors = consoleColors(entry.system);
      var face = el("span", { class: "console-face" });
      face.style.background = consoleGradient(entry.system);
      face.style.setProperty("--glow", colors.glow);
      var fan = el("span", { class: "console-fan", "aria-hidden": "true" });
      face.appendChild(fan);
      if (entry.logoUrl) {
        face.appendChild(el("img", { class: "console-logo-img", alt: "", src: entry.logoUrl }));
      }
      face.appendChild(el("span", { class: "console-gloss", "aria-hidden": "true" }));
      var logo = el("span", { class: "console-logo" });
      logo.appendChild(consoleBadge(entry.system, "console-glyph"));
      var name = el("b", { class: "console-name", text: entry.system || "ROM" });
      name.style.setProperty("--fit", String(nameFit(entry.system)));
      logo.appendChild(name);
      face.appendChild(logo);
      tile.appendChild(face);
      tile.appendChild(el("span", { class: "tiny console-count", text: gamesLabel(entry.games) }));
      scroller.appendChild(tile);
      paintConsoleArt(tile, entry.system);
    });
    row.appendChild(scroller);
    row.appendChild(el("button", { type: "button", class: "console-arrow", "data-console-nav": "next", "aria-label": "Next consoles", text: "›" }));
    return row;
  }

  // Console artwork is a fan of up to four box-art covers from that console's own
  // games, fetched once per system through the existing games and covers routes
  // and kept for the session. Nothing is downloaded from outside the library.
  function paintConsoleArt(tile, system) {
    var art = state.consoleArt[system];
    var fan = tile && tile.querySelector(".console-fan");
    if (!fan || !art || !art.uris) return;
    var uris = art.uris.slice(0, 4);
    fan.replaceChildren();
    fan.setAttribute("data-count", String(uris.length));
    uris.forEach(function (uri, index) {
      var cover = el("span", { class: "console-cover c" + index });
      cover.appendChild(el("img", { alt: "", src: uri }));
      fan.appendChild(cover);
    });
    tile.classList.toggle("has-art", uris.length > 0);
  }

  function watchConsoleArt(root) {
    if (state.artObserver) {
      state.artObserver.disconnect();
      state.artObserver = null;
    }
    var tiles = Array.prototype.slice.call(root.querySelectorAll(".console[data-console]"));
    var want = tiles.filter(function (tile) { return !state.consoleArt[tile.getAttribute("data-console")]; });
    if (!want.length) return;
    if (typeof IntersectionObserver !== "function") {
      want.forEach(function (tile) { queueConsoleArt(tile.getAttribute("data-console")); });
      return;
    }
    state.artObserver = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        if (state.artObserver) state.artObserver.unobserve(entry.target);
        queueConsoleArt(entry.target.getAttribute("data-console"));
      });
    }, { rootMargin: "120px 240px" });
    want.forEach(function (tile) { state.artObserver.observe(tile); });
  }

  function queueConsoleArt(system) {
    if (!system || state.consoleArt[system] || state.artQueue.indexOf(system) >= 0) return;
    state.artQueue.push(system);
    pumpConsoleArt();
  }

  function pickSpread(items, count) {
    if (items.length <= count) return items.slice();
    var out = [];
    for (var i = 0; i < count; i++) out.push(items[Math.floor((i * items.length) / count)]);
    return out;
  }

  async function pumpConsoleArt() {
    if (state.artBusy) return;
    state.artBusy = true;
    try {
      while (state.artQueue.length && state.route.name === "library") {
        var system = state.artQueue.shift();
        if (state.consoleArt[system]) continue;
        state.consoleArt[system] = { pending: true };
        var uris = [];
        try {
          var list = await api("games", { system: system, sort: "title", offset: 0, limit: 40 });
          var withCover = ((list && list.items) || []).filter(function (item) { return item && item.cover && item.coverPath; });
          var picks = pickSpread(withCover, 4);
          if (picks.length) {
            var result = await api("covers", {
              items: picks.map(function (item) { return { p: item.coverPath, lb: item.lb || "", w: 200 }; })
            });
            var covers = (result && result.covers) || {};
            picks.forEach(function (item) { if (covers[item.coverPath]) uris.push(covers[item.coverPath]); });
          }
          state.consoleArt[system] = { uris: uris };
        } catch (e) {
          /* No art: the card keeps its gradient, glyph and name. Try again next visit. */
          delete state.consoleArt[system];
          continue;
        }
        document.querySelectorAll('#main .console[data-console="' + cssEscape(system) + '"]').forEach(function (tile) {
          paintConsoleArt(tile, system);
        });
      }
    } finally {
      state.artBusy = false;
    }
  }

  var FILTER_ICON = "M4 6h16M7 12h10M10 18h4";
  var CHEVRON_ICON = "M6 9l6 6 6-6";

  function libraryTools() {
    var tools = el("div", { class: "filters lib-tools" });
    var seg = el("div", { class: "seg", role: "group", "aria-label": "Show" });
    [["all", "All Games"], ["favorites", "Favorites"], ["recent", "Recently Played"]].forEach(function (pair) {
      seg.appendChild(el("button", {
        type: "button",
        class: "chip" + (state.libraryView === pair[0] ? " active" : ""),
        "data-nav": "1",
        "data-lib-view": pair[0],
        "aria-pressed": state.libraryView === pair[0] ? "true" : "false",
        text: pair[1]
      }));
    });
    tools.appendChild(seg);
    var right = el("div", { class: "tools-right" });
    var wrap = el("label", { class: "sort-wrap" });
    wrap.appendChild(el("span", { class: "sort-label", text: "Sort:" }));
    var sort = el("select", { class: "sort", "data-sort": "1", "aria-label": "Sort" });
    [["title", "Title A–Z"], ["title-desc", "Title Z–A"], ["added", "Recently Added"], ["size", "Size"]].forEach(function (pair) {
      var option = el("option", { value: pair[0], text: pair[1] });
      if (pair[0] === state.sort) option.setAttribute("selected", "selected");
      sort.appendChild(option);
    });
    wrap.appendChild(sort);
    var chev = svg(CHEVRON_ICON);
    chev.classList.add("sort-chev");
    wrap.appendChild(chev);
    right.appendChild(wrap);
    var filter = el("button", {
      type: "button",
      class: "icon-btn filter-btn" + (state.query ? " on" : ""),
      "data-nav": "1",
      "data-filter": "1",
      "aria-label": "Filter",
      title: "Filter"
    });
    filter.appendChild(svg(FILTER_ICON));
    right.appendChild(filter);
    tools.appendChild(right);
    return tools;
  }

  function nudgeConsoles(dir) {
    var scroller = document.querySelector(".console-scroll");
    if (!scroller) return;
    var delta = dir === "next" ? 168 : -168;
    if (typeof scroller.scrollBy === "function") scroller.scrollBy({ left: delta, behavior: "smooth" });
    else scroller.scrollLeft = (scroller.scrollLeft || 0) + delta;
  }

  function moveCarousel(dir) {
    var tiles = Array.prototype.slice.call(document.querySelectorAll("#main .console"));
    if (!tiles.length || !document.activeElement || !document.activeElement.closest) return false;
    var current = document.activeElement.closest(".console");
    if (!current) return false;
    if (dir === "down") {
      var card = document.querySelector("#main .grid .card");
      if (card) {
        card.focus();
        if (card.scrollIntoView) card.scrollIntoView({ block: "nearest", inline: "nearest" });
      }
      return true;
    }
    if (dir !== "left" && dir !== "right") return false;
    var index = tiles.indexOf(current);
    var next = tiles[index + (dir === "right" ? 1 : -1)];
    if (next) {
      next.focus();
      if (next.scrollIntoView) next.scrollIntoView({ block: "nearest", inline: "nearest" });
    }
    return true;
  }

  async function loadMore() {
    if (viewKeysEmpty()) return;
    var token = state.token;
    var result = await api("games", libraryBody(state.list.length));
    if (token !== state.token) return;
    var items = state.list.concat(result.items || []);
    if (state.libraryView !== "all") items = sortLocal(items, state.sort);
    state.list = items;
    state.total = result.total || state.total;
    drawLibrary(document.getElementById("main"));
  }

  function renderPlatforms(main) {
    clearMain(main);
    main.appendChild(el("div", { class: "page-head" }, [el("h1", { text: "Platforms" })]));
    if (!state.systems.length) {
      main.appendChild(el("div", { class: "empty", text: state.indexing ? "Library is being indexed" : "No platforms yet." }));
      return;
    }
    var grid = el("div", { class: "platforms" });
    state.systems.forEach(function (row) {
      var tile = el("button", { type: "button", class: "tile", "data-nav": "1", "data-system": row.system });
      tile.appendChild(el("b", { text: row.system }));
      tile.appendChild(el("span", { text: row.games + " games · " + row.roms + " ROMs · " + fmt.formatBytes(row.bytes) }));
      grid.appendChild(tile);
    });
    main.appendChild(grid);
  }

  function renderAchievements(main) {
    clearMain(main);
    main.appendChild(el("div", { class: "page-head" }, [el("h1", { text: "Achievements" })]));
    main.appendChild(el("div", { class: "empty", text: "Coming later" }));
  }

  function renderCollections(main) {
    clearMain(main);
    var head = el("div", { class: "page-head" });
    head.appendChild(el("h1", { text: "Collections" }));
    head.appendChild(el("button", { type: "button", class: "primary", "data-nav": "1", "data-new-collection": "1", text: "New collection" }));
    main.appendChild(head);
    if (!state.collections.length) {
      main.appendChild(el("div", { class: "empty", text: "Name a list, then add games from their page." }));
      return;
    }
    var grid = el("div", { class: "platforms" });
    state.collections.forEach(function (collection) {
      var tile = el("button", { type: "button", class: "tile", "data-nav": "1", "data-collection": collection.name });
      tile.appendChild(el("b", { text: collection.name }));
      tile.appendChild(el("span", { text: (collection.keys || []).length + " games" }));
      grid.appendChild(tile);
    });
    main.appendChild(grid);
  }

  function renderMore(main) {
    clearMain(main);
    main.appendChild(el("div", { class: "page-head" }, [el("h1", { text: "More" })]));
    var stack = el("div", { class: "stack" });
    [["recent", "Recently Played"], ["collections", "Collections"], ["settings", "Settings"]].forEach(function (pair) {
      stack.appendChild(el("button", { type: "button", class: "text-btn", "data-nav": "1", "data-go": pair[0], text: pair[1] }));
    });
    if (state.isAdmin) {
      stack.appendChild(el("button", { type: "button", class: "text-btn", "data-nav": "1", "data-go": "duplicates", text: "Duplicates" }));
      stack.appendChild(el("button", { type: "button", class: "text-btn", "data-nav": "1", "data-go": "trash", text: "Trash" }));
    }
    stack.appendChild(el("p", { class: "tiny", text: "Total games " + (state.totals.games || 0) }));
    main.appendChild(stack);
  }

  function renderSettings(main) {
    clearMain(main);
    main.appendChild(el("div", { class: "page-head" }, [el("h1", { text: "Settings" })]));
    var kv = el("div", { class: "kv" });
    function line(label, value) {
      kv.appendChild(el("span", { text: label }));
      kv.appendChild(el("b", { text: value }));
    }
    line("Status", state.indexing ? "Library is being indexed" : "Ready");
    line("Games", String(state.totals.games || 0));
    line("ROMs", String(state.totals.roms || 0));
    line("Size", fmt.formatBytes(state.totals.bytes || 0));
    if (state.status && typeof state.status === "object") {
      Object.keys(state.status).slice(0, 8).forEach(function (key) {
        var value = state.status[key];
        if (value == null || typeof value === "object") return;
        line(key, String(value));
      });
    }
    main.appendChild(kv);
    if (!state.isAdmin) return;
    var actions = el("div", { class: "actions" });
    actions.appendChild(el("button", { type: "button", class: "primary", "data-nav": "1", "data-rescan": "1", text: "Rescan" }));
    actions.appendChild(el("button", { type: "button", class: "text-btn", "data-nav": "1", "data-go": "duplicates", text: "Duplicates" }));
    actions.appendChild(el("button", { type: "button", class: "text-btn", "data-nav": "1", "data-go": "trash", text: "Trash" }));
    main.appendChild(actions);
    main.appendChild(el("p", { class: "tiny", text: "Rescan asks Channel Manager to rebuild the index. Trash only moves files." }));
  }

  async function renderGame(main) {
    var key = state.route.key;
    clearMain(main);
    main.appendChild(el("p", { class: "muted", text: "Loading…" }));
    var token = state.token;
    try {
      var result = await api("game", { key: key });
      if (token !== state.token) return;
      state.current = result.game;
      state.indexing = !!result.indexing;
      drawGame(main, result.game);
    } catch (error) {
      showPageError(main, token, error.message || "Game not found");
    }
  }

  function drawGame(main, game) {
    clearMain(main);
    var crumbs = el("div", { class: "crumbs" });
    crumbs.appendChild(el("button", { type: "button", "data-nav": "1", "data-go": "library", text: "Library" }));
    crumbs.appendChild(document.createTextNode("›"));
    crumbs.appendChild(el("button", { type: "button", "data-nav": "1", "data-system": game.system || "", text: game.system || "Platform" }));
    crumbs.appendChild(document.createTextNode("›"));
    crumbs.appendChild(el("span", { text: game.title || "" }));
    main.appendChild(crumbs);

    var hero = el("section", { class: "hero" });
    var bg = el("div", { class: "hero-bg" });
    if (game.cover && game.coverPath) {
      bg.setAttribute("data-cover", "1");
      bg.setAttribute("data-p", game.coverPath);
      bg.setAttribute("data-w", "400");
      if (game.lb) bg.setAttribute("data-lb", game.lb);
    }
    var bgImg = el("img", { alt: "" });
    bgImg.hidden = true;
    bg.appendChild(bgImg);
    bg.appendChild(el("span", { class: "ph", text: game.system || "ROM" }));
    hero.appendChild(bg);
    hero.appendChild(el("div", { class: "hero-shade" }));
    var body = el("div", { class: "hero-body" });
    var thumb = el("div", { class: "hero-thumb" });
    if (game.cover && game.coverPath) {
      thumb.setAttribute("data-cover", "1");
      thumb.setAttribute("data-p", game.coverPath);
      thumb.setAttribute("data-w", "400");
      if (game.lb) thumb.setAttribute("data-lb", game.lb);
    }
    thumb.appendChild(el("span", { class: "ph", text: game.system || "ROM" }));
    var cover = el("img", { class: "hero-cover", alt: "" });
    cover.hidden = true;
    if (!(game.cover && game.coverPath)) cover.alt = game.system || "";
    thumb.appendChild(cover);
    body.appendChild(thumb);
    var copy = el("div", { class: "hero-copy" });
    copy.appendChild(el("h1", { text: game.title || "Untitled" }));
    copy.appendChild(el("p", { text: fmt.joinParts([game.system, game.genre, game.year, game.developer]) }));
    if (game.rating) {
      var filled = Math.max(0, Math.min(5, Math.round(Number(game.rating))));
      var glyphs = "";
      for (var starIndex = 0; starIndex < 5; starIndex++) glyphs += starIndex < filled ? "★" : "☆";
      if (game.ratingCount) glyphs += " " + game.ratingCount;
      copy.appendChild(el("p", { class: "rating", text: glyphs }));
    }
    if (game.description) copy.appendChild(el("p", { text: game.description }));
    var actions = el("div", { class: "actions" });
    actions.appendChild(el("button", { type: "button", class: "primary", "data-nav": "1", "data-play": "hero", text: "Play" }));
    actions.appendChild(el("button", { type: "button", class: "text-btn", "data-nav": "1", "data-fav": game.key, text: isFav(game.key) ? "Favorited" : "Favorite" }));
    actions.appendChild(el("button", { type: "button", class: "text-btn", "data-nav": "1", "data-collect": game.key, text: "Add to Collection" }));
    actions.appendChild(el("button", { type: "button", class: "text-btn", "data-nav": "1", "data-overflow": "1", text: "More" }));
    copy.appendChild(actions);
    body.appendChild(copy);
    hero.appendChild(body);
    main.appendChild(hero);

    var layout = el("div", { class: "detail" });
    var tabs = el("div", { class: "tabs", role: "tablist" });
    [
      ["versions", "Versions"],
      ["details", "Details"],
      ["screenshots", "Screenshots"],
      ["videos", "Videos"],
      ["achievements", "Achievements"],
      ["related", "Related Games"]
    ].forEach(function (pair) {
      tabs.appendChild(el("button", {
        type: "button",
        class: "text-btn" + (state.tab === pair[0] ? " active" : ""),
        "data-nav": "1",
        "data-tab": pair[0],
        text: pair[1]
      }));
    });
    layout.appendChild(tabs);
    layout.appendChild(gamePanel(game));
    main.appendChild(layout);
    watchCovers(main);
  }

  function gamePanel(game) {
    if (state.tab === "screenshots" || state.tab === "videos" || state.tab === "achievements") {
      return el("div", { class: "empty", text: "Coming later" });
    }
    if (state.tab === "details") {
      var kv = el("div", { class: "kv" });
      function line(label, value) {
        if (value == null || value === "") return;
        kv.appendChild(el("span", { text: label }));
        kv.appendChild(el("b", { text: String(value) }));
      }
      line("Developer", game.developer);
      line("Publisher", game.publisher);
      line("Genre", game.genre);
      line("Year", game.year);
      line("ESRB", game.esrb);
      line("Players", game.maxPlayers);
      line("LaunchBox", game.lbUrl);
      line("Versions", game.versionsCount);
      line("Size", fmt.formatBytes(game.sizeBytes));
      return kv;
    }
    if (state.tab === "related") {
      var grid = el("div", { class: "grid" });
      (game.related || []).forEach(function (item) { grid.appendChild(cardEl(item)); });
      if (!(game.related || []).length) grid.appendChild(el("div", { class: "empty", text: "No related games in this library." }));
      return grid;
    }
    var wrap = el("div", { class: "versions" });
    var versions = game.versions || [];
    wrap.appendChild(el("h2", { text: "Available Versions (" + versions.length + ")" }));
    versions.forEach(function (version, index) {
      var card = el("article", { class: "version" });
      var banner = el("div", { class: "version-banner" });
      if (version.cover && version.p) {
        banner.setAttribute("data-cover", "1");
        banner.setAttribute("data-p", version.p);
        banner.setAttribute("data-w", "200");
        if (game.lb) banner.setAttribute("data-lb", game.lb);
      }
      var bannerImg = el("img", { alt: "" });
      bannerImg.hidden = true;
      banner.appendChild(bannerImg);
      banner.appendChild(el("span", { class: "ph", text: game.system || "ROM" }));
      card.appendChild(banner);
      var info = el("div");
      var heading = el("h3", { text: fmt.versionName(version.file) || version.file || "ROM" });
      if (version.best) heading.appendChild(el("span", { class: "pill", text: "Best Experience" }));
      if (fmt.isCheatRom(version)) heading.appendChild(cheatPill());
      info.appendChild(heading);
      info.appendChild(el("p", { class: "tiny", text: fmt.joinParts([version.region, (version.ext || "").toUpperCase(), fmt.formatBytes(version.sizeBytes)]) }));
      var why = version.playable === false ? (version.reason || "Unavailable") : "";
      var play = el("button", {
        type: "button",
        class: version.best && !why ? "primary" : "text-btn",
        "data-nav": "1",
        "data-play": String(index),
        text: why || "Play",
        title: why || "Play"
      });
      info.appendChild(play);
      card.appendChild(info);
      wrap.appendChild(card);
    });
    return wrap;
  }

  async function renderDuplicates(main) {
    clearMain(main);
    if (!state.isAdmin) {
      main.appendChild(el("div", { class: "banner", text: "admin only" }));
      return;
    }
    main.appendChild(el("div", { class: "page-head" }, [el("h1", { text: "Identical copies" })]));
    main.appendChild(el("p", { class: "muted", text: "Loading copies…" }));
    var token = state.token;
    try {
      var result = await api("duplicates", { offset: 0, limit: 40 });
      if (token !== state.token) return;
      clearMain(main);
      main.appendChild(el("div", { class: "page-head" }, [el("h1", { text: "Identical copies" })]));
      main.appendChild(el("p", { class: "tiny", text: (result.total || 0) + " identical groups · " + (result.versionGroups || 0) + " version groups. Nothing is selected. Best Experience is only a label." }));
      (result.groups || []).forEach(function (group) {
        var box = el("section", { class: "group" });
        box.appendChild(el("h2", { text: (group.system || "Mixed") + " · " + (group.members || []).length + " copies" }));
        (group.members || []).forEach(function (member) {
          var label = el("label", { class: "check" });
          var input = el("input", { type: "checkbox", "data-dup": member.p, "data-nav": "1" });
          input.setAttribute("data-title", member.title || member.p);
          input.setAttribute("data-row-system", member.system || group.system || "");
          input.setAttribute("data-bytes", String(member.sizeBytes || 0));
          label.appendChild(input);
          var text = (member.title || member.file || member.p) + " · " + fmt.formatBytes(member.sizeBytes);
          var span = el("span", { text: text });
          if (member.ks) span.appendChild(el("span", { class: "pill", text: "Best Experience" }));
          if (fmt.isCheatRom(member)) span.appendChild(cheatPill());
          label.appendChild(span);
          box.appendChild(label);
        });
        main.appendChild(box);
      });
      if (!(result.groups || []).length) main.appendChild(el("div", { class: "empty", text: "No identical copies." }));
      main.appendChild(el("button", { type: "button", class: "danger", "data-nav": "1", "data-trash-selected": "1", text: "Move selected to trash" }));
    } catch (error) {
      showPageError(main, token, error.message);
    }
  }

  async function renderTrash(main) {
    clearMain(main);
    if (!state.isAdmin) {
      main.appendChild(el("div", { class: "banner", text: "admin only" }));
      return;
    }
    main.appendChild(el("div", { class: "page-head" }, [el("h1", { text: "Trash" })]));
    main.appendChild(el("p", { class: "muted", text: "Loading…" }));
    var token = state.token;
    try {
      var result = await api("trash-list", {});
      if (token !== state.token) return;
      clearMain(main);
      main.appendChild(el("div", { class: "page-head" }, [el("h1", { text: "Trash" })]));
      main.appendChild(el("p", { class: "tiny", text: fmt.formatBytes(result.bytes || 0) + " in trash. Restore puts a ROM back. Nothing here deletes it." }));
      if (!(result.items || []).length) {
        main.appendChild(el("div", { class: "empty", text: "Trash is empty." }));
        return;
      }
      (result.items || []).forEach(function (item) {
        var card = el("article", { class: "group" });
        card.appendChild(el("h3", { text: item.title || item.p }));
        card.appendChild(el("p", { class: "tiny", text: fmt.joinParts([item.system, fmt.formatBytes(item.sizeBytes), item.conflict ? "Name conflict" : ""]) }));
        card.appendChild(el("button", {
          type: "button",
          class: "text-btn",
          "data-nav": "1",
          "data-restore": item.p,
          "data-title": item.title || item.p,
          "data-row-system": item.system || "",
          "data-bytes": String(item.sizeBytes || 0),
          text: "Restore"
        }));
        main.appendChild(card);
      });
    } catch (error) {
      showPageError(main, token, error.message);
    }
  }

  function render() {
    state.token++;
    state.coverQueue = [];
    if (state.observer) {
      state.observer.disconnect();
      state.observer = null;
    }
    if (state.artObserver) {
      state.artObserver.disconnect();
      state.artObserver = null;
    }
    updateNav();
    var main = document.getElementById("main");
    var name = state.route.name;
    if (name === "home") return renderHome(main);
    if (name === "library") return renderLibrary(main);
    if (name === "favorites") return renderKeyed(main, "Favorites", state.favorites, "Star a game to keep it here.");
    if (name === "recent") return renderKeyed(main, "Recently Played", state.recent, "Games you play will show up here.");
    if (name === "collections") return renderCollections(main);
    if (name === "collection") return renderKeyed(main, state.route.id || "Collection", keysFor(state.route.id), "This collection is empty.");
    if (name === "platforms") return renderPlatforms(main);
    if (name === "achievements") return renderAchievements(main);
    if (name === "settings") return renderSettings(main);
    if (name === "game") return renderGame(main);
    if (name === "duplicates") return renderDuplicates(main);
    if (name === "trash") return renderTrash(main);
    if (name === "more") return renderMore(main);
    return renderHome(main);
  }

  function keysFor(name) {
    var found = state.collections.filter(function (collection) { return collection.name === name; })[0];
    return found ? (found.keys || []) : [];
  }

  function watchCovers(root) {
    if (state.observer) state.observer.disconnect();
    var nodes = root.querySelectorAll("[data-cover]");
    if (typeof IntersectionObserver !== "function") {
      nodes.forEach(function (node) { state.coverQueue.push(node); });
      scheduleCovers();
      return;
    }
    state.observer = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        state.observer.unobserve(entry.target);
        state.coverQueue.push(entry.target);
      });
      scheduleCovers();
    }, { rootMargin: "180px" });
    nodes.forEach(function (node) { state.observer.observe(node); });
  }

  function scheduleCovers() {
    clearTimeout(state.coverTimer);
    state.coverTimer = setTimeout(flushCovers, 40);
  }

  async function flushCovers() {
    if (state.coverBusy) return;
    var batch = state.coverQueue.splice(0, 4);
    if (!batch.length) return;
    var token = state.token;
    state.coverBusy = true;
    try {
      var items = [];
      batch.forEach(function (node) {
        var path = node.getAttribute("data-p");
        if (!path) return;
        items.push({ p: path, lb: node.getAttribute("data-lb") || "", w: Number(node.getAttribute("data-w")) || 200 });
      });
      if (items.length) {
        var result = await api("covers", { items: items });
        if (token !== state.token) return;
        applyCover((result && result.covers) || {});
      }
    } catch (e) {
      /* A missing cover stays on the platform placeholder. */
    } finally {
      state.coverBusy = false;
      if (state.coverQueue.length) scheduleCovers();
    }
  }

  function applyCover(covers) {
    Object.keys(covers).forEach(function (path) {
      var uri = covers[path];
      if (!uri) return;
      document.querySelectorAll('[data-p="' + cssEscape(path) + '"]').forEach(function (node) {
        var img = node.querySelector("img");
        if (!img) return;
        img.onload = function () {
          img.hidden = false;
          node.classList.add("has-cover");
          var ph = node.querySelector(".ph");
          if (ph) ph.hidden = true;
        };
        img.onerror = function () {
          img.hidden = true;
          node.classList.remove("has-cover");
          var ph = node.querySelector(".ph");
          if (ph) ph.hidden = false;
        };
        img.hidden = true;
        node.classList.remove("has-cover");
        var pending = node.querySelector(".ph");
        if (pending) pending.hidden = false;
        img.src = uri;
      });
    });
  }

  function cssEscape(value) {
    if (window.CSS && CSS.escape) return CSS.escape(value);
    return String(value).replace(/"/g, "");
  }

  function warningText(warning) {
    if (typeof warning === "string") return warning;
    if (!warning || typeof warning !== "object") return "";
    return warning.message || warning.warning || warning.text || warning.path || JSON.stringify(warning);
  }

  function watchDialog(cancel, ok, onCancel) {
    function focusables() {
      var dialog = cancel.closest(".dialog") || document.body;
      var nodes = dialog.querySelectorAll("a[href], button, input, select, textarea");
      var list = [cancel];
      Array.prototype.forEach.call(nodes, function (node) {
        if (node === cancel || node === ok) return;
        if (node.disabled || node.hidden || node.getAttribute("aria-disabled") === "true") return;
        list.push(node);
      });
      if (ok && list.indexOf(ok) < 0) list.push(ok);
      return list;
    }
    function onKey(event) {
      if (event.key === "Escape") {
        event.preventDefault();
        onCancel();
        return;
      }
      var active = document.activeElement;
      var arrows = event.key === "ArrowRight" || event.key === "ArrowDown" || event.key === "ArrowLeft" || event.key === "ArrowUp";
      if (arrows && active && active.tagName === "INPUT") return;
      var step = 0;
      if (event.key === "Tab") step = event.shiftKey ? -1 : 1;
      else if (arrows) step = event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 : 1;
      if (!step) return;
      var items = focusables();
      var index = items.indexOf(active);
      if (index < 0) index = 0;
      event.preventDefault();
      event.stopPropagation();
      items[(index + step + items.length) % items.length].focus();
    }
    document.addEventListener("keydown", onKey, true);
    cancel.focus();
    return function () { document.removeEventListener("keydown", onKey, true); };
  }

  function confirmDialog(opts) {
    return new Promise(function (resolve) {
      var previous = document.activeElement;
      var back = el("div", { class: "modal-back" });
      var dialog = el("div", { class: "dialog", role: "dialog", "aria-modal": "true", "aria-label": opts.title });
      dialog.appendChild(el("h2", { text: opts.title }));
      if (opts.body) dialog.appendChild(el("p", { text: opts.body }));
      if (opts.rows && opts.rows.length) {
        var list = el("ul");
        var total = 0;
        opts.rows.forEach(function (row) {
          total += Number(row.sizeBytes) || 0;
          list.appendChild(el("li", { text: (row.title || row.p) + " · " + (row.system || "Unknown") + " · " + fmt.formatBytes(row.sizeBytes) }));
        });
        dialog.appendChild(list);
        dialog.appendChild(el("p", { text: "Total " + fmt.formatBytes(total) }));
      }
      if (opts.warnings && opts.warnings.length) {
        var warnings = el("ul");
        opts.warnings.forEach(function (warning) { warnings.appendChild(el("li", { text: warningText(warning) })); });
        dialog.appendChild(warnings);
      }
      var actions = el("div", { class: "dlg-actions" });
      var cancel = el("button", { type: "button", class: "text-btn", "data-nav": "1", text: "Cancel" });
      var ok = el("button", { type: "button", class: opts.danger ? "danger" : "primary", "data-nav": "1", text: opts.confirm || "Confirm" });
      actions.appendChild(cancel);
      actions.appendChild(ok);
      dialog.appendChild(actions);
      back.appendChild(dialog);
      document.getElementById("modal").replaceChildren(back);
      var unwatch = function () {};
      function close(value) {
        unwatch();
        document.getElementById("modal").replaceChildren();
        resolve(value);
        if (previous && previous.focus) previous.focus();
      }
      unwatch = watchDialog(cancel, ok, function () { close(false); });
      cancel.addEventListener("click", function () { close(false); });
      ok.addEventListener("click", function () { close(true); });
    });
  }

  async function moveToTrash(rows) {
    if (!rows.length) {
      showToast("Select at least one ROM.");
      return;
    }
    var ok = await confirmDialog({
      title: "Move to trash?",
      body: "These ROMs move to trash. They are not deleted.",
      rows: rows,
      confirm: "Move to trash",
      danger: true
    });
    if (!ok) return;
    var paths = rows.map(function (row) { return row.p; });
    var deps;
    try {
      deps = await apiRaw("deps", { paths: paths });
    } catch (error) {
      showToast(error.message || "Could not check dependencies.");
      return;
    }
    if (!deps || (deps.ok === false && deps.error !== "needs_ack")) {
      showToast((deps && deps.error) || "Could not check dependencies.");
      return;
    }
    var warnings = deps.warnings || [];
    var ack = false;
    if (warnings.length || deps.needsAck) {
      var again = await confirmDialog({
        title: "Other games need these ROMs",
        body: "Move them to trash anyway?",
        warnings: warnings,
        confirm: "Move to trash anyway",
        danger: true
      });
      if (!again) return;
      ack = true;
    }
    var result;
    try {
      result = await apiRaw("trash", { paths: paths, ackDeps: ack });
    } catch (error) {
      showToast(error.message || "Could not move to trash.");
      return;
    }
    if (result && result.needsAck && !ack) {
      var third = await confirmDialog({
        title: "Other games need these ROMs",
        body: "Move them to trash anyway?",
        warnings: result.warnings || [],
        confirm: "Move to trash anyway",
        danger: true
      });
      if (!third) return;
      try {
        result = await apiRaw("trash", { paths: paths, ackDeps: true });
      } catch (error) {
        showToast(error.message || "Could not move to trash.");
        return;
      }
    }
    if (!result || result.ok === false) {
      showToast((result && result.error) || "Could not move to trash.");
      return;
    }
    showToast("Moved to trash");
    go(state.route.name === "game" ? { name: "library", system: state.route.system } : state.route);
    loadSystems();
  }

  async function restoreOne(row) {
    var ok = await confirmDialog({
      title: "Restore from trash?",
      body: "This puts the ROM back in the library.",
      rows: [row],
      confirm: "Restore"
    });
    if (!ok) return;
    var result;
    try {
      result = await apiRaw("restore", { paths: [row.p] });
    } catch (error) {
      showToast(error.message || "Could not restore.");
      return;
    }
    if (!result || result.ok === false) {
      showToast((result && result.error) || "Could not restore.");
      return;
    }
    showToast("Restored");
    render();
    loadSystems();
  }

  function selectedDuplicates() {
    return Array.prototype.map.call(document.querySelectorAll("[data-dup]:checked"), function (input) {
      return {
        p: input.getAttribute("data-dup"),
        title: input.getAttribute("data-title"),
        system: input.getAttribute("data-row-system"),
        sizeBytes: Number(input.getAttribute("data-bytes")) || 0
      };
    });
  }

  function collectionDialog(key) {
    return new Promise(function (resolve) {
      var back = el("div", { class: "modal-back" });
      var dialog = el("div", { class: "dialog", role: "dialog", "aria-modal": "true", "aria-label": "Collections" });
      dialog.appendChild(el("h2", { text: "Add to Collection" }));
      state.collections.forEach(function (collection) {
        var has = (collection.keys || []).indexOf(key) >= 0;
        dialog.appendChild(el("button", {
          type: "button",
          class: "text-btn",
          "data-nav": "1",
          "data-toggle-collection": collection.name,
          text: (has ? "Remove from " : "Add to ") + collection.name
        }));
      });
      var field = el("input", { id: "new-collection", placeholder: "New collection name", "aria-label": "New collection name" });
      dialog.appendChild(field);
      var actions = el("div", { class: "dlg-actions" });
      var cancel = el("button", { type: "button", class: "text-btn", text: "Close" });
      var create = el("button", { type: "button", class: "primary", text: "Create" });
      actions.appendChild(cancel);
      actions.appendChild(create);
      dialog.appendChild(actions);
      back.appendChild(dialog);
      document.getElementById("modal").replaceChildren(back);
      var unwatch = function () {};
      function close() {
        unwatch();
        document.getElementById("modal").replaceChildren();
        resolve();
      }
      cancel.addEventListener("click", close);
      create.addEventListener("click", function () {
        var name = field.value.trim();
        if (!name) return;
        if (state.collections.some(function (collection) { return collection.name.toLowerCase() === name.toLowerCase(); })) {
          showToast("That collection already exists.");
          return;
        }
        state.collections.push({ name: name, keys: [key] });
        save("collections", state.collections);
        close();
        showToast("Added to " + name);
      });
      dialog.addEventListener("click", function (event) {
        var button = event.target.closest("[data-toggle-collection]");
        if (!button) return;
        var name = button.getAttribute("data-toggle-collection");
        state.collections.forEach(function (collection) {
          if (collection.name !== name) return;
          var keys = collection.keys || [];
          collection.keys = keys.indexOf(key) >= 0 ? keys.filter(function (item) { return item !== key; }) : keys.concat([key]);
        });
        save("collections", state.collections);
        close();
      });
      unwatch = watchDialog(cancel, create, close);
    });
  }

  function promptCollection() {
    var back = el("div", { class: "modal-back" });
    var dialog = el("div", { class: "dialog", role: "dialog", "aria-modal": "true" });
    dialog.appendChild(el("h2", { text: "New collection" }));
    var field = el("input", { placeholder: "Name", "aria-label": "Collection name" });
    dialog.appendChild(field);
    var actions = el("div", { class: "dlg-actions" });
    var cancel = el("button", { type: "button", class: "text-btn", text: "Cancel" });
    var ok = el("button", { type: "button", class: "primary", text: "Create" });
    actions.appendChild(cancel);
    actions.appendChild(ok);
    dialog.appendChild(actions);
    back.appendChild(dialog);
    document.getElementById("modal").replaceChildren(back);
    var unwatch = function () {};
    function close() {
      unwatch();
      document.getElementById("modal").replaceChildren();
    }
    cancel.addEventListener("click", close);
    ok.addEventListener("click", function () {
      var name = field.value.trim();
      if (!name) return;
      if (!state.collections.some(function (collection) { return collection.name.toLowerCase() === name.toLowerCase(); })) {
        state.collections.push({ name: name, keys: [] });
        save("collections", state.collections);
      }
      close();
      render();
    });
    unwatch = watchDialog(cancel, ok, close);
  }

  function filterDialog() {
    var back = el("div", { class: "modal-back" });
    var dialog = el("div", { class: "dialog", role: "dialog", "aria-modal": "true", "aria-label": "Filter" });
    dialog.appendChild(el("h2", { text: "Filter" }));
    var field = el("input", { value: state.query || "", placeholder: "Name, genre, or year", "aria-label": "Filter games" });
    dialog.appendChild(field);
    var actions = el("div", { class: "dlg-actions" });
    var cancel = el("button", { type: "button", class: "text-btn", text: "Cancel" });
    var clear = el("button", { type: "button", class: "text-btn", text: "Clear" });
    var apply = el("button", { type: "button", class: "primary", text: "Apply" });
    actions.appendChild(cancel);
    actions.appendChild(clear);
    actions.appendChild(apply);
    dialog.appendChild(actions);
    back.appendChild(dialog);
    document.getElementById("modal").replaceChildren(back);
    var unwatch = function () {};
    function close() {
      unwatch();
      document.getElementById("modal").replaceChildren();
    }
    function commit(value) {
      state.query = String(value || "").trim();
      var searchBox = document.getElementById("search");
      if (searchBox) searchBox.value = state.query;
      close();
      render();
    }
    cancel.addEventListener("click", close);
    clear.addEventListener("click", function () { commit(""); });
    apply.addEventListener("click", function () { commit(field.value); });
    unwatch = watchDialog(cancel, apply, close);
  }

  function moreMenu() {
    if (!state.current) return;
    var back = el("div", { class: "modal-back" });
    var dialog = el("div", { class: "dialog", role: "dialog", "aria-modal": "true", "aria-label": "More" });
    dialog.appendChild(el("h2", { text: "More" }));
    [["details", "Details"], ["related", "Related Games"]].forEach(function (pair) {
      dialog.appendChild(el("button", { type: "button", class: "text-btn", "data-nav": "1", "data-jump": pair[0], text: pair[1] }));
    });
    var cancel = el("button", { type: "button", class: "text-btn", text: "Close" });
    dialog.appendChild(el("div", { class: "dlg-actions" }, [cancel]));
    back.appendChild(dialog);
    document.getElementById("modal").replaceChildren(back);
    var unwatch = function () {};
    function close() {
      unwatch();
      document.getElementById("modal").replaceChildren();
    }
    cancel.addEventListener("click", close);
    dialog.addEventListener("click", function (event) {
      var jump = event.target.closest("[data-jump]");
      if (!jump) return;
      state.tab = jump.getAttribute("data-jump");
      close();
      drawGame(document.getElementById("main"), state.current);
    });
    unwatch = watchDialog(cancel, cancel, close);
  }

  function bestVersion(game) {
    var versions = (game && game.versions) || [];
    for (var i = 0; i < versions.length; i++) if (versions[i].best) return versions[i];
    return versions[0] || null;
  }

  document.getElementById("main").addEventListener("click", function (event) {
    var fav = event.target.closest("[data-fav]");
    if (fav) {
      event.stopPropagation();
      toggleFav(fav.getAttribute("data-fav"));
      return;
    }
    var open = event.target.closest("[data-open]");
    if (open) {
      go({ name: "game", key: open.getAttribute("data-open") });
      return;
    }
    var tab = event.target.closest("[data-tab]");
    if (tab && state.current) {
      state.tab = tab.getAttribute("data-tab");
      drawGame(document.getElementById("main"), state.current);
      return;
    }
    var play = event.target.closest("[data-play]");
    if (play) {
      var index = play.getAttribute("data-play");
      var version = index === "hero" ? bestVersion(state.current) : ((state.current && state.current.versions) || [])[Number(index)];
      playGame(state.current, version, play);
      return;
    }
    var restore = event.target.closest("[data-restore]");
    if (restore) {
      restoreOne({
        p: restore.getAttribute("data-restore"),
        title: restore.getAttribute("data-title"),
        system: restore.getAttribute("data-row-system"),
        sizeBytes: Number(restore.getAttribute("data-bytes")) || 0
      });
      return;
    }
    if (event.target.closest("[data-dup]") || event.target.closest("label.check")) return;
    var picked = event.target.closest("[data-console]");
    if (picked) {
      go({ name: "library", system: picked.getAttribute("data-console") || "" });
      return;
    }
    var nudge = event.target.closest("[data-console-nav]");
    if (nudge) {
      nudgeConsoles(nudge.getAttribute("data-console-nav"));
      return;
    }
    var view = event.target.closest("[data-lib-view]");
    if (view) {
      state.libraryView = view.getAttribute("data-lib-view") || "all";
      render();
      return;
    }
    if (event.target.closest("[data-filter]")) {
      filterDialog();
      return;
    }
    var system = event.target.closest("[data-system]");
    if (system) {
      go({ name: "library", system: system.getAttribute("data-system") || "" });
      return;
    }
    var more = event.target.closest("[data-more]");
    if (more) { loadMore().catch(function (error) { showToast(error.message); }); return; }
    var goBtn = event.target.closest("[data-go]");
    if (goBtn) { go({ name: goBtn.getAttribute("data-go") }); return; }
    var collection = event.target.closest("[data-collection]");
    if (collection) { go({ name: "collection", id: collection.getAttribute("data-collection") }); return; }
    if (event.target.closest("[data-new-collection]")) { promptCollection(); return; }
    if (event.target.closest("[data-overflow]")) { moreMenu(); return; }
    if (event.target.closest("[data-collect]")) { collectionDialog(event.target.closest("[data-collect]").getAttribute("data-collect")); return; }
    if (event.target.closest("[data-rescan]")) {
      api("rescan", {}).then(function () {
        showToast("Rescan started");
        state.indexing = true;
        loadSystems().then(render);
      }).catch(function (error) { showToast(error.message); });
      return;
    }
    if (event.target.closest("[data-trash-selected]")) { moveToTrash(selectedDuplicates()); return; }
  });

  document.getElementById("main").addEventListener("change", function (event) {
    if (event.target.matches("[data-sort]")) {
      state.sort = event.target.value;
      render();
    }
  });

  document.getElementById("main").addEventListener("keydown", function (event) {
    if (event.key !== "Enter") return;
    var open = event.target.closest("[data-open]");
    if (!open) return;
    event.preventDefault();
    go({ name: "game", key: open.getAttribute("data-open") });
  });

  document.getElementById("gear").addEventListener("click", function () { go({ name: "settings" }); });
  document.getElementById("sidebar").addEventListener("click", onNav);
  document.getElementById("switcher").addEventListener("click", onNav);
  function onNav(event) {
    var button = event.target.closest("[data-go]");
    if (!button) return;
    go({ name: button.getAttribute("data-go") });
  }

  var search = document.getElementById("search");
  var searchTimer = 0;
  search.addEventListener("input", function () {
    state.query = search.value.trim();
    clearTimeout(searchTimer);
    searchTimer = setTimeout(function () {
      if (state.route.name !== "library") go({ name: "library", system: state.consoleName || "" });
      else render();
    }, 250);
  });

  function moveFocus(dir) {
    if (document.querySelector(".dialog")) return;
    var nodes = Array.prototype.filter.call(document.querySelectorAll("[data-nav]"), function (node) {
      return node.getClientRects().length > 0;
    });
    var current = document.activeElement;
    if (!current || nodes.indexOf(current) < 0) {
      if (nodes[0]) nodes[0].focus();
      return;
    }
    var rect = current.getBoundingClientRect();
    var cx = rect.left + rect.width / 2;
    var cy = rect.top + rect.height / 2;
    var best = null;
    var bestScore = Infinity;
    nodes.forEach(function (node) {
      if (node === current) return;
      var box = node.getBoundingClientRect();
      var dx = box.left + box.width / 2 - cx;
      var dy = box.top + box.height / 2 - cy;
      if (dir === "left" && dx >= -8) return;
      if (dir === "right" && dx <= 8) return;
      if (dir === "up" && dy >= -8) return;
      if (dir === "down" && dy <= 8) return;
      var primary = dir === "left" || dir === "right" ? Math.abs(dx) : Math.abs(dy);
      var secondary = dir === "left" || dir === "right" ? Math.abs(dy) : Math.abs(dx);
      var score = primary + secondary * 1.4;
      if (score < bestScore) { bestScore = score; best = node; }
    });
    if (best) {
      best.focus();
      best.scrollIntoView({ block: "nearest", inline: "nearest" });
    }
  }

  document.addEventListener("keydown", function (event) {
    if (document.querySelector(".dialog")) return;
    var typing = event.target && (event.target.tagName === "INPUT" || event.target.tagName === "TEXTAREA" || event.target.tagName === "SELECT");
    var map = { ArrowLeft: "left", ArrowRight: "right", ArrowUp: "up", ArrowDown: "down" };
    if (!map[event.key] || typing) return;
    event.preventDefault();
    if (moveCarousel(map[event.key])) return;
    moveFocus(map[event.key]);
  });

  function buildNav() {
    var sidebar = document.getElementById("side-nav");
    var switcher = document.getElementById("switcher");
    NAV.forEach(function (item) {
      var button = el("button", { type: "button", class: "nav-btn", "data-nav": "1", "data-go": item[0] });
      button.appendChild(svg(item[2]));
      button.appendChild(document.createTextNode(item[1]));
      sidebar.appendChild(button);
      switcher.appendChild(el("button", { type: "button", class: "chip", "data-nav": "1", "data-go": item[0], text: item[1] }));
    });
  }

  function tickClock() {
    var node = document.getElementById("clock");
    if (!node) return;
    var now = new Date();
    var hours = String(now.getHours()).padStart(2, "0");
    var minutes = String(now.getMinutes()).padStart(2, "0");
    node.textContent = hours + ":" + minutes;
    node.setAttribute("datetime", now.toISOString());
  }

  function readHostApi(value) {
    if (typeof value === "number" && isFinite(value)) return value;
    if (typeof value === "string" && value.trim() !== "" && isFinite(Number(value))) return Number(value);
    if (value && typeof value === "object") {
      if (value.hostApi != null) return readHostApi(value.hostApi);
      if (value.version != null) return readHostApi(value.version);
    }
    return 1;
  }

  async function loadSystems() {
    var result = await api("systems", {});
    state.systems = result.systems || [];
    state.totals = result.totals || state.totals;
    state.indexing = !!result.indexing;
    state.isAdmin = !!result.isAdmin;
    state.status = result.status || null;
    updateNav();
    return result;
  }

  async function loadStore() {
    var values = await Promise.all(["favorites", "recent", "collections"].map(function (key) {
      return hostCall("dataGet", { key: key }).then(asObject).catch(function () { return null; });
    }));
    state.favorites = Array.isArray(values[0]) ? values[0] : [];
    state.recent = Array.isArray(values[1]) ? values[1].slice(0, 30) : [];
    state.collections = Array.isArray(values[2]) ? values[2].filter(function (item) { return item && item.name; }) : [];
  }

  function markEmbedded() {
    var framed = false;
    try { framed = window.self !== window.top; } catch (e) { framed = true; }
    if (framed) document.documentElement.classList.add("embedded");
  }

  async function boot() {
    markEmbedded();
    buildNav();
    tickClock();
    setInterval(tickClock, 30000);
    var hostApi = 1;
    try {
      hostApi = readHostApi(await hostCall("getHostApiVersion", {}));
    } catch (e) {
      hostApi = 1;
    }
    if (hostApi < 2) {
      hostCall("registerNavItem", {
        id: "rom-manager",
        title: "ROM Manager",
        url: "/Moonfin/Plugins/rom-manager/files/client/index.html"
      }).catch(function () {});
    }
    if (window.MoFinHost && typeof window.MoFinHost.onEvent === "function") {
      window.MoFinHost.onEvent(function (event) {
        if (event && event.type === "gameExited") render();
      });
    }
    hostCall("getUserContext", {}).catch(function () {});
    try {
      await Promise.all([loadStore(), loadSystems()]);
    } catch (error) {
      document.getElementById("main").appendChild(el("div", { class: "banner", text: error.message || "ROM Manager could not reach the library." }));
    }
    render();
  }

  boot();
})();
