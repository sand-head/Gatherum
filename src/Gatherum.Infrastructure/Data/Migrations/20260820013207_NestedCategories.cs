using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gatherum.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class NestedCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "NodeCategories",
                columns: table => new
                {
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeCategories", x => new { x.NodeId, x.CategoryId });
                    table.ForeignKey(
                        name: "FK_NodeCategories_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NodeCategories_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
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

            migrationBuilder.CreateIndex(
                name: "IX_NodeCategories_CategoryId",
                table: "NodeCategories",
                column: "CategoryId");

            // Every tag becomes a root category of the same name, keeping what each node
            // was filed under; nesting them is then an ordinary move in the app. A slash
            // in a tag was never a path, so it flattens to a space rather than inventing
            // a parent nobody created.
            migrationBuilder.Sql(
                """
                INSERT INTO "Categories" ("Id", "Name", "Path", "ParentId")
                SELECT "Id", btrim(replace("Name", '/', ' ')),
                       btrim(replace("Name", '/', ' ')), NULL
                FROM "Tags"
                WHERE btrim(replace("Name", '/', ' ')) <> ''
                ON CONFLICT ("Path") DO NOTHING;

                INSERT INTO "NodeCategories" ("NodeId", "CategoryId")
                SELECT DISTINCT nt."NodeId", c."Id"
                FROM "NodeTags" nt
                JOIN "Tags" t ON t."Id" = nt."TagId"
                JOIN "Categories" c
                  ON c."Path" = btrim(replace(t."Name", '/', ' '))
                ON CONFLICT DO NOTHING;
                """);

            migrationBuilder.DropTable(
                name: "NodeTags");

            migrationBuilder.DropTable(
                name: "Tags");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NodeCategories");

            migrationBuilder.DropTable(
                name: "Categories");

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NodeTags",
                columns: table => new
                {
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeTags", x => new { x.NodeId, x.TagId });
                    table.ForeignKey(
                        name: "FK_NodeTags_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NodeTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NodeTags_TagId",
                table: "NodeTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Name",
                table: "Tags",
                column: "Name",
                unique: true);
        }
    }
}
