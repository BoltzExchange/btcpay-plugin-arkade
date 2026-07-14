using BTCPayServer.Client;
using BTCPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using NArk.Swaps.Services;
using NArk.Tests.End2End.Common;
using NBitcoin;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Submarine (ARK→LN) swap refund: when the Lightning payment fails, the plugin must
/// cooperatively refund the locked VTXO back into the wallet. The failure is injected by
/// cancelling the invoice on the receiving LND node between swap creation and lockup funding,
/// so Boltz's payment attempt fails terminally and emits <c>invoice.failedToPay</c>. Creation
/// and funding go through the in-process SwapsManagementService instead of the /send wizard
/// (covered by SwapsTests) because Boltz probes the invoice at creation and the wizard's
/// create+fund is atomic. Reverse (LN→ARK) swap refunds are out of scope: a never-paid
/// reverse swap locks nothing client-side, so there is nothing to refund.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class SwapRefundTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public SwapRefundTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SubmarineSwap_FailedLnPayment_RefundsFundsToWallet()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();
        var walletId = await GetStoreWalletIdAsync(storeId);
        Assert.False(string.IsNullOrWhiteSpace(walletId));

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        await PayArkadeInvoiceAsync(client, storeId, 200_000);
        var outpoints = await PollForSpendableCoinsAsync(
            storeId, "LightningInvoice", 20_000, TimeSpan.FromMinutes(10));
        Assert.NotEmpty(outpoints);

        var services = _fixture.ServerTester!.PayTester.ServiceProvider;
        var swapManagement = services.GetRequiredService<SwapsManagementService>();
        var swapStorage = services.GetRequiredService<ISwapStorage>();

        var (bolt11, rHash) = await DockerHelper.CreateLndInvoiceWithHash(amtSats: 20_000, expirySecs: 3600);
        var swapId = await swapManagement.InitiateSubmarineSwap(
            walletId!, BOLT11PaymentRequest.Parse(bolt11, Network.RegTest), autoPay: false);

        var balanceBefore = (await GetArkadeBalanceAsync(storeId)).AvailableSats;

        // Cancel before funding the lockup, so the payment can only fail — the refund
        // cannot race a settlement.
        await DockerHelper.CancelLndInvoice(rHash);

        await swapManagement.PayExistingSubmarineSwap(walletId!, swapId);

        // The lockup VTXO is deliberately not observed: the coop refund can consume it within
        // milliseconds of funding (CI-proven race), so poll the swap status directly.
        ArkSwap? current = null;
        await PollUntilAsync(async () =>
        {
            current = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();
            Assert.False(current.Status == ArkSwapStatus.Failed,
                $"swap went Failed instead of Refunded — coop refund did not run ({current.FailReason})");
            return current.Status == ArkSwapStatus.Refunded;
        }, TimeSpan.FromMinutes(4),
            () => $"swap never reached Refunded after invoice.failedToPay (status: {current?.Status})");

        // The refund lands on a freshly derived wallet contract, so assert on balance rather
        // than address equality; allow small fee slippage.
        long available = 0;
        await PollUntilAsync(async () =>
        {
            available = (await GetArkadeBalanceAsync(storeId)).AvailableSats;
            return available >= balanceBefore - 2_000;
        }, TimeSpan.FromMinutes(3),
            () => $"refunded funds never returned: {available} < {balanceBefore - 2_000} sats");
    }
}
