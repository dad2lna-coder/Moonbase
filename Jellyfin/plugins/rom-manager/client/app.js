(function () {
  var fmt = window.RomFormat;
  var state = {
    route: fmt.parseRoute(location.hash),
    query: "",
    sort: "title",
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
    observer: null
  };
  var suppressHash = false;

  var NAV = [
    ["home", "Home", "M3 11l9-7 9 7v9a2 2 0 0 1-2 2h-4v-6H9v6H5a2 2 0 0 1-2-2z"],
    ["library", "Library", "M4 5h7v14H4zM13 5h7v14h-7z"],
    ["favorites", "Favorites", "M12 20s-7-4.4-7-9a4 4 0 0 1 7-2 4 4 0 0 1 7 2c0 4.6-7 9-7 9z"],
    ["recent", "Recently Viewed", "M12 7v6l4 2M12 21a9 9 0 1 0-9-9"],
    ["collections", "Collections", "M4 7h16v12H4zM8 7V5h8v2"],
    ["platforms", "Platforms", "M4 8h16v10H4zM8 8V6h8v2"],
    ["settings", "Settings", "M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8z"]
  ];
  var TABS = [
    ["home", "Home", NAV[0][2]],
    ["library", "Library", NAV[1][2]],
    ["favorites", "Favorites", NAV[2][2]],
    ["platforms", "Platforms", NAV[5][2]],
    ["more", "More", "M6 12h.01M12 12h.01M18 12h.01"]
  ];

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
    return window.MoFinHost.call(method, params || {});
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

  function playGame(game, version) {
    void game;
    void version;
    showToast("Playing from ROM Manager is coming soon");
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
    var hash = fmt.routeHash(state.route);
    if (location.hash !== hash) {
      suppressHash = true;
      location.hash = hash;
      suppressHash = false;
    }
    render();
  }

  function updateNav() {
    var name = state.route.name === "collection" ? "collections" : state.route.name;
    document.getElementById("sidebar").querySelectorAll("[data-go]").forEach(function (button) {
      button.classList.toggle("active", button.getAttribute("data-go") === name);
    });
    document.getElementById("tabbar").querySelectorAll("[data-go]").forEach(function (button) {
      button.classList.toggle("active", button.getAttribute("data-go") === name);
    });
    var total = document.getElementById("total-games");
    if (total) total.textContent = String(state.totals.games || 0);
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

  function cardEl(item) {
    var card = el("div", { class: "card", tabindex: "0", "data-nav": "1", "data-open": item.key || "" });
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
    art.appendChild(el("span", { class: "badge", text: item.system || "" }));
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
    meta.appendChild(el("div", { class: "tiny", text: item.system || "" }));
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
    main.appendChild(el("div", { class: "page-head" }, [el("h1", { text: "Home" })]));
    main.appendChild(el("p", { class: "muted", text: "Loading rows…" }));
    var token = ++state.token;
    try {
      var jobs = [api("games", { sort: "added", offset: 0, limit: 18 })];
      jobs.push(state.recent.length ? api("games", { keys: state.recent, limit: 30 }) : Promise.resolve({ items: [] }));
      jobs.push(state.favorites.length ? api("games", { keys: state.favorites, limit: 30 }) : Promise.resolve({ items: [] }));
      var results = await Promise.all(jobs);
      if (token !== state.token) return;
      state.indexing = !!(results[0] && results[0].indexing);
      clearMain(main);
      main.appendChild(el("div", { class: "page-head" }, [el("h1", { text: "Home" })]));
      main.appendChild(row("Recently Viewed", (results[1] && results[1].items) || [], "Games you open will show up here."));
      main.appendChild(row("Recently Added", (results[0] && results[0].items) || [], "Nothing has been indexed yet."));
      main.appendChild(row("Favorites", (results[2] && results[2].items) || [], "Star a game to keep it here."));
      watchCovers(main);
    } catch (error) {
      if (token !== state.token) return;
      main.replaceChildren(el("div", { class: "banner", text: error.message || "Could not load the library." }));
    }
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
    if (!keys.length) {
      paintList(main, title, [], empty);
      return;
    }
    main.appendChild(el("p", { class: "muted", text: "Loading…" }));
    var token = ++state.token;
    try {
      var result = await api("games", { keys: keys, limit: 60 });
      if (token !== state.token) return;
      state.indexing = !!result.indexing;
      paintList(main, title, result.items || [], empty);
    } catch (error) {
      if (token !== state.token) return;
      main.replaceChildren(el("div", { class: "banner", text: error.message }));
    }
  }

  async function renderLibrary(main) {
    var system = state.route.system || "";
    main.appendChild(el("div", { class: "page-head" }, [
      el("h1", { text: system || "Library" }),
      el("span", { class: "tiny", text: "Loading…" })
    ]));
    var token = ++state.token;
    try {
      var result = await api("games", {
        system: system,
        q: state.query || "",
        sort: state.sort,
        offset: 0,
        limit: 48
      });
      if (token !== state.token) return;
      state.list = result.items || [];
      state.total = result.total || 0;
      state.indexing = !!result.indexing;
      drawLibrary(main);
    } catch (error) {
      if (token !== state.token) return;
      main.replaceChildren(el("div", { class: "banner", text: error.message }));
    }
  }

  function drawLibrary(main) {
    clearMain(main);
    var head = el("div", { class: "page-head" });
    head.appendChild(el("h1", { text: state.route.system || "Library" }));
    head.appendChild(el("span", { class: "tiny", text: state.total + " games" }));
    main.appendChild(head);
    var filters = el("div", { class: "filters" });
    filters.appendChild(chip("All", "", !state.route.system));
    state.systems.forEach(function (row) {
      filters.appendChild(chip(row.system, row.system, state.route.system === row.system));
    });
    var sort = el("select", { class: "sort", "data-sort": "1", "aria-label": "Sort" });
    [["title", "Title"], ["added", "Recently added"], ["year", "Year"]].forEach(function (pair) {
      var option = el("option", { value: pair[0], text: pair[1] });
      if (pair[0] === state.sort) option.setAttribute("selected", "selected");
      sort.appendChild(option);
    });
    filters.appendChild(sort);
    main.appendChild(filters);
    if (!state.list.length) {
      main.appendChild(el("div", { class: "empty", text: state.query ? "No games match that search." : "No games in this library yet." }));
      return;
    }
    var grid = el("div", { class: "grid" });
    state.list.forEach(function (item) { grid.appendChild(cardEl(item)); });
    main.appendChild(grid);
    if (state.list.length < state.total) {
      main.appendChild(el("button", { type: "button", class: "text-btn", "data-nav": "1", "data-more": "1", text: "Load more" }));
    }
    watchCovers(main);
  }

  function chip(label, system, active) {
    return el("button", {
      type: "button",
      class: "chip" + (active ? " active" : ""),
      "data-nav": "1",
      "data-system": system,
      text: label
    });
  }

  async function loadMore() {
    var token = state.token;
    var result = await api("games", {
      system: state.route.system || "",
      q: state.query || "",
      sort: state.sort,
      offset: state.list.length,
      limit: 48
    });
    if (token !== state.token) return;
    state.list = state.list.concat(result.items || []);
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

  function renderCollections(main) {
    main.replaceChildren();
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
    main.replaceChildren();
    main.appendChild(el("div", { class: "page-head" }, [el("h1", { text: "More" })]));
    var stack = el("div", { class: "stack" });
    [["recent", "Recently Viewed"], ["collections", "Collections"], ["settings", "Settings"]].forEach(function (pair) {
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
    main.appendChild(el("p", { class: "muted", text: "Loading…" }));
    var token = ++state.token;
    try {
      var result = await api("game", { key: key });
      if (token !== state.token) return;
      state.current = result.game;
      state.indexing = !!result.indexing;
      remember(key);
      drawGame(main, result.game);
    } catch (error) {
      if (token !== state.token) return;
      main.replaceChildren(el("div", { class: "banner", text: error.message || "Game not found" }));
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
    hero.appendChild(bg);
    hero.appendChild(el("div", { class: "hero-shade" }));
    var body = el("div", { class: "hero-body" });
    var cover = el("img", { class: "hero-cover", alt: "" });
    if (game.cover && game.coverPath) {
      cover.setAttribute("data-cover-img", game.coverPath);
    } else {
      cover.alt = game.system || "";
    }
    body.appendChild(cover);
    var copy = el("div", { class: "hero-copy" });
    copy.appendChild(el("h1", { text: game.title || "Untitled" }));
    copy.appendChild(el("p", { text: fmt.joinParts([game.system, game.genre, game.year, game.publisher]) }));
    if (game.rating) {
      var rating = "★ " + game.rating;
      if (game.ratingCount) rating += " (" + game.ratingCount + ")";
      copy.appendChild(el("p", { class: "rating", text: rating }));
    }
    if (game.description) copy.appendChild(el("p", { text: game.description }));
    var actions = el("div", { class: "actions" });
    actions.appendChild(el("button", { type: "button", class: "primary", "data-nav": "1", "data-play": "hero", "aria-disabled": "true", text: "Play", title: "Coming soon" }));
    actions.appendChild(el("button", { type: "button", class: "text-btn", "data-nav": "1", "data-fav": game.key, text: isFav(game.key) ? "Favorited" : "Favorite" }));
    actions.appendChild(el("button", { type: "button", class: "text-btn", "data-nav": "1", "data-collect": game.key, text: "Add to Collection" }));
    copy.appendChild(actions);
    body.appendChild(copy);
    hero.appendChild(body);
    main.appendChild(hero);

    var layout = el("div", { class: "detail" });
    var tabs = el("div", { class: "tabs", role: "tablist" });
    ["versions", "details", "related"].forEach(function (name) {
      var label = name.charAt(0).toUpperCase() + name.slice(1);
      tabs.appendChild(el("button", {
        type: "button",
        class: "text-btn" + (state.tab === name ? " active" : ""),
        "data-nav": "1",
        "data-tab": name,
        text: label
      }));
    });
    layout.appendChild(tabs);
    layout.appendChild(gamePanel(game));
    main.appendChild(layout);
    watchCovers(main);
  }

  function gamePanel(game) {
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
      banner.appendChild(el("span", { class: "ph", text: game.system || "ROM" }));
      card.appendChild(banner);
      var info = el("div");
      var heading = el("h3", { text: fmt.versionName(version.file) || version.file || "ROM" });
      if (version.best) heading.appendChild(el("span", { class: "pill", text: "Best Experience" }));
      info.appendChild(heading);
      info.appendChild(el("p", { class: "tiny", text: fmt.joinParts([game.system, version.region, version.revision, (version.ext || "").toUpperCase(), fmt.formatBytes(version.sizeBytes)]) }));
      var play = el("button", { type: "button", class: "primary", "data-nav": "1", "data-play": String(index), "aria-disabled": "true", text: "Play", title: "Coming soon" });
      info.appendChild(play);
      card.appendChild(info);
      wrap.appendChild(card);
    });
    return wrap;
  }

  async function renderDuplicates(main) {
    if (!state.isAdmin) {
      main.replaceChildren(el("div", { class: "banner", text: "admin only" }));
      return;
    }
    main.appendChild(el("p", { class: "muted", text: "Loading copies…" }));
    try {
      var result = await api("duplicates", { offset: 0, limit: 40 });
      main.replaceChildren();
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
          label.appendChild(span);
          box.appendChild(label);
        });
        main.appendChild(box);
      });
      if (!(result.groups || []).length) main.appendChild(el("div", { class: "empty", text: "No identical copies." }));
      main.appendChild(el("button", { type: "button", class: "danger", "data-nav": "1", "data-trash-selected": "1", text: "Move selected to trash" }));
    } catch (error) {
      main.replaceChildren(el("div", { class: "banner", text: error.message }));
    }
  }

  async function renderTrash(main) {
    if (!state.isAdmin) {
      main.replaceChildren(el("div", { class: "banner", text: "admin only" }));
      return;
    }
    try {
      var result = await api("trash-list", {});
      main.replaceChildren();
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
      main.replaceChildren(el("div", { class: "banner", text: error.message }));
    }
  }

  function render() {
    updateNav();
    var main = document.getElementById("main");
    var name = state.route.name;
    if (name === "home") return renderHome(main);
    if (name === "library") return renderLibrary(main);
    if (name === "favorites") return renderKeyed(main, "Favorites", state.favorites, "Star a game to keep it here.");
    if (name === "recent") return renderKeyed(main, "Recently Viewed", state.recent, "Games you open will show up here.");
    if (name === "collections") return renderCollections(main);
    if (name === "collection") return renderKeyed(main, state.route.id || "Collection", keysFor(state.route.id), "This collection is empty.");
    if (name === "platforms") return renderPlatforms(main);
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
        var covers = (result && result.covers) || {};
        applyCover(covers);
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
      document.querySelectorAll('[data-p="' + cssEscape(path) + '"]').forEach(function (node) {
        var img = node.querySelector("img");
        if (img) {
          img.src = uri;
          img.hidden = false;
        }
        var ph = node.querySelector(".ph");
        if (ph) ph.hidden = true;
        if (node.classList.contains("hero-bg") || node.classList.contains("version-banner")) {
          node.style.backgroundImage = 'url("' + uri + '")';
        }
      });
      document.querySelectorAll("[data-cover-img]").forEach(function (img) {
        if (img.getAttribute("data-cover-img") === path) img.src = uri;
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
    function onKey(event) {
      if (event.key === "Escape") {
        event.preventDefault();
        onCancel();
        return;
      }
      var step = 0;
      if (event.key === "Tab") step = event.shiftKey ? -1 : 1;
      else if (event.key === "ArrowRight" || event.key === "ArrowDown") step = 1;
      else if (event.key === "ArrowLeft" || event.key === "ArrowUp") step = -1;
      if (!step) return;
      var items = [cancel, ok];
      var index = items.indexOf(document.activeElement);
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
    state.token++;
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
      playGame(state.current, version);
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
  document.getElementById("tabbar").addEventListener("click", onNav);
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
      if (state.route.name !== "library" || state.route.system) go({ name: "library" });
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
    moveFocus(map[event.key]);
  });

  window.addEventListener("hashchange", function () {
    if (suppressHash) return;
    state.route = fmt.parseRoute(location.hash);
    render();
  });

  function buildNav() {
    var sidebar = document.getElementById("sidebar");
    NAV.forEach(function (item) {
      var button = el("button", { type: "button", class: "nav-btn", "data-nav": "1", "data-go": item[0] });
      button.appendChild(svg(item[2]));
      button.appendChild(document.createTextNode(item[1]));
      sidebar.appendChild(button);
    });
    var total = el("div", { class: "total" });
    total.appendChild(document.createTextNode("Total Games"));
    total.appendChild(el("b", { id: "total-games", text: "0" }));
    sidebar.appendChild(total);
    var tabs = document.getElementById("tabbar");
    TABS.forEach(function (item) {
      var button = el("button", { type: "button", class: "tab", "data-nav": "1", "data-go": item[0] });
      button.appendChild(svg(item[2]));
      button.appendChild(el("span", { text: item[1] }));
      tabs.appendChild(button);
    });
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

  async function boot() {
    buildNav();
    hostCall("registerNavItem", {
      id: "rom-manager",
      title: "ROM Manager",
      url: "/Moonfin/Plugins/rom-manager/files/client/index.html"
    }).catch(function () {});
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
