using Microsoft.EntityFrameworkCore;
using SocietyHub.Caching;
using SocietyHub.Drives.Api.Domain;
using SocietyHub.Drives.Api.Persistence;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Drives.Api.Features;

/// <summary>
/// Joining a drive, correctly, when sixty people tap at once.
///
/// <para>
/// The failure this exists to prevent is specific and expensive. A drive with a capacity of
/// forty gets forty-three simultaneous enrolments; each request reads "39 joined", each decides
/// there is room, and three residents pay for a service the vendor cannot deliver. The same
/// race one threshold lower silently charges people the wrong slab price.
/// </para>
///
/// <para>
/// A database transaction alone does not fix it — the read that decides "there is room" happens
/// before the write, and two transactions can both pass it. The correctness boundary has to be
/// a lock held across the read <em>and</em> the write, which is what this does.
/// </para>
/// </summary>
public sealed class EnrolmentService
{
    /// <summary>
    /// How long a single enrolment may hold the drive.
    ///
    /// Long enough for a database round trip and a rate-card lookup, short enough that a
    /// crashed process does not freeze a drive for minutes. The lock is re-acquirable, so the
    /// cost of it expiring early is a retry rather than a wrong count.
    /// </summary>
    private static readonly TimeSpan LockDuration = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long to wait for the lock before giving up.
    ///
    /// A resident tapping Join will wait three seconds; past that they assume it is broken and
    /// tap again, which is how one enrolment becomes three. Failing fast with a retryable error
    /// is more honest than a request that eventually succeeds after they have given up.
    /// </summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(3);

    private readonly DrivesDbContext _context;
    private readonly IDistributedLock _lock;
    private readonly ICacheStore _cache;
    private readonly IRateCardReader _rateCards;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EnrolmentService> _logger;

