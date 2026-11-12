using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentUpload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentUploads",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentUploads", x => x.DocumentId);
                    table.ForeignKey(
                        name: "FK_DocumentUploads_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentUploads");
        }
    }
}
