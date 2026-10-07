using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcademicRepository.Migrations
{
    /// <inheritdoc />
    public partial class AddSubmissionVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SubmissionVersionId",
                table: "SubmissionReviews",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "ProjectFiles",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "ProjectFiles",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "SubmissionVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectSubmissionId = table.Column<int>(type: "int", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    TitleSnapshot = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    AbstractSnapshot = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: false),
                    KeywordsSnapshot = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ProjectTypeSnapshot = table.Column<int>(type: "int", nullable: false),
                    SupervisorNameSnapshot = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CourseNameSnapshot = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CourseCodeSnapshot = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    AcademicYearSnapshot = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    SemesterSnapshot = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DepartmentIdSnapshot = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionVersions", x => x.Id);
                    table.CheckConstraint("CK_SubmissionVersions_Number", "[VersionNumber] >= 1");
                    table.ForeignKey(
                        name: "FK_SubmissionVersions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubmissionVersions_Departments_DepartmentIdSnapshot",
                        column: x => x.DepartmentIdSnapshot,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubmissionVersions_ProjectSubmissions_ProjectSubmissionId",
                        column: x => x.ProjectSubmissionId,
                        principalTable: "ProjectSubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubmissionVersionFiles",
                columns: table => new
                {
                    SubmissionVersionId = table.Column<int>(type: "int", nullable: false),
                    ProjectFileId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionVersionFiles", x => new { x.SubmissionVersionId, x.ProjectFileId });
                    table.ForeignKey(
                        name: "FK_SubmissionVersionFiles_ProjectFiles_ProjectFileId",
                        column: x => x.ProjectFileId,
                        principalTable: "ProjectFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubmissionVersionFiles_SubmissionVersions_SubmissionVersionId",
                        column: x => x.SubmissionVersionId,
                        principalTable: "SubmissionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionReviews_SubmissionVersionId",
                table: "SubmissionReviews",
                column: "SubmissionVersionId",
                unique: true,
                filter: "[SubmissionVersionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionVersionFiles_ProjectFileId",
                table: "SubmissionVersionFiles",
                column: "ProjectFileId");

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionVersions_CreatedByUserId",
                table: "SubmissionVersions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionVersions_DepartmentIdSnapshot",
                table: "SubmissionVersions",
                column: "DepartmentIdSnapshot");

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionVersions_ProjectSubmissionId_VersionNumber",
                table: "SubmissionVersions",
                columns: new[] { "ProjectSubmissionId", "VersionNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SubmissionReviews_SubmissionVersions_SubmissionVersionId",
                table: "SubmissionReviews",
                column: "SubmissionVersionId",
                principalTable: "SubmissionVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
            // Only reconstruct a single known submitted state. Ambiguous/multiple rounds remain unlinked.
            // No live submission, file or decision fields are changed by this backfill.
            migrationBuilder.Sql("""
                INSERT INTO SubmissionVersions
                    (ProjectSubmissionId,VersionNumber,CreatedAt,SubmittedAt,CreatedByUserId,TitleSnapshot,AbstractSnapshot,KeywordsSnapshot,
                     ProjectTypeSnapshot,SupervisorNameSnapshot,CourseNameSnapshot,CourseCodeSnapshot,AcademicYearSnapshot,SemesterSnapshot,DepartmentIdSnapshot)
                SELECT s.Id,1,SYSUTCDATETIME(),s.SubmittedAt,s.StudentId,s.Title,s.Abstract,s.Keywords,s.ProjectType,
                       s.SupervisorName,s.CourseName,s.CourseCode,s.AcademicYear,s.Semester,s.DepartmentId
                FROM ProjectSubmissions s
                WHERE s.Status IN (1,2,3,4) AND s.SubmittedAt IS NOT NULL
                  AND (s.UpdatedAt IS NULL OR s.UpdatedAt <= COALESCE(
                      (SELECT MAX(COALESCE(r.CompletedAt,r.StartedAt)) FROM SubmissionReviews r WHERE r.ProjectSubmissionId=s.Id),s.SubmittedAt))
                  AND NOT EXISTS (SELECT 1 FROM ProjectFiles f WHERE f.ProjectSubmissionId=s.Id AND f.UploadedAt>s.SubmittedAt)
                  AND (SELECT COUNT(*) FROM SubmissionReviews r WHERE r.ProjectSubmissionId=s.Id)<=1
                  AND NOT EXISTS (SELECT 1 FROM SubmissionReviews r WHERE r.ProjectSubmissionId=s.Id AND (r.ReviewRound<>1 OR r.StartedAt<s.SubmittedAt))
                  AND NOT EXISTS (SELECT 1 FROM SubmissionVersions v WHERE v.ProjectSubmissionId=s.Id);
                INSERT INTO SubmissionVersionFiles (SubmissionVersionId,ProjectFileId)
                SELECT v.Id,f.Id FROM SubmissionVersions v JOIN ProjectFiles f ON f.ProjectSubmissionId=v.ProjectSubmissionId;
                UPDATE r SET SubmissionVersionId=v.Id
                FROM SubmissionReviews r JOIN SubmissionVersions v ON v.ProjectSubmissionId=r.ProjectSubmissionId AND v.VersionNumber=r.ReviewRound
                WHERE r.SubmissionVersionId IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SubmissionReviews_SubmissionVersions_SubmissionVersionId",
                table: "SubmissionReviews");

            migrationBuilder.DropTable(
                name: "SubmissionVersionFiles");

            migrationBuilder.DropTable(
                name: "SubmissionVersions");

            migrationBuilder.DropIndex(
                name: "IX_SubmissionReviews_SubmissionVersionId",
                table: "SubmissionReviews");

            migrationBuilder.DropColumn(
                name: "SubmissionVersionId",
                table: "SubmissionReviews");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "ProjectFiles");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "ProjectFiles");
        }
    }
}
