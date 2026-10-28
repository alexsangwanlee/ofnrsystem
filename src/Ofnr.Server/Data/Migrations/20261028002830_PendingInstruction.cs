using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ofnr.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PendingInstruction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pending_instruction",
                table: "jobs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pending_instruction",
                table: "jobs");
        }
    }
}
