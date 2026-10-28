using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ofnr.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class MailReplyTo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reply_to",
                table: "mail_messages",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reply_to",
                table: "mail_messages");
        }
    }
}
