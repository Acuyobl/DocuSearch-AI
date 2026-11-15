using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuSearch.Data.Migrations
{
    /// <inheritdoc />
    public partial class RagFragmentsCascade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fragmentos de expedientes ya borrados: sin esta limpieza la FK no se puede crear.
            // Sus vectores en Pinecone (si los hay) se purgan con --rag-pinecone-sync.
            migrationBuilder.Sql(
                "DELETE FROM [RagFragments] WHERE [DocumentId] NOT IN (SELECT [Id] FROM [Documents]);");

            migrationBuilder.AddForeignKey(
                name: "FK_RagFragments_Documents_DocumentId",
                table: "RagFragments",
                column: "DocumentId",
                principalTable: "Documents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RagFragments_Documents_DocumentId",
                table: "RagFragments");
        }
    }
}
