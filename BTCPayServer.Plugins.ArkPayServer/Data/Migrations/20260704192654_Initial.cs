using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        private const string SchemaName = "BTCPayServer.Plugins.Boltz.Arkade";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: SchemaName);

            migrationBuilder.CreateTable(
                name: "Intents",
                schema: SchemaName,
                columns: table => new
                {
                    IntentTxId = table.Column<string>(type: "text", nullable: false),
                    IntentId = table.Column<string>(type: "text", nullable: true),
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ValidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RegisterProof = table.Column<string>(type: "text", nullable: false),
                    RegisterProofMessage = table.Column<string>(type: "text", nullable: false),
                    DeleteProof = table.Column<string>(type: "text", nullable: false),
                    DeleteProofMessage = table.Column<string>(type: "text", nullable: false),
                    BatchId = table.Column<string>(type: "text", nullable: true),
                    CommitmentTransactionId = table.Column<string>(type: "text", nullable: true),
                    CancellationReason = table.Column<string>(type: "text", nullable: true),
                    PartialForfeits = table.Column<string[]>(type: "text[]", nullable: false),
                    SignerDescriptor = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Intents", x => x.IntentTxId);
                });

            migrationBuilder.CreateTable(
                name: "Vtxos",
                schema: SchemaName,
                columns: table => new
                {
                    TransactionId = table.Column<string>(type: "text", nullable: false),
                    TransactionOutputIndex = table.Column<int>(type: "integer", nullable: false),
                    Script = table.Column<string>(type: "text", nullable: false),
                    SpentByTransactionId = table.Column<string>(type: "text", nullable: true),
                    SettledByTransactionId = table.Column<string>(type: "text", nullable: true),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    SeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Recoverable = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtHeight = table.Column<long>(type: "bigint", nullable: true),
                    Preconfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    Unrolled = table.Column<bool>(type: "boolean", nullable: false),
                    CommitmentTxids = table.Column<string>(type: "text", nullable: true),
                    ArkTxid = table.Column<string>(type: "text", nullable: true),
                    AssetsJson = table.Column<string>(type: "text", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vtxos", x => new { x.TransactionId, x.TransactionOutputIndex });
                });

            migrationBuilder.CreateTable(
                name: "Wallets",
                schema: SchemaName,
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Wallet = table.Column<string>(type: "text", nullable: true),
                    WalletDestination = table.Column<string>(type: "text", nullable: true),
                    WalletType = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    AccountDescriptor = table.Column<string>(type: "text", nullable: true),
                    LastUsedIndex = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Metadata = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Wallets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IntentVtxos",
                schema: SchemaName,
                columns: table => new
                {
                    IntentTxId = table.Column<string>(type: "text", nullable: false),
                    VtxoTransactionId = table.Column<string>(type: "text", nullable: false),
                    VtxoTransactionOutputIndex = table.Column<int>(type: "integer", nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntentVtxos", x => new { x.IntentTxId, x.VtxoTransactionId, x.VtxoTransactionOutputIndex });
                    table.ForeignKey(
                        name: "FK_IntentVtxos_Intents_IntentTxId",
                        column: x => x.IntentTxId,
                        principalSchema: SchemaName,
                        principalTable: "Intents",
                        principalColumn: "IntentTxId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IntentVtxos_Vtxos_VtxoTransactionId_VtxoTransactionOutputIn~",
                        columns: x => new { x.VtxoTransactionId, x.VtxoTransactionOutputIndex },
                        principalSchema: SchemaName,
                        principalTable: "Vtxos",
                        principalColumns: new[] { "TransactionId", "TransactionOutputIndex" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WalletContracts",
                schema: SchemaName,
                columns: table => new
                {
                    Script = table.Column<string>(type: "text", nullable: false),
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    ActivityState = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    ContractData = table.Column<string>(type: "jsonb", nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletContracts", x => new { x.Script, x.WalletId });
                    table.ForeignKey(
                        name: "FK_WalletContracts_Wallets_WalletId",
                        column: x => x.WalletId,
                        principalSchema: SchemaName,
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Swaps",
                schema: SchemaName,
                columns: table => new
                {
                    SwapId = table.Column<string>(type: "text", nullable: false),
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    SwapType = table.Column<int>(type: "integer", nullable: false),
                    Invoice = table.Column<string>(type: "text", nullable: false),
                    ExpectedAmount = table.Column<long>(type: "bigint", nullable: false),
                    ContractScript = table.Column<string>(type: "text", nullable: false),
                    Address = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    FailReason = table.Column<string>(type: "text", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Swaps", x => new { x.SwapId, x.WalletId });
                    table.ForeignKey(
                        name: "FK_Swaps_WalletContracts_ContractScript_WalletId",
                        columns: x => new { x.ContractScript, x.WalletId },
                        principalSchema: SchemaName,
                        principalTable: "WalletContracts",
                        principalColumns: new[] { "Script", "WalletId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Swaps_Wallets_WalletId",
                        column: x => x.WalletId,
                        principalSchema: SchemaName,
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Intents_IntentId",
                schema: SchemaName,
                table: "Intents",
                column: "IntentId",
                unique: true,
                filter: "\"IntentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IntentVtxos_VtxoTransactionId_VtxoTransactionOutputIndex",
                schema: SchemaName,
                table: "IntentVtxos",
                columns: new[] { "VtxoTransactionId", "VtxoTransactionOutputIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_Swaps_ContractScript_WalletId",
                schema: SchemaName,
                table: "Swaps",
                columns: new[] { "ContractScript", "WalletId" });

            migrationBuilder.CreateIndex(
                name: "IX_Swaps_WalletId",
                schema: SchemaName,
                table: "Swaps",
                column: "WalletId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletContracts_Scope",
                schema: SchemaName,
                table: "WalletContracts",
                column: "Scope");

            migrationBuilder.CreateIndex(
                name: "IX_WalletContracts_WalletId",
                schema: SchemaName,
                table: "WalletContracts",
                column: "WalletId");

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_Wallet",
                schema: SchemaName,
                table: "Wallets",
                column: "Wallet",
                unique: true,
                filter: "\"Wallet\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntentVtxos",
                schema: SchemaName);

            migrationBuilder.DropTable(
                name: "Swaps",
                schema: SchemaName);

            migrationBuilder.DropTable(
                name: "Intents",
                schema: SchemaName);

            migrationBuilder.DropTable(
                name: "Vtxos",
                schema: SchemaName);

            migrationBuilder.DropTable(
                name: "WalletContracts",
                schema: SchemaName);

            migrationBuilder.DropTable(
                name: "Wallets",
                schema: SchemaName);
        }
    }
}
