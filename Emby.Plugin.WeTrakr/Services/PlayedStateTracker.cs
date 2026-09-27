using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Emby.Plugin.WeTrakr.Services
{
    /// <summary>
    /// Drops "marked as played" events that do not change the played state of an item.
    ///
    /// Emby raises UserDataSaved with SaveReason.TogglePlayed every time something writes the
    /// played flag, even when the value is already the same, and other plugins that re-apply a
    /// watched history over a whole library do exactly that. Adding a watched item to WeTrakr
    /// again is another play, so each redundant event would inflate the user's history. This
    /// gate remembers the last played value seen per (user, item) and lets an event through
    /// only when the value flips. The first event for an item always passes: a state never seen
    /// is new information. Real rewatches arrive as playback events and are never gated here.
    /// The memory lives only as long as the server process.
    /// </summary>
    public class PlayedStateTracker
    {
        /// <summary>Entries untouched for longer than this are dropped during a sweep.</summary>
        public static readonly TimeSpan EntryTtl = TimeSpan.FromHours(12);

        /// <summary>Sweep once the map grows past this many entries.</summary>
        private const int SweepThreshold = 10000;

        private readonly ConcurrentDictionary<string, StateEntry> _state = new ConcurrentDictionary<string, StateEntry>();

        /// <summary>Key for an item as seen by one user.</summary>
        public static string KeyFor(long userId, long itemId)
        {
            return userId + "|" + itemId;
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
