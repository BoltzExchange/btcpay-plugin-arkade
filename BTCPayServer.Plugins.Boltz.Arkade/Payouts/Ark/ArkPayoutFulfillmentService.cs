using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.Boltz.Arkade.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MarkPayoutRequest = BTCPayServer.HostedServices.MarkPayoutRequest;
using PayoutData = BTCPayServer.Data.PayoutData;

namespace BTCPayServer.Plugins.Boltz.Arkade.Payouts.Ark;

/// <summary>
/// The single path through which a spend fulfills Arkade payouts. Serializes every payout
/// payment — automated processor and Send wizard alike — behind a global lock with
/// lock-then-spend ordering: the payouts are re-validated under the lock
/// <b>before</b> any funds move, so two concurrent payment attempts can never both spend
/// payouts at the same time.
/// </summary>
public class ArkPayoutFulfillmentService(
    ArkPayoutHandler payoutHandler,
    ApplicationDbContextFactory dbContextFactory,
    PullPaymentHostedService pullPaymentHostedService,
    ILogger<ArkPayoutFulfillmentService> logger)
{
    private readonly SemaphoreSlim _fulfillmentLock = new(1, 1);

    /// <summary>
    /// Executes <paramref name="spend"/> on behalf of the given payouts. The global fulfillment
    /// lock is acquired up front (no waiting) and each payout is re-loaded and re-validated
    /// under the lock; if another fulfillment is running or any payout is no longer awaiting
    /// payment, the spend does <b>not</b> execute. On success each payout is marked
    /// with the proof derived from the spend result: a real txid completes it, a swap or
    /// intent id leaves it InProgress for <see cref="ArkPayoutSettlementListener"/> to resolve.
    /// </summary>
    public async Task<PayoutFulfillmentResult> FulfillPayouts(
        IReadOnlyList<string> payoutIds,
        Func<CancellationToken, Task<SpendResult>> spend,
        CancellationToken cancellationToken)
    {
        var ids = payoutIds.Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
            throw new ArgumentException("At least one payout id is required.", nameof(payoutIds));

        if (!await _fulfillmentLock.WaitAsync(0, cancellationToken))
            return PayoutFulfillmentResult.NotExecuted(
                "Another Arkade payout fulfillment is already in progress. Try again shortly.");

        try
        {
            var payouts = await LoadPayouts(ids, cancellationToken);
            foreach (var id in ids)
            {
                if (!payouts.TryGetValue(id, out var payout))
                    return PayoutFulfillmentResult.NotExecuted($"Payout {id} was not found.");
                if (payout.GetPayoutMethodId() != payoutHandler.PayoutMethodId)
                    return PayoutFulfillmentResult.NotExecuted($"Payout {id} is not an Arkade payout.");
                if (payout.State != PayoutState.AwaitingPayment || payout.Proof is not null)
                    return PayoutFulfillmentResult.NotExecuted(
                        $"Payout {id} is no longer awaiting payment (state: {payout.State}).");
            }

            var result = await spend(cancellationToken);

            var outcomes = new List<PayoutFulfillmentOutcome>(ids.Length);
            foreach (var id in ids)
            {
                var proof = ArkPayoutProof.FromSpendResult(result);
                var state = proof.ResolvedPayoutState;
                if (state is { } resolved)
                {
                    try
                    {
                        await pullPaymentHostedService.MarkPaid(new MarkPayoutRequest
                        {
                            PayoutId = id,
                            State = resolved,
                            Proof = payoutHandler.SerializeProof(proof)
                        });
                    }
                    catch (Exception ex)
                    {
                        // The spend already executed; losing the mark leaves the payout
                        // AwaitingPayment and re-payable. Nothing can safely undo the spend
                        // here, so make the inconsistency loud for manual reconciliation.
                        logger.LogError(ex,
                            "Spend succeeded ({Proof}) but marking payout {PayoutId} {State} failed; " +
                            "the payout still shows AwaitingPayment and must be reconciled manually",
                            proof.Id, id, resolved);
                        state = null;
                    }
                }
                else
                {
                    logger.LogWarning(
                        "Spend for payout {PayoutId} produced no usable proof; payout stays awaiting payment", id);
                }

                outcomes.Add(new PayoutFulfillmentOutcome(id, proof, state));
            }

            return new PayoutFulfillmentResult(true, null, outcomes);
        }
        finally
        {
            _fulfillmentLock.Release();
        }
    }

    private async Task<Dictionary<string, PayoutData>> LoadPayouts(
        string[] payoutIds, CancellationToken cancellationToken)
    {
        await using var ctx = dbContextFactory.CreateContext();
        ctx.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        return await ctx.Payouts
            .Where(p => payoutIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
    }
}

/// <summary>
/// Outcome of <see cref="ArkPayoutFulfillmentService.FulfillPayouts"/>. When
/// <see cref="Executed"/> is false the spend never ran and <see cref="Error"/> says why;
/// otherwise <see cref="Outcomes"/> carries the proof and resulting state per payout.
/// </summary>
public sealed record PayoutFulfillmentResult(
    bool Executed,
    string? Error,
    IReadOnlyList<PayoutFulfillmentOutcome> Outcomes)
{
    public static PayoutFulfillmentResult NotExecuted(string error) => new(false, error, []);
}

/// <summary>Per-payout result: the persisted proof and the state it resolved to (null when the payout was left untouched).</summary>
public sealed record PayoutFulfillmentOutcome(string PayoutId, ArkPayoutProof Proof, PayoutState? State);
