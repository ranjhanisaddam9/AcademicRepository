using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcademicRepository.Migrations
{
    /// <inheritdoc />
    public partial class AddSubmissionReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SubmissionReviews",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectSubmissionId = table.Column<int>(type: "int", nullable: false),
                    ReviewerId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ReviewRound = table.Column<int>(type: "int", nullable: false),
                    Decision = table.Column<int>(type: "int", nullable: false),
                    Comments = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionReviews", x => x.Id);
                    table.CheckConstraint("CK_SubmissionReviews_Decision", "([Decision] = 0 AND [CompletedAt] IS NULL) OR ([Decision] IN (1,2) AND [CompletedAt] IS NOT NULL)");
                    table.CheckConstraint("CK_SubmissionReviews_RejectionComments", "[Decision] <> 2 OR ([Comments] IS NOT NULL AND LEN(LTRIM(RTRIM([Comments]))) > 0)");
                    table.CheckConstraint("CK_SubmissionReviews_Round", "[ReviewRound] >= 1");
                    table.ForeignKey(
                        name: "FK_SubmissionReviews_AspNetUsers_ReviewerId",
                        column: x => x.ReviewerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubmissionReviews_ProjectSubmissions_ProjectSubmissionId",
                        column: x => x.ProjectSubmissionId,
                        principalTable: "ProjectSubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionReviews_OnePending",
                table: "SubmissionReviews",
                column: "ProjectSubmissionId",
                unique: true,
                filter: "[Decision] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionReviews_ProjectSubmissionId_ReviewRound",
                table: "SubmissionReviews",
                columns: new[] { "ProjectSubmissionId", "ReviewRound" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionReviews_ReviewerId",
                table: "SubmissionReviews",
                column: "ReviewerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubmissionReviews");
        }
    }
}
