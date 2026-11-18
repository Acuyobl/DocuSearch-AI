using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuSearch.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiUsageGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiBudgetIncidents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Period = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FirstUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Occurrences = table.Column<int>(type: "int", nullable: false),
                    SpentMicroUsd = table.Column<long>(type: "bigint", nullable: false),
                    LimitMicroUsd = table.Column<long>(type: "bigint", nullable: false),
                    LastFeature = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiBudgetIncidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiBudgetPolicies",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    MonthlyBudgetMicroUsd = table.Column<long>(type: "bigint", nullable: false),
                    AlertPercent = table.Column<int>(type: "int", nullable: false),
                    HardStop = table.Column<bool>(type: "bit", nullable: false),
                    MonthlyTokenQuota = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiBudgetPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiUsageLedger",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Period = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    Feature = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PromptTemplateKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    PromptVersion = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RequestHash = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TokensIn = table.Column<long>(type: "bigint", nullable: false),
                    TokensOut = table.Column<long>(type: "bigint", nullable: false),
                    TokensEstimated = table.Column<bool>(type: "bit", nullable: false),
                    CostMicroUsd = table.Column<long>(type: "bigint", nullable: false),
                    LatencyMs = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EntityType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    EntityId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsageLedger", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiBudgetIncidents_Period_Kind",
                table: "AiBudgetIncidents",
                columns: new[] { "Period", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageLedger_CreatedUtc",
                table: "AiUsageLedger",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageLedger_Period_Feature",
                table: "AiUsageLedger",
                columns: new[] { "Period", "Feature" });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageLedger_RequestHash",
                table: "AiUsageLedger",
                column: "RequestHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiBudgetIncidents");

            migrationBuilder.DropTable(
                name: "AiBudgetPolicies");

            migrationBuilder.DropTable(
                name: "AiUsageLedger");
        }
    }
}
