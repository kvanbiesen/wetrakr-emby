using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.WeTrakr.Configuration;
using Emby.Plugin.WeTrakr.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugin.WeTrakr.ScheduledTasks
{
    /// <summary>
    /// Dashboard > Scheduled Tasks > "Sync WeTrakr watched history". Ticks hourly and only does
    /// something for users who switched on automatic sync and whose own interval has passed;
    /// a user's page picks that interval (every hour up to every week). It can also be run on
    /// demand from the dashboard.
    /// </summary>
    public class WeTrakrSyncTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly HistorySync _sync;
        private readonly IUserManager _users;
        private readonly ILogger _logger;

        public WeTrakrSyncTask(HistorySync sync, IUserManager users, ILogManager logManager)
        {
            _sync = sync;
            _users = users;
            _logger = logManager.GetLogger("WeTrakr Sync");
        }

        public string Name => "Sync WeTrakr watched history";
        public string Description => "For users who turned on automatic sync on their WeTrakr page: marks what they watched on WeTrakr as played in Emby and/or sends their Emby watched history to WeTrakr, as they chose. Runs hourly; each user picks how often their own account is synced.";
        public string Category => "WeTrakr";
        public string Key => "WeTrakrHistorySync";
        public bool IsEnabled => true;
        public bool IsHidden => false;
        public bool IsLogged => true;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[] { new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromHours(1).Ticks } };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            // only users who ever saved their WeTrakr options can have automatic sync on
            long[] ids;
            try
            {
                ids = _users.GetUsersWithSettings(ConfigurationFactory.OptionsKey);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Could not list users for the WeTrakr sync task", ex);
                return;
            }

            for (var i = 0; i < ids.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var user = _users.GetUserById(ids[i]);
                if (user != null && !user.Policy.IsDisabled)
                {
                    try
                    {
                        await _sync.RunScheduled(user, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("Automatic sync for " + user.Name + " failed", ex);
                    }
                }
                progress.Report((i + 1) * 100.0 / ids.Length);
            }
        }
    }
}
