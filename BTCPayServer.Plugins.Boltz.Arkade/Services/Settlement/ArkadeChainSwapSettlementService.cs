using System.Text.Json;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Safety;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Swaps.Extensions;
using NArk.Swaps.Models;
using NArk.Swaps.Services;
using NArk.Core.Transport;
using NBitcoin;

namespace BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;

public class ArkadeChainSwapSettlementService(
    SwapsManagementService swapsManagementService,
    IClientTransport clientTransport,
    ISwapStorage swapStorage,
    ISafetyService safetyService,
    ILogger<ArkadeChainSwapSettlementService>? logger = null) : ISettlementService
{
    public bool Available => true;
    public string? UnavailableReason => null;

    public async Task<SettlementTransferResult> InitiateTransfer(
        SettlementTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.AmountSats <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.AmountSats), "Settlement amount must be positive.");

        if (!IsBitcoinMainchain(request.Destination))
            throw new NotSupportedException(
                $"Settlement destination {request.Destination.Network}/{request.Destination.Asset} is not supported.");

        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var destination = BitcoinAddress.Create(request.Destination.Address, serverInfo.Network);

        // Serialize settlement swap creation per wallet. Without this, a background
        // auto-settlement and a concurrent manual send can both select overlapping VTXOs
        // before either swap reserves them, over-committing the wallet's funds. Holding the
        // lock across InitiateArkToBtcChainSwap (which spends the VTXOs to the lockup address)
        // makes coin selection see the previous swap's reservation.
        await using var walletLock = await safetyService.LockKeyAsync(
            $"settlement::{request.WalletId}", cancellationToken);

        var swapId = await swapsManagementService.InitiateArkToBtcChainSwap(
            request.WalletId,
            request.AmountSats,
            destination,
            cancellationToken);

        var swap = (await swapStorage.GetSwaps(
                walletIds: [request.WalletId],
                swapIds: [swapId],
                cancellationToken: cancellationToken))
            .FirstOrDefault();

        // The swap is created AND funded at this point — a failure reading the
        // informational amount metadata must not surface as a failed transfer,
        // or the caller retries and funds a second swap. Fall back to the
        // requested amount; the swap id is what matters.
        ChainResponse? boltzResponse = null;
        if (swap?.Get(SwapMetadata.BoltzResponse) is { } raw)
        {
            try
            {
                boltzResponse = JsonSerializer.Deserialize<ChainResponse>(raw);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "Failed to parse Boltz response metadata for settlement swap {SwapId}; reporting requested amount",
                    swapId);
            }
        }

        var sourceAmountSats = boltzResponse?.LockupDetails?.Amount ?? request.AmountSats;
        var destinationAmountSats = boltzResponse?.ClaimDetails?.Amount ?? request.AmountSats;
        var feesPaidSats = Math.Max(0, sourceAmountSats - destinationAmountSats);

        return new SettlementTransferResult(
            swapId,
            sourceAmountSats,
            destinationAmountSats,
            feesPaidSats);
    }

    private static bool IsBitcoinMainchain(SettlementDestination destination) =>
        destination.Network.Equals("bitcoin", StringComparison.OrdinalIgnoreCase) &&
        destination.Asset.Equals("BTC", StringComparison.OrdinalIgnoreCase);
}
