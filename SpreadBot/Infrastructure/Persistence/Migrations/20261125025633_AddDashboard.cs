using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpreadBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDashboard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EquitySnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BaseMarket = table.Column<string>(type: "TEXT", nullable: true),
                    TotalValue = table.Column<decimal>(type: "TEXT", nullable: false),
                    Unallocated = table.Column<decimal>(type: "TEXT", nullable: false),
                    Bots = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquitySnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PushSubscriptions",
                columns: table => new
                {
                    Endpoint = table.Column<string>(type: "TEXT", nullable: false),
                    P256dh = table.Column<string>(type: "TEXT", nullable: true),
                    Auth = table.Column<string>(type: "TEXT", nullable: true),
                    Categories = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushSubscriptions", x => x.Endpoint);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EquitySnapshots_BaseMarket_At",
                table: "EquitySnapshots",
                columns: new[] { "BaseMarket", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EquitySnapshots");

            migrationBuilder.DropTable(
                name: "PushSubscriptions");
        }
    }
}
