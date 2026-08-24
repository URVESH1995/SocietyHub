namespace SocietyHub.Contracts.Vision;

// Face recognition across residents, staff, visitors and watchlist subjects.
//
// Two constraints are structural rather than procedural, and hold for every event below.
//
// First, a template is scoped to exactly one society and is never searchable from another.
// The vault is tenant-scoped like every other table, so cross-society matching is not
// something the platform declines to do — it is something it cannot do.
//
// Second, a match is a probabilistic claim, so nothing here is wired to an automatic
// consequence. Every event raises an alert for a human to confirm. That matters most for
// watchlist hits, where acting on a false match against a named individual causes real harm.

/// <summary>
/// A resident who enrolled voluntarily was recognised. Enrolment is revocable and a
/// non-face entry path always remains available.
/// </summary>
public sealed record ResidentFaceRecognised : VisionEvent
{
    public required Guid ResidentId { get; init; }

    public required Guid FlatId { get; init; }

    public required string Direction { get; init; }

    public required Guid ConsentRecordId { get; init; }
}

/// <summary>
/// Recognised domestic help, driver or housekeeping worker, replacing a card or QR punch.
///
/// Enrolment carries notice and a working alternative. Where a worker declines, attendance
/// falls back to the QR punch and the society sees no difference in the record — which is
/// what keeps the alternative real rather than nominal.
/// </summary>
public sealed record StaffFaceRecognised : VisionEvent
{
    public required Guid StaffId { get; init; }

    public required string Direction { get; init; }

    /// <summary>Flats this worker is engaged by, for attendance attribution.</summary>
    public required IReadOnlyCollection<Guid> AssociatedFlatIds { get; init; }

    public required Guid NoticeRecordId { get; init; }
}

/// <summary>
/// A visitor's face matched a template captured on an earlier visit, letting a returning
/// courier or regular guest be admitted without re-registering.
///
/// Templates for visitors expire on a short timer regardless of activity. Nobody in this
/// category chose to be enrolled, so the retention window is the shortest that still serves
/// the security purpose the capture was justified by.
/// </summary>
public sealed record VisitorFaceRecognised : VisionEvent
{
    public required Guid VisitorProfileId { get; init; }

    public required string Direction { get; init; }

    /// <summary>The pass this arrival was matched against, when one exists.</summary>
    public Guid? VisitPassId { get; init; }

    /// <summary>Prior visits inside the retention window. Frequency alone means nothing.</summary>
    public required int PriorVisitCount { get; init; }

    /// <summary>Point-of-capture notice shown on the gate device for this arrival.</summary>
    public required Guid NoticeRecordId { get; init; }
}

/// <summary>
/// A face matched someone the society has flagged.
///
/// Raises an alert for a guard to verify in person and nothing else. It does not open or
/// hold a barrier, does not deny entry, and does not notify residents. Published accuracy
/// for face recognition does not support automating a consequence against a named person,
/// and a false match here is a serious harm rather than an inconvenience.
/// </summary>
public sealed record WatchlistFaceMatched : VisionEvent
{
    public required Guid WatchlistEntryId { get; init; }

    /// <summary>Why the flag exists — a prior incident, a committee decision, a police notice.</summary>
    public required string FlagReason { get; init; }

    /// <summary>Who raised the flag and when. Watchlists need an owner and a review date.</summary>
    public required Guid FlaggedByUserId { get; init; }

    public required DateTimeOffset FlaggedAtUtc { get; init; }

    /// <summary>
    /// True when confidence clears the society's threshold. Below it, this surfaces as a
    /// possible match for a guard to look at, never as a positive identification.
    /// </summary>
    public required bool MeetsConfidenceThreshold { get; init; }
}

// ---------------------------------------------------------------------------
// Template lifecycle — the audit trail for every stored face
// ---------------------------------------------------------------------------

/// <summary>
/// A template was created. Records the lawful basis at the moment of capture, because
/// reconstructing it afterwards is guesswork and an erasure request needs a real answer.
/// </summary>
public sealed record FaceTemplateEnrolled : IntegrationEvent
{
    public required Guid TemplateId { get; init; }

    /// <summary>Resident, Staff, Visitor or Watchlist.</summary>
    public required string SubjectType { get; init; }

    public required Guid SubjectId { get; init; }

    /// <summary>Consent, NoticedSecurityInterest or IncidentInvestigation.</summary>
    public required string LawfulBasis { get; init; }

    /// <summary>When this template is deleted. Null only for a standing resident enrolment.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; init; }

    /// <summary>The edge deployment holding it. Templates do not leave the society.</summary>
    public required string EdgeAgentId { get; init; }
}

/// <summary>
/// A template was destroyed — by revocation, by a data-subject request, or by the retention
/// timer. Consumers propagate the deletion to every edge holding a copy.
/// </summary>
public sealed record FaceTemplateErased : IntegrationEvent
{
    public required Guid TemplateId { get; init; }

    public required string SubjectType { get; init; }

    public required Guid SubjectId { get; init; }

    /// <summary>ConsentRevoked, DataSubjectRequest, RetentionExpiry or SocietyOffboarded.</summary>
    public required string Reason { get; init; }

    public required DateTimeOffset ErasedAtUtc { get; init; }
}

/// <summary>
/// Every match attempt, including the ones that failed and the ones a guard overruled.
///
/// This is the record that answers "why was this person stopped", and it is the only way to
/// notice a model degrading against part of the population — accuracy that looks fine in
/// aggregate can be poor for a specific group, and only the outcomes reveal it.
/// </summary>
public sealed record FaceMatchAudited : IntegrationEvent
{
    public required Guid MatchAttemptId { get; init; }

    public required Guid CameraId { get; init; }

    public required string SubjectType { get; init; }

    public Guid? MatchedSubjectId { get; init; }

    public required double Confidence { get; init; }

    public required bool WasMatch { get; init; }

    /// <summary>Admitted, Denied, GuardOverrode or NoActionTaken.</summary>
    public required string Outcome { get; init; }

    public Guid? ReviewedByGuardId { get; init; }

    public required string ModelVersion { get; init; }
}
