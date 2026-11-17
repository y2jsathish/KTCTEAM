using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ktc.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCashForecast : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CashDailyUsages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Day = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CapturedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AtmId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AtmName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Client = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Withdrawals = table.Column<int>(type: "int", nullable: false),
                    AmountWithdrawn = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Deposits = table.Column<int>(type: "int", nullable: false),
                    AmountDeposited = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Partial = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashDailyUsages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CashSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Day = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CapturedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AtmId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AtmName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UnitType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Denomination = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Notes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CashDailyUsages_AtmId_Day",
                table: "CashDailyUsages",
                columns: new[] { "AtmId", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashDailyUsages_Day",
                table: "CashDailyUsages",
                column: "Day");

            migrationBuilder.CreateIndex(
                name: "IX_CashSnapshots_AtmId_Day_Unit",
                table: "CashSnapshots",
                columns: new[] { "AtmId", "Day", "Unit" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashSnapshots_Day",
                table: "CashSnapshots",
                column: "Day");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CashDailyUsages");

            migrationBuilder.DropTable(
                name: "CashSnapshots");
        }
    }
}
