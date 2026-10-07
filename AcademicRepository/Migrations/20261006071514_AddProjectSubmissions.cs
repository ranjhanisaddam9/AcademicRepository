using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcademicRepository.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectSubmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    Abstract = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: false),
                    Keywords = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ProjectType = table.Column<int>(type: "int", nullable: false),
                    StudentId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    SupervisorName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CourseName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CourseCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    AcademicYear = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Semester = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectSubmissions_AspNetUsers_StudentId",
                        column: x => x.StudentId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectSubmissions_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSubmissions_DepartmentId",
                table: "ProjectSubmissions",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSubmissions_StudentId_CreatedAt",
                table: "ProjectSubmissions",
                columns: new[] { "StudentId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectSubmissions");
        }
    }
}
