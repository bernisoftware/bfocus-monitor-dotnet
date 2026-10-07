using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;

namespace Bfocus.Monitor.Internal;

/// <summary>
/// Fila em memória + uma thread de fundo que envia em lote (a cada 1 s ou 20 eventos). 429/5xx/rede:
/// uma nova tentativa depois de 2 s. 401/403: desliga o envio até o próximo Init. Nunca lança.
/// </summary>
internal sealed class Transport : IDisposable
{
    public const int MaxQueue = 100;
    public const int BatchTrigger = 20;
    public const int MaxBatch = 50;

    private readonly object _lock = new object();
    private readonly List<string> _queue = new List<string>();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Uri _endpoint;
    private readonly Uri _heartbeatEndpoint;
    private readonly string _key;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _interval;
    private readonly HttpClient _http;
    private Thread? _thread;
    private long _firstAtMs;
    private bool _sending;
    private bool _flushRequested;
    private bool _stopped;
    private volatile bool _disabled;

    public Transport(string baseUrl, string key, TimeSpan retryDelay, TimeSpan interval)
    {
        _endpoint = new Uri(baseUrl + "/api/v1/monitor/events");
        _heartbeatEndpoint = new Uri(baseUrl + "/api/v1/monitor/heartbeat");
        _key = key;
        _retryDelay = retryDelay;
        _interval = interval;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public static string ClientHeader => BfocusMonitor.SdkName + "/" + BfocusMonitor.Version;

    /// <summary>Só para teste: segura a thread (a fila enche sem ninguém consumir).</summary>
    internal volatile bool HoldForTest;

    internal int QueueCount
    {
        get
        {
            lock (_lock) return _queue.Count;
        }
    }

    /// <summary>Recusado (401/403): nada mais sai até o próximo Init.</summary>
    public bool Disabled => _disabled;

    /// <summary>Põe o evento (JSON) na fila. Cheia → descarta o mais novo (este).</summary>
    public bool Enqueue(string eventJson)
    {
        lock (_lock)
        {
            if (_stopped || _disabled || _queue.Count >= MaxQueue) return false;
            if (_queue.Count == 0) _firstAtMs = _clock.ElapsedMilliseconds;
            _queue.Add(eventJson);
            EnsureThread();
            System.Threading.Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>Envia o que está na fila e espera terminar, até o teto. Devolve se esvaziou.</summary>
    public bool Flush(TimeSpan timeout)
    {
        var deadline = _clock.ElapsedMilliseconds + (long)Math.Max(0, timeout.TotalMilliseconds);
        lock (_lock)
        {
            if (_queue.Count == 0 && !_sending) return true;
            _flushRequested = true;
            EnsureThread();
            System.Threading.Monitor.PulseAll(_lock);
            while (_queue.Count > 0 || _sending)
            {
                var left = deadline - _clock.ElapsedMilliseconds;
                if (left <= 0) return false;
                System.Threading.Monitor.Wait(_lock, (int)Math.Min(left, int.MaxValue));
            }
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _stopped = true;
            _queue.Clear();
            System.Threading.Monitor.PulseAll(_lock);
        }
    }

    private void EnsureThread()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "bfocus-monitor" };
        _thread.Start();
    }

    private void Run()
    {
        while (true)
        {
            List<string> batch;
            lock (_lock)
            {
                while (true)
                {
                    if (_stopped) { System.Threading.Monitor.PulseAll(_lock); return; }
                    if (_queue.Count == 0 || HoldForTest)
                    {
                        if (_queue.Count == 0) _flushRequested = false;
                        System.Threading.Monitor.PulseAll(_lock);
                        System.Threading.Monitor.Wait(_lock, 100);
                        continue;
                    }
                    var waited = _clock.ElapsedMilliseconds - _firstAtMs;
                    var left = (long)_interval.TotalMilliseconds - waited;
                    if (_flushRequested || _queue.Count >= BatchTrigger || left <= 0) break;
                    System.Threading.Monitor.Wait(_lock, (int)Math.Max(1, left));
                }
                var n = Math.Min(_queue.Count, MaxBatch);
                batch = _queue.GetRange(0, n);
                _queue.RemoveRange(0, n);
                if (_queue.Count > 0) _firstAtMs = _clock.ElapsedMilliseconds;
                _sending = true;
            }
            try
            {
                Send(batch);
            }
            catch
            {
                // nunca derruba o app
            }
            lock (_lock)
            {
                _sending = false;
                System.Threading.Monitor.PulseAll(_lock);
            }
        }
    }

    private void Send(List<string> batch)
    {
        var sb = new StringBuilder("{\"events\":[");
        for (var i = 0; i < batch.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(batch[i]);
        }
        sb.Append("]}");
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_disabled) return;
            var status = Post(body);
            if (status >= 200 && status < 300) return;
            if (status == 401 || status == 403)
            {
                lock (_lock)
                {
                    _disabled = true;
                    _queue.Clear();
                }
                return;
            }
            var retryable = status == -1 || status == 429 || status >= 500;
            if (!retryable || attempt == 1) return;
            Thread.Sleep(_retryDelay);
        }
    }

    /// <summary>
    /// Sinal de vida (BRIEF §7b): 204 ok; 401/403 desliga o envio como nos eventos; falha de rede é
    /// ignorada (o próximo intervalo tenta de novo). Chamado pelo timer, nunca por quem chamou o Init.
    /// </summary>
    public void SendHeartbeat(string json)
    {
        if (_disabled) return;
        lock (_lock)
        {
            if (_stopped) return;
        }
        var status = Post(Encoding.UTF8.GetBytes(json), _heartbeatEndpoint);
        if (status == 401 || status == 403)
        {
            lock (_lock)
            {
                _disabled = true;
                _queue.Clear();
            }
        }
    }

    /// <summary>Status HTTP, ou -1 em erro de rede.</summary>
    private int Post(byte[] body) => Post(body, _endpoint);

    private int Post(byte[] body, Uri endpoint)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.TryAddWithoutValidation("X-bFocus-Monitor-Key", _key);
            req.Headers.TryAddWithoutValidation("X-bFocus-Client", ClientHeader);
            req.Headers.TryAddWithoutValidation("User-Agent", ClientHeader);
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Content = content;
            using var res = _http.SendAsync(req).ConfigureAwait(false).GetAwaiter().GetResult();
            return (int)res.StatusCode;
        }
        catch
        {
            return -1;
        }
    }
}
