using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuSearch.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSqlServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Audit",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TimestampUTC = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UserRole = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DocumentCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Details = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Audit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Departments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Lead = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AllocatedStorageMB = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Id);
                    table.UniqueConstraint("AK_Departments_Name", x => x.Name);
                    table.CheckConstraint("valid_quota", "[AllocatedStorageMB] > 0");
                });

            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    InternalCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Author = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Department = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    UploadDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Confidentiality = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OcrStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Sha256Hash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    PageCount = table.Column<int>(type: "int", nullable: false),
                    ExecutiveSummary = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: false),
                    Keywords = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Tags = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PagesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NotesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HasOriginal = table.Column<bool>(type: "bit", nullable: false),
                    ViewsCount = table.Column<int>(type: "int", nullable: false),
                    DownloadsCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                    table.CheckConstraint("valid_file_size", "[FileSizeBytes] >= 0");
                    table.CheckConstraint("valid_level", "[Confidentiality] IN (N'Público',N'Uso Interno',N'Confidencial',N'Estrictamente Secreto')");
                    table.CheckConstraint("valid_notes_json", "ISJSON([NotesJson]) = 1");
                    table.CheckConstraint("valid_page_count", "[PageCount] >= 0 AND [PageCount] <= 500");
                    table.CheckConstraint("valid_pages_json", "ISJSON([PagesJson]) = 1");
                    table.ForeignKey(
                        name: "FK_Documents_Departments_Department",
                        column: x => x.Department,
                        principalTable: "Departments",
                        principalColumn: "Name",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Department = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.CheckConstraint("valid_role", "[Role] IN ('admin_general','admin_depto','gestor_documental','consultor','auditor')");
                    table.CheckConstraint("valid_user_status", "[Status] IN ('Activo','Inactivo')");
                    table.ForeignKey(
                        name: "FK_Users_Departments_Department",
                        column: x => x.Department,
                        principalTable: "Departments",
                        principalColumn: "Name",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Files",
                columns: table => new
                {
                    DocumentId = table.Column<string>(type: "nvarchar(100)", nullable: false),
                    Bytes = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Files", x => x.DocumentId);
                    table.ForeignKey(
                        name: "FK_Files_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_TimestampUTC",
                table: "Audit",
                column: "TimestampUTC");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_Department",
                table: "Documents",
                column: "Department");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_InternalCode",
                table: "Documents",
                column: "InternalCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Department",
                table: "Users",
                column: "Department");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Audit");

            migrationBuilder.DropTable(
                name: "Files");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Documents");

            migrationBuilder.DropTable(
                name: "Departments");
        }
    }
}
