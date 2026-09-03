using SocietyHub.SharedKernel.Primitives;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Vendor.Api.Domain;

/// <summary>
/// How far a vendor has got through onboarding.
///
/// A state machine rather than a pair of booleans, because the difference between "has not
/// submitted documents yet" and "submitted them and was rejected" decides whether a chase-up
/// email is helpful or insulting.
/// </summary>
public enum VendorStatus
{
    /// <summary>Registered, no documents yet. Cannot be offered work.</summary>
    Applied = 0,

    /// <summary>Documents in, awaiting a human. Still cannot be offered work.</summary>
    UnderReview = 1,

    /// <summary>Verified. The only state in which a drive may be awarded.</summary>
    Active = 2,

    /// <summary>Rejected at review, with a reason. May reapply.</summary>
    Rejected = 3,

    /// <summary>
    /// Was active, stopped. Distinct from Rejected because existing jobs must still complete
    /// and be paid — suspending a vendor mid-drive cannot strand the residents who enrolled.
    /// </summary>
    Suspended = 4,
}

/// <summary>
/// A service company: AC servicing, deep cleaning, pest control, car washing.
///
/// <para>
/// <b>Deliberately not tenant-scoped, and it is the only aggregate in the platform that is
/// not.</b> A vendor's whole value is serving many societies — that is what makes a bulk
/// discount possible at all — so scoping one to a society would defeat the feature and force
/// every society to onboard its own duplicate of the same company.
/// </para>
///
/// <para>
/// That exception is dangerous precisely because every other aggregate is scoped, and a
/// developer reading five services and then this one will assume the filter is there. It is
/// not. Access is controlled instead by authorisation: onboarding and verification are
/// platform operations, and a society sees only the read model of vendors, never this.
/// </para>
/// </summary>
public sealed class Vendor : AggregateRoot, IAuditable
{
    private readonly List<VendorDocument> _documents = [];
    private readonly List<ServiceArea> _serviceAreas = [];

    private Vendor() { }

