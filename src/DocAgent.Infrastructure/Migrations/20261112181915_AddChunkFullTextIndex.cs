using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkFullTextIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Chunks_Text_fts",
                table: "Chunks",
                column: "Text")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:TsVectorConfig", "simple");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Chunks_Text_fts",
                table: "Chunks");
        }
    }
}
