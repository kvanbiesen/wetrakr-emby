using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.WeTrakr.Api;
using Emby.Plugin.WeTrakr.Configuration;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;

namespace Emby.Plugin.WeTrakr.Services
{
    /// <summary>
    /// Reports playback to WeTrakr as it happens: start, pause, stop, and a seek as a fresh
    /// start at the new position. WeTrakr wants one call per thing the user does and no
    /// heartbeat (it works the position out from the clock and can suspend an app key that
    /// polls), so Emby's periodic progress event only produces a call when the paused flag
    /// flips, the position jumps, or a new item begins. A stop at 80% or more marks the title
    /// watched on WeTrakr's side.
    ///
    /// Everyone on the session who has connected WeTrakr and allows this kind of item is
    /// reported (Emby can share one session between profiles). The state changes happen on
    /// Emby's event thread; only the network call is asynchronous, queued per user in the
    /// order the events arrived so a stop never overtakes the start before it.
    /// </summary>
    public class Scrobbler : IServerEntryPoint
    {
        private const long SeekToleranceTicks = 30 * TimeSpan.TicksPerSecond;
        private const int PauseHoldMs = 1500;    // a pause right before a resume or stop is noise
        private const int SeekHoldMs = 2000;     // scrubbing makes a burst of jumps; only the last one counts
        private const int StoppedMemoryMs = 30000;
        private const double EndOfMediaProgress = 99.5;

        private class Target
        {
            public long UserId;
            public string Name;
        }

        private class Playback
        {
            public long ItemId;
            public bool Ignored;                 // nobody on the session reports this item
            public List<Target> Targets = new List<Target>();
            public bool Paused;
            public bool PauseSent;               // WeTrakr holds a pause for this playback
            public long PositionTicks;
            public DateTime PositionAtUtc;
            public CancellationTokenSource PendingPause;
            public CancellationTokenSource PendingSeek;
        }

        private readonly ISessionManager _sessions;
        private readonly IUserManager _users;
        private readonly ILibraryManager _library;
        private readonly WeTrakrApi _api;
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<string, Playback> _playbacks = new ConcurrentDictionary<string, Playback>();
        private readonly ConcurrentDictionary<string, DateTime> _stopped = new ConcurrentDictionary<string, DateTime>();
        private readonly ConcurrentDictionary<long, SemaphoreSlim> _gates = new ConcurrentDictionary<long, SemaphoreSlim>();

        public Scrobbler(ISessionManager sessions, IUserManager users, ILibraryManager library, IHttpClient http, ILogManager logManager, IApplicationHost host)
        {
            _sessions = sessions;
            _users = users;
            _library = library;
            _logger = logManager.GetLogger("WeTrakr Scrobbler");
            _api = new WeTrakrApi(http, users, logManager, host);
        }

        // Run() is called synchronously while Emby Server is starting up, alongside every other
        // plugin's IServerEntryPoint. An exception here must never reach Emby: on a server version
        // this plugin has not been tested against, throwing here could stall or take down startup
        // for the whole server over one plugin's feature not working, which is worse than that one
        // feature just not working.
        public void Run()
        {
            try
            {
                _sessions.PlaybackStart += OnPlaybackStart;
                _sessions.PlaybackProgress += OnPlaybackProgress;
                _sessions.PlaybackStopped += OnPlaybackStopped;
                _logger.Info("WeTrakr scrobbler started (Emby {0}, plugin {1})", ClientInfo.EmbyVersion, ClientInfo.PluginVersion);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr scrobbler failed to start; playback will not be reported", ex);
            }
        }

        public void Dispose()
        {
            try
            {
                _sessions.PlaybackStart -= OnPlaybackStart;
                _sessions.PlaybackProgress -= OnPlaybackProgress;
                _sessions.PlaybackStopped -= OnPlaybackStopped;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr scrobbler failed to stop cleanly", ex);
            }
        }

        // ---------------------------------------------------------------- events

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            try
            {
                var key = SessionKey(e);
                _stopped.TryRemove(StoppedKey(e), out _);   // the same item can genuinely start again right away
                Playback previous;
                if (_playbacks.TryRemove(key, out previous)) CancelHolds(previous);

                // without a position nothing can be reported; the first positioned progress tick begins it
                if (!e.PlaybackPositionTicks.HasValue) return;
                Begin(e, "start");
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr could not handle a playback start", ex);
            }
        }

