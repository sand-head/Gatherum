using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gatherum.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CategoriesAreNodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NodeCategories_Categories_CategoryId",
                table: "NodeCategories");

            migrationBuilder.DropTable(
                name: "Categories");

            // Every membership pointed at a row in the table above, and now has to point
            // at a node. There is no rewriting it — a category page is a file somebody has
            // to have written — so the filings are dropped and the startup scan puts them
            // back from the sidecars, which is where they were the system of record all
            // along.
            migrationBuilder.Sql("DELETE FROM \"NodeCategories\"");

            migrationBuilder.AddColumn<bool>(
                name: "IsCategory",
                table: "Nodes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_IsCategory",
                table: "Nodes",
                column: "IsCategory");

            migrationBuilder.AddForeignKey(
                name: "FK_NodeCategories_Nodes_CategoryId",
                table: "NodeCategories",
                column: "CategoryId",
                principalTable: "Nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NodeCategories_Nodes_CategoryId",
                table: "NodeCategories");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_IsCategory",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "IsCategory",
                table: "Nodes");

            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Categories_Categories_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_ParentId",
                table: "Categories",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_Path",
                table: "Categories",
                column: "Path",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_NodeCategories_Categories_CategoryId",
                table: "NodeCategories",
                column: "CategoryId",
                principalTable: "Categories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
