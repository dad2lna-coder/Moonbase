# Moonbase plugins

Plugins install under the Moonbase data folder, not the plugin dll folder, so a Moonbase update does not delete them. The server half is loaded on the next Jellyfin restart. No MoFin change is required for a plugin to register, serve files, or publish capability flags.

Host API is **1** (`minHostApi`).

## Layout

```text
{Jellyfin data}/plugins/configurations/Moonfin/moonfin-plugins/{id}/
  plugin.json
  client/                 optional web UI for the MoFin WebView
  server/                 optional module dll
  config.json             admin config, written by Moonbase
  data/{userId}/{key}.json
```

Typical data roots:

| Install | Path |
| --- | --- |
| Linux | `/var/lib/jellyfin/plugins/configurations/Moonfin/moonfin-plugins/` |
| Docker (linuxserver) | `/config/data/plugins/configurations/Moonfin/moonfin-plugins/` |
| Windows | `%ProgramData%\Jellyfin\Server\plugins\configurations\Moonfin\moonfin-plugins\` |

## Manifest (`plugin.json`)

Same model as the Moonfin client host, plus the server half and the web entry.

| Field | Meaning |
| --- | --- |
| `id` | Lowercase folder name. Letters, numbers, hyphens. |
| `name`, `version`, `description` | Shown to clients. |
| `provides` | Capability flags this package claims. A flag is not turned on unless the loaded server module lists the same flag and the plugin is active. |
| `requires` | Flags that must already be active (host or another plugin). Unmet requirements make the plugin **dormant**. That is not an error. |
| `slots` | `homeRow`, `playback`, `settings`, `events`. |
| `client.html`, `client.js` | Optional WebView files. Served from the folder that holds them (default `client/`) at `/Moonfin/Plugins/{id}/files/`. These files are public. |
| `server` | Optional type name of the `IMoonbaseModule` in the server dll. |
| `minHostApi` | Must be `1` on this Moonbase. A higher value stays dormant and its DLL is not loaded. |
| `proxyAccess` | `admin` (default) or `user`. Who may call `POST /Moonfin/Plugins/{id}/Proxy`. |

A client-only plugin (no `server`) can be active and can fill a slot, but it cannot turn a capability flag on.

Host flags a plugin may require, when that part of Moonbase is actually configured:

`settingsSync`, `seerr`, `mdblist`, `tmdb`, `tmdbThumbs`, `tmdbDetails`, `tmdbReviews`, `tmdbPersonLinks`, `recommendations`, `messages`, `incompleteRecordings`.

`clientLog` is not a server flag. Requiring it keeps the plugin dormant.

## Server module

```csharp
public interface IMoonbaseModule
{
    string Id { get; }
    string Version { get; }
    IReadOnlyCollection<string> Provides { get; }
    IReadOnlyCollection<string> Requires { get; }
    void RegisterServices(IServiceCollection services);
    void MapRoutes(IMoonbaseRouteTable routes) { }
    void OnStart(IServiceProvider services) { }
    void OnStop() { }
}
```

`PluginServiceRegistrator` discovers modules and calls `RegisterServices` only for plugins that are enabled and whose `requires` are already met by the host or by another enabled plugin's declared provides. A disabled plugin's DLL is not loaded on the next Jellyfin restart. Put the dll in `server/`. It must be named by `server` (simple type name or namespace-qualified name). Reference `Moonfin.Server` and do not ship that dll or Jellyfin's assemblies inside the zip. `RegisterServices` runs at Jellyfin startup, so a newly uploaded dll is not in the container until restart.

Routes map under `/Moonfin/Plugins/{id}/`. Do not use `files`, `Data`, or `Proxy` — Moonbase owns those.

`IMoonbaseEvents.Emit(pluginId, type, payload, userId)` writes to the existing `GET /Moonfin/Settings/Stream`. Plugins cannot emit the built-in types `settingsUpdated`, `themesChanged`, or `messagesChanged`. Every accepted event is sent as `plugin:{id}:{type}` (for example `plugin:hello:custom`). Pass a user id to deliver it to that user; omit it to broadcast.

## Routes

| Method | Path | Who |
| --- | --- | --- |
| GET | `/Moonfin/Plugins` | Signed-in user. Plugins (`enabled`, `active`, `dormantReason`, `provides`, `slots`, `clientEntryUrl`, `version`) and the capability map. |
| GET | `/Moonfin/Plugins/{id}/files/{path}` | Public, no login. An enabled, active plugin's client folder only. |
| GET/PUT/DELETE | `/Moonfin/Plugins/{id}/Data/{key}` | Signed-in user. The plugin must be active. Per-user JSON, one token key. 200 keys and 5 MB per user; over that returns 413. |
| POST | `/Moonfin/Plugins/{id}/Proxy` | The plugin must be active. Administrators only, unless `proxyAccess` is `user`. Allowlisted upstream. Moonbase adds the secret. |
| GET/POST/PUT/DELETE | `/Moonfin/Plugins/{id}/{path}` | Routes the active module mapped. Dormant modules return 409. |
| POST | `/Moonfin/Admin/Plugins/Install` | Admin zip upload. |
| POST | `/Moonfin/Admin/Plugins/{id}/Enable` | Admin. |
| POST | `/Moonfin/Admin/Plugins/{id}/Disable` | Admin. |
| DELETE | `/Moonfin/Admin/Plugins/{id}` | Admin. |
| GET/PUT | `/Moonfin/Admin/Plugins/{id}/Config` | Admin. Secret values are never returned. |

Client files are public. MoFin's plugin WebView sends the Authorization header only on the first page load, so `GET /Moonfin/Plugins/{id}/files/{path}` does not require a login and every sub-file (html, js, css, images, fonts) has to load without one. Never put secrets, tokens, or private config in that folder. Anything private goes through the Data or Proxy routes, which still require a signed-in user. Disabled, dormant, and unknown plugins all answer 404, the same as a missing file. `plugin.json`, `config.json`, `data/`, dotfiles, the server half, and native libraries are not served. Only an enabled, active plugin's client folder is, and only web and asset extensions.

`GET /Moonfin/Ping` gains a `plugins` object. Existing ping fields are unchanged. Example: `{ "plugins": { "hello.homeRow": true } }`. `true` means an active server module listed that flag. Anything else is `false` or absent.

## Capability rules

1. Disabled plugins are inactive. `dormantReason` is null.
2. Enabled plugins with unmet `requires` are dormant. The registry still returns them.
3. A declared server module is loaded on startup only when the plugin is enabled and its requires are met. Otherwise the DLL stays unloaded. If it is allowed to load but the process has not restarted since install, it stays dormant until Jellyfin restarts.
4. A flag is turned on only when the plugin is active **and** the loaded module's `Provides` contains that flag **and** `plugin.json` lists it too.
5. Plugins do not flip existing ping fields such as `tmdbAvailable`.

## Config and proxy

`config.json` shape:

```json
{
  "settings": { "label": "Hello" },
  "secrets": { "apiKey": "stored-on-the-server" },
  "proxyAllow": [
    {
      "name": "example",
      "baseUrl": "https://api.example.com/v1/",
      "inject": "header",
      "header": "Authorization",
      "scheme": "Bearer",
      "secret": "apiKey"
    },
    {
      "name": "tmdb",
      "baseUrl": "https://api.themoviedb.org/3/",
      "inject": "query",
      "param": "api_key",
      "secret": "tmdbKey"
    }
  ]
}
```

GET config returns `secretNames` instead of secret values. Property names containing `secret`, `password`, `token`, or `apiKey` are redacted outside `proxyAllow` (there, `secret` is the **name** of a key). PUT with `"********"` or omitting a secret keeps the stored value. An empty string clears it. Reinstalling a zip keeps `config.json` and `data/`.

Proxy body:

```json
{ "name": "example", "path": "items", "method": "GET", "query": { "q": "hi" } }
```

`path` is relative to `baseUrl`. `..`, other hosts, and redirects are rejected. `inject` is `header` (the default, using `header` and optional `scheme`) or `query` (using `param`, for a TMDB-style `?api_key=`). The secret is added by Moonbase and stripped from text responses if it echoes. Image bodies other than `image/svg+xml` are passed through as raw bytes and are not decoded or scrubbed. `image/svg+xml` and anything that is not JSON (`application/json` or `+json`) or an image is returned as `text/plain`. Every proxy response sends `X-Content-Type-Options: nosniff` and `Content-Security-Policy: sandbox`.

## Packaging a client + server pair

1. Build the server class library (`net8.0`) that implements `IMoonbaseModule`.
2. Zip only:

```text
plugin.json
client/index.html
client/app.js
server/Your.Plugin.dll
```

Do not put `Moonfin.Server.dll` or Jellyfin dlls in the zip. Paths must stay inside the archive (no `..`, no absolute paths). One top-level folder is fine; Moonbase strips it when `plugin.json` is inside.

3. Install either by copying the folder to `moonfin-plugins/{id}/` or by posting the zip to `/Moonfin/Admin/Plugins/Install` as an administrator.
4. Restart Jellyfin. The module's `RegisterServices`, `MapRoutes`, and `OnStart` run on startup.

## Hello sample

Source: `Jellyfin/samples/plugins/hello`.

```bash
dotnet build Jellyfin/samples/plugins/hello/Moonfin.Plugin.Hello.csproj -c Release
```

Zip these files:

- `samples/plugins/hello/plugin.json`
- `samples/plugins/hello/client/index.html`
- `samples/plugins/hello/client/app.js`
- `samples/plugins/hello/bin/Release/net8.0/Moonfin.Plugin.Hello.dll` stored as `server/Moonfin.Plugin.Hello.dll`

Copy the unzipped `hello` folder into `moonfin-plugins/hello/`, or upload the zip, then restart Jellyfin.

After restart:

- `GET /Moonfin/Plugins` shows `hello` active, with `clientEntryUrl` `/Moonfin/Plugins/hello/files/client/index.html`.
- `GET /Moonfin/Ping` includes `"plugins": { "hello.homeRow": true }`.
- `GET /Moonfin/Plugins/hello/rows` returns a one-item home row titled "Hello, Moonfin".
