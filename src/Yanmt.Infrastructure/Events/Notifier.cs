using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data.Entities;

namespace Yanmt.Infrastructure.Events;

/// <summary>A committed event, as handed to notifiers. Carries no secrets.</summary>
public sealed record EventNotice(long Id, DateTime TimestampUtc, int? NodeId, StatusSourceType Source, Severity? OldState, Severity NewState, string Message);

/// <summary>
/// The one seam every event goes through once it is stored. v1 has only the in-app
/// implementation; email or webhooks would be added as further implementations.
/// </summary>
public interface INotifier
{
    Task NotifyAsync(IReadOnlyList<EventNotice> events, CancellationToken cancellationToken = default);
}

/// <summary>
/// The in-app notifier: tells open pages that new events exist. The events are already in the
/// database (StatusEngine writes them), so pages simply re-query. Subscribers run on the caller's
/// thread and must marshal to their own context.
/// </summary>
public sealed class InAppNotifier : INotifier
{
    public event Action<IReadOnlyList<EventNotice>>? EventsRecorded;

    public Task NotifyAsync(IReadOnlyList<EventNotice> events, CancellationToken cancellationToken = default)
    {
        foreach (var handler in EventsRecorded?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<IReadOnlyList<EventNotice>>)handler)(events);
            }
            catch (Exception)
            {
                // A disconnected circuit must not stop the others, or the engine.
            }
        }

        return Task.CompletedTask;
    }
}
