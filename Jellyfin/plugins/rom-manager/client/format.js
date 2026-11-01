(function (root, factory) {
  var api = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  } else {
    root.RomFormat = api;
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  var units = ["B", "KB", "MB", "GB", "TB"];

  function formatBytes(value) {
    var n = Number(value);
    if (!isFinite(n) || n <= 0) return "0 B";
    var exp = Math.min(Math.floor(Math.log(n) / Math.log(1024)), units.length - 1);
    var scaled = n / Math.pow(1024, exp);
    var text = exp === 0 || scaled >= 10 ? String(Math.round(scaled)) : String(Math.round(scaled * 10) / 10);
    return text + " " + units[exp];
  }

  function joinParts(parts) {
    var out = [];
    for (var i = 0; i < (parts || []).length; i++) {
      var part = parts[i];
      if (part === 0) part = "0";
      if (part == null) continue;
      part = String(part).trim();
      if (part) out.push(part);
    }
    return out.join(" • ");
  }

  function versionName(file) {
    var name = String(file || "");
    var slash = Math.max(name.lastIndexOf("/"), name.lastIndexOf("\\"));
    if (slash >= 0) name = name.slice(slash + 1);
    var dot = name.lastIndexOf(".");
    return dot > 0 ? name.slice(0, dot) : name;
  }

  // Cheat / trainer ROMs: a tag naming "cheat" or "trainer" in any case, or a
  // GoodTools trainer tag in its lowercase form ([t], [t1], [t+3], [t1+2C]).
  // Capital [T+Eng] / [T-Fre] are GoodTools translations, not cheats.
  var TRAINER_TAG = /^\[t\d*(\+[^\]]*)?\]$/;

  function isCheatTag(tag) {
    var text = String(tag == null ? "" : tag).trim();
    if (!text) return false;
    if (/cheat|trainer/i.test(text)) return true;
    return TRAINER_TAG.test(text);
  }

  function fileTags(file) {
    return versionName(file).match(/\([^()]*\)|\[[^\[\]]*\]/g) || [];
  }

  function isCheatRom(entry) {
    if (!entry) return false;
    if (entry.cheat === true) return true;
    var tags = Array.isArray(entry.tags) ? entry.tags : Array.isArray(entry.tg) ? entry.tg : [];
    for (var i = 0; i < tags.length; i++) if (isCheatTag(tags[i])) return true;
    var tagged = fileTags(entry.file || entry.romFile || "");
    for (var j = 0; j < tagged.length; j++) if (isCheatTag(tagged[j])) return true;
    return false;
  }

  function safeDecode(value) {
    try {
      return decodeURIComponent(value || "");
    } catch (e) {
      return value || "";
    }
  }

  function parseRoute(hash) {
    var text = String(hash || "");
    if (text.charAt(0) === "#") text = text.slice(1);
    if (text.charAt(0) === "/") text = text.slice(1);
    if (!text || text === "home") return { name: "home" };
    var cut = text.indexOf("/");
    var head = cut === -1 ? text : text.slice(0, cut);
    var rest = cut === -1 ? "" : text.slice(cut + 1);
    if (head === "game") return { name: "game", key: safeDecode(rest) };
    if (head === "collection") return { name: "collection", id: safeDecode(rest) };
    if (head === "platform") return { name: "library", system: safeDecode(rest) };
    if (head === "library" || head === "favorites" || head === "recent" || head === "collections" || head === "platforms" || head === "achievements" || head === "settings" || head === "duplicates" || head === "trash" || head === "more") {
      return { name: head };
    }
    return { name: "home" };
  }

  function routeHash(route) {
    var name = (route && route.name) || "home";
    if (name === "home") return "#/home";
    if (name === "game") return "#/game/" + encodeURIComponent(route.key || "");
    if (name === "collection") return "#/collection/" + encodeURIComponent(route.id || "");
    if (name === "library" && route.system) return "#/platform/" + encodeURIComponent(route.system);
    return "#/" + name;
  }

  return {
    formatBytes: formatBytes,
    joinParts: joinParts,
    versionName: versionName,
    isCheatTag: isCheatTag,
    fileTags: fileTags,
    isCheatRom: isCheatRom,
    parseRoute: parseRoute,
    routeHash: routeHash
  };
});
