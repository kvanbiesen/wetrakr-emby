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
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.Plugin.WeTrakr.Services
{
    /// <summary>
    /// "Mark as played" done by hand in Emby (a movie, an episode, a season or a whole series)
    /// goes to the user's WeTrakr history. Marking a season or series makes Emby raise one event
    /// per episode, so events are collected for a moment and sent as one batched call per user,
    /// which is also what WeTrakr asks of writers. Playback saves its own state with another
    /// reason and is left to the scrobbler; the plugin's own imports use yet another, so
    /// nothing echoes back.
    ///
    /// Unmarking is mirrored for a single movie or a single episode. Several unmarks at once are a
    /// season, a series or a clean-up in Emby, and must not wipe a WeTrakr history. A single episode
    /// is removed by its own external id with no show around it and no list status, so the request
    /// can only name that one play (the latest); an episode without an external id is left alone.
    /// </summary>
    public class WatchedMarks : IServerEntryPoint
    {
        private const int HoldMs = 2000;

        /// <summary>
        /// The most marks one burst may send. Someone can mark a whole long series played in one click, so this is far above
        /// what a sync sends, but a burst of thousands is another plugin or a glitch re-marking a library, and is not written
        /// to WeTrakr.
        /// </summary>
        public const int MaxMarksPerBurst = 1000;

        public static bool WithinBurstLimit(int marks)
        {
            return marks <= MaxMarksPerBurst;
        }

        private static readonly TimeSpan BatchPause = TimeSpan.FromMilliseconds(1100);   // WeTrakr's write limit is 60 a minute

        private class Batch
        {
            public readonly List<WatchedItem> Adds = new List<WatchedItem>();
            public readonly Dictionary<long, WatchedItem> Removals = new Dictionary<long, WatchedItem>();
            public readonly Dictionary<long, WatchedItem> AddsById = new Dictionary<long, WatchedItem>();
            public readonly HashSet<long> Unmarked = new HashSet<long>();   // every unmark in this hold, eligible or not
            public bool FolderUnmarked;
            public CancellationTokenSource Pending;
            public readonly SemaphoreSlim Sending = new SemaphoreSlim(1, 1);
        }

        private readonly IUserDataManager _userData;
        private readonly IUserManager _users;
        private readonly ILibraryManager _library;
        private readonly WeTrakrApi _api;
        private readonly ILogger _logger;
        private readonly PlayedStateTracker _played = new PlayedStateTracker();
        private readonly ConcurrentDictionary<long, Batch> _batches = new ConcurrentDictionary<long, Batch>();

        public WatchedMarks(IUserDataManager userData, IUserManager users, ILibraryManager library, IHttpClient http, ILogManager logManager, IApplicationHost host)
        {
            _userData = userData;
            _users = users;
            _library = library;
            _logger = logManager.GetLogger("WeTrakr Watched");
            _api = new WeTrakrApi(http, users, logManager, host);
        }

        // See the note on Scrobbler.Run(): this runs during Emby's own startup sequence and must
        // never throw into it, on any server version.
        public void Run()
        {
            try
            {
                _userData.UserDataSaved += OnUserDataSaved;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr watched-mark mirroring failed to start", ex);
            }
        }

        public void Dispose()
        {
            try
            {
                _userData.UserDataSaved -= OnUserDataSaved;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr watched-mark mirroring failed to stop cleanly", ex);
            }
        }

        // raised inline by Emby's own save: it has to stay quick and must never throw
        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            try
            {
                if (e.SaveReason != UserDataSaveReason.TogglePlayed || e.User == null || e.UserData == null || e.Item == null) return;

                var user = e.User;
                var item = e.Item;
                var played = e.UserData.Played;

                // a re-mark that does not change anything carries no information
                if (!_played.ShouldDispatch(PlayedStateTracker.KeyFor(user.InternalId, item.InternalId), played, DateTime.UtcNow)) return;

                var watched = Eligible(user, item, e.UserData) ? ItemMapper.ToWatched(item, WatchedDate(e.UserData)) : null;
                if (played && watched == null) return;

                var batch = _batches.GetOrAdd(user.InternalId, id => new Batch());
                CancellationTokenSource pending;
                lock (batch)
                {
                    // every unmark counts towards "several at once", eligible or not: a season unmark fires one
                    // event per episode plus one for the folder, and an excluded episode must not make the rest look single
                    if (!played)
                    {
                        batch.Unmarked.Add(item.InternalId);
                        if (item is Folder) batch.FolderUnmarked = true;
                    }

                    if (watched != null) Collect(batch, item, watched, played);

                    batch.Pending?.Cancel();
                    pending = batch.Pending = new CancellationTokenSource();
                }
                Flush(user, batch, pending);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr could not queue a watched mark", ex);
            }
        }

        // a mark and an unmark of the same item inside one hold cancel out: an accidental click sends nothing
        private static void Collect(Batch batch, BaseItem item, WatchedItem watched, bool played)
        {
            if (played)
            {
                if (batch.Removals.Remove(item.InternalId)) return;
                if (!batch.AddsById.ContainsKey(item.InternalId))
                {
                    batch.AddsById[item.InternalId] = watched;
                    batch.Adds.Add(watched);
                }
                return;
            }

            WatchedItem added;
            if (batch.AddsById.TryGetValue(item.InternalId, out added))
            {
                batch.AddsById.Remove(item.InternalId);
                batch.Adds.Remove(added);
                return;
            }
            batch.Removals[item.InternalId] = watched;
        }

        private bool Eligible(User user, BaseItem item, UserItemData data)
        {
            if (!ItemMapper.IsTrackable(item) || !user.IsGrantedAccessToFeature(Plugin.StaticId)) return false;
            if (!ConfigurationFactory.IsConnected(_users, user.InternalId)) return false;

            var options = ConfigurationFactory.LoadOptions(_users, user.InternalId);
            if (item is Movie ? !options.scrobbleMovies : !options.scrobbleShows) return false;
            return !ExclusionFilter.For(_library, user, options).IsExcluded(item);
        }

        private static DateTime WatchedDate(UserItemData data)
        {
            return data.LastPlayedDate?.UtcDateTime ?? DateTime.UtcNow;
        }

        // waits for the burst to end, then sends everything collected for the user together
        private async void Flush(User user, Batch batch, CancellationTokenSource pending)
        {
            try
            {
                try
                {
                    await Task.Delay(HoldMs, pending.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;   // a newer mark restarted the hold and flushes for both
                }

                List<WatchedItem> adds;
                List<WatchedItem> removals;
                bool bulkUnmark;
                lock (batch)
                {
                    if (batch.Pending != pending) return;
                    adds = new List<WatchedItem>(batch.Adds);
                    removals = batch.Removals.Values.ToList();
                    bulkUnmark = batch.Unmarked.Count > 1 || batch.FolderUnmarked;
                    batch.Adds.Clear();
                    batch.AddsById.Clear();
                    batch.Removals.Clear();
                    batch.Unmarked.Clear();
                    batch.FolderUnmarked = false;
                    batch.Pending = null;
                }

                // the user may have disconnected during the hold
                if (!ConfigurationFactory.IsConnected(_users, user.InternalId)) return;

                await batch.Sending.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (WithinBurstLimit(adds.Count)) await SendAdds(user, adds).ConfigureAwait(false);
                    else _logger.Warn("{0} marked {1} items as played within seconds; more than the {2} sent at once, so none were sent to WeTrakr (looks like a glitch or another plugin, not a person)", user.Name, adds.Count, MaxMarksPerBurst);
                    if (removals.Count > 0 && bulkUnmark) _logger.Info("{0} unmarked several items at once in Emby; only a single unmark is sent to WeTrakr", user.Name);
                    else await SendRemovals(user, removals).ConfigureAwait(false);
                }
                finally
                {
                    batch.Sending.Release();
                }
            }
            catch (NotConnectedException)
            {
                _logger.Info("WeTrakr login of {0} is no longer valid; they need to connect again", user.Name);
            }
            catch (QuotaExceededException ex)
            {
                _logger.Info("WeTrakr quota reached for {0}, watched marks not sent: {1}", user.Name, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr could not send watched marks for " + user.Name, ex);
            }
        }

        private async Task SendAdds(User user, List<WatchedItem> adds)
        {
            if (adds.Count == 0) return;
            int skipped;
            var body = TrackingBuilder.Build(adds, out skipped);
            var sent = 0;
            var notFound = 0;
            var first = true;

            foreach (var part in TrackingBatcher.Split(body))
            {
                if (!first) await Task.Delay(BatchPause).ConfigureAwait(false);
                first = false;
                var result = await _api.AddTracking(user.InternalId, part, CancellationToken.None).ConfigureAwait(false);
                sent += TrackingBuilder.Count(part);
                notFound += result?.NotFoundCount ?? 0;
            }

            _logger.Info("WeTrakr: sent {0} watched item(s) for {1}{2}{3}", sent, user.Name,
                notFound == 0 ? "" : ", " + notFound + " not found on WeTrakr",
                skipped == 0 ? "" : ", " + skipped + " without usable ids skipped");
        }

        private async Task SendRemovals(User user, List<WatchedItem> removals)
        {
            if (removals.Count == 0) return;
            int skipped;
            var body = TrackingBuilder.BuildRemoval(removals, out skipped);
            if (skipped > 0) _logger.Info("WeTrakr: {0} unmarked item(s) of {1} have no external id, so they stay on WeTrakr", skipped, user.Name);
            if (TrackingBuilder.Count(body) == 0) return;

            // no tracked_at is sent, so WeTrakr removes the latest play, which is what an unmark means
            await _api.RemoveTracking(user.InternalId, body, CancellationToken.None).ConfigureAwait(false);
            _logger.Info("WeTrakr: removed {0} unmarked item(s) for {1}", TrackingBuilder.Count(body), user.Name);
        }
    }
}
