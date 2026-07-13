namespace CafePOS.Api.Domain;

public enum SupportTicketStatus
{
    Open,
    Resolved,
}

/// <summary>A cafe's help request to the platform operator — backs the Help Center's
/// "raise a ticket" flow and the Super Admin's Support inbox.</summary>
public class SupportTicket : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public required string Subject { get; set; }
    public SupportTicketStatus Status { get; set; } = SupportTicketStatus.Open;
    public int CreatedByUserId { get; set; }
    public required string CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<SupportTicketMessage> Messages { get; set; } = [];
}

/// <summary>One chat message in a ticket's thread — from either the cafe (FromAdmin =
/// false) or the platform Super Admin replying (FromAdmin = true).</summary>
public class SupportTicketMessage : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int TicketId { get; set; }
    public bool FromAdmin { get; set; }
    public required string SenderName { get; set; }
    public required string Body { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