        private void OnPlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            try
            {
                if (!e.PlaybackPositionTicks.HasValue || e.Item == null) return;
                var key = SessionKey(e);

                Playback state;
                if (!_playbacks.TryGetValue(key, out state) || state.ItemId != e.Item.InternalId)
                {
                    // a tick of a playback that just ended, still in flight while its stop was handled, must not reopen it
                    DateTime endedAt;
                    if (_stopped.TryGetValue(StoppedKey(e), out endedAt) && (DateTime.UtcNow - endedAt).TotalMilliseconds < StoppedMemoryMs) return;
                    // Emby Web reports "paused" when the media runs out, a moment before the stop event
                    if (e.IsPaused && (ItemMapper.Progress(e.PlaybackPositionTicks, e.Item.RunTimeTicks) ?? 0) >= EndOfMediaProgress) return;

                    Playback old;
                    if (_playbacks.TryRemove(key, out old)) CancelHolds(old);
                    Begin(e, e.IsPaused ? "pause" : "start");
                    return;
                }

                if (state.Ignored) return;

                lock (state)
                {
                    var seek = !e.IsPaused && Jumped(state, e);
                    // only playing ticks move the clock: while paused the position stays put
                    if (!e.IsPaused)
                    {
                        state.PositionTicks = e.PlaybackPositionTicks.Value;
                        state.PositionAtUtc = DateTime.UtcNow;
                    }
                    if (state.Paused == e.IsPaused && !seek) return;

                    if (e.IsPaused)
                    {
                        state.Paused = true;
                        HoldPause(e, state, Renew(ref state.PendingPause));
                        return;
                    }

                    // resumed (or seeked): a pause still on hold is dropped and needs no matching start
                    state.Paused = false;
                    Cancel(ref state.PendingPause);
                    var pauseWasSent = state.PauseSent;
                    state.PauseSent = false;

                    if (seek) HoldSeek(e, state, Renew(ref state.PendingSeek));
                    else if (pauseWasSent) Send(state.Targets, e.Item, "start", ItemMapper.Progress(e.PlaybackPositionTicks, e.Item.RunTimeTicks));
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr could not handle a playback progress tick", ex);
            }
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            try
            {
                var key = SessionKey(e);
                Playback state;
                _playbacks.TryRemove(key, out state);
                if (state != null) CancelHolds(state);

                _stopped[StoppedKey(e)] = DateTime.UtcNow;
                foreach (var stale in _stopped.Where(p => (DateTime.UtcNow - p.Value).TotalHours > 1).Select(p => p.Key).ToList()) _stopped.TryRemove(stale, out _);

                var item = e.Item;
                if (item == null || (state != null && state.Ignored)) return;

                // a reported position decides; without one the last playing tick stands in, and
                // only a positionless completion counts as having reached the end
                var progress = e.PlaybackPositionTicks.HasValue
                    ? ItemMapper.Progress(e.PlaybackPositionTicks, item.RunTimeTicks)
                    : e.PlayedToCompletion ? 100 : ItemMapper.Progress(state?.PositionTicks, item.RunTimeTicks);

                var targets = state != null ? state.Targets : Eligible(e, item);
                Send(targets, item, "stop", progress);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr could not handle a playback stop", ex);
            }
        }

        // ---------------------------------------------------------------- state

        private void Begin(PlaybackProgressEventArgs e, string action)
        {
            var item = e.Item;
            if (item == null || !ItemMapper.IsTrackable(item)) return;

            var state = new Playback
            {
                ItemId = item.InternalId,
                Targets = Eligible(e, item),
                Paused = action == "pause",
                PauseSent = action == "pause",
                PositionTicks = e.PlaybackPositionTicks ?? 0,
                PositionAtUtc = DateTime.UtcNow
            };
            state.Ignored = state.Targets.Count == 0;
            _playbacks[SessionKey(e)] = state;
            if (state.Ignored) return;

            Send(state.Targets, item, action, ItemMapper.Progress(e.PlaybackPositionTicks, item.RunTimeTicks));
        }

