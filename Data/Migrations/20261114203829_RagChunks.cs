using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuSearch.Data.Migrations
{
    /// <inheritdoc />
    public partial class RagChunks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RagFragments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    DocumentId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    InternalCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    PageNumber = table.Column<int>(type: "int", nullable: false),
                    ChunkIndex = table.Column<int>(type: "int", nullable: false),
                    StartOffset = table.Column<int>(type: "int", nullable: false),
                    EndOffset = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Dimensions = table.Column<int>(type: "int", nullable: false),
                    Vector = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RagFragments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RagFragments_DocumentId_ChunkIndex",
                table: "RagFragments",
                columns: new[] { "DocumentId", "ChunkIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RagFragments");
        }
    }
}


