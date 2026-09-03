using SocietyHub.SharedKernel.Primitives;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Vendor.Api.Domain;

/// <summary>
/// A person a vendor sends into somebody's home.
///
/// Modelled separately from the vendor because that is the unit a society actually cares
/// about. A committee approving a drive is not letting a company in, it is letting named
/// individuals through the gate — and the gate log records the person, not the logo on the van.
/// </summary>
public sealed class Technician : AggregateRoot, IAuditable
{
    private Technician() { }

    public Technician(
        Guid id,
        Guid vendorId,
        string fullName,
        string phone,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        VendorId = vendorId;
        FullName = fullName;
        Phone = phone;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid VendorId { get; private set; }

    public string FullName { get; private set; } = string.Empty;

    public string Phone { get; private set; } = string.Empty;

    /// <summary>
    /// Whether police verification is on file and current.
    ///
    /// Held as a date rather than a flag because these expire, and a verification from 2019 is
    /// not a verification. A society is entitled to see that this is stale.
    /// </summary>
    public DateTimeOffset? PoliceVerifiedUntilUtc { get; private set; }

    public bool IsActive { get; private set; } = true;

    /// <summary>
    /// Jobs this person can take in one day.
    ///
    /// Capacity lives on the technician rather than the vendor because it is a property of a
    /// human with travel time between flats, and a scheduler that treats a vendor as an
    /// undifferentiated pool will cheerfully book forty jobs for one person.
    /// </summary>
    public int DailyJobCapacity { get; private set; } = 6;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    /// <summary>
    /// Whether this technician may be assigned work on a given day.
    ///
    /// Takes the date rather than reading the clock, so a schedule built for next month is
    /// checked against the verification status that will apply then — not the one that
    /// happens to be true today.
    /// </summary>
    public bool CanWorkOn(DateTimeOffset date) =>
        IsActive
        && PoliceVerifiedUntilUtc is not null
        && PoliceVerifiedUntilUtc > date;

    public Result RecordPoliceVerification(DateTimeOffset validUntilUtc, DateTimeOffset nowUtc)
    {
        if (validUntilUtc <= nowUtc)
        {
            return Error.Validation(
                "technician.verification_expired",
                "A verification that has already expired cannot be recorded as current.");
        }

        PoliceVerifiedUntilUtc = validUntilUtc;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    public Result SetDailyCapacity(int jobs, DateTimeOffset nowUtc)
    {
        if (jobs is < 1 or > 20)
        {
            // Twenty is already implausible for anything involving travel; beyond it the
            // number is a typo, and an unchecked one lets a vendor accept a drive they
            // physically cannot staff.
            return Error.Validation(
                "technician.bad_capacity", "Daily capacity must be between 1 and 20 jobs.");
        }

        DailyJobCapacity = jobs;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    public void Deactivate(DateTimeOffset nowUtc)
    {
        IsActive = false;
        ModifiedAtUtc = nowUtc;
    }
}

/// <summary>
/// A vendor's track record, rebuilt from completed jobs.
///
/// Kept as a maintained projection rather than computed on demand. A society browsing vendors
/// asks for this on every page load, and recomputing an average over every job a vendor has
/// ever done, per request, is the query that quietly becomes the platform's slowest.
/// </summary>
public sealed class VendorPerformance : Entity
{
    private VendorPerformance() { }

    public VendorPerformance(Guid id, Guid vendorId)
        : base(id) => VendorId = vendorId;

    public Guid VendorId { get; private set; }

    public int JobsCompleted { get; private set; }

    public int JobsCancelledByVendor { get; private set; }

    public int JobsNoShow { get; private set; }

    /// <summary>Sum of ratings, kept alongside the count so the average stays exact.</summary>
    public int RatingTotal { get; private set; }

    public int RatingCount { get; private set; }

    public DateTimeOffset? LastJobAtUtc { get; private set; }

    /// <summary>
    /// The average, or null when nobody has rated them.
    ///
    /// Null rather than zero, and the distinction matters commercially: a new vendor with no
    /// ratings displayed as 0.0 is unhirable forever, and no vendor would join a platform that
    /// did that to them.
    /// </summary>
    public decimal? AverageRating =>
        RatingCount == 0 ? null : Math.Round((decimal)RatingTotal / RatingCount, 2);

    /// <summary>
    /// Share of accepted jobs actually completed.
    ///
    /// A no-show is counted against the vendor separately from a cancellation, because a
    /// cancellation with notice lets a society rebook and a no-show wastes a resident's
    /// morning. A single reliability number that treated them alike would hide the difference
    /// that residents care most about.
    /// </summary>
    public decimal? ReliabilityPercent
    {
        get
        {
            var accepted = JobsCompleted + JobsCancelledByVendor + JobsNoShow;

            return accepted == 0 ? null : Math.Round(JobsCompleted * 100m / accepted, 1);
        }
    }

    public void RecordCompletion(int? rating, DateTimeOffset completedAtUtc)
    {
        JobsCompleted++;
        LastJobAtUtc = completedAtUtc;

        // Rating is optional. Requiring one would either block completion or produce a wall of
        // fives from people tapping through, and a rating nobody meant is worse than none.
        if (rating is >= 1 and <= 5)
        {
            RatingTotal += rating.Value;
            RatingCount++;
        }
    }

    public void RecordCancellation(DateTimeOffset atUtc)
    {
        JobsCancelledByVendor++;
        LastJobAtUtc = atUtc;
    }

    public void RecordNoShow(DateTimeOffset atUtc)
    {
        JobsNoShow++;
        LastJobAtUtc = atUtc;
    }
}