        // pause is held briefly: a resume or stop right after it makes it noise
        private void HoldPause(PlaybackProgressEventArgs e, Playback state, CancellationTokenSource hold)
        {
            var item = e.Item;
            var progress = ItemMapper.Progress(e.PlaybackPositionTicks, item.RunTimeTicks);
            Task.Delay(PauseHoldMs, hold.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                lock (state)
                {
                    if (state.PendingPause != hold || !state.Paused) return;
                    state.PauseSent = true;
                }
                Send(state.Targets, item, "pause", progress);
            }, TaskScheduler.Default);
        }

        // a seek is a fresh start at the new place; scrubbing collapses into the last position
        private void HoldSeek(PlaybackProgressEventArgs e, Playback state, CancellationTokenSource hold)
        {
            var item = e.Item;
            var progress = ItemMapper.Progress(e.PlaybackPositionTicks, item.RunTimeTicks);
            Task.Delay(SeekHoldMs, hold.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                lock (state)
                {
                    // a pause that arrived during the hold reports the position itself
                    if (state.PendingSeek != hold || state.Paused) return;
                }
                Send(state.Targets, item, "start", progress);
            }, TaskScheduler.Default);
        }

        private static CancellationTokenSource Renew(ref CancellationTokenSource hold)
        {
            Cancel(ref hold);
            return hold = new CancellationTokenSource();
        }

        private static void Cancel(ref CancellationTokenSource hold)
        {
            var current = hold;
            hold = null;
            current?.Cancel();
        }

        private static void CancelHolds(Playback state)
        {
            lock (state)
            {
                Cancel(ref state.PendingPause);
                Cancel(ref state.PendingSeek);
            }
        }

        private static bool Jumped(Playback state, PlaybackProgressEventArgs e)
        {
            var expected = state.Paused ? state.PositionTicks : state.PositionTicks + (DateTime.UtcNow - state.PositionAtUtc).Ticks;
            return Math.Abs(e.PlaybackPositionTicks.Value - expected) > SeekToleranceTicks;
        }

        private static string SessionKey(PlaybackProgressEventArgs e)
        {
            return e.PlaySessionId ?? e.Session?.Id ?? string.Empty;
        }

        private static string StoppedKey(PlaybackProgressEventArgs e)
        {
            return SessionKey(e) + "|" + e.Item?.InternalId;
        }

        // ---------------------------------------------------------------- who is reported

        private List<Target> Eligible(PlaybackProgressEventArgs e, BaseItem item)
        {
            var targets = new List<Target>();
            if (!ItemMapper.IsTrackable(item)) return targets;

            var users = e.Users != null && e.Users.Count > 0 ? e.Users : new List<User> { _users.GetUserById(e.Session?.UserId ?? string.Empty) };
            foreach (var user in users)
            {
                if (user == null || !user.IsGrantedAccessToFeature(Plugin.StaticId)) continue;
                if (!ConfigurationFactory.IsConnected(_users, user.InternalId)) continue;

                var options = ConfigurationFactory.LoadOptions(_users, user.InternalId);
                if (!Wanted(options, item)) continue;
                if (ExclusionFilter.For(_library, user, options).IsExcluded(item)) continue;

                targets.Add(new Target { UserId = user.InternalId, Name = user.Name });
            }
            return targets;
        }

        /// <summary>The kind of item is switched on and long enough to be worth reporting.</summary>
        public static bool Wanted(UserOptions options, BaseItem item)
        {
            if (item.RunTimeTicks.HasValue && item.RunTimeTicks.Value < TimeSpan.TicksPerMinute * Math.Max(0, options.minLength)) return false;
            return item is Movie ? options.scrobbleMovies : options.scrobbleShows;
        }

        // ---------------------------------------------------------------- sending

        private void Send(List<Target> targets, BaseItem item, string action, double? progress)
        {
            if (progress == null || targets.Count == 0) return;
            var body = ItemMapper.ToScrobble(item, progress.Value);
            if (body == null) return;

            foreach (var target in targets)
            {
                var gate = _gates.GetOrAdd(target.UserId, _ => new SemaphoreSlim(1, 1));
                // queued here, on the event thread, so the order events happened in is the order they are sent
                var turn = gate.WaitAsync();
                var t = target;
                Post(turn, gate, t, item, action, body);
            }
        }

        private async void Post(Task turn, SemaphoreSlim gate, Target target, BaseItem item, string action, ScrobbleBody body)
        {
            try
            {
                await turn.ConfigureAwait(false);
                try
                {
                    _logger.Info("WeTrakr {0} {1}% for {2}: {3}", action, body.Progress, target.Name, item.Name);
                    await _api.Scrobble(target.UserId, action, body, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (NotConnectedException)
            {
                _logger.Info("WeTrakr login of {0} is no longer valid; they need to connect again", target.Name);
            }
            catch (ApiKeyMissingException)
            {
                _logger.Debug("WeTrakr has no API key configured; not reporting {0}", item.Name);
            }
            catch (QuotaExceededException ex)
            {
                _logger.Info("WeTrakr quota reached for {0}: {1}", target.Name, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr could not report " + action + " for " + target.Name, ex);
            }
        }
    }
}
