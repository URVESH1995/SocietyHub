using SocietyHub.Client.Shared.Models;

namespace SocietyHub.Admin.Web.Platform;

/// <summary>
/// Who this console is for.
///
/// The server is and remains the security boundary — every endpoint has its own authorisation
/// policy and a resident who forged their way past this check would still be refused on every
/// request. This decides something different: whether to hand someone a tool that is not
/// theirs.
///
/// Before this existed, a guard could sign in and reach a home screen where every panel failed
/// with 403. That is correct behaviour and a terrible thing to look at, and it tells them
/// nothing about what they should be using instead.
/// </summary>
public static class ConsoleAccess
{
    /// <summary>
    /// The roles this console is built for.
    ///
    /// Duplicated from <c>SocietyHub.Web.Security.SocietyHubRoles</c>, because the client
    /// deliberately does not reference the server's web assembly — sharing it would couple a
    /// WebAssembly build to ASP.NET. A test pins these strings against that file, so a rename
    /// on either side fails the build rather than quietly locking out a committee.
    /// </summary>
    public static readonly IReadOnlySet<string> AdmittedRoles =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "SuperAdmin",
            "SocietyAdmin",
            "CommitteeMember",
        };

    /// <summary>
    /// Whether the signed-in person may use the console <em>for the society they signed in
    /// to</em>.
    ///
    /// Per society, not per person, and that distinction is the whole design. Roles are held on
    /// a membership, so the same phone can be a Guard at one address and on the committee at
    /// another. Refusing on "this person is a guard somewhere" would lock out a legitimate
    /// committee member, and the society switcher exists precisely because that case is real.
    /// </summary>
    public static bool IsAdmitted(MeView? me) =>
        me is not null && me.Roles.Any(AdmittedRoles.Contains);
}
