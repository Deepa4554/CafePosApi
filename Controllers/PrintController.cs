using System.Net;
using System.Net.Sockets;
using CafePOS.Api.Contracts;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Printer connectivity for the POS. WiFi/LAN thermal printers only — any staff
/// member can print a receipt, so this only requires being logged in (see the
/// fallback auth policy in Program.cs), nothing higher. Bluetooth printing happens
/// entirely on-device via a native module (src/core/printing/BluetoothPrinter.ts)
/// and never touches the backend.
/// </summary>
[ApiController]
[Route("api/print")]
public class PrintController : ControllerBase
{
    private const int ConnectTimeoutMs = 5000;
    private const int MaxPayloadBytes = 64 * 1024; // a receipt is a few hundred bytes at most; guards against abuse

    [HttpPost("wifi")]
    public async Task<IActionResult> PrintWifi(WifiPrintRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Ip) || !IPAddress.TryParse(req.Ip.Trim(), out var ip))
            throw new ApiValidationException("Enter a valid printer IP address (e.g. 192.168.1.50).");
        if (req.Port is < 1 or > 65535)
            throw new ApiValidationException("Port must be between 1 and 65535 (network printers typically use 9100).");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(req.DataBase64);
        }
        catch (FormatException)
        {
            throw new ApiValidationException("Print data is malformed.");
        }
        if (bytes.Length == 0)
            throw new ApiValidationException("Nothing to print.");
        if (bytes.Length > MaxPayloadBytes)
            throw new ApiValidationException("Print payload is too large.");

        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(ConnectTimeoutMs);
        try
        {
            await client.ConnectAsync(ip, req.Port, cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new ApiConflictException($"Could not reach the printer at {req.Ip}:{req.Port} — timed out. Check it's powered on and connected to the same network.");
        }
        catch (SocketException)
        {
            throw new ApiConflictException($"Could not connect to the printer at {req.Ip}:{req.Port}. Check the IP address and that it's on the same network.");
        }

        await using var stream = client.GetStream();
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();

        return NoContent();
    }
}
