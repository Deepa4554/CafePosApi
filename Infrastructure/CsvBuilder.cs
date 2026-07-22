using System.Text;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Hand-rolled RFC4180 CSV writer — every use in this app (bank export, salary
/// register, attendance reports) is a fixed-shape, write-only, flat list, so a full
/// CSV library (CsvHelper etc., built for read/parse scenarios this app doesn't have)
/// isn't warranted. UTF-8 with a BOM so Excel renders ₹ and Indian names correctly
/// instead of guessing the wrong codepage.
/// </summary>
public static class CsvBuilder
{
    public static byte[] Build(IEnumerable<string> headers, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", headers.Select(EscapeField))).Append("\r\n");
        foreach (var row in rows)
            sb.Append(string.Join(",", row.Select(v => EscapeField(v?.ToString() ?? "")))).Append("\r\n");

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(sb.ToString());
        var result = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
        return result;
    }

    private static string EscapeField(string field) =>
        field.IndexOfAny(['"', ',', '\n', '\r']) >= 0 ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
}
