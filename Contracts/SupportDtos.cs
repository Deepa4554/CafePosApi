using CafePOS.Api.Domain;

namespace CafePOS.Api.Contracts;

public record SupportTicketDto(int Id, string Subject, string Status, string CreatedByName, DateTime CreatedAt, DateTime UpdatedAt, int MessageCount, string? TenantName)
{
    public static SupportTicketDto From(SupportTicket t, string? tenantName = null) =>
        new(t.Id, t.Subject, t.Status.ToString().ToUpperInvariant(), t.CreatedByName, t.CreatedAt, t.UpdatedAt, t.Messages.Count, tenantName);
}

public record SupportMessageDto(int Id, bool FromAdmin, string SenderName, string Body, DateTime CreatedAt)
{
    public static SupportMessageDto From(SupportTicketMessage m) => new(m.Id, m.FromAdmin, m.SenderName, m.Body, m.CreatedAt);
}

public record SupportTicketDetailDto(int Id, string Subject, string Status, string CreatedByName, DateTime CreatedAt, DateTime UpdatedAt, string? TenantName, List<SupportMessageDto> Messages)
{
    public static SupportTicketDetailDto From(SupportTicket t, string? tenantName = null) =>
        new(t.Id, t.Subject, t.Status.ToString().ToUpperInvariant(), t.CreatedByName, t.CreatedAt, t.UpdatedAt, tenantName,
            t.Messages.OrderBy(m => m.CreatedAt).Select(SupportMessageDto.From).ToList());
}

public record CreateTicketRequest(string Subject, string Message);
public record AddTicketMessageRequest(string Body);
public record SetTicketStatusRequest(string Status);
