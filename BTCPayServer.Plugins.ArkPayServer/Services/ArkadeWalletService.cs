using BTCPayServer.Plugins.ArkPayServer.Models;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Wallet-level queries and maintenance shared by <see cref="Controllers.ArkController"/> (MVC)
/// and <see cref="Controllers.ArkGreenfieldController"/> (Greenfield REST).
/// </summary>
public class ArkadeWalletService(
    ISpendingService arkadeSpender,
    IBitcoinBlockchain bitcoinTimeChainProvider,
    IClientTransport clientTransport,
    IVtxoStorage vtxoStorage,
    IIntentStorage intentStorage,
    IContractStorage contractStorage,
    VtxoSynchronizationService vtxoSyncService,
    BoardingUtxoSyncService boardingUtxoSyncService)
{
    /// <summary>
    /// Compute the wallet's balance breakdown (available/locked/recoverable/unspendable/boarding),
    /// all in satoshis.
    /// </summary>
    public async Task<ArkBalancesViewModel> GetBalances(string walletId, CancellationToken cancellationToken)
    {
        var currentTime = await bitcoinTimeChainProvider.GetChainTime(cancellationToken);
        var allCoins = await arkadeSpender.GetAvailableCoins(walletId, cancellationToken);

        var coinsByRecoverableStatus = allCoins.ToLookup(coin => coin.IsRecoverable(currentTime));

        var allSpendableOutpoints = allCoins.Select(coin => coin.Outpoint).ToHashSet();

        // Unspendable: unspent VTXOs that don't pass contract conditions yet (e.g., HTLC timelock not reached).
        var all = await vtxoStorage.GetVtxos(
            walletIds: [walletId],
            includeSpent: false,
            cancellationToken: cancellationToken);

        var unspendableBalance = all
            .Where(vtxo => !allSpendableOutpoints.Contains(vtxo.OutPoint))
            .Sum(vtxo => (long)vtxo.Amount);

        var availableBalance = coinsByRecoverableStatus[false]
            .Where(coin => !coin.Unrolled)
            .Sum(coin => coin.Amount.Satoshi);
        var recoverableBalance = coinsByRecoverableStatus[true].Sum(coin => coin.Amount.Satoshi);
        var boardingBalance = allCoins.Where(coin => coin.Unrolled).Sum(coin => coin.Amount.Satoshi);

        // Locked: VTXOs committed to active intents (WaitingToSubmit, WaitingForBatch)
        var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(walletId, cancellationToken);
        var lockedSet = new HashSet<OutPoint>(lockedOutpoints);
        var lockedBalance = coinsByRecoverableStatus[false]
            .Where(coin => !coin.Unrolled && lockedSet.Contains(coin.Outpoint))
            .Sum(coin => coin.Amount.Satoshi);

        return new ArkBalancesViewModel
        {
            AvailableBalance = availableBalance - lockedBalance,
            LockedBalance = lockedBalance,
            RecoverableBalance = recoverableBalance,
            UnspendableBalance = unspendableBalance,
            BoardingBalance = boardingBalance,
        };
    }

    /// <summary>
    /// Find the wallet's active manually generated receive address (a payment contract still
    /// awaiting funds), if any.
    /// </summary>
    public async Task<string?> FindManualReceiveAddress(string walletId, CancellationToken cancellationToken)
    {
        var existingContracts = await contractStorage.GetContracts(
            walletIds: [walletId],
            isActive: true,
            contractTypes: [ArkPaymentContract.ContractType],
            cancellationToken: cancellationToken);

        var manualContract = existingContracts
            .FirstOrDefault(c =>
                c.ActivityState == ContractActivityState.AwaitingFundsBeforeDeactivate &&
                c.Metadata?.GetValueOrDefault("Source") == "manual");

        if (manualContract == null) return null;

        var terms = await clientTransport.GetServerInfoAsync(cancellationToken);
        var script = Script.FromHex(manualContract.Script);
        var serverKey = terms.SignerKey.Extract().XOnlyPubKey;
        var arkAddr = ArkAddress.FromScriptPubKey(script, serverKey);
        return arkAddr.ToString(terms.Network.ChainName == ChainName.Mainnet);
    }

    /// <summary>
    /// Find the wallet's active manually generated boarding address (still awaiting funds), if any.
    /// </summary>
    public async Task<string?> FindManualBoardingAddress(string walletId, CancellationToken cancellationToken)
    {
        var existingContracts = await contractStorage.GetContracts(
            walletIds: [walletId],
            isActive: true,
            contractTypes: [ArkBoardingContract.ContractType],
            cancellationToken: cancellationToken);

        var boardingEntity = existingContracts
            .FirstOrDefault(c =>
                c.ActivityState == ContractActivityState.AwaitingFundsBeforeDeactivate &&
                c.Metadata?.GetValueOrDefault("Source") == "manual");

        if (boardingEntity == null) return null;

        var terms = await clientTransport.GetServerInfoAsync(cancellationToken);
        var boardingContract = (ArkBoardingContract)ArkContractParser.Parse(
            boardingEntity.Type, boardingEntity.AdditionalData, terms.Network)!;
        return boardingContract.GetOnchainAddress(terms.Network).ToString();
    }

    /// <summary>
    /// Sync the wallet: poll the indexer for VTXOs on all contract scripts, then sync boarding UTXOs.
    /// </summary>
    public async Task SyncWallet(string walletId, CancellationToken cancellationToken)
    {
        var contracts = await contractStorage.GetContracts(
            walletIds: [walletId], cancellationToken: cancellationToken);
        await vtxoSyncService.PollScriptsForVtxos(
            contracts.Select(c => c.Script).ToHashSet(), cancellationToken);
        await boardingUtxoSyncService.SyncAsync(cancellationToken);
    }
}
