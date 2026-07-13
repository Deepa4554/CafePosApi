namespace CafePOS.Api.Contracts;

/// <summary>
/// Relays raw ESC/POS bytes to a network (WiFi/LAN) thermal printer over TCP —
/// browsers can't open raw sockets, so this lets the web app print too, not just the
/// native mobile build. DataBase64 is the already-built ESC/POS command stream (see
/// src/core/printing/escpos.ts on the client) — the backend doesn't interpret it, just
/// forwards the bytes.
/// </summary>
public record WifiPrintRequest(string Ip, int Port, string DataBase64);
