namespace CafePOS.Api.Contracts;

/// <summary>What the admin QR-ordering screen reads to render the "PDF Menu" section — never
/// includes the bytes themselves. <see cref="HasPdf"/> false means nothing has been uploaded
/// yet (every other field is null/false in that case).</summary>
public record MenuPdfStatusDto(
    bool HasPdf,
    bool Enabled,
    string? FileName,
    long SizeBytes,
    DateTime? UpdatedAt);

/// <summary>Upload of a new menu PDF. <see cref="DataUri"/> is the "data:application/pdf;base64,…"
/// string the client produces from the picked file — same shape the image uploads use.</summary>
public record UploadMenuPdfRequest(string DataUri, string? FileName);

/// <summary>Flip the general-QR-shows-PDF switch without re-uploading the file.</summary>
public record ToggleMenuPdfRequest(bool Enabled);
