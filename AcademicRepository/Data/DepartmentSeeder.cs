using AcademicRepository.Models;
using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Data;

public static class DepartmentSeeder
{
    public static readonly IReadOnlyDictionary<string, string> Mappings = new Dictionary<string, string>
    {
        ["BUS"] = "Business Administration", ["CSC"] = "Computer Science",
        ["BSE"] = "Software Engineering", ["ENG"] = "English", ["ENV"] = "Environment",
        ["BDS"] = "Data Science", ["BAI"] = "Artificial Intelligence", ["BIT"] = "Information Technology"
    };

    public static async Task SeedAsync(ApplicationDbContext db)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var (keyword, name) in Mappings)
        {
            // Keep existing IDs, department codes, active flags and all student assignments.
            if (await db.Departments.AnyAsync(d => d.StudentEmailKeyword == keyword)) continue;
            var department = await db.Departments.SingleOrDefaultAsync(d => d.Name == name);
            if (department is not null)
            {
                if (department.StudentEmailKeyword is not null)
                    throw new InvalidOperationException($"Department '{name}' already has another Student email keyword. Resolve the mapping before startup.");
                department.StudentEmailKeyword = keyword;
            }
            else
            {
                if (await db.Departments.AnyAsync(d => d.Code == keyword))
                    throw new InvalidOperationException($"Department code '{keyword}' already belongs to a different department. Resolve the mapping before startup.");
                db.Departments.Add(new Department { Name = name, Code = keyword, StudentEmailKeyword = keyword });
            }
            await db.SaveChangesAsync();
        }
        await transaction.CommitAsync();
    }
}