    public EnrolmentService(
        DrivesDbContext context,
        IDistributedLock distributedLock,
        ICacheStore cache,
        IRateCardReader rateCards,
        TimeProvider timeProvider,
        ILogger<EnrolmentService> logger)
    {
        _context = context;
        _lock = distributedLock;
        _cache = cache;
        _rateCards = rateCards;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Enrols a flat, holding the drive against every other concurrent join.
    ///
    /// The whole read-decide-write sequence runs inside the lock. Narrowing it to just the
    /// write would be faster and would reintroduce the exact race it exists to close.
    /// </summary>
    public async Task<Result<DriveEnrolment>> EnrolAsync(
        Guid driveId,
        Guid userId,
        Guid flatId,
        int units,
        CancellationToken cancellationToken = default)
    {
        await using var handle = await _lock.TryAcquireAsync(
            LockResource(driveId), LockDuration, LockWait, cancellationToken);

        if (handle is null)
        {
            // Contention this heavy means a popular drive, which is a good problem — but the
            // caller must be told to retry rather than shown a failure that reads as final.
            _logger.LogWarning(
                "Could not acquire the enrolment lock for drive {DriveId} within {Wait}.",
                driveId, LockWait);

            return Error.Conflict(
                "drive.busy", "This drive is busy right now. Please try again in a moment.");
        }

        var drive = await _context.Drives
            .Include(d => d.Enrolments)
            .FirstOrDefaultAsync(d => d.Id == driveId, cancellationToken);

        if (drive is null)
        {
            return Error.NotFound("drive.not_found", "No such drive.");
        }

        // The count that decides the slab is read here, inside the lock, from the database
        // rather than the cache. The cache is a display optimisation; a price must never be
        // decided from a value that may be stale.
        var unitsAfterJoin = drive.ActiveUnitCount + units;

        var price = await _rateCards.UnitPriceForAsync(
            drive.RateCardId, unitsAfterJoin, cancellationToken);

        if (price.IsFailure)
        {
            return price.Error;
        }

        var enrolment = drive.Enrol(
            userId, flatId, units, price.Value, _timeProvider.GetUtcNow());

        if (enrolment.IsFailure)
        {
            return enrolment.Error;
        }

        await _context.SaveChangesAsync(cancellationToken);

        // Refreshed after the commit, never before. A counter that leads the database shows a
        // resident a drive is fuller than it is, and the next person to join is told there is
        // no room when there is.
        await RefreshCounterAsync(drive, cancellationToken);

        return enrolment.Value;
    }

    public async Task<Result> WithdrawAsync(
        Guid driveId, Guid flatId, CancellationToken cancellationToken = default)
    {
        await using var handle = await _lock.TryAcquireAsync(
            LockResource(driveId), LockDuration, LockWait, cancellationToken);

        if (handle is null)
        {
            return Error.Conflict(
                "drive.busy", "This drive is busy right now. Please try again in a moment.");
        }

        var drive = await _context.Drives
            .Include(d => d.Enrolments)
            .FirstOrDefaultAsync(d => d.Id == driveId, cancellationToken);

        if (drive is null)
        {
            return Error.NotFound("drive.not_found", "No such drive.");
        }

        // Under the same lock as enrolment. A withdrawal racing a join can otherwise take the
        // count past capacity in the gap between them.
        var result = drive.Withdraw(flatId, _timeProvider.GetUtcNow());

        if (result.IsFailure)
        {
            return result;
        }

        await _context.SaveChangesAsync(cancellationToken);
        await RefreshCounterAsync(drive, cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// The live figures a drive screen shows.
    ///
    /// Served from cache, and that is safe precisely because nothing decides anything from it:
    /// a count that is one behind for two seconds costs a resident nothing, while a database
    /// round trip on every poll of a popular drive costs the platform a great deal.
    /// </summary>
    public async Task<DriveCounter?> GetCounterAsync(
        Guid driveId, Guid societyId, CancellationToken cancellationToken = default)
    {
        var cached = await _cache.GetAsync<DriveCounter>(
            CounterKey(societyId, driveId), cancellationToken);

        if (cached is not null)
        {
            return cached;
        }

        var drive = await _context.Drives
            .Include(d => d.Enrolments)
            .FirstOrDefaultAsync(d => d.Id == driveId, cancellationToken);

        if (drive is null)
        {
            return null;
        }

        return await RefreshCounterAsync(drive, cancellationToken);
    }

    private async Task<DriveCounter> RefreshCounterAsync(
        ServiceDrive drive, CancellationToken cancellationToken)
    {
        var counter = new DriveCounter(
            drive.ActiveParticipantCount,
            drive.ActiveUnitCount,
            drive.Quorum,
            drive.Capacity,
            drive.HasReachedQuorum);

        // Short-lived on purpose. Every write refreshes it, so the TTL only bounds how stale a
        // counter can get if a write's cache update failed — which is the case it exists for.
        await _cache.SetAsync(
            CounterKey(drive.SocietyId, drive.Id),
            counter,
            TimeSpan.FromMinutes(2),
            cancellationToken);

        return counter;
    }

    /// <summary>
    /// The lock name.
    ///
    /// Per drive, not per society or globally. A global lock would serialise every enrolment on
    /// the platform through one queue; a per-society lock would make two unrelated drives in
    /// one society block each other. The drive is the smallest thing the invariant is about.
    /// </summary>
    private static string LockResource(Guid driveId) => $"drive:enrolment:{driveId:N}";

    private static CacheKey CounterKey(Guid societyId, Guid driveId) =>
        CacheKey.ForSociety(societyId, "drive-counter", driveId.ToString("N"));
}

/// <summary>What a drive screen needs to render its progress bar.</summary>
public sealed record DriveCounter(
    int Participants, int Units, int Quorum, int? Capacity, bool QuorumReached)
{
    public int? PlacesLeft => Capacity is null ? null : Math.Max(0, Capacity.Value - Participants);

    public int ParticipantsToQuorum => Math.Max(0, Quorum - Participants);
}

/// <summary>
/// Reads a vendor's rate card.
///
/// An interface because the rate card lives in the Vendor service. This is the seam where a
/// cross-service call happens, and keeping it narrow means the pricing rules stay testable
/// without standing up a second service.
/// </summary>
public interface IRateCardReader
{
    Task<Result<long>> UnitPriceForAsync(
        Guid rateCardId, int units, CancellationToken cancellationToken = default);
}
