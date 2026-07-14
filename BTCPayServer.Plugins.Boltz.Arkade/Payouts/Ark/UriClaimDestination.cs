#nullable enable
using BTCPayServer.Data;
using NArk.Abstractions;
using NBitcoin;
using NBitcoin.Payment;

namespace BTCPayServer.Plugins.Boltz.Arkade.Payouts.Ark
{
    public class ArkUriClaimDestination : IClaimDestination
    {
        private readonly BitcoinUrlBuilder _bitcoinUrl;

        public ArkUriClaimDestination(BitcoinUrlBuilder bitcoinUrl) 
        {
            ArgumentNullException.ThrowIfNull(bitcoinUrl);
            if (bitcoinUrl.Address is null)
                throw new ArgumentException(nameof(bitcoinUrl));
            _bitcoinUrl = bitcoinUrl;
        }
        public BitcoinUrlBuilder BitcoinUrl => _bitcoinUrl;
        public override string ToString()
        {
            return _bitcoinUrl.ToString();
        }

        public string? Id => ArkAddress?.ToString(_bitcoinUrl.Network.ChainName == ChainName.Mainnet) ??
                             _bitcoinUrl.Address?.ToString();

        public decimal? Amount => _bitcoinUrl.Amount?.ToDecimal(MoneyUnit.BTC);

        public ArkAddress? ArkAddress
        {
            get
            {
                if (!_bitcoinUrl.UnknownParameters.TryGetValue("ark", out var value) ||
                    string.IsNullOrWhiteSpace(value?.ToString()))
                    return null;

                return ArkAddress.Parse(value.ToString()!);
            }
        }
    }
}
