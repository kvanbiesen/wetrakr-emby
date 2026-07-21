# WeTrakr for Emby

Emby Server plugin that scrobbles your playback activity to [WeTrakr](https://wetrakr.com). Port of
[wetrakr-jf](https://github.com/wetrakr/wetrakr-jf) (the Jellyfin plugin) targeting the current Emby
Server plugin API.

Sends `PlaybackStart`, `PlaybackProgress` (periodic), `PlaybackPause`, `PlaybackUnpause`, and
`PlaybackStop` events with provider IDs (IMDb, TMDB, TVDB) for both movies and episodes.

## Install

1. Build the plugin (see below) or download a release DLL.
2. Copy `Emby.Plugin.WeTrakr.dll` into your Emby Server plugins folder, e.g.
   `%ProgramData%\Emby-Server\programdata\plugins\WeTrakr` on Windows, or
   `/var/lib/emby/plugins/WeTrakr` on Linux.
3. Restart Emby Server.
4. Open **Dashboard → My Plugins → WeTrakr**. Pick the Emby user you want to connect from the
   **Manage connection for** dropdown, then click **Connect**.
5. A short code is shown. Open `https://wetrakr.com/activate?platform=jellyfin`, paste the code, confirm.
6. The plugin page flips to **Connected** for that user. Repeat from step 4 for any other Emby
   users on the server who want their own playback scrobbled — connecting is per-user, so
   household members who don't opt in are never sent to WeTrakr.

## Uninstall

Dashboard → My Plugins → WeTrakr → Uninstall → restart.

## Development

Requires the .NET 8 SDK.

```bash
# Build
dotnet build Emby.Plugin.WeTrakr/Emby.Plugin.WeTrakr.csproj -c Release

# Publish the plugin DLL
dotnet publish Emby.Plugin.WeTrakr/Emby.Plugin.WeTrakr.csproj -c Release -f netstandard2.0 -o publish
```

Copy `publish/Emby.Plugin.WeTrakr.dll` into your Emby Server's plugin directory and restart the
server to load it locally.

### Point the plugin at a non-production API

Edit `ApiBaseUrl` in the plugin's configuration XML on disk (`config/plugins/configurations/Emby.Plugin.WeTrakr.xml`).

## How it works

Emby's plugin API differs from Jellyfin's in two places this plugin cares about:

- **Startup/lifecycle**: Emby plugins hook into the server via `IServerEntryPoint`
  (`Run()` / `Dispose()`), not `IHostedService`. See
  [`Scrobbling/ScrobbleManager.cs`](Emby.Plugin.WeTrakr/Scrobbling/ScrobbleManager.cs).
- **HTTP API**: Emby still uses its original ServiceStack-style API layer — plain request DTOs
  decorated with `[Route(...)]` and `[Authenticated(Roles = "Admin")]`, dispatched to a class
  implementing the `IService` marker interface — rather than Jellyfin's ASP.NET Core MVC
  controllers. See [`Api/WeTrakrService.cs`](Emby.Plugin.WeTrakr/Api/WeTrakrService.cs).
- **Outbound HTTP**: Emby plugins use `IHttpClient` + `IJsonSerializer` (both DI-provided) rather
  than `IHttpClientFactory` with `System.Net.Http.Json` helpers. See
  [`Api/WeTrakrClient.cs`](Emby.Plugin.WeTrakr/Api/WeTrakrClient.cs) and
  [`Api/DeviceCodeClient.cs`](Emby.Plugin.WeTrakr/Api/DeviceCodeClient.cs).

One deliberate behavior change from the Jellyfin plugin: **connections are per Emby user**, not
one global server-wide connection. `PluginConfiguration.Users` holds one `WeTrakrUserConfig`
(WebhookToken/Username/toggles) per paired Emby user, and `ScrobbleManager` looks up the playing
or rating user's entry before sending anything — an Emby user who hasn't connected their own
WeTrakr account is never scrobbled, even if another user on the same server has. See
[`Configuration/PluginConfiguration.cs`](Emby.Plugin.WeTrakr/Configuration/PluginConfiguration.cs).

Otherwise the behavior is the same as the Jellyfin plugin:

- Subscribes to `ISessionManager.PlaybackStart / PlaybackProgress / PlaybackStopped`.
- Derives pause/unpause events from `IsPaused` transitions on progress events (Emby does not fire
  dedicated pause events).
- Posts a JSON body to `{ApiBaseUrl}/webhooks/jellyfin/{WebhookToken}`.
- `WebhookToken` is obtained via the WeTrakr device-code OAuth flow
  (`/oauth/device/code?platform=jellyfin` + `/oauth/device/token`).

### Why this plugin identifies as `platform=jellyfin`

The wetrakr-api backend has no separate "emby" connection type yet, and this plugin's scrobble
payload is byte-for-byte identical to wetrakr-jf's, so it piggybacks on the existing jellyfin
webhook route and OAuth platform value rather than requiring backend changes. The trade-off: a
WeTrakr account can only have one of {Jellyfin server, Emby server} paired at a time — connecting
the second overwrites `auth.connections.jellyfin.webhook_token` from the first. If/when the
backend grows a dedicated `emby` connection slot and a `/webhooks/emby/:token` route, switch
`platform=jellyfin` → `platform=emby` in
[`Api/DeviceCodeClient.cs`](Emby.Plugin.WeTrakr/Api/DeviceCodeClient.cs) and
`/webhooks/jellyfin/` → `/webhooks/emby/` in
[`Api/WeTrakrClient.cs`](Emby.Plugin.WeTrakr/Api/WeTrakrClient.cs).

## Roadmap

- v2: `ItemMarkedPlayed` (manual watched toggle in Emby).
- v3: `UserDataSaved` (ratings and favorites).

## License

MIT. Emby logo is © Emby, used under fair use for interoperability documentation.
