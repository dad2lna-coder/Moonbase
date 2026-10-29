(async function () {
  var list = document.getElementById("row");
  var response = await fetch("/Moonfin/Plugins/hello/rows", { headers: { "Accept": "application/json" } });
  if (!response.ok) {
    list.textContent = "Plugin row is not available.";
    return;
  }

  var row = await response.json();
  (row.items || []).forEach(function (item) {
    var entry = document.createElement("li");
    entry.textContent = item.title;
    list.appendChild(entry);
  });
})();
