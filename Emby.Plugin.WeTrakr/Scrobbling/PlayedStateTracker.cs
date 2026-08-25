using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Emby.Plugin.WeTrakr.Scrobbling
{
    /// <summary>
    /// Suppresses "ItemMarkedPlayed" dispatches that do not actually change the
    /// played state of an item.
    ///
    /// Emby (like Jellyfin) raises UserDataSaved with SaveReason.TogglePlayed
    /// every time something writes the played flag, EVEN IF the value is
    /// already the same. Ported from wetrakr-jf's PlayedStateTracker after a
    /// confirmed production incident on that plugin: a library-wide re-mark
    /// (e.g. from a scheduled sync task re-applying watched state) produced
    /// 35,958 redundant events in 24h from one server, over 351 items, because
    /// every one of them used to be forwarded — the user's watch time grew
    /// without bound. Our own SyncToWeTrakrTask reads Played rather than
    /// writing it, so it doesn't trigger this directly, but any other
    /// mechanism that re-writes played state on this server would.
    ///
    /// This gate remembers the last played value dispatched per (user, item)
    /// and lets an event through only when the value actually flips. The
    /// first event for an item is always dispatched: a state we have never
    /// seen is, as far as this plugin knows, new information.
    ///
    /// Real rewatches are unaffected: they arrive as PlaybackStart /
    /// PlaybackStop, which are never gated here. Un-marking and re-marking an
    /// item also flips the value twice, so both events are dispatched.
    /// </summary>
    public class PlayedStateTracker
    {
        /// <summary>Entries untouched for longer than this are dropped during a sweep.</summary>
        public static readonly TimeSpan EntryTtl = TimeSpan.FromHours(12);

        /// <summary>Sweep once the map grows past this many entries.</summary>
        private const int SweepThreshold = 10000;

        private readonly ConcurrentDictionary<string, StateEntry> _state = new ConcurrentDictionary<string, StateEntry>();

        /// <summary>Key for an item as seen by one user.</summary>
        public static string KeyFor(Guid userId, Guid itemId)
        {
            return userId.ToString("N") + "|" + itemId.ToString("N");
        }

        /// <summary>
        /// Returns true when this played value differs from the last one dispatched
        /// for the same user + item (or when we have never seen the item), recording
        /// the new value. Returns false for a redundant re-mark.
        /// </summary>
        public bool ShouldDispatch(string key, bool played, DateTime nowUtc)
        {
            StateEntry previous;
            if (_state.TryGetValue(key, out previous) && previous.Played == played)
            {
                // Refresh the timestamp so an item that keeps being re-marked does
                // not expire and start passing through again on the next sweep.
                _state[key] = new StateEntry(played, nowUtc);
                return false;
            }

            _state[key] = new StateEntry(played, nowUtc);

            if (_state.Count > SweepThreshold)
            {
                Sweep(nowUtc);
            }

            return true;
        }

        /// <summary>Drops entries older than <see cref="EntryTtl"/>.</summary>
        private void Sweep(DateTime nowUtc)
        {
            var stale = _state
                .Where(kv => nowUtc - kv.Value.LastUtc > EntryTtl)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in stale)
            {
                StateEntry discard;
                _state.TryRemove(key, out discard);
            }
        }

        private struct StateEntry
        {
            public readonly bool Played;
            public readonly DateTime LastUtc;

            public StateEntry(bool played, DateTime lastUtc)
            {
                Played = played;
                LastUtc = lastUtc;
            }
        }
    }
}
