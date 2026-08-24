namespace SocietyHub.SharedKernel.Biometrics;

/// <summary>
/// Who a stored face template belongs to. Drives retention, the lawful basis recorded
/// against it, and how an erasure request is honoured — the three things that differ most
/// between a resident who opted in and a delivery rider who simply arrived.
/// </summary>
public enum BiometricSubjectType
{
    /// <summary>Enrolled voluntarily, revocable at any time.</summary>
    Resident = 0,

    /// <summary>Domestic help, drivers, housekeeping. Recurring, and enrolled with notice.</summary>
    Staff = 1,

    /// <summary>Guests, deliveries, cabs. Captured at the gate, retained briefly.</summary>
    Visitor = 2,

    /// <summary>A person the society has flagged. Matches alert a guard; they never act alone.</summary>
    Watchlist = 3,
}

/// <summary>
/// How long a template of each kind may be kept, and what happens when the clock runs out.
///
/// Retention is per subject type rather than a single platform-wide number, because the
/// justification differs. A resident's template is kept while they choose to be enrolled. A
/// visitor's exists to answer "who came to this building last month" and has no purpose
/// beyond that window, so it is deleted on a timer whether or not anyone remembers to ask.
/// </summary>
public static class BiometricRetention
{
    /// <summary>Kept while enrolment stands. Revoking erases within minutes.</summary>
    public static readonly TimeSpan? Resident = null;

    /// <summary>Kept while the engagement is active, then a short tail for dispute resolution.</summary>
    public static readonly TimeSpan Staff = TimeSpan.FromDays(30);

    /// <summary>
    /// Short by default and configurable downward but never upward. A visitor never asked to
    /// be in this system, so the window is the shortest one that still serves the security
    /// purpose the capture was justified by.
    /// </summary>
    public static readonly TimeSpan Visitor = TimeSpan.FromDays(30);

    /// <summary>Kept while the flag stands, subject to periodic committee review.</summary>
    public static readonly TimeSpan Watchlist = TimeSpan.FromDays(180);

    public static TimeSpan? For(BiometricSubjectType subjectType) => subjectType switch
    {
        BiometricSubjectType.Resident => Resident,
        BiometricSubjectType.Staff => Staff,
        BiometricSubjectType.Visitor => Visitor,
        BiometricSubjectType.Watchlist => Watchlist,
        _ => Visitor,
    };
}

/// <summary>
/// Why this person's face is being processed, recorded against every template.
///
/// DPDP requires a stated basis, and the honest answer differs by subject: a resident gives
/// consent, a visitor is processed under the society's security interest with notice at the
/// point of capture. Storing which one applies is what makes an erasure request answerable
/// later without guessing.
/// </summary>
public enum BiometricLawfulBasis
{
    /// <summary>Freely given, specific, revocable. The only basis valid for a resident.</summary>
    Consent = 0,

    /// <summary>Notice given at the point of capture; no affirmative consent obtained.</summary>
    NoticedSecurityInterest = 1,

    /// <summary>Recorded in connection with a specific reported incident.</summary>
    IncidentInvestigation = 2,
}
