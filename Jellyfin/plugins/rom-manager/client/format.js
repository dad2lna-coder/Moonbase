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
    if (head === "library" || head === "favorites" || head === "recent" || head === "collections" || head === "platforms" || head === "settings" || head === "duplicates" || head === "trash" || head === "more") {
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
    parseRoute: parseRoute,
    routeHash: routeHash
  };
});
