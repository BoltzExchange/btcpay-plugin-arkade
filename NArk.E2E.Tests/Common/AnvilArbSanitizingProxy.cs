using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace NArk.Tests.End2End.Common;

/// <summary>
/// Minimal JSON-RPC pass-through proxy in front of the stack's anvil-arb.
/// anvil in Arbitrum fork mode serializes <c>l1BlockNumber</c> on locally
/// mined blocks as a decimal JSON integer (remote-forked blocks keep the
/// provider's hex string; reproduced on anvil 1.7.1 and 1.7.2-nightly). The
/// native boltz-client's alloy deserializer requires hex QUANTITY strings and
/// fails the stable leg on such blocks, so the fixture points the client at
/// this proxy, which rewrites integer <c>l1BlockNumber</c> values to hex.
/// </summary>
public sealed class AnvilArbSanitizingProxy : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly HttpClient _upstream = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly CancellationTokenSource _cts = new();

    public string Url { get; }

    public AnvilArbSanitizingProxy(int port = 18549)
    {
        Url = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch when (_cts.IsCancellationRequested)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            string requestBody;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                requestBody = await reader.ReadToEndAsync();

            using var upstreamResponse = await _upstream.PostAsync(
                ArbitrumForkHelper.RpcUrl,
                new StringContent(requestBody, Encoding.UTF8, "application/json"));
            var responseBody = await upstreamResponse.Content.ReadAsStringAsync();

            if (JsonNode.Parse(responseBody) is { } node)
            {
                HexifyL1BlockNumbers(node);
                responseBody = node.ToJsonString();
            }

            var payload = Encoding.UTF8.GetBytes(responseBody);
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload);
        }
        catch
        {
            try
            {
                context.Response.StatusCode = 502;
            }
            catch
            {
                // response already committed
            }
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
                // client already gone
            }
        }
    }

    private static void HexifyL1BlockNumbers(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (key == "l1BlockNumber" && value is JsonValue jsonValue &&
                        jsonValue.TryGetValue<long>(out var number))
                    {
                        obj[key] = $"0x{number:x}";
                    }
                    else if (value is not null)
                    {
                        HexifyL1BlockNumbers(value);
                    }
                }
                break;
            case JsonArray array:
                foreach (var element in array)
                {
                    if (element is not null)
                        HexifyL1BlockNumbers(element);
                }
                break;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // already stopped
        }
        _listener.Close();
        _upstream.Dispose();
    }
}
