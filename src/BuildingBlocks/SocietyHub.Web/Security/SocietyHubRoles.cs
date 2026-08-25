namespace SocietyHub.Web.Security;

/// <summary>
/// The seven roles the platform issues. A user holds a role <em>within a society</em> — the
/// same person can be a Resident in one and a CommitteeMember in another, which is why the
/// role is only ever meaningful alongside the <c>society_id</c> claim on the same token.
/// </summary>
public static class SocietyHubRoles
{
    /// <summary>Platform operator. Spans societies, and only ever with an audit trail.</summary>
    public const string SuperAdmin = "SuperAdmin";

    /// <summary>Runs one society day to day. Onboards flats, manages guards and settings.</summary>
    public const string SocietyAdmin = "SocietyAdmin";

    /// <summary>Elected committee. Approves notices, sees escalations, opens bulk drives.</summary>
    public const string CommitteeMember = "CommitteeMember";

    /// <summary>Owner or tenant of a flat.</summary>
    public const string Resident = "Resident";

    /// <summary>Security staff at the gate, working from a shared device.</summary>
    public const string Guard = "Guard";

    /// <summary>A service company fulfilling bulk drives.</summary>
    public const string Vendor = "Vendor";

    /// <summary>An individual sent by a vendor to perform a job.</summary>
    public const string Technician = "Technician";

    public static IReadOnlyList<string> All { get; } =
    [
        SuperAdmin, SocietyAdmin, CommitteeMember, Resident, Guard, Vendor, Technician,
    ];
}

/// <summary>
/// Named authorisation policies. Endpoints reference these rather than listing roles inline,
/// so widening who may cancel a bulk drive is one edit here instead of a search across every
/// service for a string.
/// </summary>
public static class SocietyHubPolicies
{
    /// <summary>
    /// Baseline for anything society-scoped: authenticated, with a usable <c>society_id</c>.
    ///
    /// Nearly every endpoint wants this. Without it a token missing the claim reaches the
    /// handler, the query filter resolves to <see cref="Guid.Empty"/>, and the caller gets a
    /// confusing empty result instead of a clear 403.
    /// </summary>
    public const string RequireSociety = "society:required";

    /// <summary>Society administration — settings, flats, guard accounts.</summary>
    public const string SocietyAdministration = "society:administer";

    /// <summary>Committee decisions — notices, escalations, opening drives.</summary>
    public const string CommitteeDecisions = "society:committee";

    /// <summary>Gate operations, for guards and administrators.</summary>
    public const string GateOperations = "gate:operate";

    /// <summary>Resident self-service.</summary>
    public const string ResidentAccess = "resident:access";

    /// <summary>
    /// Cross-society operations. Requires both the platform claim and the SuperAdmin role, so
    /// a leaked claim alone is not enough, and every use is deliberate and auditable.
    /// </summary>
    public const string PlatformOperations = "platform:operate";
}
