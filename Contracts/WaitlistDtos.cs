using CafePOS.Api.Domain;

namespace CafePOS.Api.Contracts;

public record WaitlistEntryDto(int Id, string Name, string Phone, int PartySize, DateTime CreatedAt)
{
    public static WaitlistEntryDto From(WaitlistEntry e) => new(e.Id, e.Name, e.Phone, e.PartySize, e.CreatedAt);
}

public record SeatWaitlistEntryRequest(int TableId);

public record JoinWaitlistRequest(string Name, string Phone, int PartySize);
