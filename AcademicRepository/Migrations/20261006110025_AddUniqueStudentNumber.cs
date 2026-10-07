using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcademicRepository.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueStudentNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<string>(
                name: "StudentNumber",
                table: "AspNetUsers",
                type: "nvarchar(11)",
                maxLength: 11,
                nullable: true,
                collation: "Latin1_General_100_CI_AS");

            // Backfill only structurally valid Student emails. Preserve legacy accounts for review.
            // Unique indexes deliberately fail atomically on conflicts rather than deleting users.
            migrationBuilder.Sql("""
                UPDATE u SET StudentNumber = UPPER(LEFT(u.Email,11))
                FROM AspNetUsers u
                WHERE LEN(u.Email) = 23
                  AND u.Email COLLATE Latin1_General_100_CI_AS LIKE '[0-9][0-9][A-Z]-[A-Z][A-Z][A-Z]-[0-9][0-9][0-9]@smiu.edu.pk'
                  AND EXISTS (SELECT 1 FROM AspNetUserRoles ur JOIN AspNetRoles r ON r.Id=ur.RoleId WHERE ur.UserId=u.Id AND r.Name='Student');
                """);

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail",
                unique: true,
                filter: "[NormalizedEmail] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_StudentNumber",
                table: "AspNetUsers",
                column: "StudentNumber",
                unique: true,
                filter: "[StudentNumber] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_StudentNumber",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StudentNumber",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");
        }
    }
}
