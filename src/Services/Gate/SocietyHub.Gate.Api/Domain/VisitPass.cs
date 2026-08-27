using System.Security.Cryptography;
using System.Text;
using SocietyHub.SharedKernel.Primitives;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Gate.Api.Domain;

/// <summary>Why someone is at the gate. Drives notification urgency and retention.</summary>
public enum VisitorType
{
    /// <summary>A guest the resident is expecting.</summary>
    Guest = 0,

    /// <summary>Courier or food delivery. Usually brief, often left at the gate.</summary>
    Delivery = 1,

    /// <summary>Taxi or ride-hail, typically waiting rather than entering.</summary>
    Cab = 2,

    /// <summary>A service provider — plumber, electrician, appliance technician.</summary>
    Vendor = 3,

    /// <summary>Domestic help with a standing arrangement. Tracked via attendance, not passes.</summary>
    Staff = 4,
}

public enum PassStatus
{
    /// <summary>Created by a resident, not yet used.</summary>
    Pending = 0,

    /// <summary>Visitor is inside.</summary>
    CheckedIn = 1,

    /// <summary>Visit finished.</summary>
    CheckedOut = 2,

    /// <summary>The resident cancelled before arrival.</summary>
    Cancelled = 3,

    /// <summary>The window passed without an arrival.</summary>
    Expired = 4,

    /// <summary>The resident refused the visitor at the gate.</summary>
    Denied = 5,
}

/// <summary>
/// Permission for one visitor to enter, once, within a window.
///
/// The pass exists so the guard is not the decision-maker. Without one, admitting a visitor
/// means a guard phoning a flat and taking somebody's word — which is slow at 7pm and
/// unauditable afterwards. With one, the resident decided in advance and the gate merely
/// checks a code.
///
/// The code is a shared secret between the resident, the visitor and the gate, so it is
/// stored hashed and compared in fixed time, exactly like the sign-in OTP. A visitor who can
/// read a pass code from a database row could walk into a building.
/// </summary>
public sealed class VisitPass : AggregateRoot, ITenantScoped, IAuditable
{
    /// <summary>
    /// Six digits, matching the sign-in OTP. Residents read it aloud over the phone and
    /// visitors retype it at a gate, so length is a usability constraint before a security one
    /// — the attempt cap below is what actually makes it safe.
    /// </summary>
    public const int CodeLength = 6;

    /// <summary>
    /// Guards mistype, and visitors misremember. Five is generous enough not to strand a
    /// legitimate guest and far too few to walk a six-digit space.
    /// </summary>
    public const int MaxVerificationAttempts = 5;

    public VisitPass(
        Guid id,
        Guid societyId,
        Guid flatId,
        Guid authorisedByUserId,
        string visitorName,
        string? visitorPhone,
        VisitorType visitorType,
        DateTimeOffset validFromUtc,
        DateTimeOffset validUntilUtc) : base(id)
    {
        SocietyId = societyId;
        FlatId = flatId;
        AuthorisedByUserId = authorisedByUserId;
        VisitorName = visitorName;
        VisitorPhone = visitorPhone;
        VisitorType = visitorType;
        ValidFromUtc = validFromUtc;
        ValidUntilUtc = validUntilUtc;
        Status = PassStatus.Pending;
    }

    private VisitPass()
    {
    }

    public Guid SocietyId { get; private set; }

    public Guid FlatId { get; private set; }

    /// <summary>The resident who authorised the visit. Answers "who let them in".</summary>
    public Guid AuthorisedByUserId { get; private set; }

    public string VisitorName { get; private set; } = string.Empty;

    /// <summary>Optional: a courier at the gate may not give one.</summary>
    public string? VisitorPhone { get; private set; }

    public VisitorType VisitorType { get; private set; }

    public PassStatus Status { get; private set; }

    public DateTimeOffset ValidFromUtc { get; private set; }

    public DateTimeOffset ValidUntilUtc { get; private set; }

    public string CodeHash { get; private set; } = string.Empty;

    public string CodeSalt { get; private set; } = string.Empty;

    public int VerificationAttempts { get; private set; }

    public DateTimeOffset? CheckedInAtUtc { get; private set; }

    public DateTimeOffset? CheckedOutAtUtc { get; private set; }

    public Guid? CheckedInByGuardId { get; private set; }

    /// <summary>Vehicle the visitor arrived in, captured at the gate when there is one.</summary>
    public string? VehicleNumber { get; set; }

