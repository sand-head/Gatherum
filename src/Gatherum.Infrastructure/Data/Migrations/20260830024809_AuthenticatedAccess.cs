using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gatherum.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuthenticatedAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ListedToSignedIn",
                table: "Nodes",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ListedToSignedIn",
                table: "Nodes");
        }
    }
}
