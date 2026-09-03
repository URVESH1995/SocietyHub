namespace SocietyHub.Contracts.Drives;

/// <summary>A committee opened a drive. Residents can now join.</summary>
public sealed record DriveOpened : IntegrationEvent
{
    public required Guid DriveId { get; init; }

    public required string ServiceCode { get; init; }

    public required Guid VendorId { get; init; }

    public required int Quorum { get; init; }

    public required DateTimeOffset CutOffAtUtc { get; init; }

    public required DateTimeOffset ServiceDateUtc { get; init; }
}

/// <summary>
/// Enough residents joined that the drive will go ahead.
///
/// Published when the count crosses quorum, not at cut-off. Residents want to know the moment
/// their drive is safe — it is the difference between "I might get this" and "this is
/// happening", and it is what makes them tell a neighbour.
/// </summary>
public sealed record DriveQuorumReached : IntegrationEvent
{
    public required Guid DriveId { get; init; }

    public required string ServiceCode { get; init; }

    public required int Participants { get; init; }
}

/// <summary>
/// Enrolment closed with quorum met. The signal the vendor and scheduler act on.
/// </summary>
public sealed record DriveConfirmed : IntegrationEvent
{
    public required Guid DriveId { get; init; }

    public required string ServiceCode { get; init; }

    public required Guid VendorId { get; init; }

    public required int Participants { get; init; }

    public required int TotalUnits { get; init; }

    public required long FinalUnitPricePaise { get; init; }

    public required DateTimeOffset ServiceDateUtc { get; init; }
}

/// <summary>
/// Enrolment closed without quorum. Every participant who paid is owed their money.
///
/// Carries the count and the target so a notification can say "we needed 10 and reached 7"
/// rather than a bare apology — a committee deciding whether to run it again needs the number,
/// and a resident is far less annoyed by a reason than by a cancellation.
/// </summary>
public sealed record DriveCancelled : IntegrationEvent
{
    public required Guid DriveId { get; init; }

    public required string ServiceCode { get; init; }

    public required int Participants { get; init; }

    public required int Quorum { get; init; }

    public required string Reason { get; init; }

    /// <summary>How many refunds the compensation path has to complete.</summary>
    public required int RefundsDue { get; init; }
}

/// <summary>
/// One participant's money is owed back.
///
/// Per participant rather than one event for the whole drive, because refunds are individual
/// gateway calls that fail and retry independently. A single event would make the retry
/// all-or-nothing, and a crash after forty of sixty would re-refund the forty.
/// </summary>
public sealed record DriveRefundRequested : IntegrationEvent
{
    public required Guid DriveId { get; init; }

    public required Guid EnrolmentId { get; init; }

    public required Guid UserId { get; init; }

    public required string PaymentReference { get; init; }

    public required long AmountPaise { get; init; }

    /// <summary>
    /// Why the money is going back: <c>quorum_missed</c>, <c>withdrawn</c>, or
    /// <c>price_settled</c> for the partial refund an early joiner is owed.
    /// </summary>
    public required string Reason { get; init; }
}

/// <summary>Every refund for a cancelled drive has landed.</summary>
public sealed record DriveRefundsCompleted : IntegrationEvent
{
    public required Guid DriveId { get; init; }

    public required int RefundsIssued { get; init; }
}

/// <summary>
/// One participant's money is back.
///
/// Closes the loop the Drives service opened with <see cref="DriveRefundRequested"/>. Drives
/// marks the enrolment settled on receiving it, and moves the drive to Cancelled once none
/// remain outstanding — which is why this is per participant rather than per drive.
/// </summary>
public sealed record DriveRefundIssued : IntegrationEvent
{
    public required Guid DriveId { get; init; }

    public required Guid EnrolmentId { get; init; }

    public required string RefundReference { get; init; }

    public required long AmountPaise { get; init; }
}
