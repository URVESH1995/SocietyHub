using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Payments.Api.Gateway;

public sealed class RazorpayOptions
{
    public const string SectionName = "Razorpay";

    public string KeyId { get; set; } = string.Empty;

    /// <summary>
    /// Signs requests and verifies webhooks.
    ///
    /// Loaded from configuration in development and from Key Vault via managed identity in
    /// production, never from a file that ships. A leaked secret here does not merely read
    /// data — it authorises refunds and payouts.
    /// </summary>
    public string KeySecret { get; set; } = string.Empty;

    /// <summary>Separate from the key secret. Razorpay issues it independently.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// When false, no gateway is called and payments are simulated.
    ///
    /// The default, because the platform must be developable and demonstrable without a
    /// merchant account. Every simulated reference is prefixed so it can never be mistaken for
    /// a real one in a ledger, and a reconciliation report says plainly that it is simulated.
    /// </summary>
    public bool Enabled { get; set; }
}

/// <summary>What the platform needs a payment gateway to do.</summary>
public interface IPaymentGateway
{
    Task<Result<GatewayOrder>> CreateOrderAsync(
        Guid orderId, long amountPaise, CancellationToken cancellationToken = default);

    Task<Result<GatewayRefund>> RefundAsync(
        string gatewayPaymentId,
        long amountPaise,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a webhook body genuinely came from the gateway.
    ///
    /// The single most important method in this file. An unverified webhook endpoint lets
    /// anybody who guesses the URL mark orders paid, and the platform would deliver services
    /// nobody paid for while its ledger insisted otherwise.
    /// </summary>
    bool VerifyWebhookSignature(string payload, string signature);
}

public sealed record GatewayOrder(string GatewayOrderId, long AmountPaise);

public sealed record GatewayRefund(string GatewayRefundId, long AmountPaise);

/// <summary>
/// Razorpay, and a simulator for when it is switched off.
///
/// <para>
/// Both live in one class on purpose. Two implementations behind an interface drift — the
/// simulated one keeps working while the real one grows a bug nobody sees until a merchant
/// account is connected. Here the branch is one <c>if</c> per method, visible at the point it
/// matters.
/// </para>
/// </summary>
public sealed class RazorpayGateway : IPaymentGateway
{
    private const string SimulatedPrefix = "sim_";

    private readonly HttpClient _http;
    private readonly RazorpayOptions _options;
    private readonly ILogger<RazorpayGateway> _logger;

    public RazorpayGateway(
        HttpClient http, RazorpayOptions options, ILogger<RazorpayGateway> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task<Result<GatewayOrder>> CreateOrderAsync(
        Guid orderId, long amountPaise, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return new GatewayOrder($"{SimulatedPrefix}order_{orderId:N}", amountPaise);
        }

        try
        {
            using var response = await _http.PostAsJsonAsync(
                "v1/orders",
                new
                {
                    amount = amountPaise,
                    currency = "INR",

                    // Razorpay's receipt field, capped at 40 characters. Carrying our own
                    // order id makes their dashboard searchable by ours during a dispute,
                    // which is the only time anybody looks at it.
                    receipt = orderId.ToString("N"),
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                _logger.LogError(
                    "Razorpay refused order {OrderId}: {Status} {Body}",
                    orderId, (int)response.StatusCode, body);

                return Error.Failure(
                    "gateway.order_failed", "The payment provider could not create an order.");
            }

            var created = await response.Content.ReadFromJsonAsync<RazorpayOrderResponse>(
                cancellationToken: cancellationToken);

            return created is null
                ? Error.Failure("gateway.bad_response", "The payment provider returned nothing.")
                : new GatewayOrder(created.Id, created.Amount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not reach Razorpay to create order {OrderId}.", orderId);

            return Error.Failure(
                "gateway.unreachable", "The payment provider is unreachable. Try again shortly.");
        }
    }

    /// <summary>
    /// Refunds, whole or partial.
    ///
    /// <paramref name="idempotencyKey"/> is the enrolment id, so the compensation loop asking
    /// twice produces one refund. Razorpay honours it, and the local ledger checks again — two
    /// defences, because this is the operation where a duplicate is money leaving permanently.
    /// </summary>
    public async Task<Result<GatewayRefund>> RefundAsync(
        string gatewayPaymentId,
        long amountPaise,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            // Derived from the key, not random, so a simulated retry returns the same
            // reference — exactly as the real gateway does under an idempotency key.
            return new GatewayRefund($"{SimulatedPrefix}rfnd_{idempotencyKey}", amountPaise);
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"v1/payments/{gatewayPaymentId}/refund")
            {
                Content = JsonContent.Create(new { amount = amountPaise }),
            };

            request.Headers.TryAddWithoutValidation("X-Razorpay-Idempotency-Key", idempotencyKey);

            using var response = await _http.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                _logger.LogError(
                    "Razorpay refused a refund on {PaymentId}: {Status} {Body}",
                    gatewayPaymentId, (int)response.StatusCode, body);

                return Error.Failure(
                    "gateway.refund_failed", "The payment provider could not issue the refund.");
            }

            var refund = await response.Content.ReadFromJsonAsync<RazorpayRefundResponse>(
                cancellationToken: cancellationToken);

            return refund is null
                ? Error.Failure("gateway.bad_response", "The payment provider returned nothing.")
                : new GatewayRefund(refund.Id, refund.Amount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not reach Razorpay to refund {PaymentId}.", gatewayPaymentId);

            return Error.Failure(
                "gateway.unreachable", "The payment provider is unreachable. Try again shortly.");
        }
    }

    /// <summary>
    /// HMAC-SHA256 over the raw body, compared in fixed time.
    ///
    /// The body must be the exact bytes received — re-serialising a parsed object changes
    /// whitespace and key order and the signature never matches, which is the classic way a
    /// webhook endpoint ends up with verification quietly disabled to "make it work".
    /// </summary>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        if (!_options.Enabled)
        {
            // Not "return true". A disabled gateway sends no webhooks, so anything arriving at
            // that endpoint is either a test or somebody probing — and accepting it would make
            // the development configuration a way to forge payments.
            return false;
        }

        if (string.IsNullOrWhiteSpace(signature)
            || string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            return false;
        }

        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(_options.WebhookSecret),
                Encoding.UTF8.GetBytes(payload)));

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature.Trim().ToLower(CultureInfo.InvariantCulture)));
    }

    /// <summary>Whether a reference came from the simulator rather than a real gateway.</summary>
    public static bool IsSimulated(string? reference) =>
        reference?.StartsWith(SimulatedPrefix, StringComparison.Ordinal) ?? false;

    private sealed record RazorpayOrderResponse(string Id, long Amount);

    private sealed record RazorpayRefundResponse(string Id, long Amount);
}
