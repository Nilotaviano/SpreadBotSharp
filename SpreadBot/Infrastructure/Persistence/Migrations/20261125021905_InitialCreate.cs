using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpreadBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Configurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Strategy = table.Column<string>(type: "TEXT", nullable: false),
                    BaseMarket = table.Column<string>(type: "TEXT", nullable: true),
                    MaxPercentChangeFromPreviousDay = table.Column<decimal>(type: "TEXT", nullable: false),
                    MinimumSpreadPercentage = table.Column<decimal>(type: "TEXT", nullable: false),
                    MinimumQuoteVolume = table.Column<decimal>(type: "TEXT", nullable: false),
                    MinimumPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    MinimumNegotiatedAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                    AllocatedAmountOfBaseCurrency = table.Column<decimal>(type: "TEXT", nullable: false),
                    SpreadThresholdBeforeCancelingCurrentOrder = table.Column<decimal>(type: "TEXT", nullable: false),
                    MinutesForLoss = table.Column<int>(type: "INTEGER", nullable: false),
                    AvoidTokenizedSecurities = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Configurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dust",
                columns: table => new
                {
                    MarketSymbol = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dust", x => x.MarketSymbol);
                });

            migrationBuilder.CreateTable(
                name: "Bots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConfigurationId = table.Column<int>(type: "INTEGER", nullable: false),
                    MarketSymbol = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Balance = table.Column<decimal>(type: "TEXT", nullable: false),
                    HeldAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                    BoughtPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    BuyFilledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CurrentOrderClientId = table.Column<string>(type: "TEXT", nullable: true),
                    BidRate = table.Column<decimal>(type: "TEXT", nullable: true),
                    AskRate = table.Column<decimal>(type: "TEXT", nullable: true),
                    LastTradeRate = table.Column<decimal>(type: "TEXT", nullable: true),
                    PriceTick = table.Column<decimal>(type: "TEXT", nullable: true),
                    AllocatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Profit = table.Column<decimal>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bots_Configurations_ConfigurationId",
                        column: x => x.ConfigurationId,
                        principalTable: "Configurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExchangeOrderId = table.Column<string>(type: "TEXT", nullable: true),
                    ClientOrderId = table.Column<string>(type: "TEXT", nullable: true),
                    MarketSymbol = table.Column<string>(type: "TEXT", nullable: true),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    Limit = table.Column<decimal>(type: "TEXT", nullable: false),
                    FillQuantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    Proceeds = table.Column<decimal>(type: "TEXT", nullable: false),
                    Commission = table.Column<decimal>(type: "TEXT", nullable: false),
                    CoinCommission = table.Column<decimal>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.RecordId);
                    table.ForeignKey(
                        name: "FK_Orders_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bots_ConfigurationId",
                table: "Bots",
                column: "ConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_Bots_FinishedAt",
                table: "Bots",
                column: "FinishedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Bots_MarketSymbol",
                table: "Bots",
                column: "MarketSymbol");

            migrationBuilder.CreateIndex(
                name: "IX_Configurations_Strategy_BaseMarket_MaxPercentChangeFromPreviousDay_MinimumSpreadPercentage_MinimumQuoteVolume_AllocatedAmountOfBaseCurrency_SpreadThresholdBeforeCancelingCurrentOrder_MinutesForLoss_MinimumPrice_MinimumNegotiatedAmount",
                table: "Configurations",
                columns: new[] { "Strategy", "BaseMarket", "MaxPercentChangeFromPreviousDay", "MinimumSpreadPercentage", "MinimumQuoteVolume", "AllocatedAmountOfBaseCurrency", "SpreadThresholdBeforeCancelingCurrentOrder", "MinutesForLoss", "MinimumPrice", "MinimumNegotiatedAmount" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BotId",
                table: "Orders",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ClientOrderId",
                table: "Orders",
                column: "ClientOrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dust");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "Bots");

            migrationBuilder.DropTable(
                name: "Configurations");
        }
    }
}
