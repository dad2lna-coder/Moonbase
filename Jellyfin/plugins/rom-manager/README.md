# ROM Manager

Phone-first ROM browser for MoFin. The page runs in the plugin iframe and only talks to this module. The module, loaded inside Jellyfin, is what calls Channel Manager.

Browse by platform, search, open a game, and keep favorites, a recently played list (last 30), and named collections. Those lists are stored per user. Admins can rescan, review identical copies, and move ROMs to trash. Trash is a move. This plugin never deletes a ROM permanently.

Play hands a library ROM to MoFin's existing EmulatorJS player. It does not stream the file itself.

## Build

From this folder:

```bash
./build-zip.sh
```

That builds `Moonfin.Plugin.RomManager.dll` and writes `dist/rom-manager-<version>.zip`. The zip contains only `plugin.json`, `client/`, and `server/Moonfin.Plugin.RomManager.dll`. It does not ship `Moonfin.Server.dll` or Jellyfin assemblies. Moonbase stores that server dll as `server/Moonfin.Plugin.RomManager.dll.mbmod` so Jellyfin's plugin scan does not load it.

`dotnet test` for `Jellyfin/server-plugin.sln` runs the xunit suite. `npm test` checks the client helpers, that every page renders, and that Play calls the host only when the launch route says the ROM is playable. The smoke test's jsdom dependency stays out of the zip.

## Install

As a Jellyfin admin, `POST` the zip to `/Moonfin/Admin/Plugins/Install`, then restart Jellyfin. The module's routes and `OnStart` run on startup. Copying the unzipped folder to `moonfin-plugins/rom-manager/` is the same thing.

On host API 1 the page registers a **ROM Manager** nav item on first load. Host API 2 and newer already own that item, so the page skips registration. Open it from MoFin. Do not browse the iframe to another document after that; the bridge dies on the next load. When the player exits, the host event `{ "type": "gameExited" }` refreshes the page in place.

The plugin version is 0.2.0. `plugin.json` slots is `games`, so it survives normalisation and shows on `GET /Moonfin/Plugins`. `proxyAccess` is `user`: any signed-in user can launch. Admin routes still check `IsAdmin`.

## Config

Channel Manager defaults to `http://192.168.1.119:3002`, then `http://172.17.0.1:3002` if that host does not answer. No secret is required or stored.

To point at a different base URL, `PUT /Moonfin/Admin/Plugins/rom-manager/Config` as an admin:

```json
{ "settings": { "romManagerUrl": "http://192.168.1.119:3002" } }
```

`romManagerUrl` may also sit on the root of the config object. Only `http` and `https` URLs are accepted. The index is cached in memory and refreshed at most every 60 seconds. While Channel Manager answers 202, the page says the library is being indexed.

ROM files are read from `romRoot` (default `/data/media/roms`). Set it the same way, as `settings.romRoot` or a top-level `romRoot`. A path containing `..` is ignored. On disk a version is `<romRoot>/<System>/<GameFolder>/<file>`. BIOS files, when a core needs them, live in `<romRoot>/.bios/<System>/` (NES FDS is the one present today: `.bios/NES/fdsbios.zip`).

## Play

`POST launch` with `{ "key", "p" }`. `key` must be a game in the index and `p` one of its versions. The path has to pass the same checks as every other ROM path, and it cannot be a trash path. The module asks `GamesService.LocateRomFile` for the library id, ROM token, system, core, and file name.

The answer is `{ ok, libraryId, gameId, core, fileName, title, system, playable, reason }`. `playable` is false, with a plain reason, when no game library covers the file, when the system is PSP (`PSP is too heavy for iPhone`), when it is ColecoVision, MSX, or Channel F (`No emulator for this system yet`), or when a needed BIOS file is missing. ZX Spectrum uses the installed EmulatorJS 4.2.3 core name `fuse`.

The page calls that route from `playGame`. Only a playable result calls `hostCall("playGame", { libraryId, gameId, title })`. Otherwise the reason is a toast and the button says why. A playable game is written to the recent list before the handoff. The iframe is not navigated.

## Safety

- Move to trash lists every title, platform, and size, plus the total. Cancel is focused. If other games depend on the ROM, a second confirm is required.
- Restore asks first too.
- Identical copies are never pre-selected. **Best Experience** is a label, not a choice the plugin makes for you. Trash is only the copies you tick on the Duplicates page.
- Admin buttons are hidden for everyone else. The module checks `IsAdmin` again.
- Cover paths are checked before they are forwarded (no `..`, no absolute path, two segments, or three for a trash path). Images over 300 KB are dropped, and a response stays small enough for the plugin bridge.
- Play is one function, `playGame`. It only talks to the `launch` route and the host player. It does not call Channel Manager write routes.