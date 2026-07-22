# WeTrakr for Emby

Emby Server plugin that scrobbles your playback activity to [WeTrakr](https://wetrakr.com). Port of
[wetrakr-jf](https://github.com/wetrakr/wetrakr-jf) (the Jellyfin plugin) targeting the current Emby
Server plugin API.

Sends `PlaybackStart`, `PlaybackProgress` (periodic), `PlaybackPause`, `PlaybackUnpause`,
`PlaybackStop`, `ItemMarkedPlayed` (manual watched toggle), and `UserDataSaved` (ratings) events
with provider IDs (IMDb, TMDB, TVDB) for both movies and episodes — the same event set wetrakr-jf
sends, since this is a faithful port rather than a reduced one.

Connections are **per Emby user**, not one global server-wide connection: each user pairs their
own WeTrakr account independently, so a household member who hasn't connected is never scrobbled.

## Install

1. Build the plugin (see below) or download a release DLL.
2. Copy `Emby.Plugin.WeTrakr.dll` into your Emby Server plugins folder, e.g.
   `%ProgramData%\Emby-Server\programdata\plugins\WeTrakr` on Windows, or
   `/var/lib/emby/plugins/WeTrakr` on Linux.
3. Restart Emby Server.
4. Open **Dashboard → My Plugins → WeTrakr**. Pick the Emby user you want to connect from the
   **Manage connection for** dropdown, then click **Connect**.
5. A short code is shown. The admin does *not* need that user's WeTrakr password — hand the code
   to the actual user, who opens `https://wetrakr.com/activate?platform=jellyfin` on their own
   device, logs into their own WeTrakr account, and enters it there. The dashboard page updates to
   **Connected** automatically once they confirm.
6. Repeat step 4 for any other Emby users who want their own playback scrobbled.

Only admins can reach `Dashboard → Plugins` at all — this is an Emby platform constraint, not a
choice this plugin makes (Trakt's Emby plugin works the same way: one admin-only page with a
per-user dropdown, not a self-service page under each user's own login).

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
This is a top-level setting shared by all paired users, not per-user.

## How it works

Emby's plugin API differs from Jellyfin's in a few places this plugin cares about:

- **Startup/lifecycle**: Emby plugins hook into the server via `IServerEntryPoint`
  (`Run()` / `Dispose()`), not `IHostedService`. See
  [`Scrobbling/ScrobbleManager.cs`](Emby.Plugin.WeTrakr/Scrobbling/ScrobbleManager.cs).
- **HTTP API**: Emby still uses its original ServiceStack-style API layer — plain request DTOs
  decorated with `[Route(...)]` and `[Authenticated(Roles = "Admin")]`, dispatched to a class
  implementing the `IService` marker interface — rather than Jellyfin's ASP.NET Core MVC
  controllers. See [`Api/WeTrakrService.cs`](Emby.Plugin.WeTrakr/Api/WeTrakrService.cs).
- **Outbound HTTP**: Emby plugins use `IHttpClient` (DI-provided) rather than
  `IHttpClientFactory` with `System.Net.Http.Json` helpers. See
  [`Api/WeTrakrClient.cs`](Emby.Plugin.WeTrakr/Api/WeTrakrClient.cs) and
  [`Api/DeviceCodeClient.cs`](Emby.Plugin.WeTrakr/Api/DeviceCodeClient.cs).
- **JSON serialization**: talking to WeTrakr's API (and returning data to the config page's own
  JS) uses `System.Text.Json.JsonSerializer` directly rather than Emby's `IJsonSerializer`. Emby's
  implementation does not honor `[JsonPropertyName]` in either direction — it silently produced
  empty fields both when reading WeTrakr's snake_case API responses and when serializing outbound
  requests/responses that relied on renamed properties. Using the real `System.Text.Json` API
  sidesteps that entirely.

One deliberate behavior change from the Jellyfin plugin: **connections are per Emby user**.
`PluginConfiguration.Users` holds one `WeTrakrUserConfig` (WebhookToken/Username/toggles) per
paired Emby user, and `ScrobbleManager` looks up the playing/rating user's entry before sending
anything. See [`Configuration/PluginConfiguration.cs`](Emby.Plugin.WeTrakr/Configuration/PluginConfiguration.cs).

Otherwise the behavior matches the Jellyfin plugin:

- Subscribes to `ISessionManager.PlaybackStart / PlaybackProgress / PlaybackStopped` and
  `IUserDataManager.UserDataSaved`.
- Derives pause/unpause events from `IsPaused` transitions on progress events (Emby does not fire
  dedicated pause events).
- Sends `ItemMarkedPlayed` when a user manually toggles an item watched, and `UserDataSaved` when
  a user rates an item — same as wetrakr-jf. Any downstream behavior on WeTrakr's side (e.g. list
  mapping, periodic re-sync) is backend logic that consumes these webhook events; it isn't
  implemented in either plugin's own source.
- Posts a JSON body to `{ApiBaseUrl}/webhooks/jellyfin/{WebhookToken}`.
- `WebhookToken` is obtained via the WeTrakr device-code OAuth flow
  (`/oauth/device/code?platform=jellyfin` + `/oauth/device/token`).

### Why this plugin identifies as `platform=jellyfin`

The wetrakr-api backend has no separate "emby" connection type yet, and this plugin's scrobble
payload is byte-for-byte identical to wetrakr-jf's, so it piggybacks on the existing jellyfin
webhook route and OAuth platform value rather than requiring backend changes. The trade-off: a
WeTrakr account can only have one of {Jellyfin server, Emby server} paired at a time — connecting
the second overwrites `auth.connections.jellyfin.webhook_token` from the first.

**Status: the WeTrakr backend team is planning a dedicated Emby endpoint** (mirroring the existing
jellyfin one). Once that lands, switch over by changing `platform=jellyfin` → `platform=emby` in
[`Api/DeviceCodeClient.cs`](Emby.Plugin.WeTrakr/Api/DeviceCodeClient.cs) and
`/webhooks/jellyfin/` → `/webhooks/emby/` in
[`Api/WeTrakrClient.cs`](Emby.Plugin.WeTrakr/Api/WeTrakrClient.cs) — no other changes should be
needed, since the payload shape and event set are already identical.

## License

MIT. Emby logo is © Emby, used under fair use for interoperability documentation.
