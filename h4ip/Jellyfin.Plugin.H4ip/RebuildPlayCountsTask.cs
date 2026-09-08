using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.H4ip.Data;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.H4ip;

/// <summary>
/// Scheduled task that rebuilds the H4ip play counts from Jellyfin user data.
/// </summary>
public class RebuildPlayCountsTask : IScheduledTask
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly H4ipRepository _repository;
    private readonly ILogger<RebuildPlayCountsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RebuildPlayCountsTask"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="repository">The play count repository.</param>
    /// <param name="logger">The logger.</param>
    public RebuildPlayCountsTask(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        H4ipRepository repository,
        ILogger<RebuildPlayCountsTask> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _repository = repository;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Rebuild H4ip play counts";

    /// <inheritdoc />
    public string Key => "H4ipRebuildPlayCounts";

    /// <inheritdoc />
    public string Description => "Recomputes artist/album/track play counts from Jellyfin listening history.";

    /// <inheritdoc />
    public string Category => "H4ip";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        PlayCountBackfill.Run(_userManager, _libraryManager, _userDataManager, _repository, _logger);
        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
            },
        };
    }
}
