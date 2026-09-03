using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Payments.Api.Domain;
using SocietyHub.Payments.Api.Gateway;
using SocietyHub.Payments.Api.Persistence;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;

namespace SocietyHub.Payments.Api.Features;

public sealed record CreateOrderRequest(string Purpose, Guid ReferenceId, long AmountPaise);

public sealed record ConfirmPaymentRequest(string GatewayPaymentId, long CapturedPaise);

public sealed record ReconciliationView(
    long CapturedPaise,
    long RefundedPaise,
    long NetPaise,
    int OrderCount,
    int SimulatedOrderCount);

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/payments").WithTags("Payments");

        group.MapPost("/orders", CreateOrderAsync)
             .RequireAuthorization(SocietyHubPolicies.ResidentAccess)
             .WithSummary("Creates a payment order for a drive enrolment.");

        group.MapPost("/orders/{id:guid}/confirm", ConfirmAsync)
             .RequireAuthorization(SocietyHubPolicies.ResidentAccess)
             .WithSummary("Records a capture reported by the client. Idempotent.");

        group.MapGet("/orders/{id:guid}", GetOrderAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithSummary("One order with its ledger.");

        group.MapGet("/reconciliation", ReconcileAsync)
             .RequireAuthorization(SocietyHubPolicies.SocietyAdministration)
             .WithSummary("What the society has paid, been refunded, and is holding.");

        // Unauthenticated by necessity — the gateway has no token. The signature is the
        // authentication, which is why verification is not optional and why a disabled gateway
        // rejects everything rather than trusting it.
        app.MapPost("/api/payments/webhook", WebhookAsync)
           .AllowAnonymous()
           .WithTags("Payments")
           .WithSummary("Razorpay webhook. Verified by HMAC signature.");

        return app;
    }

    private static async Task<IResult> CreateOrderAsync(
        CreateOrderRequest request,
        PaymentsDbContext context,
        IPaymentGateway gateway,
        ITenantContext tenant,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (request.AmountPaise <= 0)
        {
            return Error.Validation(
                "payment.bad_amount", "An order must be for more than zero.").ToProblem();
        }

        // Idempotent on the thing being paid for. A resident tapping Join twice on a slow
        // connection must reach one order, not two charges.
        var existing = await context.Orders
            .Include(o => o.Ledger)
            .FirstOrDefaultAsync(
                o => o.ReferenceId == request.ReferenceId && o.Purpose == request.Purpose,
                cancellationToken);

        if (existing is not null)
        {
            return Results.Ok(new
            {
                id = existing.Id,
                gatewayOrderId = existing.GatewayOrderId,
                amountPaise = existing.AmountPaise,
                status = existing.Status.ToString(),
            });
        }

        var now = timeProvider.GetUtcNow();

        var order = new PaymentOrder(
            Guid.CreateVersion7(),
            tenant.RequireSocietyId(),
            currentUser.RequireUserId(),
            request.Purpose,
            request.ReferenceId,
            request.AmountPaise,
            now);

        var gatewayOrder = await gateway.CreateOrderAsync(
            order.Id, order.AmountPaise, cancellationToken);

        if (gatewayOrder.IsFailure)
        {
            // Not persisted. An order with no gateway counterpart is one a resident can never
            // pay and nobody will ever clean up.
            return gatewayOrder.Error.ToProblem();
        }

        order.AttachGatewayOrder(gatewayOrder.Value.GatewayOrderId, now);

        context.Orders.Add(order);
        await context.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/payments/orders/{order.Id}", new
        {
            id = order.Id,
            gatewayOrderId = order.GatewayOrderId,
            amountPaise = order.AmountPaise,
        });
    }

    /// <summary>
    /// Records a capture the client reported.
    ///
    /// Deliberately duplicated by the webhook. A client callback is lost whenever somebody
    /// closes the tab after paying, and a webhook is lost whenever the platform is restarting —
    /// so both paths exist and both are idempotent, and the order is whichever arrives first.
    /// </summary>
    private static async Task<IResult> ConfirmAsync(
        Guid id,
        ConfirmPaymentRequest request,
        PaymentsDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var order = await context.Orders
            .Include(o => o.Ledger)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order is null)
        {
            return Error.NotFound("payment.not_found", "No such order.").ToProblem();
        }

        var result = order.MarkPaid(
            request.GatewayPaymentId, request.CapturedPaise, timeProvider.GetUtcNow());

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        await context.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { order.Id, status = order.Status.ToString() });
    }

    private static async Task<IResult> GetOrderAsync(
        Guid id, PaymentsDbContext context, CancellationToken cancellationToken)
    {
        var order = await context.Orders
            .AsNoTracking()
            .Include(o => o.Ledger)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order is null)
        {
            return Error.NotFound("payment.not_found", "No such order.").ToProblem();
        }

        return Results.Ok(new
        {
            order.Id,
            order.Purpose,
            order.ReferenceId,
            order.AmountPaise,
            order.RefundedPaise,
            order.NetPaise,
            Status = order.Status.ToString(),
            order.GatewayOrderId,
            order.GatewayPaymentId,
            Ledger = order.Ledger
                .OrderBy(e => e.OccurredAtUtc)
                .Select(e => new
                {
                    Kind = e.Kind.ToString(),
                    e.AmountPaise,
                    e.GatewayReference,
                    e.Reason,
                    e.OccurredAtUtc,
                }),
        });
    }

    /// <summary>
    /// What the society has paid and been refunded, summed from the ledger.
    ///
    /// A <c>SUM</c> over signed entries rather than a procedure. Reconciliation that requires
    /// somebody to write the arithmetic correctly is reconciliation that will one day be
    /// written incorrectly.
    /// </summary>
    private static async Task<IResult> ReconcileAsync(
        PaymentsDbContext context, CancellationToken cancellationToken)
    {
        var entries = await context.Ledger
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var orders = await context.Orders
            .AsNoTracking()
            .Select(o => new { o.GatewayOrderId })
            .ToListAsync(cancellationToken);

        var captured = entries
            .Where(e => e.Kind == LedgerEntryKind.Capture)
            .Sum(e => e.AmountPaise);

        var refunded = -entries
            .Where(e => e.Kind == LedgerEntryKind.Refund)
            .Sum(e => e.AmountPaise);

        return Results.Ok(new ReconciliationView(
            captured,
            refunded,

            // Summing the signed column, which is the point of signing it.
            entries.Sum(e => e.AmountPaise),
            orders.Count,

            // Reported separately and never hidden. A total that silently mixes simulated
            // money with real money is a total nobody can act on.
            orders.Count(o => RazorpayGateway.IsSimulated(o.GatewayOrderId))));
    }

    /// <summary>
    /// The gateway's webhook.
    ///
    /// Reads the raw body before anything parses it, because the signature is over exactly
    /// those bytes. Re-serialising a parsed object changes whitespace and key order, the
    /// signature stops matching, and the usual response to that is to turn verification off.
    /// </summary>
    private static async Task<IResult> WebhookAsync(
        HttpRequest httpRequest,
        PaymentsDbContext context,
        IPaymentGateway gateway,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("PaymentWebhook");

        using var reader = new StreamReader(httpRequest.Body);
        var payload = await reader.ReadToEndAsync(cancellationToken);

        var signature = httpRequest.Headers["X-Razorpay-Signature"].ToString();

        if (!gateway.VerifyWebhookSignature(payload, signature))
        {
            // 400 rather than 401, because a gateway retrying on a 401 forever is worse than
            // one that gives up. The log line is what matters: an unverified webhook is either
            // a misconfiguration or somebody probing, and both need looking at.
            logger.LogWarning("Rejected a webhook with an invalid or missing signature.");

            return Results.BadRequest(new { error = "invalid_signature" });
        }

        WebhookPayload? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<WebhookPayload>(
                payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "A signed webhook could not be parsed.");

            // 200 on purpose. The signature was valid, so this is our problem, not theirs —
            // and a gateway retrying a body we will never parse achieves nothing.
            return Results.Ok();
        }

        if (parsed?.Payload?.Payment?.Entity is not { } payment)
        {
            return Results.Ok();
        }

        var order = await context.Orders
            .Include(o => o.Ledger)
            .FirstOrDefaultAsync(o => o.GatewayOrderId == payment.OrderId, cancellationToken);

        if (order is null)
        {
            // A webhook for an order we do not have. Acknowledged rather than retried: the
            // gateway would resend for hours and the order is never going to appear.
            logger.LogWarning(
                "Webhook for unknown gateway order {GatewayOrderId}.", payment.OrderId);

            return Results.Ok();
        }

        var now = timeProvider.GetUtcNow();

        var result = parsed.Event switch
        {
            "payment.captured" => order.MarkPaid(payment.Id, payment.Amount, now),
            "payment.failed" => order.MarkFailed(payment.ErrorDescription ?? "Declined.", now),
            _ => Result.Success(),
        };

        if (result.IsFailure)
        {
            // Logged and acknowledged. A failure here is almost always a duplicate or a late
            // delivery, and asking the gateway to retry would just replay it.
            logger.LogWarning(
                "Webhook {Event} on order {OrderId} was not applied: {Code}",
                parsed.Event, order.Id, result.Error.Code);
        }

        await context.SaveChangesAsync(cancellationToken);

        return Results.Ok();
    }

    private sealed record WebhookPayload(string Event, WebhookBody? Payload);

    private sealed record WebhookBody(WebhookPaymentWrapper? Payment);

    private sealed record WebhookPaymentWrapper(WebhookPayment? Entity);

    private sealed record WebhookPayment(
        string Id, string OrderId, long Amount, string? ErrorDescription);
}
