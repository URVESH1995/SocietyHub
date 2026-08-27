namespace SocietyHub.Helpdesk.Api.Domain;

/// <summary>What the complaint is about. Drives routing and the SLA window.</summary>
public enum ComplaintCategory
{
    Plumbing = 0,
    Electrical = 1,
    Lift = 2,
    WaterSupply = 3,
    Housekeeping = 4,
    Security = 5,
    Parking = 6,
    CommonArea = 7,
    Noise = 8,
    Other = 9,
}

public enum ComplaintPriority
{
    Low = 0,
    Normal = 1,
    High = 2,

    /// <summary>Someone is trapped, flooded or without power. Hours matter, not days.</summary>
    Emergency = 3,
}

public enum ComplaintStatus
{
    Open = 0,
    Assigned = 1,
    InProgress = 2,
    Resolved = 3,

    /// <summary>Resident confirmed the fix, or the auto-close window elapsed.</summary>
    Closed = 4,

    /// <summary>Not actionable — a duplicate, or outside the society's responsibility.</summary>
    Rejected = 5,
}

/// <summary>
/// Turns a category and priority into a deadline.
///
/// The product promise is "resolved in 24 hours", and the temptation is to implement that as
/// <c>raisedAt.AddHours(24)</c>. That is wrong in a way residents notice immediately.
///
/// A complaint raised at 11pm would be due at 11pm the following night, having consumed most
/// of its window while nobody was working. Meanwhile a plumber cannot be sent to a flat at
/// 2am, so the society could not have met it even in principle. The clock therefore only runs
/// during the society's working hours, in the society's own timezone — which is what makes
/// the promise both honest and achievable.
///
/// Emergencies are the exception: a stuck lift does not wait for morning, so those run on
/// wall-clock time.
/// </summary>
public static class SlaPolicy
{
    /// <summary>Working hours in society-local time. Maintenance staff are on site 8am–8pm.</summary>
    public static readonly TimeOnly WorkdayStart = new(8, 0);

    public static readonly TimeOnly WorkdayEnd = new(20, 0);

    private static readonly TimeSpan WorkdayLength = WorkdayEnd - WorkdayStart;

    /// <summary>
    /// Working hours allowed per priority. The headline promise is Normal at 24 working hours,
    /// which is two full days on site.
    /// </summary>
    public static TimeSpan BudgetFor(ComplaintPriority priority) => priority switch
    {
        ComplaintPriority.Emergency => TimeSpan.FromHours(2),
        ComplaintPriority.High => TimeSpan.FromHours(8),
        ComplaintPriority.Normal => TimeSpan.FromHours(24),
        ComplaintPriority.Low => TimeSpan.FromHours(72),
        _ => TimeSpan.FromHours(24),
    };

    /// <summary>
    /// Some categories are urgent regardless of what the resident selected.
    ///
    /// A resident reporting a stuck lift as "Normal" is describing an emergency and does not
    /// know the platform's vocabulary. Escalating on their behalf is better than holding them
    /// to a category they picked from a dropdown while stressed.
    /// </summary>
    public static ComplaintPriority EffectivePriority(
        ComplaintCategory category,
        ComplaintPriority requested) => category switch
    {
        ComplaintCategory.Lift when requested < ComplaintPriority.High => ComplaintPriority.High,
        ComplaintCategory.Electrical when requested < ComplaintPriority.High => ComplaintPriority.High,
        ComplaintCategory.Security when requested < ComplaintPriority.High => ComplaintPriority.High,
        _ => requested,
    };

    /// <summary>
    /// Computes the deadline by walking the working-hours budget forward from
    /// <paramref name="raisedAtUtc"/>, in <paramref name="societyTimeZone"/>.
    /// </summary>
    public static DateTimeOffset CalculateDueAt(
        DateTimeOffset raisedAtUtc,
        ComplaintPriority priority,
        TimeZoneInfo societyTimeZone)
    {
        var budget = BudgetFor(priority);

        // An emergency ignores working hours entirely. Nobody trapped in a lift cares that
        // it is 3am, and a two-hour promise measured in working hours would mean "tomorrow".
        if (priority == ComplaintPriority.Emergency)
        {
            return raisedAtUtc.Add(budget);
        }

        var local = TimeZoneInfo.ConvertTime(raisedAtUtc, societyTimeZone);
        var cursor = local;

        // Raised outside working hours: the clock starts at the next opening rather than
        // burning the budget overnight.
        if (TimeOnly.FromDateTime(cursor.DateTime) < WorkdayStart)
        {
            cursor = OpeningOn(cursor, cursor.Date);
        }
        else if (TimeOnly.FromDateTime(cursor.DateTime) >= WorkdayEnd)
        {
            cursor = OpeningOn(cursor, cursor.Date.AddDays(1));
        }

        var remaining = budget;

        // Walk day by day, consuming what is left of each working day. Bounded at 365 days so
        // a mistaken budget can never spin here.
        for (var guard = 0; guard < 365 && remaining > TimeSpan.Zero; guard++)
        {
            var closingToday = OpeningOn(cursor, cursor.Date).Add(WorkdayLength);
            var availableToday = closingToday - cursor;

            if (remaining <= availableToday)
            {
                cursor = cursor.Add(remaining);
                remaining = TimeSpan.Zero;
                break;
            }

            remaining -= availableToday;
            cursor = OpeningOn(cursor, cursor.Date.AddDays(1));
        }

        return TimeZoneInfo.ConvertTime(cursor, TimeZoneInfo.Utc);
    }

    /// <summary>
    /// How much of the budget is left, in working hours. Negative once breached.
    ///
    /// Used to warn before a breach rather than only reporting one after the fact — a
    /// complaint at 80% consumed is still saveable.
    /// </summary>
    public static TimeSpan RemainingBudget(DateTimeOffset dueAtUtc, DateTimeOffset nowUtc) =>
        dueAtUtc - nowUtc;

    private static DateTimeOffset OpeningOn(DateTimeOffset reference, DateTime date) =>
        new(
            date.Year,
            date.Month,
            date.Day,
            WorkdayStart.Hour,
            WorkdayStart.Minute,
            0,
            reference.Offset);
}
