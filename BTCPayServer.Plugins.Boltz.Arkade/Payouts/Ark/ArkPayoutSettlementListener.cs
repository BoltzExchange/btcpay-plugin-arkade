using System.Threading.Channels;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Intents;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Extensions;
using NArk.Swaps.Models;
using NBitcoin;
using MarkPayoutRequest = BTCPayServer.HostedServices.MarkPayoutRequest;
using PayoutData = BTCPayServer.Data.PayoutData;

namespace BTCPayServer.Plugins.Boltz.Arkade.Payouts.Ark;

/// <summary>
/// Resolves payouts whose spend only *initiated* a transfer: an Ark->BTC chain swap or a
/// Lightning submarine swap (proof carries the swap id in
/// <see cref="ArkPayoutProof.TransferId"/>), or a batch intent (proof carries the intent tx id
/// in <see cref="ArkPayoutProof.IntentTxId"/>). The send path leaves such payouts InProgress;
/// this listener watches swap and intent storage and marks the payout Completed once the
/// transfer settles — for intents with the batch's on-chain commitment txid as final proof —
/// or reverts it to AwaitingPayment (clearing the proof so it can be retried) when the swap
/// fails or is refunded, or the intent is cancelled, expires, or its batch fails. A failed or
/// cancelled intent never left the wallet's VTXOs, so the revert is unconditionally safe.
/// </summary>
public class ArkPayoutSettlementListener(
    ISwapStorage swapStorage,
    IIntentStorage intentStorage,
    ArkPayoutHandler payoutHandler,
    ApplicationDbContextFactory dbContextFactory,
    PullPaymentHostedService pullPaymentHostedService,
    ILogger<ArkPayoutSettlementListener> logger) : BackgroundService
{
    private enum SettlementSource { Swap, Intent }

    /// <summary>
    /// A transfer reaching a terminal state, flattened to what payout resolution needs.
    /// <see cref="Key"/> is the swap id or intent tx id the payout proof carries;
    /// <see cref="Settled"/> is false for every terminal failure (swap Failed/Refunded,
    /// intent Cancelled/BatchFailed); <see cref="OnchainTxId"/> upgrades the proof to a real
    /// txid when available (batch commitment transaction).
    /// </summary>
    private sealed record SettlementSignal(SettlementSource Source, string Key, bool Settled, string? OnchainTxId);

    private readonly Channel<SettlementSignal> _queue = Channel.CreateUnbounded<SettlementSignal>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        swapStorage.SwapsChanged += OnSwapChanged;
        intentStorage.IntentChanged += OnIntentChanged;

        try
        {
            await ReconcilePendingPayouts(stoppingToken);

            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_queue.Reader.TryRead(out var signal))
                {
                    try
                    {
                        await ApplySignal(signal, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to update payouts for {Source} {Key}",
                            signal.Source, signal.Key);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            swapStorage.SwapsChanged -= OnSwapChanged;
            intentStorage.IntentChanged -= OnIntentChanged;
        }
    }

    private void OnSwapChanged(object? sender, ArkSwap swap)
    {
        if (swap.Status.IsTerminalState())
            _queue.Writer.TryWrite(ToSignal(swap));
    }

    private void OnIntentChanged(object? sender, ArkIntent intent)
    {
        if (IsTerminal(intent.State))
            _queue.Writer.TryWrite(ToSignal(intent));
    }

    private static SettlementSignal ToSignal(ArkSwap swap) =>
        new(SettlementSource.Swap, swap.SwapId, swap.Status == ArkSwapStatus.Settled, null);

    private static SettlementSignal ToSignal(ArkIntent intent) =>
        new(SettlementSource.Intent, intent.IntentTxId,
            intent.State == ArkIntentState.BatchSucceeded, intent.CommitmentTransactionId);

    private static bool IsTerminal(ArkIntentState state) =>
        state is ArkIntentState.BatchSucceeded or ArkIntentState.BatchFailed or ArkIntentState.Cancelled;

    /// <summary>
    /// Catches up payouts whose swap or intent reached a terminal state while the server
    /// was down and no storage event fired.
    /// </summary>
    private async Task ReconcilePendingPayouts(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var (payout, proof) in await GetInProgressPayouts(cancellationToken))
            {
                var signal = await TryResolveSignal(proof, cancellationToken);
                if (signal is not null)
                    await ApplySignalToPayout(signal, payout, proof);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reconcile in-progress payouts");
        }
    }

    private async Task<SettlementSignal?> TryResolveSignal(ArkPayoutProof proof, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(proof.TransferId))
        {
            var swap = (await swapStorage.GetSwaps(
                    swapIds: [proof.TransferId!],
                    cancellationToken: cancellationToken))
                .FirstOrDefault();
            return swap is not null && swap.Status.IsTerminalState() ? ToSignal(swap) : null;
        }

        if (!string.IsNullOrEmpty(proof.IntentTxId))
        {
            var intent = (await intentStorage.GetIntents(
                    intentTxIds: [proof.IntentTxId!],
                    cancellationToken: cancellationToken))
                .FirstOrDefault();
            return intent is not null && IsTerminal(intent.State) ? ToSignal(intent) : null;
        }

        return null;
    }

    private async Task ApplySignal(SettlementSignal signal, CancellationToken cancellationToken)
    {
        foreach (var (payout, proof) in await GetInProgressPayouts(cancellationToken))
        {
            if (Matches(proof, signal))
                await ApplySignalToPayout(signal, payout, proof);
        }
    }

    private static bool Matches(ArkPayoutProof proof, SettlementSignal signal) => signal.Source switch
    {
        SettlementSource.Swap => proof.TransferId == signal.Key,
        SettlementSource.Intent => proof.IntentTxId == signal.Key,
        _ => false
    };

    private async Task ApplySignalToPayout(SettlementSignal signal, PayoutData payout, ArkPayoutProof proof)
    {
        if (signal.Settled)
        {
            if (signal.OnchainTxId is not null && uint256.TryParse(signal.OnchainTxId, out var onchainTxId))
                proof.TransactionId = onchainTxId;

            var completed = await pullPaymentHostedService.MarkPaid(new MarkPayoutRequest
            {
                PayoutId = payout.Id,
                State = PayoutState.Completed,
                Proof = payoutHandler.SerializeProof(proof)
            });
            logger.LogInformation(
                "{Source} {Key} settled; marking payout {PayoutId} completed ({Result})",
                signal.Source, signal.Key, payout.Id, completed);
        }
        else
        {
            var reverted = await pullPaymentHostedService.MarkPaid(new MarkPayoutRequest
            {
                PayoutId = payout.Id,
                State = PayoutState.AwaitingPayment
            });
            logger.LogWarning(
                "{Source} {Key} terminated without settling; reverting payout {PayoutId} to awaiting payment for retry ({Result})",
                signal.Source, signal.Key, payout.Id, reverted);
        }
    }

    private async Task<List<(PayoutData Payout, ArkPayoutProof Proof)>> GetInProgressPayouts(
        CancellationToken cancellationToken)
    {
        var payoutMethodId = payoutHandler.PayoutMethodId.ToString();
        await using var ctx = dbContextFactory.CreateContext();
        ctx.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var payouts = await ctx.Payouts
            .Where(p => p.PayoutMethodId == payoutMethodId && p.State == PayoutState.InProgress)
            .ToListAsync(cancellationToken);

        return payouts
            .Select(p => (Payout: p, Proof: payoutHandler.ParseProof(p) as ArkPayoutProof))
            .Where(t => !string.IsNullOrEmpty(t.Proof?.TransferId) || !string.IsNullOrEmpty(t.Proof?.IntentTxId))
            .Select(t => (t.Payout, Proof: t.Proof!))
            .ToList();
    }
}
