using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcademicRepository.Migrations
{
    /// <inheritdoc />
    public partial class AddOtpAuthenticationAndOricQec : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuthenticationCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    Nonce = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SecurityStamp = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    WindowStartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentInWindow = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RecoveryGrantHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RecoveryGrantExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthenticationCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthenticationCodes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationCodes_Nonce",
                table: "AuthenticationCodes",
                column: "Nonce",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationCodes_UserId_Purpose",
                table: "AuthenticationCodes",
                columns: new[] { "UserId", "Purpose" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthenticationCodes");
        }
    }
}
