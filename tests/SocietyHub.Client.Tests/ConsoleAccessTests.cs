using System.Text.RegularExpressions;

namespace SocietyHub.Client.Tests;

/// <summary>
/// Pins the admin console's admission list to the roles the platform actually issues.
///
/// The console cannot reference <c>SocietyHubRoles</c> — that lives in the server's web
/// assembly, and a WebAssembly build has no business taking a dependency on ASP.NET. So three
/// role names are duplicated, and duplication across an assembly boundary is exactly what
/// produced the sign-in error-code bug: written from memory, wrong, and invisible until
/// someone hit it.
///
/// The failure here would be worse than that one. A renamed role does not throw; it silently
/// stops matching, and an entire society's committee is locked out of the console with a
/// message telling them to use the resident app.
/// </summary>
public sealed class ConsoleAccessTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    /// <summary>The roles the console admits, read from its own source.</summary>
    private static IReadOnlySet<string> AdmittedRoles
    {
        get
        {
            var source = File.ReadAllText(Path.Combine(
                RepositoryRoot,
                "src", "Clients", "SocietyHub.Admin.Web", "Platform", "ConsoleAccess.cs"));

            // Only the initialiser block, so the surrounding prose cannot contribute matches.
            var block = Regex.Match(
                source, @"AdmittedRoles\s*=[\s\S]*?\{([\s\S]*?)\};", RegexOptions.None);

            Assert.True(block.Success, "Could not find the AdmittedRoles initialiser.");

            return Regex.Matches(block.Groups[1].Value, @"""([A-Za-z]+)""")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>Every role the platform issues, from the server's canonical list.</summary>
    private static IReadOnlySet<string> PlatformRoles
    {
        get
        {
            var source = File.ReadAllText(Path.Combine(
                RepositoryRoot,
                "src", "BuildingBlocks", "SocietyHub.Web", "Security", "SocietyHubRoles.cs"));

            // Stops at the policies class, which shares the file and uses colon-separated
            // names that are not roles.
            var rolesOnly = source[..source.IndexOf("SocietyHubPolicies", StringComparison.Ordinal)];

            return Regex.Matches(rolesOnly, @"public const string \w+ = ""([A-Za-z]+)"";")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    [Fact]
    public void Both_lists_were_actually_found()
    {
        // Guards everything below. A moved file would make the comparisons pass vacuously,
        // which is the failure mode these tests exist to prevent.
        Assert.Equal(7, PlatformRoles.Count);
        Assert.NotEmpty(AdmittedRoles);
    }

    [Fact]
    public void Every_admitted_role_is_a_real_platform_role()
    {
        // A typo or a rename here does not throw — it silently admits nobody, and a
        // committee finds itself locked out of its own console.
        var unknown = AdmittedRoles.Except(PlatformRoles).Order().ToList();

        Assert.True(
            unknown.Count == 0,
            $"The console admits roles the platform does not issue: {string.Join(", ", unknown)}");
    }

    [Fact]
    public void The_console_admits_exactly_the_governing_roles()
    {
        // Stated explicitly rather than inferred, because widening this is a product decision
        // and should require editing a test that says so.
        Assert.Equal(
            new[] { "CommitteeMember", "SocietyAdmin", "SuperAdmin" },
            AdmittedRoles.Order().ToArray());
    }

    [Fact]
    public void The_roles_that_have_their_own_app_are_not_admitted()
    {
        // Residents have the mobile app and guards have the gate tablet. Admitting them here
        // gives them a console whose navigation is mostly hidden and whose panels mostly 403 —
        // worse than the app built for them.
        foreach (var role in new[] { "Resident", "Guard", "Vendor", "Technician" })
        {
            Assert.DoesNotContain(role, AdmittedRoles);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SocietyHub.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
