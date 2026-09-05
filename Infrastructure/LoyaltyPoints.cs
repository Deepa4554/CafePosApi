namespace CafePOS.Api.Infrastructure;

/// <summary>The single place a bill turns into loyalty points.
///
/// It is a class of its own because the arithmetic has three callers, not one:
/// OrderBuildingService.RecordVisit credits the guest when the order is rung up, and
/// OrdersController.AttachCustomerAsync both debits the record the order was sitting on and
/// credits the one it moves to when a real mobile number arrives late. Those three ran the
/// same expression inline. That was survivable while the rate was the constant 1-point-per-₹1,
/// and stops being survivable the moment it comes from CafeSettings: change the rate, move an
/// order between customers, and a debit computed at the old rate against a credit computed at
/// the new one leaves points behind on a record nobody is looking at.
/// </summary>
public static class LoyaltyPoints
{
    /// <summary>Points earned on <paramref name="amountSpent"/> at <paramref name="earnPct"/>.
    ///
    /// Floored, never rounded: rounding up hands out a point nobody paid for, and on a busy
    /// till that is a real, if small, running cost. A rate of 0 turns earning off entirely
    /// rather than being a special case anywhere else.</summary>
    public static int ForSpend(decimal amountSpent, decimal earnPct)
    {
        if (amountSpent <= 0 || earnPct <= 0) return 0;
        return (int)Math.Floor(amountSpent * earnPct / 100m);
    }
}
