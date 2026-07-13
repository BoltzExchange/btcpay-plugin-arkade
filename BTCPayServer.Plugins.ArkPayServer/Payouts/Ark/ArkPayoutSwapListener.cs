using System.Threading.Channels;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Extensions;
using NArk.Swaps.Models;
using MarkPayoutRequest = BTCPayServer.HostedServices.MarkPayoutRequest;
using PayoutData = BTCPayServer.Data.PayoutData;

namespace BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;

/// <summary>
/// Completes payouts settled through a swap: an Ark->BTC chain swap for bitcoin
/// destinations, or a Lightning submarine swap for BOLT11 destinations. The send
/// path leaves such payouts InProgress with the swap id as proof
/// (<see cref="ArkPayoutProof.TransferId"/>); this listener watches the swap
/// storage and marks the payout Completed once the swap settles, or reverts it
/// to AwaitingPayment (clearing the proof so it can be retried) when the swap
/// fails or is refunded.
/// </summary>
public class ArkPayoutSwapListener(
    ISwapStorage swapStorage,
    ArkPayoutHandler payoutHandler,
    ApplicationDbContextFactory dbContextFactory,
    PullPaymentHostedService pullPaymentHostedService,
    ILogger<ArkPayoutSwapListener> logger) : BackgroundService
{
    private readonly Channel<ArkSwap> _swapQueue = Channel.CreateUnbounded<ArkSwap>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        swapStorage.SwapsChanged += OnSwapChanged;

        try
        {
            await ReconcilePendingPayouts(stoppingToken);

            while (await _swapQueue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_swapQueue.Reader.TryRead(out var swap))
                {
                    try
                    {
                        await ApplySwap(swap, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to update payouts for swap {SwapId}", swap.SwapId);
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
        }
    }

    private void OnSwapChanged(object? sender, ArkSwap swap)
    {
        if (swap.Status.IsTerminalState())
            _swapQueue.Writer.TryWrite(swap);
    }

    /// <summary>
    /// Catches up payouts whose swap reached a terminal state while the server
    /// was down and no <see cref="ISwapStorage.SwapsChanged"/> event fired.
    /// </summary>
    private async Task ReconcilePendingPayouts(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var (payout, proof) in await GetInProgressSwapPayouts(cancellationToken))
            {
                var swap = (await swapStorage.GetSwaps(
                        swapIds: [proof.TransferId!],
                        cancellationToken: cancellationToken))
                    .FirstOrDefault();

                if (swap is not null && swap.Status.IsTerminalState())
                    await ApplySwapToPayout(swap, payout, proof);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reconcile in-progress swap payouts");
        }
    }

    private async Task ApplySwap(ArkSwap swap, CancellationToken cancellationToken)
    {
        foreach (var (payout, proof) in await GetInProgressSwapPayouts(cancellationToken))
        {
            if (proof.TransferId == swap.SwapId)
                await ApplySwapToPayout(swap, payout, proof);
        }
    }

    private async Task ApplySwapToPayout(ArkSwap swap, PayoutData payout, ArkPayoutProof proof)
    {
        switch (swap.Status)
        {
            case ArkSwapStatus.Settled:
                var completed = await pullPaymentHostedService.MarkPaid(new MarkPayoutRequest
                {
                    PayoutId = payout.Id,
                    State = PayoutState.Completed,
                    Proof = payoutHandler.SerializeProof(proof)
                });
                logger.LogInformation(
                    "Swap {SwapId} settled; marking payout {PayoutId} completed ({Result})",
                    swap.SwapId, payout.Id, completed);
                break;
            case ArkSwapStatus.Failed or ArkSwapStatus.Refunded:
                var reverted = await pullPaymentHostedService.MarkPaid(new MarkPayoutRequest
                {
                    PayoutId = payout.Id,
                    State = PayoutState.AwaitingPayment
                });
                logger.LogWarning(
                    "Swap {SwapId} ended {Status}; reverting payout {PayoutId} to awaiting payment for retry ({Result})",
                    swap.SwapId, swap.Status, payout.Id, reverted);
                break;
        }
    }

    private async Task<List<(PayoutData Payout, ArkPayoutProof Proof)>> GetInProgressSwapPayouts(
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
            .Where(t => !string.IsNullOrEmpty(t.Proof?.TransferId))
            .Select(t => (t.Payout, Proof: t.Proof!))
            .ToList();
    }
}
