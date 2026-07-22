using CafePOS.Api.Domain;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Renders a payslip PDF straight from a PayrollLine — no separate "payslip" data
/// model, no persisted file, generated fresh on every request. Exact same shape as
/// ReceiptPdfBuilder: a static Build(...) returning byte[], nothing stateful.
/// </summary>
public static class PayslipPdfBuilder
{
    public static byte[] Build(CafeSettings settings, PayrollRun run, PayrollLine line)
    {
        var businessName = string.IsNullOrWhiteSpace(settings.BusinessName) ? "CafePOS" : settings.BusinessName;

        void Row(ColumnDescriptor col, string label, string value, bool bold = false, float size = 10)
        {
            col.Item().Row(row =>
            {
                var l = row.RelativeItem().Text(label).FontSize(size);
                var v = row.RelativeItem().AlignRight().Text(value).FontSize(size);
                if (bold) { l.Bold(); v.Bold(); }
            });
        }

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Content().Column(col =>
                {
                    col.Spacing(4);

                    col.Item().AlignCenter().Text(businessName).FontSize(18).Bold();
                    col.Item().AlignCenter().Text("Payslip").FontSize(12);
                    col.Item().AlignCenter().Text($"{run.PeriodStart:dd MMM yyyy} – {run.PeriodEnd:dd MMM yyyy}").FontSize(9);

                    col.Item().PaddingTop(8).LineHorizontal(0.5f);
                    col.Item().Text(line.StaffName).Bold().FontSize(12);
                    col.Item().Text($"Salary Type: {line.SalaryType}").FontSize(9);

                    col.Item().PaddingTop(8).Text("Attendance").Bold().FontSize(11);
                    Row(col, "Present Days", $"{line.PresentDays}");
                    Row(col, "Late Days", $"{line.LateDays}");
                    Row(col, "Half Days", $"{line.HalfDays}");
                    Row(col, "Absent Days", $"{line.AbsentDays}");
                    Row(col, "Paid Leave", $"{line.PaidLeaveDays}");
                    Row(col, "Unpaid Leave", $"{line.UnpaidLeaveDays}");
                    Row(col, "Overtime Hours", $"{line.OvertimeHours:0.0}");

                    col.Item().PaddingTop(8).LineHorizontal(0.5f);
                    col.Item().Text("Earnings").Bold().FontSize(11);
                    Row(col, "Basic Salary", $"{line.BasicSalary:0.00}");
                    Row(col, "Overtime Pay", $"{line.OvertimePay:0.00}");
                    foreach (var a in line.Allowances)
                        Row(col, a.Name, $"{a.Amount:0.00}");
                    Row(col, "Gross Earnings", $"{line.GrossEarnings:0.00}", bold: true);

                    col.Item().PaddingTop(8).LineHorizontal(0.5f);
                    col.Item().Text("Deductions").Bold().FontSize(11);
                    Row(col, "Leave Deduction", $"{line.LeaveDeduction:0.00}");
                    Row(col, "Late Deduction", $"{line.LateDeduction:0.00}");
                    Row(col, "Loan Deduction", $"{line.LoanDeduction:0.00}");
                    Row(col, "PF", $"{line.PfDeduction:0.00}");
                    Row(col, "ESIC", $"{line.EsicDeduction:0.00}");
                    Row(col, "Professional Tax", $"{line.ProfessionalTaxDeduction:0.00}");
                    Row(col, "Total Deductions", $"{line.TotalDeductions:0.00}", bold: true);

                    col.Item().PaddingTop(10).LineHorizontal(0.5f);
                    Row(col, "Net Salary", $"{line.NetSalary:0.00}", bold: true, size: 14);

                    if (!string.IsNullOrWhiteSpace(line.BankAccountNumberSnapshot))
                    {
                        var last4 = line.BankAccountNumberSnapshot.Length > 4
                            ? line.BankAccountNumberSnapshot[^4..]
                            : line.BankAccountNumberSnapshot;
                        col.Item().PaddingTop(10).AlignCenter().Text($"Paid to account ending {last4}").FontSize(8).Italic();
                    }
                });
            });
        }).GeneratePdf();
    }
}
