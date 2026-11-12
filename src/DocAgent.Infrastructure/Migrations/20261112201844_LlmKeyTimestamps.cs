using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LlmKeyTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ValidatedAt",
                table: "UserLlmCredentials",
                newName: "UpdatedAt");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "UserLlmCredentials",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "UserLlmCredentials");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                table: "UserLlmCredentials",
                newName: "ValidatedAt");
        }
    }
}