    public Vendor(
        Guid id,
        string legalName,
        string tradingName,
        string contactPhone,
        string? contactEmail,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        LegalName = legalName;
        TradingName = tradingName;
        ContactPhone = contactPhone;
        ContactEmail = contactEmail;
        Status = VendorStatus.Applied;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>The name on the GST certificate, used on invoices and in disputes.</summary>
    public string LegalName { get; private set; } = string.Empty;

    /// <summary>The name residents recognise, which is often not the legal one.</summary>
    public string TradingName { get; private set; } = string.Empty;

    public string ContactPhone { get; private set; } = string.Empty;

    public string? ContactEmail { get; private set; }

    public VendorStatus Status { get; private set; }

    /// <summary>15-character GSTIN. Null until KYC.</summary>
    public string? GstNumber { get; private set; }

    /// <summary>10-character PAN.</summary>
    public string? PanNumber { get; private set; }

    /// <summary>
    /// Why the vendor was rejected or suspended.
    ///
    /// Required at the point of the decision, because six months later nobody remembers whether
    /// a vendor was suspended for a safety incident or a paperwork lapse — and those have
    /// opposite answers to "can we bring them back".
    /// </summary>
    public string? StatusReason { get; private set; }

    public DateTimeOffset? VerifiedAtUtc { get; private set; }

    public Guid? VerifiedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public IReadOnlyCollection<VendorDocument> Documents => _documents;

    public IReadOnlyCollection<ServiceArea> ServiceAreas => _serviceAreas;

    /// <summary>
    /// Whether this vendor may be awarded a drive.
    ///
    /// The single question the drive saga asks, and the reason status is a machine rather than
    /// a flag. Awarding to an unverified vendor means residents paying money to a company
    /// nobody has checked.
    /// </summary>
    public bool CanBeAwardedWork => Status == VendorStatus.Active;

    public Result SubmitKyc(string gstNumber, string panNumber, DateTimeOffset nowUtc)
    {
        if (Status is VendorStatus.Suspended)
        {
            return Error.Conflict(
                "vendor.suspended", "A suspended vendor cannot resubmit documents.");
        }

        if (!IsPlausibleGstin(gstNumber))
        {
            return Error.Validation(
                "vendor.bad_gstin", "A GSTIN is 15 characters: 2 digits, 10 PAN, 3 more.");
        }

        if (!IsPlausiblePan(panNumber))
        {
            return Error.Validation(
                "vendor.bad_pan", "A PAN is 5 letters, 4 digits and a letter.");
        }

        // The GSTIN embeds the PAN at positions 3-12. Checking the two agree catches a
        // transposed document upload before a human spends time on it — and catches the more
        // interesting case of a vendor pasting somebody else's GSTIN.
        if (!gstNumber.AsSpan(2, 10).SequenceEqual(panNumber.AsSpan()))
        {
            return Error.Validation(
                "vendor.gstin_pan_mismatch",
                "The PAN inside the GSTIN does not match the PAN given.");
        }

        GstNumber = gstNumber.ToUpperInvariant();
        PanNumber = panNumber.ToUpperInvariant();
        Status = VendorStatus.UnderReview;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    public Result AddDocument(VendorDocumentKind kind, string storageKey, DateTimeOffset nowUtc)
    {
        if (_documents.Any(d => d.Kind == kind))
        {
            // Replaced rather than duplicated. A review screen showing two GST certificates
            // makes a reviewer guess which is current, and they will sometimes guess wrong.
            _documents.RemoveAll(d => d.Kind == kind);
        }

        _documents.Add(new VendorDocument(Guid.CreateVersion7(), Id, kind, storageKey, nowUtc));
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    /// <summary>
    /// Approves a vendor after a human has looked at the documents.
    ///
    /// Deliberately not automatic on document upload. Verifying a company is a judgement — the
    /// documents can be genuine and the company still wrong for the platform — and a rule that
    /// approved on upload would make the whole KYC step decorative.
    /// </summary>
    public Result Verify(Guid verifiedByUserId, DateTimeOffset nowUtc)
    {
        if (Status is not VendorStatus.UnderReview)
        {
            return Error.Conflict(
                "vendor.not_under_review",
                "Only a vendor that has submitted documents can be verified.");
        }

        if (_documents.Count == 0)
        {
            return Error.Conflict(
                "vendor.no_documents", "There are no documents to have verified.");
        }

        Status = VendorStatus.Active;
        VerifiedAtUtc = nowUtc;
        VerifiedByUserId = verifiedByUserId;
        StatusReason = null;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    public Result Reject(string reason, DateTimeOffset nowUtc)
    {
        if (Status is VendorStatus.Active)
        {
            return Error.Conflict(
                "vendor.already_active", "Suspend an active vendor rather than rejecting it.");
        }

        Status = VendorStatus.Rejected;
        StatusReason = reason;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    /// <summary>
    /// Stops a vendor being offered new work.
    ///
    /// Existing jobs are untouched on purpose. A vendor suspended mid-drive still has residents
    /// who paid and expect a technician; cancelling those from here would strand them with no
    /// service and no refund path. The drive saga decides what happens to work in flight.
    /// </summary>
    public Result Suspend(string reason, DateTimeOffset nowUtc)
    {
        if (Status is not VendorStatus.Active)
        {
            return Error.Conflict("vendor.not_active", "Only an active vendor can be suspended.");
        }

        Status = VendorStatus.Suspended;
        StatusReason = reason;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    public Result Reinstate(DateTimeOffset nowUtc)
    {
        if (Status is not VendorStatus.Suspended)
        {
            return Error.Conflict(
                "vendor.not_suspended", "Only a suspended vendor can be reinstated.");
        }

        Status = VendorStatus.Active;
        StatusReason = null;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    /// <summary>
    /// Where the vendor will travel. Matched against a society's postal code when a drive is
    /// opened, so a society is never offered a vendor who will not come.
    /// </summary>
    public void CoverArea(string city, string postalCode)
    {
        if (_serviceAreas.Any(a =>
                string.Equals(a.PostalCode, postalCode, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _serviceAreas.Add(new ServiceArea(Guid.CreateVersion7(), Id, city, postalCode));
    }

    public bool Covers(string postalCode) =>
        _serviceAreas.Any(a =>
            string.Equals(a.PostalCode, postalCode, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Shape check only, not a checksum and not a government lookup.
    ///
    /// Deliberate: a real GSTIN validation calls the GST portal, which is slow, rate-limited
    /// and sometimes down, and blocking onboarding on it means a vendor cannot register on a
    /// bad afternoon. The shape catches typos; the human review catches the rest.
    /// </summary>
    internal static bool IsPlausibleGstin(string? value) =>
        value is { Length: 15 }
        && char.IsAsciiDigit(value[0]) && char.IsAsciiDigit(value[1])
        && IsPlausiblePan(value.Substring(2, 10))
        && char.IsAsciiDigit(value[12])
        && char.IsAsciiLetterOrDigit(value[13])
        && char.IsAsciiLetterOrDigit(value[14]);

    internal static bool IsPlausiblePan(string? value) =>
        value is { Length: 10 }
        && value.AsSpan(0, 5).ToArray().All(char.IsAsciiLetterUpper)
        && value.AsSpan(5, 4).ToArray().All(char.IsAsciiDigit)
        && char.IsAsciiLetterUpper(value[9]);
}

public enum VendorDocumentKind
{
    GstCertificate = 0,
    PanCard = 1,
    Insurance = 2,

    /// <summary>
    /// Proof that technicians are police-verified. Not legally required everywhere, but a
    /// society letting strangers into flats will ask, and a vendor who cannot produce it is
    /// one a committee should be able to see is missing it.
    /// </summary>
    PoliceVerification = 3,

    TradeLicence = 4,
}

public sealed class VendorDocument : Entity
{
    private VendorDocument() { }

    public VendorDocument(
        Guid id, Guid vendorId, VendorDocumentKind kind, string storageKey, DateTimeOffset uploadedAtUtc)
        : base(id)
    {
        VendorId = vendorId;
        Kind = kind;
        StorageKey = storageKey;
        UploadedAtUtc = uploadedAtUtc;
    }

    public Guid VendorId { get; private set; }

    public VendorDocumentKind Kind { get; private set; }

    /// <summary>
    /// A blob key, never the document itself. KYC papers carry a company's tax identity and
    /// have no business in a relational row that ends up in every backup and every query plan.
    /// </summary>
    public string StorageKey { get; private set; } = string.Empty;

    public DateTimeOffset UploadedAtUtc { get; private set; }
}

public sealed class ServiceArea : Entity
{
    private ServiceArea() { }

    public ServiceArea(Guid id, Guid vendorId, string city, string postalCode)
        : base(id)
    {
        VendorId = vendorId;
        City = city;
        PostalCode = postalCode;
    }

    public Guid VendorId { get; private set; }

    public string City { get; private set; } = string.Empty;

    public string PostalCode { get; private set; } = string.Empty;
}
