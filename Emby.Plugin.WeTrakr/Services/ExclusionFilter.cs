using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Emby.Plugin.WeTrakr.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Emby.Plugin.WeTrakr.Services
{
    /// <summary>
    /// Items in a library the user excluded are never sent to WeTrakr and never changed from
    /// it (home videos, kids' libraries, ...). Libraries are stored by id and matched by their
    /// folders, the same way for live events and for a sync run.
    /// </summary>
    public class ExclusionFilter
    {
        public static readonly ExclusionFilter None = new ExclusionFilter(new string[0]);

        private readonly string[] _roots;

        private ExclusionFilter(string[] roots)
        {
            _roots = roots;
        }

        public bool IsEmpty => _roots.Length == 0;

        public static ExclusionFilter For(ILibraryManager library, User user, UserOptions options)
        {
            if (options?.excludedLibraries == null || options.excludedLibraries.Length == 0) return None;

            var wanted = new HashSet<string>(options.excludedLibraries);
            var roots = library.GetVirtualFolders(user, false, CancellationToken.None)
                .Where(folder => wanted.Contains(folder.ItemId))
                .SelectMany(folder => folder.Locations ?? new string[0])
                .Where(location => !string.IsNullOrWhiteSpace(location))
                .ToArray();
            return roots.Length == 0 ? None : new ExclusionFilter(roots);
        }

        /// <summary>The libraries the user can see, for the page's checkbox list.</summary>
        public static List<VirtualFolderInfo> Libraries(ILibraryManager library, User user)
        {
            return library.GetVirtualFolders(user, false, CancellationToken.None);
        }

        public bool IsExcluded(BaseItem item)
        {
            if (_roots.Length == 0 || item == null || string.IsNullOrEmpty(item.Path)) return false;
            return IsUnderAny(item.Path, _roots);
        }

        public static bool IsUnderAny(string path, IEnumerable<string> roots)
        {
            foreach (var root in roots)
            {
                if (IsUnder(path, root)) return true;
            }
            return false;
        }

        /// <summary>True when path is root or inside it. "/media/tv" does not contain "/media/tv2/x".</summary>
        public static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            var trimmed = root.TrimEnd('/', '\\');
            if (trimmed.Length == 0) return true;   // the filesystem root contains everything
            if (!path.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)) return false;
            return path.Length == trimmed.Length || path[trimmed.Length] == '/' || path[trimmed.Length] == '\\';
        }
    }
}
