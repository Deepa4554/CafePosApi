using CafePOS.Api.Domain;

namespace CafePOS.Api.Contracts;

/// <summary>What a guest's phone sends when they press Call Waiter or Request Bill. `OrderToken`
/// is the signed per-order token the counter flow already holds (see PublicController's
/// CounterOrderStatus) — sent only by flows that have no table, so the call can still say which
/// token number is asking.</summary>
public record GuestCallRequest(string Kind, string? OrderToken);

/// <summary>One outstanding call, as the floor screen reads it. `Label` is pre-composed here
/// rather than assembled on the client so the pill, the sheet and any future notification all
/// name a call the same way.</summary>
public record GuestCallDto(
    int Id,
    string Kind,
    string Label,
    string? TableCode,
    int? TokenNumber,
    int? OrderId,
    DateTime CreatedAt)
{
    public static GuestCallDto From(GuestCall c) => new(
        c.Id,
        c.Kind.ToString(),
        c.TableCode is not null ? $"Table {c.TableCode}"
            : c.TokenNumber is not null ? $"Token #{c.TokenNumber}"
            : "Counter",
        c.TableCode,
        c.TokenNumber,
        c.OrderId,
        c.CreatedAt);
}

/// <summary>A guest opening a payment for their own bill from the QR tab they ordered in.
/// `OrderToken` is the signed per-order token that flow already holds — the order id is never
/// sent in plain text, so nobody holding the (shared, printed) QR can pay, or read, someone
/// else's bill by guessing a number.</summary>
public record PayBillRequest(string? OrderToken);
