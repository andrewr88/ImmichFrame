using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ImmichFrame.WebApi.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddSettingsDocumentFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Format",
                table: "SettingsDocuments",
                type: "TEXT",
                nullable: false,
                // Rows written before this column existed came from upstream's single-document
                // store, which only ever held JSON.
                defaultValue: "json");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Format",
                table: "SettingsDocuments");
        }
    }
}
