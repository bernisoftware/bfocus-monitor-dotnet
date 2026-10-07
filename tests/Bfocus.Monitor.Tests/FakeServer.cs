using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Bfocus.Monitor.Tests;

internal sealed record Received(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    public JsonArray Events => (JsonArray)JsonNode.Parse(Body)!["events"]!;
}

/// <summary>
/// Servidor HTTP local (HttpListener): grava o que chega e responde o roteiro (depois dele, 202). Eventos e
/// sinais de vida ficam separados: o roteiro de eventos não é consumido pelo heartbeat (que responde 204).
/// </summary>
internal sealed class FakeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<Received> _received = new();
    private readonly Queue<(int Status, string Body)> _script;
    private readonly List<Received> _heartbeats = new();

    public const string HeartbeatPath = "/api/v1/monitor/heartbeat";

    /// <summary>Respostas do heartbeat (depois delas, 204).</summary>
    public Queue<int> HeartbeatScript { get; } = new();

    public IReadOnlyList<Received> Heartbeats
    {
        get
        {
            lock (_received) return _heartbeats.ToList();
        }
    }

    /// <summary>Espera chegar ao menos <paramref name="count"/> heartbeats.</summary>
    public IReadOnlyList<Received> WaitHeartbeats(int count, int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Heartbeats.Count < count && Environment.TickCount64 < until) Thread.Sleep(20);
        return Heartbeats;
    }

    public FakeServer(IEnumerable<(int Status, string Body)>? script = null)
    {
        _script = new Queue<(int, string)>(script ?? Array.Empty<(int, string)>());
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string BaseUrl { get; }

    public IReadOnlyList<Received> Requests
    {
        get
        {
            lock (_received) return _received.ToList();
        }
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }
            try
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in ctx.Request.Headers.AllKeys)
                    if (name != null) headers[name] = ctx.Request.Headers[name] ?? "";
                (int Status, string Body) reply;
                lock (_received)
                {
                    var got = new Received(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, headers, body);
                    if (got.Path == HeartbeatPath)
                    {
                        _heartbeats.Add(got);
                        reply = (HeartbeatScript.Count > 0 ? HeartbeatScript.Dequeue() : 204, "");
                    }
                    else
                    {
                        _received.Add(got);
                        reply = _script.Count > 0 ? _script.Dequeue() : (202, "{\"accepted\":1}");
                    }
                }
                var bytes = Encoding.UTF8.GetBytes(reply.Body);
                ctx.Response.StatusCode = reply.Status;
                if (bytes.Length > 0)
                {
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                }
                ctx.Response.Close();
            }
            catch
            {
                // teste segue
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // nada
        }
    }
}
