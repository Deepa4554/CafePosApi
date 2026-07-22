namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Simplified default Indian statutory deduction formulas — approximate defaults, not
/// a real per-state PF/ESIC/Professional-Tax slab system. Every input is a plain
/// decimal already scaled to the payroll run's period by the caller (PayrollController
/// prorates the monthly ceiling constants below by period-days/30), so swapping in a
/// real slab table later is a pure calculator change with zero schema impact — a
/// PayrollLine's Pf/Esic/ProfessionalTax columns just store whatever a calculator
/// produced, they don't reference a formula. Any individual line can also be
/// manually overridden pre-lock via PayrollController's line-edit endpoint (e.g. to
/// exempt an intern from PF).
/// </summary>
public static class StatutoryDeductionCalculator
{
    public const decimal MonthlyPfWageCeiling = 15000m;
    public const decimal MonthlyEsicGrossCeiling = 21000m;
    public const decimal MonthlyProfessionalTaxThreshold = 15000m;
    public const decimal MonthlyProfessionalTaxFlat = 200m;

    /// <summary>12% of basic salary, capped at the (period-scaled) wage ceiling.</summary>
    public static decimal Pf(decimal basicSalaryForPeriod, decimal wageCeilingForPeriod) =>
        Math.Round(Math.Min(basicSalaryForPeriod, wageCeilingForPeriod) * 0.12m, 2);

    /// <summary>0.75% of gross earnings, only when gross is at or below the (period-scaled) ceiling.</summary>
    public static decimal Esic(decimal grossEarnings, decimal grossCeilingForPeriod) =>
        grossEarnings <= grossCeilingForPeriod ? Math.Round(grossEarnings * 0.0075m, 2) : 0m;

    /// <summary>Flat amount once gross earnings exceed the (period-scaled) threshold.</summary>
    public static decimal ProfessionalTax(decimal grossEarnings, decimal thresholdForPeriod, decimal flatAmountForPeriod) =>
        grossEarnings > thresholdForPeriod ? flatAmountForPeriod : 0m;
}
