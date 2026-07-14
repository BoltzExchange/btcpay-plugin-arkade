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
/// in <see cref="ArkPayoutProof.IntentTxId"/>). Marks the payout Completed once the transfer
/// settles — for intents with the batch's on-chain commitment txid as final proof — or reverts
/// it to AwaitingPayment for retry when the transfer terminally fails (a failed or cancelled
/// intent never left the wallet's VTXOs, so the revert is unconditionally safe).
/// <para>
/// Events are contentless wake-ups: every reconcile pass re-reads each pending payout's
/// transfer from storage, so ordering doesn't matter — a fast transfer can settle before any
/// payout carries its id, but whichever write lands second still triggers a covering pass.
/// </para>
/// </summary>
public class ArkPayoutSettlementListener(
    ISwapStorage swapStorage,
    IIntentStorage intentStorage,
    ArkPayoutHandler payoutHandler,
    ApplicationDbContextFactory dbContextFactory,
    PullPaymentHostedService pullPaymentHostedService,
    EventAggregator eventAggregator,
    ILogger<ArkPayoutSettlementListener> logger) : BackgroundService
{
    private enum SettlementSource { Swap, Intent }

    /// <summary>
    /// A transfer's terminal state, flattened to what payout resolution needs.
    /// <see cref="Settled"/> is false for every terminal failure; <see cref="OnchainTxId"/>
    /// upgrades the proof to the batch commitment txid when available.
    /// </summary>
    private sealed record SettlementSignal(SettlementSource Source, string Key, bool Settled, string? OnchainTxId);

    /// <summary>
    /// Single-slot wake-up — only safe while messages carry no data: an event arriving
    /// mid-pass lands in the emptied slot and is covered by the next pass.
    /// </summary>
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        swapStorage.SwapsChanged += OnSwapChanged;
        intentStorage.IntentChanged += OnIntentChanged;
        using var payoutSubscription = eventAggregator.Subscribe<PayoutEvent>(OnPayoutEvent);

        try
        {
            await ReconcilePendingPayouts(stoppingToken);

            while (await _wake.Reader.WaitToReadAsync(stoppingToken))
            {
                _wake.Reader.TryRead(out _);
                await ReconcilePendingPayouts(stoppingToken);
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
            _wake.Writer.TryWrite(true);
    }

    private void OnIntentChanged(object? sender, ArkIntent intent)
    {
        if (IsTerminal(intent.State))
            _wake.Writer.TryWrite(true);
    }

    private void OnPayoutEvent(PayoutEvent evt)
    {
        if (evt.Payout.State == PayoutState.InProgress &&
            evt.Payout.PayoutMethodId == payoutHandler.PayoutMethodId.ToString())
            _wake.Writer.TryWrite(true);
    }

    private static SettlementSignal ToSignal(ArkSwap swap) =>
        new(SettlementSource.Swap, swap.SwapId, swap.Status == ArkSwapStatus.Settled, null);

    private static SettlementSignal ToSignal(ArkIntent intent) =>
        new(SettlementSource.Intent, intent.IntentTxId,
            intent.State == ArkIntentState.BatchSucceeded, intent.CommitmentTransactionId);

    private static bool IsTerminal(ArkIntentState state) =>
        state is ArkIntentState.BatchSucceeded or ArkIntentState.BatchFailed or ArkIntentState.Cancelled;

    /// <summary>
    /// Resolves every InProgress payout whose transfer already reached a terminal state.
    /// Runs once at startup (catch-up) and again on every wake-up.
    /// </summary>
    private async Task ReconcilePendingPayouts(CancellationToken cancellationToken)
    {
        List<(PayoutData Payout, ArkPayoutProof Proof)> pending;
        try
        {
            pending = await GetInProgressPayouts(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load in-progress payouts");
            return;
        }

        foreach (var (payout, proof) in pending)
        {
            try
            {
                var signal = await TryResolveSignal(proof, cancellationToken);
                if (signal is not null)
                    await ApplySignalToPayout(signal, payout, proof);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to resolve payout {PayoutId}", payout.Id);
            }
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
