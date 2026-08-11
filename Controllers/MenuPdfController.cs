using System.Text.RegularExpressions;
using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Manages the cafe's optional designed PDF menu (see <see cref="MenuPdf"/>). When one is
/// uploaded and enabled, scanning the general (menu-only) QR opens the PDF instead of the
/// live digital menu — the guest-facing side of that lives in PublicOrderPageController and
/// the public serve endpoint in PublicController; this controller is the Owner/Manager admin
/// side (upload / toggle / remove / status).
///
/// The bytes are stored inline in the DB rather than going through IImageStorageService: that
/// service is image-only (rejects application/pdf) and optional per deployment, whereas this
/// must work everywhere. The one row per tenant is enforced by a unique index; an upload
/// upserts it.
/// </summary>
[ApiController]
[Route("api/menu-pdf")]
[Authorize(Policy = Policies.OwnerOrManager)]
public class MenuPdfController(CafePosDbContext db, IAuditService audit) : ControllerBase
{
    /// <summary>Menu PDFs run large (multi-page designed layouts). 15 MB decoded keeps a
    /// generous ceiling while staying under Kestrel's ~28 MB request-body default once base64
    /// inflation (~33%) is accounted for.</summary>
    private const int MaxPdfBytes = 15 * 1024 * 1024;

    private const int MaxFileNameLength = 200;

    [HttpGet]
    public async Task<MenuPdfStatusDto> GetStatus()
    {
        var pdf = await db.MenuPdfs.AsNoTracking()
            .Select(p => new { p.Enabled, p.FileName, p.SizeBytes, p.UpdatedAt })
            .FirstOrDefaultAsync();

        return pdf is null
            ? new MenuPdfStatusDto(false, false, null, 0, null)
            : new MenuPdfStatusDto(true, pdf.Enabled, pdf.FileName, pdf.SizeBytes, pdf.UpdatedAt);
    }

    [HttpPost]
    public async Task<ActionResult<MenuPdfStatusDto>> Upload(UploadMenuPdfRequest req)
    {
        var bytes = ParsePdfDataUri(req.DataUri);
        var fileName = NormalizeFileName(req.FileName);

        var pdf = await db.MenuPdfs.FirstOrDefaultAsync();
        var isNew = pdf is null;
        if (pdf is null)
        {
            pdf = new MenuPdf { Data = bytes, FileName = fileName, SizeBytes = bytes.Length };
            db.MenuPdfs.Add(pdf);
        }
        else
        {
            pdf.Data = bytes;
            pdf.FileName = fileName;
            pdf.SizeBytes = bytes.Length;
            // A fresh upload re-enables the feature — replacing the file is a clear "use this".
            pdf.Enabled = true;
            pdf.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        await audit.LogAsync(isNew ? AuditAction.Create : AuditAction.Update, AuditResource.Menu, pdf.Id.ToString(),
            $"Menu PDF {(isNew ? "uploaded" : "replaced")} ({fileName}).", AuditSeverity.Low);

        return new MenuPdfStatusDto(true, pdf.Enabled, pdf.FileName, pdf.SizeBytes, pdf.UpdatedAt);
    }

    [HttpPut("toggle")]
    public async Task<ActionResult<MenuPdfStatusDto>> Toggle(ToggleMenuPdfRequest req)
    {
        var pdf = await db.MenuPdfs.FirstOrDefaultAsync();
        if (pdf is null)
            throw new ApiValidationException("Upload a menu PDF before turning it on.");

        pdf.Enabled = req.Enabled;
        pdf.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.Update, AuditResource.Menu, pdf.Id.ToString(),
            $"Menu PDF {(req.Enabled ? "enabled" : "disabled")} on the general QR.", AuditSeverity.Low);

        return new MenuPdfStatusDto(true, pdf.Enabled, pdf.FileName, pdf.SizeBytes, pdf.UpdatedAt);
    }

    [HttpDelete]
    public async Task<ActionResult<MenuPdfStatusDto>> Remove()
    {
        var pdf = await db.MenuPdfs.FirstOrDefaultAsync();
        if (pdf is not null)
        {
            db.MenuPdfs.Remove(pdf);
            await db.SaveChangesAsync();
            await audit.LogAsync(AuditAction.Delete, AuditResource.Menu, pdf.Id.ToString(),
                "Menu PDF removed.", AuditSeverity.Low);
        }
        return new MenuPdfStatusDto(false, false, null, 0, null);
    }

    /// <summary>Decodes and validates a "data:application/pdf;base64,…" upload. Mirrors the
    /// defensive shape of ImageStorageService.ParseDataUri: bound by string length before
    /// decoding, then by decoded length, then a magic-byte check so a mislabeled payload can't
    /// pose as a PDF.</summary>
    private static byte[] ParsePdfDataUri(string dataUri)
    {
        if (string.IsNullOrWhiteSpace(dataUri))
            throw new ApiValidationException("No file was uploaded.");

        // Decoding allocates ~3/4 of the string length in bytes — reject grossly oversized
        // payloads before doing any of that.
        if (dataUri.Length > MaxPdfBytes / 3 * 4 + 256)
            throw new ApiValidationException($"PDF is too large — max {MaxPdfBytes / (1024 * 1024)} MB.");

        var match = Regex.Match(dataUri, @"^data:(?<mime>[\w/+.-]+);base64,(?<data>.+)$", RegexOptions.Singleline);
        if (!match.Success)
            throw new ApiValidationException("That doesn't look like a valid PDF upload.");

        if (!string.Equals(match.Groups["mime"].Value, "application/pdf", StringComparison.OrdinalIgnoreCase))
            throw new ApiValidationException("Only PDF files are allowed here.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(match.Groups["data"].Value);
        }
        catch (FormatException)
        {
            throw new ApiValidationException("That doesn't look like a valid PDF upload.");
        }

        if (bytes.Length == 0)
            throw new ApiValidationException("That PDF file is empty.");
        if (bytes.Length > MaxPdfBytes)
            throw new ApiValidationException($"PDF is too large — max {MaxPdfBytes / (1024 * 1024)} MB.");

        // Every PDF begins with the "%PDF-" signature. A renamed image/zip won't, and would
        // otherwise sit at a public URL claiming to be a PDF.
        if (bytes.Length < 5 || bytes[0] != 0x25 || bytes[1] != 0x50 || bytes[2] != 0x44 || bytes[3] != 0x46 || bytes[4] != 0x2D)
            throw new ApiValidationException("The file data doesn't look like a PDF.");

        return bytes;
    }

    private static string NormalizeFileName(string? fileName)
    {
        var name = fileName?.Trim();
        if (string.IsNullOrEmpty(name)) return "menu.pdf";
        if (name.Length > MaxFileNameLength) name = name[..MaxFileNameLength];
        // Strip path separators so a crafted name can never influence a download path.
        name = name.Replace('\\', '_').Replace('/', '_');
        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
        return name;
    }
}
