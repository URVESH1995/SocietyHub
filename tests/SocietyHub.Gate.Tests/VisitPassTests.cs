using SocietyHub.Gate.Api.Domain;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Gate.Tests;

/// <summary>
/// A pass code is what opens a gate, so it gets the same treatment as a sign-in credential.
/// These cover the ways one could be reused, brute-forced or replayed.
/// </summary>
public sealed class VisitPassTests
{
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid FlatId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ResidentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid GuardId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly DateTimeOffset Now = new(2026, 8, 27, 18, 0, 0, TimeSpan.Zero);

    private static (VisitPass Pass, string Code) Issue(
        DateTimeOffset? from = null,
        DateTimeOffset? until = null) =>
        VisitPass.Issue(
            SocietyId,
            FlatId,
            ResidentId,
            "Ramesh Kumar",
            "+919876543210",
            VisitorType.Guest,
            from ?? Now,
            until ?? Now.AddHours(4));

    [Fact]
    public void An_issued_pass_is_pending_with_a_six_digit_code()
    {
        var (pass, code) = Issue();

        Assert.Equal(PassStatus.Pending, pass.Status);
        Assert.Equal(6, code.Length);
        Assert.All(code, c => Assert.True(char.IsDigit(c)));
        Assert.True(pass.IsOpen(Now));
    }

    [Fact]
    public void The_code_is_never_stored_in_readable_form()
    {
        var (pass, code) = Issue();

        Assert.DoesNotContain(code, pass.CodeHash);
        Assert.NotEmpty(pass.CodeSalt);
    }

    [Fact]
    public void Two_passes_with_the_same_code_hash_differently()
    {
        // Without a per-pass salt the table becomes a lookup table for live gate codes.
        var hashes = Enumerable.Range(0, 25).Select(_ => Issue().Pass.CodeHash).ToList();

        Assert.Equal(hashes.Count, hashes.Distinct().Count());
    }

    [Fact]
    public void The_correct_code_admits_the_visitor()
    {
        var (pass, code) = Issue();

        var result = pass.CheckIn(code, GuardId, Now.AddMinutes(30));

        Assert.True(result.IsSuccess);
        Assert.Equal(PassStatus.CheckedIn, pass.Status);
        Assert.Equal(GuardId, pass.CheckedInByGuardId);
    }

    [Fact]
    public void A_pass_admits_exactly_one_visit()
    {
        // Otherwise a code shared once becomes a standing key to the building.
        var (pass, code) = Issue();
        pass.CheckIn(code, GuardId, Now);

        var replay = pass.CheckIn(code, GuardId, Now.AddMinutes(5));

        Assert.True(replay.IsFailure);
        Assert.Equal("Pass.AlreadyUsed", replay.Error.Code);
    }

    [Fact]
    public void A_wrong_code_is_refused_and_counted()
    {
        var (pass, _) = Issue();

        var result = pass.CheckIn("000000", GuardId, Now);

        Assert.True(result.IsFailure);
        Assert.Equal("Pass.InvalidCode", result.Error.Code);
        Assert.Equal(1, pass.VerificationAttempts);
        Assert.Equal(PassStatus.Pending, pass.Status);
    }

    [Fact]
    public void Five_wrong_codes_close_the_pass_to_further_attempts()
    {
        // The attempt cap is what makes six digits safe at a gate an attacker can stand at.
        var (pass, code) = Issue();

        for (var i = 0; i < VisitPass.MaxVerificationAttempts; i++)
        {
            pass.CheckIn("000000", GuardId, Now);
        }

        var withCorrectCode = pass.CheckIn(code, GuardId, Now);

        Assert.True(withCorrectCode.IsFailure);
        Assert.Equal("Pass.TooManyAttempts", withCorrectCode.Error.Code);
    }

    [Fact]
    public void A_pass_used_before_its_window_is_refused()
    {
        var (pass, code) = Issue(from: Now.AddHours(2));

        var result = pass.CheckIn(code, GuardId, Now);

        Assert.True(result.IsFailure);
        Assert.Equal("Pass.NotYetValid", result.Error.Code);
    }

    [Fact]
    public void An_expired_pass_is_refused_and_marked_expired()
    {
        var (pass, code) = Issue(until: Now.AddHours(1));

        var result = pass.CheckIn(code, GuardId, Now.AddHours(2));

        Assert.True(result.IsFailure);
        Assert.Equal("Pass.Expired", result.Error.Code);
        Assert.Equal(PassStatus.Expired, pass.Status);
    }

    [Fact]
    public void An_expired_pass_does_not_burn_an_attempt()
    {
        // Guards against the inversion where expiry is checked after counting, which would
        // let someone exhaust a pass without ever guessing.
        var (pass, _) = Issue(until: Now.AddHours(1));

        pass.CheckIn("000000", GuardId, Now.AddHours(2));

        Assert.Equal(0, pass.VerificationAttempts);
    }

    [Fact]
    public void A_cancelled_pass_cannot_be_used()
    {
        var (pass, code) = Issue();
        pass.Cancel();

        var result = pass.CheckIn(code, GuardId, Now);

        Assert.True(result.IsFailure);
        Assert.Equal("Pass.Cancelled", result.Error.Code);
    }

    [Fact]
    public void A_used_pass_cannot_be_cancelled()
    {
        var (pass, code) = Issue();
        pass.CheckIn(code, GuardId, Now);

        var result = pass.Cancel();

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    [Fact]
    public void Check_out_requires_the_visitor_to_be_inside()
    {
        // "Who is still in the building" is the question that matters during a fire, so the
        // two ends of a visit are tracked separately rather than inferred.
        var (pass, code) = Issue();

        Assert.True(pass.CheckOut(Now).IsFailure);

        pass.CheckIn(code, GuardId, Now);
        var result = pass.CheckOut(Now.AddHours(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(PassStatus.CheckedOut, pass.Status);
        Assert.Equal(Now.AddHours(1), pass.CheckedOutAtUtc);
    }

    [Fact]
    public void A_visitor_who_already_entered_cannot_be_denied_retroactively()
    {
        var (pass, code) = Issue();
        pass.CheckIn(code, GuardId, Now);

        Assert.True(pass.Deny().IsFailure);
    }

    [Fact]
    public void Codes_are_not_predictable()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => Issue().Code).ToList();

        Assert.True(codes.Distinct().Count() > 190);
    }
}
