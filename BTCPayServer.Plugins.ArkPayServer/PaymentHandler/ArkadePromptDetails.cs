using NArk.Abstractions.Contracts;
using NArk.Core.Contracts;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>
/// Payment prompt details for Ark payments.
/// Stores the contract as a serialized string to avoid JSON converter issues with Network-dependent parsing.
/// </summary>
public record ArkadePromptDetails
{
    /// <summary>
    /// Creates prompt details from a wallet ID and contract.
    /// </summary>
    public ArkadePromptDetails(string walletId, ArkContract contract)
        : this(walletId, contract.ToString())
    {
    }

    /// <summary>
    /// Payment prompt details for Ark payments.
    /// Stores the contract as a serialized string to avoid JSON converter issues with Network-dependent parsing.
    /// </summary>
    public ArkadePromptDetails(string WalletId,
        string ContractString)
    {
        this.WalletId = WalletId;
        this.ContractString = ContractString;
    }
    
    public ArkadePromptDetails()
    {
        
    }

    public string WalletId { get; init; }
    public string ContractString { get; init; }

    /// <summary>
    /// Whether the BIP21 payment link should embed the Lightning invoice. Decided once,
    /// asynchronously, when the prompt is configured (Boltz limits check) so the sync
    /// checkout path never blocks on it. Defaults to true for prompts created before
    /// this field existed — matching the old include-when-possible behavior.
    /// </summary>
    public bool IncludeLightningInPaymentLink { get; init; } = true;

    /// <summary>
    /// Parses the contract with the specified network.
    /// </summary>
    public ArkContract? GetContract(Network network)
    {
        if (string.IsNullOrEmpty(ContractString))
            return null;
        return ArkContractParser.Parse(ContractString, network);
    }

}
