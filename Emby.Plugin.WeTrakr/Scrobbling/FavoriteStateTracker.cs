using System.Collections.Concurrent;

namespace Emby.Plugin.WeTrakr.Scrobbling
{
    /// <summary>
    /// Tracks the last-known IsFavorite state per (user, item) so a genuine
    /// favorite/unfavorite toggle can be detected regardless of which
    /// UserDataSaveReason Emby happens to report it under. Emby's
    /// UserDataSaveReason enum has no dedicated "favorite toggled" value, and
    /// UserDataSaved fires on unrelated saves too (e.g. every playback
    /// progress tick carries the item's current IsFavorite value even when it
    /// hasn't changed) — comparing against the last-seen value is the only
    /// reliable way to catch just the real transitions.
    ///
    /// Unbounded for the process lifetime (one entry per user+item ever
    /// touched), same trade-off as other in-memory trackers in this plugin —
    /// negligible in practice for a single Emby server's scale.
    /// </summary>
    public class FavoriteStateTracker
    {
        private readonly ConcurrentDictionary<string, bool> _state = new ConcurrentDictionary<string, bool>();

        /// <summary>
        /// Records the current favorite state and returns true if it differs
        /// from the last-seen value for this key. The first observation of a
        /// key only counts as "changed" if it's currently favorited — an
        /// item seen for the first time (e.g. after a server restart) as
        /// not-favorited isn't treated as a transition.
        /// </summary>
        public bool HasChanged(string key, bool isFavorite)
        {
            bool previous;
            var seen = _state.TryGetValue(key, out previous);
            _state[key] = isFavorite;
            return seen ? previous != isFavorite : isFavorite;
        }
    }
}
