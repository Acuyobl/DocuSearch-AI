using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuSearch.Data.Migrations
{
    /// <inheritdoc />
    public partial class McpApiTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpApiTokens",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUsedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpApiTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpApiTokens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpApiTokens_TokenHash",
                table: "McpApiTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpApiTokens_UserId_RevokedUtc",
                table: "McpApiTokens",
                columns: new[] { "UserId", "RevokedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpApiTokens");
        }
    }
}
