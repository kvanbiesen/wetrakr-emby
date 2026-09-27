# WeTrakr for Emby

Automatically track what you watch on [WeTrakr](https://wetrakr.com) from Emby Server — live
scrobbling and watched-history sync, per user.

## Features

- **Live scrobbling** — play, pause, resume and stop are reported to WeTrakr as they happen, so it
  shows what you're watching right now. Finishing a movie or episode marks it watched.
- **Mark as played, mirrored** — toggling something as watched in Emby marks it on WeTrakr too.
  Unmarking a single movie or episode removes that play; unmarking several things at once (a
  season, a series, a library clean-up) is left alone, so a bulk change in Emby can never wipe
  your WeTrakr history.
- **Two-way watched history sync** — bring your existing WeTrakr history into Emby, and keep
  WeTrakr caught up with new plays from Emby. Handles large libraries. Only ever pulls small,
  recent amounts from the Emby side, so a library glitch (a rebuild, a bad metadata refresh) can't
  flood your WeTrakr history with plays that never happened.
- **Automatic sync** — choose how often each user's history is kept in sync, from hourly to
  weekly, right from Emby's own Scheduled Tasks screen.
- **Per user, opt-in** — every Emby user connects their own WeTrakr account with a short sign-in
  code. Nobody, including the server admin, ever sees anyone's WeTrakr password. An admin can also
  switch WeTrakr off for a specific user.
- **Favorite movies, mirrored both ways** — favoriting a movie in Emby favorites it on WeTrakr
  right away, and a sync brings favorites from WeTrakr into Emby. Off by default. Movies only:
  WeTrakr has no way for this plugin to address a favorited show or episode.
- **Exclude libraries** — keep specific libraries (home videos, kids' content, etc.) out of
  WeTrakr entirely.
- **Admin overview** — see who's connected and manage any user's settings from one place in
  Dashboard → Plugins → WeTrakr.

Ratings and lists aren't part of this plugin — Emby has no personal rating feature to sync from.

## Install

1. Download `Emby.Plugin.WeTrakr.dll` from the
   [latest release](https://github.com/kvanbiesen/wetrakr-emby/releases).
2. Copy it into your Emby Server's plugins folder and restart Emby Server.
3. Each user opens their profile menu → **WeTrakr** → **Connect**, then enters the code shown at
   wetrakr.com to link their own account.

Tested against Emby Server 4.10. It should also work on 4.9 — the server interfaces this plugin
uses are unchanged between the two — but that combination hasn't actually been run and confirmed
yet.

## Support

Questions, issues or feature requests:
[github.com/kvanbiesen/wetrakr-emby](https://github.com/kvanbiesen/wetrakr-emby/issues).

## License

MIT — see [LICENSE](LICENSE). Free to use, modify and redistribute, as long as the original
copyright notice is kept.
