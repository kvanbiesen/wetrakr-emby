using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.WeTrakr.Api;
using Emby.Plugin.WeTrakr.Configuration;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.Plugin.WeTrakr.Services
{
    /// <summary>What a page or the task shows about a user's sync. lowerCamelCase because the page reads it verbatim.</summary>
    public class SyncStatus
    {
        public bool running { get; set; }
        public string message { get; set; }
        public string error { get; set; }
        public string lastRunAt { get; set; }
        public string lastAutoRunAt { get; set; }
    }

    /// <summary>
    /// Keeps a user's Emby and WeTrakr watched history together, following WeTrakr's sync guidelines:
    ///
    ///   1. The first import reads the whole watched list, a page of 100 at a time (the most the API gives).
    ///   2. Every run stores the timestamps of /sync/last_activities as they were when it began.
    ///   3. The next run compares them and reads only what moved, with from_date; if nothing
    ///      moved it does not read the history at all.
    ///   4. Writes are grouped: everything to send goes in as few calls as the API's limits allow
    ///      (up to 5,000 items a call).
    ///
    /// WeTrakr to Emby is the bulk direction. It only ever marks items as played and never unmarks
    /// anything, so a run can not wipe watched status in Emby. A large first import resumes where it
    /// stopped if WeTrakr's daily quota runs out.
    ///
    /// Emby to WeTrakr is live first: scrobbles and mark-as-played (see Scrobbler and WatchedMarks) go out
    /// as they happen. A sync only catches up on what those missed, and never copies a history in bulk:
    /// the first run records where sending starts, only plays newer than that are sent, only ones WeTrakr
    /// does not already list (every extra add is another play there), and a run that finds more than
    /// SyncLogic.MaxPushItems sends nothing, since that many at once is a glitch in Emby, not watching.
    ///
    /// Emby builds a new instance per request, so run state is static.
    /// </summary>
    public class HistorySync
    {
        public const int CooldownMinutes = 2;
        public const string InterruptedSummary = "The last sync did not finish because the server restarted. Run it again.";
        private static readonly TimeSpan WritePause = TimeSpan.FromMilliseconds(1100);   // WeTrakr's write limit is 60 a minute

        private class RunInfo
        {
            public bool Running;
            public bool Reset;
            public bool Scheduled;
            public string Message;
            public string Error;
            public DateTime StartedUtc;
            public DateTime? FinishedUtc;
        }

        private class Job
        {
            public User User;
            public UserOptions Options;
            public bool Reset;
            public DateTime Started;
            public LibraryIndex Index;
            public readonly HashSet<long> Seen = new HashSet<long>();   // an episode file covering two episodes is handled once
            public readonly RemoteKeys Remote = new RemoteKeys();
            public readonly HashSet<string> ShowsMissing = new HashSet<string>();
            public int MoviesMarked, EpisodesMarked, AlreadyPlayed, Refreshed, MoviesMissing, EpisodesMissing;
            public int MoviesSent, EpisodesSent, NotFound, Skipped, PushOverLimit;
            public bool PushBaselined;
            public int FavoritesMarked, FavoritesMissing;
        }

        private static readonly ConcurrentDictionary<long, RunInfo> Runs = new ConcurrentDictionary<long, RunInfo>();
        private static readonly ConcurrentDictionary<long, DateTime> LastFinish = new ConcurrentDictionary<long, DateTime>();

        private readonly ILibraryManager _library;
        private readonly IUserDataManager _userData;
        private readonly IUserManager _users;
        private readonly WeTrakrApi _api;
        private readonly ILogger _logger;

        public HistorySync(ILibraryManager library, IUserDataManager userData, IUserManager users, IHttpClient http, ILogManager logManager, IApplicationHost host)
        {
            _library = library;
            _userData = userData;
            _users = users;
            _logger = logManager.GetLogger("WeTrakr Sync");
            _api = new WeTrakrApi(http, users, logManager, host);
        }

        public SyncStatus Status(User user)
        {
            RunInfo run;
            Runs.TryGetValue(user.InternalId, out run);
            return ToStatus(run, ConfigurationFactory.LoadSync(_users, user.InternalId));
        }

        /// <summary>Starts a sync in the background. reset = read everything again and refresh Emby's watch dates.</summary>
        public SyncStatus Start(User user, bool reset, bool automatic)
        {
            var why = Refusal(user, true);
            if (why != null) return Refused(user, why);

            var run = new RunInfo { Running = true, Reset = reset, Scheduled = automatic, Message = "Starting...", StartedUtc = DateTime.UtcNow };
            if (!Register(user, run)) return Refused(user, "A sync is running right now. Try again when it finishes.");

            Task.Run(() => RunAsync(user, run, CancellationToken.None));
            return Status(user);
        }

        /// <summary>The scheduled task: runs for a user whose own interval has passed; never queued behind a run in progress.</summary>
        public async Task RunScheduled(User user, CancellationToken ct)
        {
            var options = ConfigurationFactory.LoadOptions(_users, user.InternalId);
            if (!options.autoSync || Refusal(user, false) != null) return;
            if (!SyncLogic.Due(ConfigurationFactory.LoadSync(_users, user.InternalId), options.autoSyncHours, DateTime.UtcNow)) return;

            var run = new RunInfo { Running = true, Scheduled = true, Message = "Starting...", StartedUtc = DateTime.UtcNow };
            if (!Register(user, run)) return;
            await RunAsync(user, run, ct).ConfigureAwait(false);
        }

        // ---------------------------------------------------------------- run

        private async Task RunAsync(User user, RunInfo run, CancellationToken ct)
        {
            var options = ConfigurationFactory.LoadOptions(_users, user.InternalId);
            var state = ConfigurationFactory.LoadSync(_users, user.InternalId);
            var job = new Job { User = user, Options = options, Reset = run.Reset, Started = run.StartedUtc };

            try
            {
                MarkStarted(user, state, run);

                var activities = await _api.GetActivities(user.InternalId, ct).ConfigureAwait(false);
                var now = SyncLogic.Cursors.From(activities);

                var plan = SyncLogic.PlanRun(state, options, run.Reset, now);
                var pushedBefore = !string.IsNullOrEmpty(state.pushedAt);

                if (plan.Read) await Read(job, run, state, plan.Full, ct).ConfigureAwait(false);
                if (options.syncFavorites) await ReadFavorites(job, run, state, ct).ConfigureAwait(false);
                if (options.syncPush) await Send(job, run, state, pushedBefore, ct).ConfigureAwait(false);

                SyncLogic.Remember(state, now);
                state.syncedAt = activities?.All ?? SyncLogic.Stamp(run.StartedUtc);   // WeTrakr's own clock, read before the pull
                if (plan.Full) state.fullFetchAt = SyncLogic.Stamp(DateTime.UtcNow);
                // the end of the run, not its start: what this run imported is stamped during it and must not be sent back next time
                if (options.syncPush) state.pushedAt = SyncLogic.Stamp(DateTime.UtcNow);
                state.resumeTarget = "";
                state.resumePage = 0;

                run.Message = (run.Reset ? "Everything read again. " : "") + Summary(job, options, !plan.Read);
                _logger.Info("Sync for {0}: {1}", user.Name, run.Message);
            }
            catch (NotConnectedException ex)
            {
                run.Error = ex.Message;
            }
            catch (ApiKeyMissingException ex)
            {
                run.Error = ex.Message;
            }
            catch (QuotaExceededException)
            {
                run.Error = "WeTrakr's daily request limit for this account is used up. The sync continues from where it stopped on its next run.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                run.Error = "The sync was stopped before it finished. It picks up again on its next run.";
                throw;
            }
            catch (WeTrakrApiException ex)
            {
                _logger.Error("Sync for {0} failed: {1} ({2})", user.Name, ex.Message, ex.Code);
                run.Error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Sync for " + user.Name + " failed", ex);
                run.Error = ex.Message;
            }
            finally
            {
                Finish(user, state, run);
            }
        }

        private void MarkStarted(User user, SyncState state, RunInfo run)
        {
            var account = ConfigurationFactory.LoadAuth(_users, user.InternalId).username ?? "";

            // everything remembered belongs to the WeTrakr account it was read under; another login starts over
            if (run.Reset || (!string.IsNullOrEmpty(state.accountUser) && !string.Equals(state.accountUser, account, StringComparison.OrdinalIgnoreCase)))
            {
                state.fullFetchAt = "";
                state.syncedAt = "";
                state.cursorMovies = "";
                state.cursorEpisodes = "";
                state.cursorShows = "";
                state.pushedAt = "";
                state.favoritesSyncedAt = "";
                state.resumeTarget = "";
                state.resumePage = 0;
            }
            state.accountUser = account;

            // marker so a run lost to a restart is reported instead of looking like the previous result
            state.lastRunAt = SyncLogic.Stamp(run.StartedUtc);
            state.lastSummary = InterruptedSummary;
            if (run.Scheduled) state.lastAutoRunAt = state.lastRunAt;   // set at the start so a failing run also waits its interval
            Persist(user, state);
        }

        private void Finish(User user, SyncState state, RunInfo run)
        {
            try
            {
                run.FinishedUtc = DateTime.UtcNow;
                if (run.Error != null) run.Message = "Sync failed: " + run.Error;
                LastFinish[user.InternalId] = run.FinishedUtc.Value;
                state.lastRunAt = SyncLogic.Stamp(run.FinishedUtc.Value);
                state.lastSummary = run.Message;
                Persist(user, state);
            }
            finally
            {
                run.Running = false;
            }
        }

        // ---------------------------------------------------------------- WeTrakr -> Emby

        // reads the watched movies and episodes: everything on a full read, otherwise only rows changed since the last run
        private async Task Read(Job job, RunInfo run, SyncState state, bool full, CancellationToken ct)
        {
            var from = full ? null : SyncLogic.FromDate(state);
            var targets = new[] { "movies", "episodes" };
            var startTarget = full && !string.IsNullOrEmpty(state.resumeTarget) ? state.resumeTarget : "movies";
            var startPage = full && state.resumePage > 1 ? state.resumePage : 1;
            var skipping = startTarget == "episodes";

            foreach (var target in targets)
            {
                if (skipping && target != startTarget) continue;
                skipping = false;

                var page = target == startTarget ? startPage : 1;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    run.Message = (full ? "Reading your whole " : "Reading new ") + target + " history from WeTrakr (page " + page + ")...";
                    var result = await _api.GetWatchedPage(job.User.InternalId, target, page, from, ct).ConfigureAwait(false);

                    foreach (var entry in result.Entries) Absorb(job, target, entry, ct);

                    if (full)
                    {
                        // remembered per page, so a stop by the daily quota continues here instead of starting over
                        state.resumeTarget = target;
                        state.resumePage = page + 1;
                        Persist(job.User, state);
                    }

                    if (page >= result.PageCount) break;
                    page++;
                }

                if (full)
                {
                    state.resumeTarget = target == "movies" ? "episodes" : "";
                    state.resumePage = 1;
                }
            }
        }

        private void Absorb(Job job, string target, TrackedEntry entry, CancellationToken ct)
        {
            var when = SyncLogic.ParseDate(entry.WatchedAt);

            if (target == "movies")
            {
                if (job.Options.syncPush) job.Remote.AddMovie(entry.Ids);
                if (job.Options.syncPull) ApplyMovie(job, entry, when, ct);
                return;
            }

            if (entry.SeasonNumber == null || entry.Number == null) return;
            if (job.Options.syncPush) job.Remote.AddEpisode(entry.Show?.Ids, entry.SeasonNumber.Value, entry.Number.Value, entry.Ids);
            if (job.Options.syncPull) ApplyEpisode(job, entry, when, ct);
        }

        private LibraryIndex Index(Job job, CancellationToken ct)
        {
            return job.Index ?? (job.Index = new LibraryIndex(_library, job.User, ExclusionFilter.For(_library, job.User, job.Options), ct));
        }

        private void ApplyMovie(Job job, TrackedEntry entry, DateTime? when, CancellationToken ct)
        {
            var movie = Index(job, ct).FindMovie(entry.Ids);
            if (movie == null)
            {
                job.MoviesMissing++;
                _logger.Debug("Not in library: movie {0}", entry.Title);
                return;
            }
            if (MarkPlayed(job, movie, when)) job.MoviesMarked++;
        }

        private void ApplyEpisode(Job job, TrackedEntry entry, DateTime? when, CancellationToken ct)
        {
            var index = Index(job, ct);
            var series = index.FindSeries(entry.Show?.Ids);
            var episode = index.FindEpisode(series, entry.SeasonNumber.Value, entry.Number.Value, entry.Ids);
            if (episode == null)
            {
                if (series == null) job.ShowsMissing.Add(entry.Show?.Title ?? entry.Show?.Id?.ToString(CultureInfo.InvariantCulture) ?? "?");
                job.EpisodesMissing++;
                _logger.Debug("Not in library: {0} S{1}E{2}", entry.Show?.Title, entry.SeasonNumber, entry.Number);
                return;
            }
            if (MarkPlayed(job, episode, when)) job.EpisodesMarked++;
        }

        private bool MarkPlayed(Job job, BaseItem item, DateTime? watchedAt)
        {
            if (!job.Seen.Add(item.InternalId)) return false;
            var data = _userData.GetUserData(job.User, item);

            // something the user changed by hand after this run started wins over what the run applies
            if (data.PlaystateLastModified.HasValue && data.PlaystateLastModified.Value.UtcDateTime > job.Started) return false;

            if (data.Played)
            {
                // never touched, unless a reset asks for WeTrakr's date of this very item
                job.AlreadyPlayed++;
                if (!job.Reset || !watchedAt.HasValue) return false;
                job.Refreshed++;
                data.LastPlayedDate = new DateTimeOffset(watchedAt.Value, TimeSpan.Zero);
                data.PlaystateLastModified = DateTimeOffset.UtcNow;
                _userData.SaveUserData(job.User, item, data, UserDataSaveReason.Import, CancellationToken.None);
                return false;
            }

            data.Played = true;
            data.PlayCount = Math.Max(data.PlayCount, 1);
            if (watchedAt.HasValue) data.LastPlayedDate = new DateTimeOffset(watchedAt.Value, TimeSpan.Zero);
            data.PlaybackPositionTicks = 0;
            data.PlaystateLastModified = DateTimeOffset.UtcNow;
            _userData.SaveUserData(job.User, item, data, UserDataSaveReason.Import, CancellationToken.None);
            return true;
        }

        // ---------------------------------------------------------------- WeTrakr -> Emby (favorites)

        // WeTrakr -> Emby only: a favorite toggled in Emby is already sent live by FavoritesSync, so a
        // run only needs to bring favorites the other way in. Like the watched pull, this never
        // unfavorites anything in Emby - an incremental read also returns recent unfavorites (so WeTrakr
        // can tell a caller something changed), and those are simply skipped rather than applied. Its own
        // cursor (favoritesSyncedAt) keeps this independent of the watched-history cursors above.
        private async Task ReadFavorites(Job job, RunInfo run, SyncState state, CancellationToken ct)
        {
            var from = string.IsNullOrEmpty(state.favoritesSyncedAt) ? null : state.favoritesSyncedAt;
            var startedAt = SyncLogic.Stamp(DateTime.UtcNow);
            var index = Index(job, ct);
            var page = 1;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                run.Message = "Reading favorite movies from WeTrakr (page " + page + ")...";
                var result = await _api.GetFavoritesPage(job.User.InternalId, page, from, ct).ConfigureAwait(false);

                foreach (var entry in result.Entries)
                {
                    if (entry.Interactions?.Favorite?.Value != true) continue;
                    var movie = index.FindMovie(entry.Ids);
                    if (movie == null) { job.FavoritesMissing++; continue; }

                    var data = _userData.GetUserData(job.User, movie);
                    if (data.IsFavorite) continue;
                    data.IsFavorite = true;
                    _userData.SaveUserData(job.User, movie, data, UserDataSaveReason.Import, CancellationToken.None);
                    job.FavoritesMarked++;
                }

                if (page >= result.PageCount) break;
                page++;
            }

            state.favoritesSyncedAt = startedAt;
        }

        // ---------------------------------------------------------------- Emby -> WeTrakr

        // Emby -> WeTrakr catch-up: plays that changed in Emby since the last sync and that WeTrakr does not already list.
        // Live scrobbles and mark-as-played have already sent almost everything, so this is a handful of items. It is
        // deliberately not a way to send a whole history: the first run only records where sending starts, and a run
        // that finds more than SyncLogic.MaxPushItems leaves them alone, since that many at once is a glitch in Emby
        // (a rebuilt library, everything suddenly played), not someone watching, and must not be written to WeTrakr.
        private async Task Send(Job job, RunInfo run, SyncState state, bool hasBaseline, CancellationToken ct)
        {
            var since = SyncLogic.ParseDate(state.pushedAt);
            if (SyncLogic.DecidePush(hasBaseline && since.HasValue, 0) == SyncLogic.PushDecision.Baseline)
            {
                job.PushBaselined = true;
                return;
            }

            run.Message = "Looking for new plays in Emby that WeTrakr does not have...";
            var index = Index(job, ct);
            var candidates = new List<WatchedItem>();

            foreach (var movie in index.Movies)
            {
                ct.ThrowIfCancellationRequested();
                var data = _userData.GetUserData(job.User, movie);
                if (!Wanted(data, since)) continue;
                if (job.Remote.HasMovie(ItemMapper.IdsOf(movie))) continue;
                var item = ItemMapper.ToWatched(movie, PlayDate(data));
                if (item != null) candidates.Add(item);
            }

            foreach (var episode in index.Episodes)
            {
                ct.ThrowIfCancellationRequested();
                if (episode.ParentIndexNumber == null || episode.IndexNumber == null) continue;
                var data = _userData.GetUserData(job.User, episode);
                if (!Wanted(data, since)) continue;
                if (job.Remote.HasEpisode(ItemMapper.IdsOf(episode.Series), episode.ParentIndexNumber.Value, episode.IndexNumber.Value, ItemMapper.IdsOf(episode))) continue;
                var item = ItemMapper.ToWatched(episode, PlayDate(data));
                if (item != null) candidates.Add(item);
            }

            var decision = SyncLogic.DecidePush(true, candidates.Count);
            if (decision == SyncLogic.PushDecision.Nothing) return;
            if (decision == SyncLogic.PushDecision.OverLimit)
            {
                job.PushOverLimit = candidates.Count;
                _logger.Warn("Sync for {0}: {1} new plays in Emby is more than the {2} one sync sends, so none were sent (looks like a glitch, not watching)", job.User.Name, candidates.Count, SyncLogic.MaxPushItems);
                return;
            }

            int skipped;
            var body = TrackingBuilder.Build(candidates, out skipped);
            job.Skipped += skipped;

            // one request for everything (the limit is 5,000 items a call); more than that is split, at most one write a second
            var first = true;
            foreach (var part in TrackingBatcher.Split(body))
            {
                ct.ThrowIfCancellationRequested();
                if (!first) await Task.Delay(WritePause, ct).ConfigureAwait(false);
                first = false;

                run.Message = "Sending new plays to WeTrakr (" + (job.MoviesSent + job.EpisodesSent) + " of " + (candidates.Count - skipped) + ")...";
                var result = await _api.AddTracking(job.User.InternalId, part, ct).ConfigureAwait(false);
                job.MoviesSent += part.Movies?.Count ?? 0;
                job.EpisodesSent += TrackingBuilder.Count(part) - (part.Movies?.Count ?? 0);
                job.NotFound += result?.NotFoundCount ?? 0;
            }
        }

        // played, and changed since the last send
        private static bool Wanted(UserItemData data, DateTime? since)
        {
            if (data == null || !data.Played) return false;
            if (!since.HasValue) return true;
            var changed = data.PlaystateLastModified?.UtcDateTime ?? DateTime.MinValue;
            var played = data.LastPlayedDate?.UtcDateTime ?? DateTime.MinValue;
            return changed > since.Value || played > since.Value;
        }

        private static DateTime PlayDate(UserItemData data)
        {
            return data.LastPlayedDate?.UtcDateTime ?? data.PlaystateLastModified?.UtcDateTime ?? DateTime.UtcNow;
        }

        // ---------------------------------------------------------------- plumbing

        private string Refusal(User user, bool manual)
        {
            if (!user.IsGrantedAccessToFeature(Plugin.StaticId)) return "WeTrakr is not enabled for this user.";
            if (!_api.HasApiKey) return "This plugin has no WeTrakr API key yet. An admin can add one on the WeTrakr plugin page.";
            if (!ConfigurationFactory.IsConnected(_users, user.InternalId)) return "Connect to WeTrakr first.";

            var options = ConfigurationFactory.LoadOptions(_users, user.InternalId);
            if (!options.syncPull && !options.syncPush && !options.syncFavorites) return "Turn on importing from WeTrakr, sending new Emby plays, or syncing favorites first.";
            return manual ? Cooldown(user) : null;
        }

        private static string Cooldown(User user)
        {
            DateTime last;
            if (!LastFinish.TryGetValue(user.InternalId, out last)) return null;
            var elapsed = DateTime.UtcNow - last;
            if (elapsed.TotalMinutes >= CooldownMinutes) return null;
            var wait = (int)Math.Ceiling(CooldownMinutes - elapsed.TotalMinutes);
            return "A sync finished a moment ago. Wait " + wait + (wait == 1 ? " minute" : " minutes") + " before running another.";
        }

        // false when another run of this user is active; a finished entry is replaced
        private static bool Register(User user, RunInfo run)
        {
            while (!Runs.TryAdd(user.InternalId, run))
            {
                RunInfo previous;
                if (!Runs.TryGetValue(user.InternalId, out previous)) continue;
                if (previous.Running) return false;
                if (Runs.TryUpdate(user.InternalId, run, previous)) return true;
            }
            return true;
        }

        private void Persist(User user, SyncState state)
        {
            try
            {
                ConfigurationFactory.SaveSync(_users, user.InternalId, state);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Could not save the sync state of " + user.Name, ex);
            }
        }

        private static string Summary(Job job, UserOptions options, bool nothingChanged)
        {
            var parts = new List<string>();
            if (options.syncPull)
            {
                parts.Add(nothingChanged
                    ? "Nothing new on WeTrakr since the last sync."
                    : "Marked " + job.MoviesMarked + " movies and " + job.EpisodesMarked + " episodes as played in Emby (" + job.AlreadyPlayed + " already played"
                      + (job.Reset ? ", " + job.Refreshed + " watch dates refreshed" : "") + "). Not in your Emby library: "
                      + job.MoviesMissing + " movies, " + job.ShowsMissing.Count + " shows, " + job.EpisodesMissing + " episodes.");
            }
            if (options.syncPush)
            {
                if (job.PushBaselined) parts.Add("Sending starts from now: what you watched in Emby before was not sent.");
                else if (job.PushOverLimit > 0) parts.Add("Nothing was sent to WeTrakr: " + job.PushOverLimit + " new plays appeared in Emby at once, more than the " + SyncLogic.MaxPushItems + " a sync sends, which looks like a glitch rather than watching.");
                else parts.Add("Sent " + job.MoviesSent + " movies and " + job.EpisodesSent + " episodes to WeTrakr"
                    + (job.NotFound > 0 ? " (" + job.NotFound + " not found there)" : "")
                    + (job.Skipped > 0 ? " (" + job.Skipped + " skipped: no usable ids)" : "") + ".");
            }
            if (options.syncFavorites)
            {
                parts.Add("Marked " + job.FavoritesMarked + " movies as favorite in Emby from WeTrakr"
                    + (job.FavoritesMissing > 0 ? " (" + job.FavoritesMissing + " not in your library)" : "") + ".");
            }
            return string.Join(" ", parts);
        }

        private static SyncStatus ToStatus(RunInfo run, SyncState state)
        {
            var interrupted = run == null && state.lastSummary == InterruptedSummary;
            var finished = run != null && !run.Running && run.FinishedUtc.HasValue;
            return new SyncStatus
            {
                running = run?.Running ?? false,
                message = run?.Message ?? Stored(state.lastSummary),
                error = run?.Error ?? (interrupted ? InterruptedSummary : null),
                lastRunAt = finished ? SyncLogic.Stamp(run.FinishedUtc.Value) : Stored(state.lastRunAt),
                lastAutoRunAt = Stored(state.lastAutoRunAt)
            };
        }

        private SyncStatus Refused(User user, string error)
        {
            var status = ToStatus(null, ConfigurationFactory.LoadSync(_users, user.InternalId));
            status.error = error;
            return status;
        }

        private static string Stored(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
