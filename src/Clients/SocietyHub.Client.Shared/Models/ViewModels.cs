namespace SocietyHub.Client.Shared.Models;

/// <summary>
/// What the clients receive.
///
/// Deliberately separate from the server's domain types and not shared with them. A shared
/// assembly across the wire looks like it saves work and then quietly couples six services to
/// three apps that ship on different cycles — the mobile build from eighteen months ago has to
/// keep deserialising today's response, which is only possible if these are allowed to lag.
///
/// Every field is nullable-tolerant or defaulted for the same reason: an older app must ignore
/// what it does not know rather than fail to parse it.
/// </summary>
public sealed record VisitorView
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Phone { get; init; }

    public string? Purpose { get; init; }

    /// <summary>Expected, CheckedIn, CheckedOut, Denied.</summary>
    public string Status { get; init; } = string.Empty;

    public DateTimeOffset? ExpectedAtUtc { get; init; }

    public DateTimeOffset? CheckedInAtUtc { get; init; }

    public string? PhotoUrl { get; init; }

    public string? PassCode { get; init; }
}

public sealed record GatePassView
{
    public Guid Id { get; init; }

    public string PassCode { get; init; } = string.Empty;

    public DateTimeOffset ValidUntilUtc { get; init; }
}

public sealed record ComplaintView
{
    public Guid Id { get; init; }

    public string TicketNumber { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string Priority { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public DateTimeOffset RaisedAtUtc { get; init; }

    public DateTimeOffset SlaDueAtUtc { get; init; }

    public bool IsBreached { get; init; }

    public string? AssignedToName { get; init; }

    /// <summary>
    /// Whether the deadline has passed. Computed on the client from the server's timestamp
    /// rather than trusted from a flag, so a complaint does not stay green on screen for
    /// however long the page has been open.
    /// </summary>
    public bool IsOverdue(DateTimeOffset nowUtc) =>
        IsBreached || (SlaDueAtUtc < nowUtc && Status is not "Resolved" and not "Closed");
}

public sealed record NoticeView
{
    public Guid Id { get; init; }

    public string Category { get; init; } = string.Empty;

    /// <summary>Already resolved to the caller's language by the server.</summary>
    public string Title { get; init; } = string.Empty;

    public string Body { get; init; } = string.Empty;

    public string AuthorName { get; init; } = string.Empty;

    public bool IsPinned { get; init; }

    public bool RequiresAcknowledgement { get; init; }

    public bool HasAcknowledged { get; init; }

    public DateTimeOffset? PublishedAtUtc { get; init; }

    public DateTimeOffset? ExpiresAtUtc { get; init; }

    public bool NeedsMyAcknowledgement => RequiresAcknowledgement && !HasAcknowledged;
}

public sealed record PollView
{
    public Guid Id { get; init; }

    public string Kind { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string QuestionEn { get; init; } = string.Empty;

    public string? QuestionHi { get; init; }

    public DateTimeOffset? ClosesAtUtc { get; init; }

    public IReadOnlyList<PollOptionView> Options { get; init; } = [];

    /// <summary>
    /// The question in the reader's language. Unlike a notice, a poll ships both and the
    /// client chooses — a poll's wording is the thing being voted on, so both versions have
    /// to remain inspectable side by side if the result is ever disputed.
    /// </summary>
    public string QuestionFor(string languageTag) =>
        languageTag.StartsWith("hi", StringComparison.OrdinalIgnoreCase) && QuestionHi is not null
            ? QuestionHi
            : QuestionEn;
}

public sealed record PollOptionView
{
    public Guid Id { get; init; }

    public string LabelEn { get; init; } = string.Empty;

    public string? LabelHi { get; init; }

    public string LabelFor(string languageTag) =>
        languageTag.StartsWith("hi", StringComparison.OrdinalIgnoreCase) && LabelHi is not null
            ? LabelHi
            : LabelEn;
}

public sealed record FeatureManifestView
{
    public Guid SocietyId { get; init; }

    public IReadOnlyList<string> Features { get; init; } = [];

    public string Plan { get; init; } = "Basic";

    public DateTimeOffset RetrievedAtUtc { get; init; }

    public bool Has(string featureKey) =>
        Features.Contains(featureKey, StringComparer.OrdinalIgnoreCase);
}

public sealed record TokenPairView
{
    public string AccessToken { get; init; } = string.Empty;

    public string RefreshToken { get; init; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; init; }
}

/// <summary>RFC 7807, plus the <c>code</c> extension every SocietyHub error carries.</summary>
public sealed record ProblemView
{
    public string? Title { get; init; }

    public string? Detail { get; init; }

    public int? Status { get; init; }

    public string? Code { get; init; }
}

// ---- requests ----------------------------------------------------------

public sealed record PreApproveVisitorRequest(
    string Name, string? Phone, string? Purpose, DateTimeOffset ExpectedAtUtc, Guid FlatId);

public sealed record CheckInRequest(
    string? PassCode, string? Name, string? Phone, string? Purpose, Guid? FlatId);

public sealed record RaiseComplaintRequest(
    Guid FlatId, string Category, string Priority, string Title, string Description);