    /// <summary>Blob key for the gate photo. Never a public URL — see <c>VisitorPhotoService</c>.</summary>
    public string? PhotoBlobKey { get; set; }

    /// <summary>How many people the pass admits. Compared against what the camera counts.</summary>
    public int ExpectedPersonCount { get; private set; } = 1;

    public string? Purpose { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public bool IsOpen(DateTimeOffset now) =>
        Status == PassStatus.Pending && now >= ValidFromUtc && now <= ValidUntilUtc;

    /// <summary>
    /// Issues a pass and returns the code in readable form once, for delivery to the visitor.
    /// </summary>
    public static (VisitPass Pass, string Code) Issue(
        Guid societyId,
        Guid flatId,
        Guid authorisedByUserId,
        string visitorName,
        string? visitorPhone,
        VisitorType visitorType,
        DateTimeOffset validFromUtc,
        DateTimeOffset validUntilUtc,
        int expectedPersonCount = 1)
    {
        var pass = new VisitPass(
            Guid.CreateVersion7(),
            societyId,
            flatId,
            authorisedByUserId,
            visitorName,
            visitorPhone,
            visitorType,
            validFromUtc,
            validUntilUtc)
        {
            ExpectedPersonCount = Math.Max(1, expectedPersonCount),
        };

        // Cryptographic, not Random. A predictable gate code is a door that opens for anyone
        // who can count.
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString($"D{CodeLength}");
        pass.CodeSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        pass.CodeHash = HashCode(code, pass.CodeSalt);

        return (pass, code);
    }

    /// <summary>
    /// Verifies a code at the gate and admits the visitor.
    ///
    /// Counts the attempt whether or not it matches, so abandoning a request mid-way cannot
    /// dodge the cap.
    /// </summary>
    public Result CheckIn(string submittedCode, Guid guardId, DateTimeOffset now)
    {
        if (Status == PassStatus.Cancelled)
        {
            return Error.Conflict("Pass.Cancelled", "That pass was cancelled by the resident.");
        }

        if (Status is PassStatus.CheckedIn or PassStatus.CheckedOut)
        {
            // A pass admits one visit. Reuse would let a code shared once become a standing
            // key to the building.
            return Error.Conflict("Pass.AlreadyUsed", "That pass has already been used.");
        }

        if (now < ValidFromUtc)
        {
            return Error.Conflict("Pass.NotYetValid", "That pass is not valid yet.");
        }

        if (now > ValidUntilUtc)
        {
            Status = PassStatus.Expired;
            return Error.Conflict("Pass.Expired", "That pass has expired.");
        }

        if (VerificationAttempts >= MaxVerificationAttempts)
        {
            return Error.Conflict("Pass.TooManyAttempts", "Too many incorrect codes.");
        }

        VerificationAttempts++;

        var matches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(HashCode(submittedCode, CodeSalt)),
            Encoding.UTF8.GetBytes(CodeHash));

        if (!matches)
        {
            return Error.Unauthorized("Pass.InvalidCode", "That code is not valid.");
        }

        Status = PassStatus.CheckedIn;
        CheckedInAtUtc = now;
        CheckedInByGuardId = guardId;

        return Result.Success();
    }

    /// <summary>
    /// Records the visitor leaving.
    ///
    /// Kept separate from check-in rather than inferred from a timer, because "who is still
    /// inside the building" is the question that matters during a fire.
    /// </summary>
    public Result CheckOut(DateTimeOffset now)
    {
        if (Status != PassStatus.CheckedIn)
        {
            return Error.Conflict("Pass.NotCheckedIn", "That visitor is not currently inside.");
        }

        Status = PassStatus.CheckedOut;
        CheckedOutAtUtc = now;

        return Result.Success();
    }

    /// <summary>The resident changed their mind before the visitor arrived.</summary>
    public Result Cancel()
    {
        if (Status != PassStatus.Pending)
        {
            return Error.Conflict("Pass.NotPending", "Only an unused pass can be cancelled.");
        }

        Status = PassStatus.Cancelled;
        return Result.Success();
    }

    /// <summary>The resident refused the visitor while they were standing at the gate.</summary>
    public Result Deny()
    {
        if (Status is PassStatus.CheckedIn or PassStatus.CheckedOut)
        {
            return Error.Conflict("Pass.AlreadyUsed", "That visitor has already entered.");
        }

        Status = PassStatus.Denied;
        return Result.Success();
    }

    private static string HashCode(string code, string salt) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(salt + ':' + code)));
}
