using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DemoUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DemoUsages",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoUsages", x => new { x.UserId, x.Day });
                    table.ForeignKey(
                        name: "FK_DemoUsages_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DemoUsages");
        }
    }
}
