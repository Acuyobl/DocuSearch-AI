using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuSearch.Data.Migrations
{
    /// <inheritdoc />
    public partial class Governance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "After",
                table: "Audit",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Before",
                table: "Audit",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DurationMs",
                table: "Audit",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Entity",
                table: "Audit",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntityId",
                table: "Audit",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Ip",
                table: "Audit",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApprovalRequests",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityLabel = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    RequestedById = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RequestedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequestedByRole = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    RequestedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DecidedById = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DecidedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DecidedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoutineRuns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RoutineKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    StartedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Output = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutineRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Routines",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    IntervalMinutes = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    LastRunUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextRunUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastStatus = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Routines", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_Entity_EntityId",
                table: "Audit",
                columns: new[] { "Entity", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_Action_EntityId_Status",
                table: "ApprovalRequests",
                columns: new[] { "Action", "EntityId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_Status_RequestedUtc",
                table: "ApprovalRequests",
                columns: new[] { "Status", "RequestedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RoutineRuns_RoutineKey_StartedUtc",
                table: "RoutineRuns",
                columns: new[] { "RoutineKey", "StartedUtc" });

            // Bitácora de solo inserción: SQL Server rechaza cualquier UPDATE o DELETE sobre Audit,
            // venga de la aplicación o de una consulta manual.
            migrationBuilder.Sql(@"CREATE TRIGGER [TR_Audit_AppendOnly] ON [Audit]
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, N'La bitácora es de solo inserción: no se permiten UPDATE ni DELETE.', 1;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [TR_Audit_AppendOnly];");

            migrationBuilder.DropTable(
                name: "ApprovalRequests");

            migrationBuilder.DropTable(
                name: "RoutineRuns");

            migrationBuilder.DropTable(
                name: "Routines");

            migrationBuilder.DropIndex(
                name: "IX_Audit_Entity_EntityId",
                table: "Audit");

            migrationBuilder.DropColumn(
                name: "After",
                table: "Audit");

            migrationBuilder.DropColumn(
                name: "Before",
                table: "Audit");

            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "Audit");

            migrationBuilder.DropColumn(
                name: "Entity",
                table: "Audit");

            migrationBuilder.DropColumn(
                name: "EntityId",
                table: "Audit");

            migrationBuilder.DropColumn(
                name: "Ip",
                table: "Audit");
        }
    }
}
