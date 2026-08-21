using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Gatherum.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Embeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AddColumn<string>(
                name: "EmbeddedFingerprint",
                table: "Nodes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TextFingerprint",
                table: "Nodes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                computedColumnSql: "md5(coalesce(\"Title\", '') || E'\\n' || coalesce(\"SearchText\", ''))",
                stored: true);

            migrationBuilder.CreateTable(
                name: "NodeEmbeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Embedding = table.Column<Vector>(type: "vector", nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeEmbeddings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NodeEmbeddings_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_EmbeddedFingerprint_TextFingerprint",
                table: "Nodes",
                columns: new[] { "EmbeddedFingerprint", "TextFingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_NodeEmbeddings_Hash",
                table: "NodeEmbeddings",
                column: "Hash");

            migrationBuilder.CreateIndex(
                name: "IX_NodeEmbeddings_NodeId_Ordinal",
                table: "NodeEmbeddings",
                columns: new[] { "NodeId", "Ordinal" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NodeEmbeddings");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_EmbeddedFingerprint_TextFingerprint",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "TextFingerprint",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "EmbeddedFingerprint",
                table: "Nodes");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
