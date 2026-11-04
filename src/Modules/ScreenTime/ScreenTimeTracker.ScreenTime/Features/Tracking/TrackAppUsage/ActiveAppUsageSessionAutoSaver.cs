using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppUsageSessions;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Features.Tracking.TrackAppUsage;

public partial class ActiveAppUsageSessionAutoSaver(
    IServiceScopeFactory scopeFactory,
    ActiveAppUsageSessionStore activeSessionStore,
    TimeProvider timeProvider,
    ILogger<ActiveAppUsageSessionAutoSaver> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = await GetAutoSaveIntervalAsync(stoppingToken);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var latestInterval = await GetAutoSaveIntervalAsync(stoppingToken);
                    if (latestInterval != interval)
                    {
                        interval = latestInterval;
                        timer.Period = interval;
                    }

                    var activeSession = activeSessionStore.Current;
                    if (activeSession is null)
                        continue;

                    var now = timeProvider.GetUtcNow();
                    using var scope = scopeFactory.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
                    await context.PersistActiveSessionAsync(activeSession, now, stoppingToken);
                    await context.SaveChangesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    // The session may have been removed or merged by the idle handler or the
                    // optimization worker while this tick was running. The next tick will read
                    // the current state again, so there is nothing to recover here.
                    LogSessionChangedConcurrently(logger, ex);
                }
                catch (Exception ex)
                {
                    LogAutoSaveFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<TimeSpan> GetAutoSaveIntervalAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        ScreenTimeDbContext context =
            scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
        var userSettings = await context.UserSettings.AsNoTracking().SingleAsync(cancellationToken);
        return userSettings.AppTracking.ActiveUsageSessionAutoSaveInterval;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The active app usage session was changed concurrently; skipping this auto-save tick."
    )]
    private static partial void LogSessionChangedConcurrently(
        ILogger logger,
        Exception exception
    );

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failed to auto-save the active app usage session; retrying on the next tick."
    )]
    private static partial void LogAutoSaveFailed(ILogger logger, Exception exception);
}
