using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Resolves the StaffMember row behind the calling JWT for every self-service HR endpoint
/// (Attendance/Payroll/StaffLoans "me" routes) — same lookup, same failure, previously
/// copy-pasted identically into three controllers.
///
/// An AppUser (login) and a StaffMember (roster row — what Attendance/Payroll actually key
/// off) are separate records with no guaranteed link: RegisterCafe creates only the login,
/// never a roster row, so every real cafe's Owner used to hit "This login has no linked
/// staff roster entry" on My Attendance/My Payslips until someone manually created and
/// linked one for them (see DemoHrSeedData's LinkSignedInOwnerToRoster for how that was
/// done by hand). Self-provisioning here closes that gap for every cafe, past and future,
/// with no manual/support step — but only for Owner logins: a Manager/Waiter's roster row
/// carries real HR data (Department, SalaryType, BasicSalary, bank details) an Owner has to
/// set deliberately, so those still hit the original error and route through Team Portal's
/// "Add Staff Member" (optionally with Grant Access, which creates the login and roster row
/// together and is therefore never affected by this gap).
/// </summary>
public static class CurrentStaffResolver
{
    public static async Task<StaffMember> GetOrCreateCurrentStaffAsync(this ControllerBase controller, CafePosDbContext db)
    {
        var idClaim = controller.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? controller.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var userId = idClaim is not null && int.TryParse(idClaim, out var id) ? id : (int?)null;
        if (userId is null) throw new ApiValidationException("This login has no linked staff roster entry.");

        var staff = await db.Staff.FirstOrDefaultAsync(s => s.UserId == userId);
        if (staff is not null) return staff;

        var user = await db.Users.FindAsync(userId.Value);
        if (user is null || user.Role != AppRole.Owner)
            throw new ApiValidationException("This login has no linked staff roster entry.");

        staff = new StaffMember
        {
            UserId = user.Id,
            Name = user.Name,
            Role = "Owner",
            Email = user.Email,
            Phone = user.Phone,
            Status = StaffStatus.Active,
            JoinedAt = user.CreatedAt,
        };
        db.Staff.Add(staff);
        await db.SaveChangesAsync();
        return staff;
    }
}
