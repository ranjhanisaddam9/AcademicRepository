using System.ComponentModel.DataAnnotations;

namespace AcademicRepository.Models;

public sealed class SystemSetting
{
    public int Id { get; set; }
    [MaxLength(80), Required] public string Key { get; set; } = "";
    [MaxLength(256), Required] public string Value { get; set; } = "";
    [MaxLength(32), Required] public string DataType { get; set; } = "";
    [MaxLength(240), Required] public string Description { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    [MaxLength(450), Required] public string UpdatedByUserId { get; set; } = "";
}
