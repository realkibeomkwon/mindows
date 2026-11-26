using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;

namespace Mindows;

/// <summary>Binance's BTCUSDT mini ticker, pushed once a second, shared by every menu bar so there is one connection.</summary>
static class BtcTicker
{
    const string Url = "wss://stream.binance.com:9443/ws/btcusdt@miniTicker";

    /// <summary>The last price and the 24h change in percent; null while there is no live connection.</summary>
    public static (decimal Price, decimal Percent)? Latest { get; private set; }
    public static event Action? Changed; // raised on a background thread

    static CancellationTokenSource? running; // touched from the UI thread only

    static BtcTicker()
    {
        Debug.Assert(Parse("""{"e":"24hrMiniTicker","c":"110.00","o":"100.00"}""") == (110m, 10m)
            && Parse("""{"c":"95.00","o":"100.00"}""") == (95m, -5m),
            "the 24h change must be the close against the open, in percent");
    }

    public static void Start()
    {
        if (running != null) return;
        var source = running = new CancellationTokenSource();
        _ = Task.Run(() => Run(source.Token));
    }

    public static void Stop()
    {
        running?.Cancel();
        running = null;
        Latest = null;
    }

    static (decimal, decimal) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var close = decimal.Parse(doc.RootElement.GetProperty("c").GetString()!, CultureInfo.InvariantCulture);
        var open = decimal.Parse(doc.RootElement.GetProperty("o").GetString()!, CultureInfo.InvariantCulture);
        return (close, (close - open) / open * 100);
    }

    static async Task Run(CancellationToken token)
    {
        var buffer = new byte[4096];
        while (true)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(Url), token);
                while (true)
                {
                    // The server pushes every second, so a silence this long means the connection is dead.
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer, timeout.Token);
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    Latest = Parse(System.Text.Encoding.UTF8.GetString(message.ToArray()));
                    Changed?.Invoke();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { } // offline or dropped: hide the price and reconnect
            if (token.IsCancellationRequested) return; // Stop already cleared the price
            Latest = null;
            Changed?.Invoke();
            try { await Task.Delay(5000, token); } catch (OperationCanceledException) { return; }
        }
    }
}
