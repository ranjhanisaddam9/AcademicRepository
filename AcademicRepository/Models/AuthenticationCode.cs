namespace AcademicRepository.Models;

public enum CodePurpose { StudentLogin, StaffRecovery }

public class AuthenticationCode
{
    public int Id { get; set; }
    public string? UserId { get; set; }
    public ApplicationUser? User { get; set; }
    public string Email { get; set; } = "";
    public CodePurpose Purpose { get; set; }
    public Guid Nonce { get; set; }
    public string CodeHash { get; set; } = "";
    public string SecurityStamp { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastSentAt { get; set; }
    public DateTimeOffset WindowStartedAt { get; set; }
    public int SentInWindow { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public string? RecoveryGrantHash { get; set; }
    public DateTimeOffset? RecoveryGrantExpiresAt { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
