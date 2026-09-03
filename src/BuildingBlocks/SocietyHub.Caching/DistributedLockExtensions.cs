namespace SocietyHub.Caching;

public static class DistributedLockExtensions
{
    /// <summary>
    /// How long to sleep between attempts.
    ///
    /// Short enough that an uncontended handover is not noticeably slower than a single try,
    /// long enough that fifty waiters do not turn one busy lock into a Redis flood. The jitter
    /// below matters more than the interval: without it, every waiter released by one unlock
    /// retries on the same tick and one of them wins while the rest add load.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Tries to take a lock, waiting up to <paramref name="wait"/> for the holder to release.
    ///
    /// <para>
    /// The base <c>TryAcquireAsync</c> is a single attempt, which is right for background work
    /// that can simply come back later. It is wrong for anything a person is waiting on: sixty
    /// residents tapping Join on the same drive would see fifty-nine immediate failures, and
    /// every one of them would tap again.
    /// </para>
    ///
    /// <para>
    /// Returns null on timeout rather than throwing, because a caller has to distinguish
    /// "busy, try again" from "broken" and an exception makes both look the same.
    /// </para>
    /// </summary>
    public static async Task<ILockHandle?> TryAcquireAsync(
        this IDistributedLock distributedLock,
        string resource,
        TimeSpan ttl,
        TimeSpan wait,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(distributedLock);

        var deadline = DateTimeOffset.UtcNow + wait;

        while (true)
        {
            var handle = await distributedLock.TryAcquireAsync(resource, ttl, cancellationToken);

            if (handle is not null)
            {
                return handle;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return null;
            }

            // Jittered, so waiters released together do not retry in lockstep. Without it a
            // contended lock produces a thundering herd on every release — the classic failure
            // of a naive poll loop, and one that only shows up under the load it was built for.
            var jitter = Random.Shared.Next(0, (int)PollInterval.TotalMilliseconds);

            await Task.Delay(
                PollInterval + TimeSpan.FromMilliseconds(jitter), cancellationToken);
        }
    }
}
