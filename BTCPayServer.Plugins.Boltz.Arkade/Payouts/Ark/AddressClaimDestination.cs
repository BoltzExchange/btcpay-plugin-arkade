using BTCPayServer.Data;
using NArk.Abstractions;

namespace BTCPayServer.Plugins.Boltz.Arkade.Payouts.Ark
{
    public interface IArkClaimDestination : IClaimDestination
    {
        ArkAddress Address { get; }
    }
    
    public record ArkAddressClaimDestination(ArkAddress Address, bool IsMainNet) : IArkClaimDestination
    {
        public string? Id => Address.ToString(IsMainNet);

        public virtual decimal? Amount => null;

        public override string ToString() => Id ?? throw new NotImplementedException();
    }

}
