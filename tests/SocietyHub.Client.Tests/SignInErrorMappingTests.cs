using System.Reflection;
using System.Text.RegularExpressions;

namespace SocietyHub.Client.Tests;

/// <summary>
/// Pins the sign-in screen's error-code mapping to the codes the Identity service actually
/// emits.
///
/// This test exists because the mapping was written from memory and was wrong — every code was
/// guessed in lower_snake_case while the server emits Pascal.Case, so a rate-limited resident
/// saw "something went wrong" instead of "too many attempts". Nothing failed, nothing logged,
/// and it was only visible by signing in enough times to trip the limiter.
///
/// The two sides live in different assemblies that deploy separately, so a compiler cannot
/// connect them. This reads both files as text and compares — crude, but it is the only thing
/// that catches a rename on either side.
/// </summary>
public sealed class SignInErrorMappingTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    private static string SignInFormSource => File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "src", "Clients", "SocietyHub.Client.Shared", "Components", "SignInForm.razor"));

    /// <summary>Every error code the sign-in form claims to understand.</summary>
    private static IReadOnlySet<string> MappedCodes =>
        Regex.Matches(SignInFormSource, @"""((?:Otp|Auth|User|Phone)\.[A-Za-z]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Every error code the Identity service actually raises.</summary>
    private static IReadOnlySet<string> ServerCodes
    {
        get
        {
            var identity = Path.Combine(
                RepositoryRoot, "src", "Services", "Identity", "SocietyHub.Identity.Api");

            var codes = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in Directory.EnumerateFiles(identity, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                foreach (Match match in Regex.Matches(
                             File.ReadAllText(file), @"""((?:Otp|Auth|User)\.[A-Za-z]+)"""))
                {
                    codes.Add(match.Groups[1].Value);
                }
            }

            return codes;
        }
    }

    [Fact]
    public void The_form_maps_no_code_the_server_never_sends()
    {
        // A mapped code that does not exist is dead branch — usually a guess, occasionally a
        // rename the client missed. Either way the real code is falling through to the
        // generic message.
        var invented = MappedCodes.Except(ServerCodes).Order().ToList();

        Assert.True(
            invented.Count == 0,
            $"The sign-in form maps codes Identity never emits: {string.Join(", ", invented)}");
    }

    [Fact]
    public void The_errors_a_person_can_actually_hit_are_all_mapped()
    {
        // Not every server code — several are internal states a caller never sees. These are
        // the ones a real person reaches by mistyping, waiting too long, or trying too often,
        // and each has a distinct thing they should do next.
        string[] mustBeMapped =
        [
            "Otp.Invalid",
            "Otp.TooManyRequests",
            "Auth.NotRegistered",
            "Auth.AccountDisabled",
        ];

        var missing = mustBeMapped.Except(MappedCodes).ToList();

        Assert.True(
            missing.Count == 0,
            $"These would show the generic error instead of something useful: "
            + $"{string.Join(", ", missing)}");
    }

    [Fact]
    public void The_server_codes_were_found_at_all()
    {
        // Guards the two tests above. A path change would make both pass vacuously by
        // comparing against an empty set, which is exactly the failure this file exists
        // to prevent elsewhere.
        Assert.NotEmpty(ServerCodes);
        Assert.NotEmpty(MappedCodes);
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
