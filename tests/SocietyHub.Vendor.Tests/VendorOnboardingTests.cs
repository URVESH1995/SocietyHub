using SocietyHub.SharedKernel.Primitives;
using SocietyHub.Vendor.Api.Domain;
using SocietyHub.Vendor.Api.Persistence;

namespace SocietyHub.Vendor.Tests;

/// <summary>
/// Onboarding and KYC.
///
/// The gate between "a company filled in a form" and "residents are paying them to enter
/// flats". Every rule here is about not letting the second happen without the first being
/// checked by a person.
/// </summary>
public sealed class VendorOnboardingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Reviewer = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private const string ValidPan = "AABCU9603R";
    private const string ValidGstin = "27AABCU9603R1ZM";

    private static Vendor.Api.Domain.Vendor NewVendor() =>
        new(Guid.CreateVersion7(),
            "Cool Air Services Private Limited",
            "CoolAir",
            "+919000000010",
            "ops@coolair.test",
            Now);

    private static Vendor.Api.Domain.Vendor VerifiedVendor()
    {
        var vendor = NewVendor();
        vendor.SubmitKyc(ValidGstin, ValidPan, Now);
        vendor.AddDocument(VendorDocumentKind.GstCertificate, "blob/gst.pdf", Now);
        vendor.Verify(Reviewer, Now);

        return vendor;
    }

    [Fact]
    public void A_new_vendor_cannot_be_awarded_work()
    {
        // The default has to be "no". A vendor that could take work the moment it registered
        // would make KYC decorative.
        Assert.False(NewVendor().CanBeAwardedWork);
    }

    [Fact]
    public void Submitting_kyc_does_not_by_itself_verify()
    {
        // Approving on upload would mean nobody ever looks. The documents can be genuine and
        // the company still wrong for the platform.
        var vendor = NewVendor();

        vendor.SubmitKyc(ValidGstin, ValidPan, Now);

        Assert.Equal(VendorStatus.UnderReview, vendor.Status);
        Assert.False(vendor.CanBeAwardedWork);
    }

    [Fact]
    public void A_gstin_whose_embedded_pan_disagrees_is_rejected()
    {
        // The GSTIN contains the PAN at positions 3-12. A mismatch is either a transposed
        // upload or somebody pasting another company's number, and the second is the
        // interesting case.
        var vendor = NewVendor();

        var result = vendor.SubmitKyc("27AABCU9603R1ZM", "ZZZZZ1111Z", Now);

        Assert.True(result.IsFailure);
        Assert.Equal("vendor.gstin_pan_mismatch", result.Error.Code);
    }

    [Theory]
    [InlineData("27AABCU9603R1Z")]      // too short
    [InlineData("XXAABCU9603R1ZM")]     // state code not numeric
    [InlineData("27aabcu9603r1zm")]     // lower case PAN section
    public void A_malformed_gstin_is_rejected(string gstin)
    {
        Assert.True(NewVendor().SubmitKyc(gstin, ValidPan, Now).IsFailure);
    }

    [Fact]
    public void Verification_requires_at_least_one_document()
    {
        // Verifying with nothing on file is a reviewer clicking through, and it produces a
        // record that says a human checked when no human could have.
        var vendor = NewVendor();
        vendor.SubmitKyc(ValidGstin, ValidPan, Now);

        var result = vendor.Verify(Reviewer, Now);

        Assert.True(result.IsFailure);
        Assert.Equal("vendor.no_documents", result.Error.Code);
    }

    [Fact]
    public void A_verified_vendor_can_be_awarded_work()
    {
        var vendor = VerifiedVendor();

        Assert.Equal(VendorStatus.Active, vendor.Status);
        Assert.True(vendor.CanBeAwardedWork);
        Assert.Equal(Reviewer, vendor.VerifiedByUserId);
    }

    [Fact]
    public void Re_uploading_a_document_replaces_rather_than_duplicates()
    {
        // A review screen showing two GST certificates makes a reviewer guess which is
        // current, and they will sometimes guess wrong.
        var vendor = NewVendor();

        vendor.AddDocument(VendorDocumentKind.GstCertificate, "blob/old.pdf", Now);
        vendor.AddDocument(VendorDocumentKind.GstCertificate, "blob/new.pdf", Now);

        Assert.Single(vendor.Documents);
        Assert.Equal("blob/new.pdf", vendor.Documents.Single().StorageKey);
    }

    [Fact]
    public void Suspension_stops_new_work_and_records_why()
    {
        var vendor = VerifiedVendor();

        var result = vendor.Suspend("Two no-shows at Sunrise Apartments in one week.", Now);

        Assert.True(result.IsSuccess);
        Assert.False(vendor.CanBeAwardedWork);
        Assert.Contains("no-shows", vendor.StatusReason);
    }

    [Fact]
    public void An_active_vendor_cannot_be_rejected()
    {
        // Rejection is for applications. Using it on a live vendor would skip the question of
        // what happens to the jobs they are part-way through.
        var result = VerifiedVendor().Reject("changed our mind", Now);

        Assert.True(result.IsFailure);
        Assert.Equal("vendor.already_active", result.Error.Code);
    }

    [Fact]
    public void Reinstating_clears_the_suspension_reason()
    {
        // A stale reason on an active vendor reads as a live concern to the next person who
        // opens the record.
        var vendor = VerifiedVendor();
        vendor.Suspend("Paperwork lapsed.", Now);

        vendor.Reinstate(Now.AddDays(7));

        Assert.Equal(VendorStatus.Active, vendor.Status);
        Assert.Null(vendor.StatusReason);
    }

    [Fact]
    public void Service_areas_are_matched_case_insensitively_and_not_duplicated()
    {
        var vendor = VerifiedVendor();

        vendor.CoverArea("Pune", "411045");
        vendor.CoverArea("Pune", "411045");

        Assert.Single(vendor.ServiceAreas);
        Assert.True(vendor.Covers("411045"));
        Assert.False(vendor.Covers("400001"));
    }
}

/// <summary>
/// The Vendor service is the only one with no tenant filter. That is correct and it is
/// dangerous, so it is asserted rather than left to a comment nobody reads.
/// </summary>
public sealed class VendorTenancyTests
{
    [Fact]
    public void No_entity_in_the_vendor_context_is_society_scoped()
    {
        // A future aggregate that genuinely belongs to one society must not be added to this
        // context: it would sit there with no filter and no guard, looking exactly like every
        // other entity in the service.
        var scoped = typeof(VendorDbContext).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.IsAssignableTo(typeof(ITenantScoped)))
            .Select(t => t.Name)
            .ToList();

        Assert.True(
            scoped.Count == 0,
            "These are tenant-scoped but live in the un-filtered Vendor service: "
            + string.Join(", ", scoped));
    }
}
