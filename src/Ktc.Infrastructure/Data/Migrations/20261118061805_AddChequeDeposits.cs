using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ktc.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChequeDeposits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AmountChequeDeposited",
                table: "CashDailyUsages",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "ChequeDeposits",
                table: "CashDailyUsages",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmountChequeDeposited",
                table: "CashDailyUsages");

            migrationBuilder.DropColumn(
                name: "ChequeDeposits",
                table: "CashDailyUsages");
        }
    }
}
