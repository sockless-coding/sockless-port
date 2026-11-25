using Microsoft.AspNetCore.SignalR;
using Sockless.Port.Work;

namespace Sockless.Port.Web.Hubs;

/// <summary>
/// Tells every browser that the list of background work changed, at most every
/// <see cref="Interval"/>: a pull reports progress many times a second. The message carries
/// nothing; browsers fetch the list (<c>/tasks?handler=Tray</c>), which shows each user only what
/// they may see.
/// </summary>
internal sealed partial class WorkBroadcaster(WorkTracker work, IHubContext<EngineHub> hub, ILogger<WorkBroadcaster> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(750);

    private readonly SemaphoreSlim _signal = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        work.Changed += OnChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(stoppingToken);
                try
                {
                    await hub.Clients.All.SendAsync("work", stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailed(logger, ex.Message);
                }
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            work.Changed -= OnChanged;
        }
    }

    private void OnChanged()
    {
        // One pending signal is enough: changes during the wait are sent with it.
        try
        {
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Couldn't tell browsers the background work changed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
