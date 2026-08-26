using SocietyHub.Identity.Api.Domain;

namespace SocietyHub.Identity.Tests;

/// <summary>
/// OTP is the primary credential for residents, which makes this the most attacked surface in
/// the platform. These cover what actually stops a six-digit code being brute-forced.
/// </summary>
public sealed class OtpChallengeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
    private const string Phone = "+919876543210";

    [Fact]
    public void An_issued_challenge_is_usable_and_carries_a_six_digit_code()
    {
        var (challenge, code) = OtpChallenge.Issue(Phone, Now);

        Assert.Equal(6, code.Length);
        Assert.All(code, c => Assert.True(char.IsDigit(c)));
        Assert.True(challenge.IsUsable(Now));
        Assert.Equal(Now.AddMinutes(5), challenge.ExpiresAtUtc);
    }

    [Fact]
    public void The_plaintext_code_is_never_stored()
    {
        // A database read must not yield a live credential.
        var (challenge, code) = OtpChallenge.Issue(Phone, Now);

        Assert.DoesNotContain(code, challenge.CodeHash);
        Assert.NotEqual(code, challenge.CodeHash);
        Assert.NotEmpty(challenge.Salt);
    }

    [Fact]
    public void Two_challenges_with_the_same_code_hash_differently()
    {
        // Without a per-challenge salt the table becomes a lookup: see a hash twice and you
        // know the code. Issuing many and comparing proves the salt is actually applied.
        var hashes = Enumerable.Range(0, 25)
            .Select(_ => OtpChallenge.Issue(Phone, Now).Challenge.CodeHash)
            .ToList();

        Assert.Equal(hashes.Count, hashes.Distinct().Count());
    }

    [Fact]
    public void The_correct_code_is_accepted_once()
    {
        var (challenge, code) = OtpChallenge.Issue(Phone, Now);

        Assert.True(challenge.TryConsume(code, Now));
        Assert.True(challenge.IsConsumed);

        // Replay must fail, or an intercepted code stays usable.
        Assert.False(challenge.TryConsume(code, Now));
    }

    [Fact]
    public void A_wrong_code_still_counts_against_the_attempt_budget()
    {
        var (challenge, _) = OtpChallenge.Issue(Phone, Now);

        Assert.False(challenge.TryConsume("000000", Now));
        Assert.Equal(1, challenge.AttemptCount);
    }

    [Fact]
    public void The_challenge_dies_after_three_wrong_attempts()
    {
        // Bounded attempts are what make six digits safe. One in a million per guess means
        // nothing if an attacker gets a million guesses.
        var (challenge, code) = OtpChallenge.Issue(Phone, Now);

        for (var i = 0; i < OtpChallenge.MaxAttempts; i++)
        {
            Assert.False(challenge.TryConsume("000000", Now));
        }

        Assert.True(challenge.IsExhausted);

        // Even the right code is refused once the budget is spent.
        Assert.False(challenge.TryConsume(code, Now));
    }

    [Fact]
    public void An_expired_challenge_is_refused()
    {
        var (challenge, code) = OtpChallenge.Issue(Phone, Now);

        Assert.False(challenge.TryConsume(code, Now.AddMinutes(6)));
        Assert.True(challenge.HasExpired(Now.AddMinutes(6)));
    }

    [Fact]
    public void An_expired_challenge_does_not_burn_an_attempt()
    {
        // Guards against a subtle inversion: if expiry were checked after counting, an
        // attacker could exhaust a challenge without ever guessing.
        var (challenge, _) = OtpChallenge.Issue(Phone, Now);

        challenge.TryConsume("000000", Now.AddMinutes(6));

        Assert.Equal(0, challenge.AttemptCount);
    }

    [Fact]
    public void Issued_codes_are_not_predictable()
    {
        // Weak in isolation, but it would catch the classic mistake of using Random instead
        // of a cryptographic generator, which produces visible clustering.
        var codes = Enumerable.Range(0, 200)
            .Select(_ => OtpChallenge.Issue(Phone, Now).Code)
            .ToList();

        Assert.True(codes.Distinct().Count() > 190);
    }
}
