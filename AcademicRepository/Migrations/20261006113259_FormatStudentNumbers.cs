using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcademicRepository.Migrations
{
    /// <inheritdoc />
    public partial class FormatStudentNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Reformat existing compact numbers without changing emails, IDs or relationships.
            // The existing unique index rejects conflicting identities atomically.
            migrationBuilder.Sql("""
                UPDATE AspNetUsers
                SET StudentNumber = UPPER(LEFT(StudentNumber,3) + '-' + SUBSTRING(StudentNumber,4,3) + '-' + RIGHT(StudentNumber,3))
                WHERE LEN(StudentNumber) = 9
                  AND StudentNumber COLLATE Latin1_General_100_CI_AS LIKE '[A-Z][A-Z][A-Z][0-9][0-9][FS][0-9][0-9][0-9]';
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Keep corrected identity data; this migration changes no schema.

        }
    }
}
