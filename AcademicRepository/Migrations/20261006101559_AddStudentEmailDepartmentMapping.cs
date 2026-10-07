using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcademicRepository.Migrations
{
    /// <inheritdoc />
    public partial class AddStudentEmailDepartmentMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuthenticationCodes_UserId_Purpose",
                table: "AuthenticationCodes");

            migrationBuilder.AddColumn<string>(
                name: "StudentEmailKeyword",
                table: "Departments",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: true,
                collation: "Latin1_General_100_CI_AS");

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "AuthenticationCodes",
                type: "nvarchar(450)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "AuthenticationCodes",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_CI_AS");

            // Backfill before enforcing uniqueness. Existing users and submissions are untouched.
            // Old challenges use the previous digest format and must be requested again.
            migrationBuilder.Sql("""
                UPDATE c SET Email = UPPER(COALESCE(NULLIF(u.Email, ''), CONCAT(u.Id, '@legacy.invalid'))),
                    ConsumedAt = COALESCE(c.ConsumedAt, SYSUTCDATETIME()),
                    RecoveryGrantHash = NULL, RecoveryGrantExpiresAt = NULL
                FROM AuthenticationCodes c INNER JOIN AspNetUsers u ON c.UserId = u.Id;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Departments_StudentEmailKeyword",
                table: "Departments",
                column: "StudentEmailKeyword",
                unique: true,
                filter: "[StudentEmailKeyword] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationCodes_Email_Purpose",
                table: "AuthenticationCodes",
                columns: new[] { "Email", "Purpose" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationCodes_UserId",
                table: "AuthenticationCodes",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM AuthenticationCodes WHERE UserId IS NULL)
                    THROW 50000, 'Pending Student challenges must be handled before rolling back this migration.', 1;
                IF EXISTS (SELECT 1 FROM AuthenticationCodes GROUP BY UserId, Purpose HAVING COUNT(*) > 1)
                    THROW 50000, 'Multiple email challenges exist for a user; review them before rollback.', 1;
                """);
            migrationBuilder.DropIndex(
                name: "IX_Departments_StudentEmailKeyword",
                table: "Departments");

            migrationBuilder.DropIndex(
                name: "IX_AuthenticationCodes_Email_Purpose",
                table: "AuthenticationCodes");

            migrationBuilder.DropIndex(
                name: "IX_AuthenticationCodes_UserId",
                table: "AuthenticationCodes");

            migrationBuilder.DropColumn(
                name: "StudentEmailKeyword",
                table: "Departments");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "AuthenticationCodes");

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "AuthenticationCodes",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationCodes_UserId_Purpose",
                table: "AuthenticationCodes",
                columns: new[] { "UserId", "Purpose" },
                unique: true);
        }
    }
}
