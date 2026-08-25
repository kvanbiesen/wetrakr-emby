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

Another addition ported from [wetrakr-kodi](https://github.com/wetrakr/wetrakr-kodi)'s
`resources/lib/api.py`: `WeTrakrClient` treats an HTTP `401`/`403` on a scrobble POST as "this
token was revoked" and clears it immediately, rather than retrying forever against a dead token
while `Status` keeps incorrectly reporting the user as connected.

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

### Additions beyond wetrakr-jf

**Favorites sync.** Emby's stock apps have no personal star-rating UI (confirmed with an Emby
moderator — "There is none currently"), so the rating half of `UserDataSaved` has nothing to
trigger it in practice. Favoriting an item, however, is a real native Emby feature, and Emby's
`UserDataSaveReason` enum has no dedicated value for it — favoriting/unfavoriting just shows up as
a `UserDataSaved` fire (often alongside unrelated saves, like every playback-progress tick, which
also carry the item's current favorite state even when unchanged).
[`Scrobbling/FavoriteStateTracker.cs`](Emby.Plugin.WeTrakr/Scrobbling/FavoriteStateTracker.cs)
tracks the last-known `IsFavorite` per (user, item) and only dispatches on an actual transition, so
favorites sync correctly without depending on knowing which internal reason Emby happens to report.

**Watched-history scheduled sync (Emby → WeTrakr only).** A native Emby scheduled task,
[`Scrobbling/SyncToWeTrakrTask.cs`](Emby.Plugin.WeTrakr/Scrobbling/SyncToWeTrakrTask.cs), shows up
in Dashboard → Scheduled Tasks as "Sync WeTrakr watched history" (category "WeTrakr"). It has no
default trigger — same as Trakt's own scheduled tasks — so it only runs when you add a recurring
trigger yourself or click "Run Now". For each user with the **"Update WeTrakr watched history
during scheduled sync"** toggle on, it walks their library and sends an `ItemMarkedPlayed` event
(the same event live watched-toggles use, with `save_reason: "ScheduledSync"`) for every
already-`Played` Movie/Episode not under an excluded folder. Excluded folders are configured per
user on the config page, populated from Emby's own `ApiClient.getVirtualFolders`, mirroring
Trakt's `LocationsExcluded`.

This is a one-way backfill, not a real sync: **there is no WeTrakr → Emby pull direction yet.**
"Skip unwatched import" and "update Emby from WeTrakr's watched list" (both requested) depend on
WeTrakr exposing a read API for a user's watched history, which — as of 2026-07 — doesn't appear to
exist yet (wetrakr.com's own "Import one time: Jellyfin history → WeTrakr" button, the *opposite*
write direction, is itself still labeled "coming soon"). Implementing the pull direction without a
confirmed backend contract risks the same wrong-wire-format bugs the pairing flow hit twice before
being fixed — so it's deliberately not built until that's confirmed.

There's also no bulk-import API on WeTrakr's side, so the scheduled task sends one webhook POST per
watched item — same endpoint live scrobbles use — throttled with a 400ms delay between sends.
WeTrakr's device-code endpoints showed aggressive rate limiting (`429`) under light manual testing
during development, so a large library's first sync will take a while, and a per-user run stops
early if WeTrakr rejects the token (`401`/`403`) partway through. Revisit this once/if WeTrakr
exposes a real bulk history-import endpoint.

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

### Backported from wetrakr-jf (2026-08)

wetrakr-jf independently converged on the same per-Jellyfin-user connection model this plugin
already had — no action needed there, ours has no legacy-migration baggage to carry since it was
per-user from the start. Three other fixes from that work were worth pulling in regardless:

- **[`Scrobbling/ProgressThrottle.cs`](Emby.Plugin.WeTrakr/Scrobbling/ProgressThrottle.cs)** — plain
  `PlaybackProgress` events are now throttled to at most one per 10 minutes per session (Start/Stop/
  Pause/Unpause are never throttled). Emby fires progress every few seconds; forwarding each one
  floods WeTrakr's API — this plugin has already been rate-limited under far lighter load than a
  real progress stream during earlier testing.
- **[`Scrobbling/PlayedStateTracker.cs`](Emby.Plugin.WeTrakr/Scrobbling/PlayedStateTracker.cs)** —
  `ItemMarkedPlayed` is now only dispatched when the played flag actually flips for a (user, item)
  pair. `UserDataSaveReason.TogglePlayed` fires even when the value is unchanged, and wetrakr-jf hit
  a confirmed production incident from this: a scheduled sync task re-applying watched state across
  a library produced ~36,000 redundant events in 24h from a single server.
- **`is_owner` field** on `ScrobblePayload` — added for wire parity with the jellyfin webhook
  contract this plugin piggybacks on. Always `true` here (see the field's own doc comment for why
  wetrakr-jf's fuller `is_owner`/legacy-pairing logic doesn't apply to this plugin's architecture).

## License

MIT. Emby logo is © Emby, used under fair use for interoperability documentation.

Thank you
