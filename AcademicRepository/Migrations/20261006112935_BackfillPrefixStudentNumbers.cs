using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcademicRepository.Migrations
{
    /// <inheritdoc />
    public partial class BackfillPrefixStudentNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE u SET StudentNumber = UPPER(LEFT(u.Email,9))
                FROM AspNetUsers u
                WHERE u.StudentNumber IS NULL AND LEN(u.Email) = 21
                  AND u.Email COLLATE Latin1_General_100_CI_AS LIKE '[A-Z][A-Z][A-Z][0-9][0-9][FS][0-9][0-9][0-9]@smiu.edu.pk'
                  AND EXISTS (SELECT 1 FROM AspNetUserRoles ur JOIN AspNetRoles r ON r.Id=ur.RoleId WHERE ur.UserId=u.Id AND r.Name='Student');
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Preserve corrected identity data; no schema was changed by this backfill.

        }
    }
}
