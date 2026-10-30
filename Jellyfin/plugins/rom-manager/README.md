# ROM Manager

Phone-first ROM browser for MoFin. The page runs in the plugin iframe and only talks to this module. The module, loaded inside Jellyfin, is what calls Channel Manager.

Browse by platform, search, open a game, and keep favorites, a recently viewed list (last 30), and named collections. Those lists are stored per user. Admins can rescan, review identical copies, and move ROMs to trash. Trash is a move. This plugin never deletes a ROM permanently.

## Build

From this folder:

```bash
./build-zip.sh
```

That builds `Moonfin.Plugin.RomManager.dll` and writes `dist/rom-manager-<version>.zip`. The zip contains only `plugin.json`, `client/`, and `server/Moonfin.Plugin.RomManager.dll`. It does not ship `Moonfin.Server.dll` or Jellyfin assemblies.

`dotnet test` for `Jellyfin/server-plugin.sln` runs the xunit suite. `node --test format.test.mjs` checks the small client helpers.

## Install

As a Jellyfin admin, `POST` the zip to `/Moonfin/Admin/Plugins/Install`, then restart Jellyfin. The module's routes and `OnStart` run on startup. Copying the unzipped folder to `moonfin-plugins/rom-manager/` is the same thing.

The page registers a **ROM Manager** nav item on first load. Open it from MoFin. Do not browse the iframe to another document after that; the bridge dies on the next load.

## Config

Channel Manager defaults to `http://192.168.1.119:3002`, then `http://172.17.0.1:3002` if that host does not answer. No secret is required or stored.

To point at a different base URL, `PUT /Moonfin/Admin/Plugins/rom-manager/Config` as an admin:

```json
{ "settings": { "romManagerUrl": "http://192.168.1.119:3002" } }
```

`romManagerUrl` may also sit on the root of the config object. Only `http` and `https` URLs are accepted. The index is cached in memory and refreshed at most every 60 seconds. While Channel Manager answers 202, the page says the library is being indexed.

## Safety

- Move to trash lists every title, platform, and size, plus the total. Cancel is focused. If other games depend on the ROM, a second confirm is required.
- Restore asks first too.
- Identical copies are never pre-selected. **Best Experience** is a label, not a choice the plugin makes for you.
- Admin buttons are hidden for everyone else. The module checks `IsAdmin` again.
- Cover paths are checked before they are forwarded (no `..`, no absolute path, two segments, or three for a trash path). Images over 300 KB are dropped, and a response stays small enough for the plugin bridge.
- Play is one function, `playGame`, and it only says playback is coming soon.
