using Microsoft.AspNetCore.Identity;

namespace AcademicRepository.Models;

public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = "";
    public string? StudentNumber { get; set; }
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = [];
}
