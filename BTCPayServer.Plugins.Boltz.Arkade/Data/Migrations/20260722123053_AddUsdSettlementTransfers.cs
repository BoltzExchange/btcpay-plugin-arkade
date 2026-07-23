using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Boltz.Arkade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUsdSettlementTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NativeKeyIndices",
                schema: "BTCPayServer.Plugins.Boltz.Arkade",
                columns: table => new
                {
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    NextIndex = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NativeKeyIndices", x => x.WalletId);
                });

            migrationBuilder.CreateTable(
                name: "NativeSwaps",
                schema: "BTCPayServer.Plugins.Boltz.Arkade",
                columns: table => new
                {
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    SwapId = table.Column<string>(type: "text", nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false),
                    IsTerminal = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NativeSwaps", x => new { x.WalletId, x.SwapId });
                });

            migrationBuilder.CreateTable(
                name: "UsdSettlementTransfers",
                schema: "BTCPayServer.Plugins.Boltz.Arkade",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    DestinationNetwork = table.Column<string>(type: "text", nullable: false),
                    DestinationAsset = table.Column<string>(type: "text", nullable: false),
                    DestinationAddress = table.Column<string>(type: "text", nullable: false),
                    SourceAmountSats = table.Column<long>(type: "bigint", nullable: false),
                    InvoiceAmountSats = table.Column<long>(type: "bigint", nullable: false),
                    ExpectedOutputAtomic = table.Column<long>(type: "bigint", nullable: false),
                    DeliveredOutputAtomic = table.Column<long>(type: "bigint", nullable: true),
                    RustSwapId = table.Column<string>(type: "text", nullable: true),
                    Invoice = table.Column<string>(type: "text", nullable: true),
                    PaymentHash = table.Column<string>(type: "text", nullable: true),
                    NnarkSwapId = table.Column<string>(type: "text", nullable: true),
                    ArkFundingTxId = table.Column<string>(type: "text", nullable: true),
                    BridgeKind = table.Column<string>(type: "text", nullable: true),
                    TbtcLockupTxId = table.Column<string>(type: "text", nullable: true),
                    ArbitrumClaimTxHash = table.Column<string>(type: "text", nullable: true),
                    BridgeRef = table.Column<string>(type: "text", nullable: true),
                    StableLegFeeSats = table.Column<long>(type: "bigint", nullable: true),
                    ArkLegFeeSats = table.Column<long>(type: "bigint", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsdSettlementTransfers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NativeSwaps_WalletId_IsTerminal",
                schema: "BTCPayServer.Plugins.Boltz.Arkade",
                table: "NativeSwaps",
                columns: new[] { "WalletId", "IsTerminal" });

            migrationBuilder.CreateIndex(
                name: "IX_UsdSettlementTransfers_NnarkSwapId",
                schema: "BTCPayServer.Plugins.Boltz.Arkade",
                table: "UsdSettlementTransfers",
                column: "NnarkSwapId",
                unique: true,
                filter: "\"NnarkSwapId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UsdSettlementTransfers_RustSwapId",
                schema: "BTCPayServer.Plugins.Boltz.Arkade",
                table: "UsdSettlementTransfers",
                column: "RustSwapId",
                unique: true,
                filter: "\"RustSwapId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UsdSettlementTransfers_WalletId_State",
                schema: "BTCPayServer.Plugins.Boltz.Arkade",
                table: "UsdSettlementTransfers",
                columns: new[] { "WalletId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NativeKeyIndices",
                schema: "BTCPayServer.Plugins.Boltz.Arkade");

            migrationBuilder.DropTable(
                name: "NativeSwaps",
                schema: "BTCPayServer.Plugins.Boltz.Arkade");

            migrationBuilder.DropTable(
                name: "UsdSettlementTransfers",
                schema: "BTCPayServer.Plugins.Boltz.Arkade");
        }
    }
}
