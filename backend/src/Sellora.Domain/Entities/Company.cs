namespace Sellora.Domain.Entities;

/// <summary>A tenant. Business identity/profile lives in the 1:1 <see cref="BusinessSettings"/> row (ADR-07).</summary>
public class Company
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
