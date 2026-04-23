using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Susurri.Modules.Users.Core.DAL.Migrations
{
    /// <inheritdoc />
    public partial class UserIntegrityIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Users_LastSeenAt",
                table: "Users",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_LastSeenAt",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_Username",
                table: "Users");
        }
    }
}
