using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gatherum.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class UnlistedReach : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add first and carry the old value across before dropping it. Reach is
            // derived, and the startup recompute would rebuild it anyway — but scaffolding
            // put the drop first, which silently unpublishes everything published on any
            // instance that has the recompute switched off.
            migrationBuilder.AddColumn<int>(
                name: "Reach",
                table: "Nodes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // 2 is NodeReach.Listed — what EffectivePublic meant.
            migrationBuilder.Sql("""UPDATE "Nodes" SET "Reach" = 2 WHERE "EffectivePublic";""");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_EffectivePublic",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "EffectivePublic",
                table: "Nodes");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_Reach",
                table: "Nodes",
                column: "Reach");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Nodes_Reach",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "Reach",
                table: "Nodes");

            migrationBuilder.AddColumn<bool>(
                name: "EffectivePublic",
                table: "Nodes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""UPDATE "Nodes" SET "EffectivePublic" = true WHERE "Reach" = 2;""");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_EffectivePublic",
                table: "Nodes",
                column: "EffectivePublic");
        }
    }
}
