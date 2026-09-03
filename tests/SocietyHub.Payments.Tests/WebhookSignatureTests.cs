using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SocietyHub.Payments.Api.Gateway;

namespace SocietyHub.Payments.Tests;

/// <summary>
/// Webhook signature verification.
///
/// The webhook endpoint is unauthenticated by necessity — a payment gateway has no token. The
/// signature <em>is</em> the authentication, so this is the single most security-critical piece
/// of the payments service: without it, anybody who guesses the URL can mark orders paid, and
/// the platform delivers services nobody paid for while its ledger insists otherwise.
/// </summary>
public sealed class WebhookSignatureTests
{
    private const string Secret = "whsec_test_secret_value";

    private static RazorpayGateway Gateway(bool enabled = true, string secret = Secret) =>
        new(new HttpClient(),
            new RazorpayOptions { Enabled = enabled, WebhookSecret = secret },
            NullLogger<RazorpayGateway>.Instance);

    private static string Sign(string payload, string secret = Secret) =>
        Convert.ToHexStringLower(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

    [Fact]
    public void A_correctly_signed_payload_is_accepted()
    {
        const string payload = """{"event":"payment.captured"}""";

        Assert.True(Gateway().VerifyWebhookSignature(payload, Sign(payload)));
    }

    [Fact]
    public void A_payload_signed_with_the_wrong_secret_is_rejected()
    {
        const string payload = """{"event":"payment.captured"}""";

        Assert.False(
            Gateway().VerifyWebhookSignature(payload, Sign(payload, "the_wrong_secret")));
    }

    [Fact]
    public void A_tampered_payload_is_rejected()
    {
        // The attack this exists for: take a real captured-payment webhook and change the
        // amount or the order id.
        const string original = """{"event":"payment.captured","amount":100}""";
        const string tampered = """{"event":"payment.captured","amount":999999}""";

        Assert.False(Gateway().VerifyWebhookSignature(tampered, Sign(original)));
    }

    [Fact]
    public void A_missing_signature_is_rejected()
    {
        Assert.False(Gateway().VerifyWebhookSignature("{}", string.Empty));
    }

    [Fact]
    public void Whitespace_and_case_in_the_header_are_tolerated()
    {
        // Proxies trim and normalise headers. Rejecting over that produces a webhook endpoint
        // that fails only in production, which is where somebody disables verification to
        // "make it work".
        const string payload = """{"event":"payment.captured"}""";
        var signature = Sign(payload).ToUpperInvariant();

        Assert.True(Gateway().VerifyWebhookSignature(payload, $"  {signature}  "));
    }

    [Fact]
    public void A_disabled_gateway_rejects_everything_rather_than_trusting_it()
    {
        // Not "return true". A disabled gateway sends no webhooks, so anything arriving is a
        // test or somebody probing — and accepting it would make the development configuration
        // a way to forge payments.
        const string payload = """{"event":"payment.captured"}""";

        Assert.False(
            Gateway(enabled: false).VerifyWebhookSignature(payload, Sign(payload)));
    }

    [Fact]
    public void No_configured_secret_rejects_everything()
    {
        // A blank secret would otherwise sign to a predictable value that anybody could
        // reproduce, which is worse than having no verification at all because it looks
        // verified.
        const string payload = """{"event":"payment.captured"}""";

        Assert.False(
            Gateway(secret: string.Empty).VerifyWebhookSignature(payload, Sign(payload, "")));
    }

    [Fact]
    public async Task Simulated_references_are_marked_so_they_cannot_be_mistaken_for_real_ones()
    {
        // A ledger that mixes simulated money with real money is a ledger nobody can act on.
        var gateway = Gateway(enabled: false);

        var order = await gateway.CreateOrderAsync(Guid.CreateVersion7(), 50_000);

        Assert.True(order.IsSuccess);
        Assert.True(RazorpayGateway.IsSimulated(order.Value.GatewayOrderId));
        Assert.False(RazorpayGateway.IsSimulated("order_NkR9pQ2mLxYz"));
    }

    [Fact]
    public async Task A_simulated_refund_returns_the_same_reference_for_the_same_key()
    {
        // Mirrors what the real gateway does under an idempotency key. A simulator that
        // returned a fresh reference each time would hide a double-refund bug until the day a
        // merchant account was connected.
        var gateway = Gateway(enabled: false);
        var key = Guid.CreateVersion7().ToString("N");

        var first = await gateway.RefundAsync("pay_1", 10_000, key);
        var second = await gateway.RefundAsync("pay_1", 10_000, key);

        Assert.Equal(first.Value.GatewayRefundId, second.Value.GatewayRefundId);
    }
}
