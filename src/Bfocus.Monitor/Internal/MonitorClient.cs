using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Bfocus.Monitor.Internal;

/// <summary>Um monitor ligado (um por Init). Toda falha interna é engolida.</summary>
internal sealed class MonitorClient
{
    public const int MaxBreadcrumbs = 30;
    public const int MaxMessage = 2000;
    public const int MaxEventBytes = 64 * 1024;
    public const long DedupeMs = 30_000;
    public const int MaxPerMinute = 100;
    public const int MaxChain = 10;

    private readonly object _gate = new object();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = new Random();
    private readonly Dictionary<string, long> _seen = new Dictionary<string, long>();
    private readonly Dictionary<string, string> _tags = new Dictionary<string, string>();
    private readonly List<MonitorBreadcrumb> _crumbs = new List<MonitorBreadcrumb>();
    private readonly List<string> _ignore;
    private readonly List<System.Text.RegularExpressions.Regex> _ignorePatterns;
    private readonly List<string> _inAppPrefixes;
    private Identity? _identity;
    private long _windowStartMs;
    private int _windowCount;
    private volatile bool _closed;
    private System.Threading.Timer? _heartbeat;

    public MonitorClient(MonitorOptions options)
    {
        Options = options;
        _ignore = (options.Ignore ?? new List<string>()).Where(s => !string.IsNullOrEmpty(s)).ToList();
        _ignorePatterns = (options.IgnorePatterns ?? new List<System.Text.RegularExpressions.Regex>()).Where(r => r != null).ToList();
        _inAppPrefixes = (options.InAppPrefixes ?? new List<string>()).Where(s => !string.IsNullOrEmpty(s)).ToList();
        var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? "https://api.bfocus.com.br" : options.BaseUrl.Trim().TrimEnd('/');
        Transport = new Transport(baseUrl, options.Key.Trim(), options.RetryDelay, options.BatchInterval);
    }

    public MonitorOptions Options { get; }

    public Transport Transport { get; }

    public bool Disabled => Transport.Disabled;

    public bool AutoCapture => Options.AutoCapture;

    private long NowSeconds() => Options.SigningClock?.Invoke() ?? Signing.NowSeconds();

    // ---- captura ----

    public void CaptureException(Exception? exception, MonitorLevel level, IDictionary<string, string>? tags, IEnumerable<string>? fingerprint)
    {
        if (exception == null || _closed) return;
        try
        {
            // Encadeada: tipo e mensagem da causa raiz (a mais interna; o grupo é dela) e a externa como
            // contexto: " (dentro de: <TipoExterno>: <msg externa>)".
            var root = exception;
            var depth = 0;
            while (root.InnerException != null && depth < MaxChain)
            {
                root = root.InnerException;
                depth++;
            }
            var type = TypeName(root);
            var message = root.Message ?? "";
            if (!ReferenceEquals(root, exception))
            {
                var outer = exception.Message ?? "";
                var outerType = TypeName(exception);
                // Mensagem externa que já contém a interna (AggregateException, por exemplo): só o tipo.
                message += string.IsNullOrEmpty(outer) || (message.Length > 0 && outer.IndexOf(message, StringComparison.Ordinal) >= 0)
                    ? " (dentro de: " + outerType + ")"
                    : " (dentro de: " + outerType + ": " + outer + ")";
            }
            var frames = StackFrames.FromException(root, _inAppPrefixes);
            if (frames.Count == 0 && !ReferenceEquals(root, exception))
                frames = StackFrames.FromException(exception, _inAppPrefixes);
            Enqueue(type, message, frames, level, tags, fingerprint);
        }
        catch
        {
            // nunca derruba o app
        }
    }

    public void CaptureMessage(string? message, MonitorLevel level)
    {
        if (string.IsNullOrEmpty(message) || _closed) return;
        try
        {
            Enqueue("Message", message!, new List<MonitorFrame>(), level, null, null);
        }
        catch
        {
            // nunca derruba o app
        }
    }

    internal static string TypeName(Exception e)
    {
        var t = e.GetType();
        return t.FullName ?? t.Name;
    }

    private void Enqueue(string type, string message, List<MonitorFrame> frames, MonitorLevel level,
        IDictionary<string, string>? extraTags, IEnumerable<string>? fingerprint)
    {
        if (_closed || Transport.Disabled) return;
        if (Ignored(message)) return;
        var rate = Options.SampleRate;
        if (rate < 1.0)
        {
            double roll;
            lock (_random) roll = _random.NextDouble();
            if (roll >= rate) return;
        }

        // Sem repetir: o mesmo erro 1 vez a cada 30 s; no máximo 100 eventos por minuto.
        MonitorFrame? top = null;
        for (var i = frames.Count - 1; i >= 0; i--)
            if (frames[i].InApp) { top = frames[i]; break; }
        top ??= frames.Count > 0 ? frames[frames.Count - 1] : null;
        var dedupeKey = type + "|" + message + "|" + top?.File + ":" + top?.Function + ":" + top?.Line;
        var now = _clock.ElapsedMilliseconds;
        lock (_gate)
        {
            if (_seen.TryGetValue(dedupeKey, out var last) && now - last < DedupeMs) return;
            if (now - _windowStartMs >= 60_000)
            {
                _windowStartMs = now;
                _windowCount = 0;
            }
            if (_windowCount >= MaxPerMinute) return;
            _windowCount++;
            if (_seen.Count > 500)
            {
                foreach (var k in _seen.Where(kv => now - kv.Value >= DedupeMs).Select(kv => kv.Key).ToList()) _seen.Remove(k);
            }
            _seen[dedupeKey] = now;
        }

        MonitorEvent? ev = Build(type, message, frames, level, extraTags, fingerprint);
        if (Options.BeforeSend != null)
        {
            try
            {
                ev = Options.BeforeSend(ev);
            }
            catch
            {
                // beforeSend com erro: manda como está
            }
            if (ev == null) return;
        }
        var json = Fit(ev);
        if (json != null) Transport.Enqueue(json);
    }

    private bool Ignored(string message)
    {
        foreach (var s in _ignore)
            if (message.IndexOf(s, StringComparison.Ordinal) >= 0) return true;
        foreach (var r in _ignorePatterns)
        {
            try
            {
                if (r.IsMatch(message)) return true;
            }
            catch
            {
                // regex com tempo esgotado: não ignora
            }
        }
        return false;
    }

    private MonitorEvent Build(string type, string message, List<MonitorFrame> frames, MonitorLevel level,
        IDictionary<string, string>? extraTags, IEnumerable<string>? fingerprint)
    {
        var scope = Scope.Current;
        var ev = new MonitorEvent
        {
            Timestamp = Iso(DateTime.UtcNow),
            Level = level,
            Release = string.IsNullOrEmpty(Options.Release) ? null : Options.Release,
            Environment = string.IsNullOrEmpty(Options.Environment) ? "production" : Options.Environment,
            Exception = new MonitorException { Type = type, Message = Cut(message, MaxMessage), Frames = frames },
            Contexts = Contexts(),
        };

        Identity? id;
        lock (_gate)
        {
            foreach (var kv in _tags) ev.Tags[kv.Key] = kv.Value;
            ev.Breadcrumbs = new List<MonitorBreadcrumb>(_crumbs);
            id = _identity;
        }
        if (scope != null)
        {
            lock (scope.Gate)
            {
                foreach (var kv in scope.Tags) ev.Tags[kv.Key] = kv.Value;
                foreach (var b in scope.Breadcrumbs) ev.Breadcrumbs.Add(b);
                id = scope.Identity ?? id;
            }
            ev.Transaction = scope.Transaction;
            ev.Url = scope.Url;
        }
        if (ev.Breadcrumbs.Count > MaxBreadcrumbs)
            ev.Breadcrumbs = ev.Breadcrumbs.OrderBy(b => b.Timestamp, StringComparer.Ordinal).Skip(ev.Breadcrumbs.Count - MaxBreadcrumbs).ToList();
        if (extraTags != null)
            foreach (var kv in extraTags)
                if (kv.Key != null && kv.Value != null) ev.Tags[Cut(kv.Key, 64)] = Cut(kv.Value, 200);
        if (fingerprint != null)
        {
            var fp = fingerprint.Where(x => !string.IsNullOrEmpty(x)).Take(10).ToList();
            if (fp.Count > 0) ev.Fingerprint = fp;
        }
        if (id != null)
        {
            if (!string.IsNullOrEmpty(id.UserExternalId))
                ev.User = new MonitorUser { ExternalId = id.UserExternalId!, UserHash = id.GivenHash ?? CurrentSignature(id) };
            if (!string.IsNullOrEmpty(id.CustomerExternalId))
                ev.Customer = new MonitorCustomer { ExternalId = id.CustomerExternalId! };
        }
        return ev;
    }

    private string? CurrentSignature(Identity id)
    {
        lock (id)
        {
            if (id.SignedHash == null) return null;
            var now = NowSeconds();
            if (now - id.SignedAt > Signing.MaxAgeSeconds && !string.IsNullOrEmpty(Options.SigningSecret))
            {
                id.SignedHash = Signing.UserHash(Options.SigningSecret!, now, id.UserExternalId!, id.CustomerExternalId!);
                id.SignedAt = now;
            }
            return id.SignedHash;
        }
    }

    /// <summary>JSON do evento com no máximo 64 KB (corta mensagem, frames e passos antes de desistir).</summary>
    internal static string? Fit(MonitorEvent ev)
    {
        var json = EventJson.Serialize(ev);
        if (Encoding.UTF8.GetByteCount(json) <= MaxEventBytes) return json;
        var steps = new (int msg, int frames, int crumbs)[] { (500, 30, 10), (200, 10, 0), (100, 3, 0) };
        foreach (var (msg, frameCount, crumbCount) in steps)
        {
            ev.Exception.Message = Cut(ev.Exception.Message, msg);
            if (ev.Exception.Frames.Count > frameCount)
                ev.Exception.Frames = ev.Exception.Frames.Skip(ev.Exception.Frames.Count - frameCount).ToList();
            if (ev.Breadcrumbs.Count > crumbCount)
                ev.Breadcrumbs = ev.Breadcrumbs.Skip(ev.Breadcrumbs.Count - crumbCount).ToList();
            foreach (var f in ev.Exception.Frames)
            {
                f.File = f.File == null ? null : Cut(f.File, 300);
                f.Function = f.Function == null ? null : Cut(f.Function, 200);
            }
            json = EventJson.Serialize(ev);
            if (Encoding.UTF8.GetByteCount(json) <= MaxEventBytes) return json;
        }
        return null;
    }

    // ---- contexto ----

    public void SetUser(string? userExternalId, string? customerExternalId, string? userHash)
    {
        try
        {
            Identity? id = null;
            if (!string.IsNullOrEmpty(userExternalId) || !string.IsNullOrEmpty(customerExternalId))
            {
                id = new Identity
                {
                    UserExternalId = string.IsNullOrEmpty(userExternalId) ? null : userExternalId,
                    CustomerExternalId = string.IsNullOrEmpty(customerExternalId) ? null : customerExternalId,
                    GivenHash = string.IsNullOrEmpty(userHash) ? null : userHash,
                };
                if (id.GivenHash == null && !string.IsNullOrEmpty(Options.SigningSecret)
                    && id.UserExternalId != null && id.CustomerExternalId != null)
                {
                    id.SignedAt = NowSeconds();
                    id.SignedHash = Signing.UserHash(Options.SigningSecret!, id.SignedAt, id.UserExternalId, id.CustomerExternalId);
                }
            }
            var scope = Scope.Current;
            if (scope != null)
            {
                lock (scope.Gate) scope.Identity = id;
            }
            else
            {
                lock (_gate) _identity = id;
            }
        }
        catch
        {
            // nunca derruba o app
        }
    }

    public void SetTag(string? key, string? value)
    {
        if (string.IsNullOrEmpty(key) || value == null) return;
        var k = Cut(key!, 64);
        var v = Cut(value, 200);
        var scope = Scope.Current;
        if (scope != null)
        {
            lock (scope.Gate) scope.Tags[k] = v;
        }
        else
        {
            lock (_gate) _tags[k] = v;
        }
    }

    public void AddBreadcrumb(string? category, string? message, MonitorLevel level)
    {
        if (string.IsNullOrEmpty(category) && string.IsNullOrEmpty(message)) return;
        var crumb = new MonitorBreadcrumb
        {
            Timestamp = Iso(DateTime.UtcNow),
            Category = Cut(category ?? "", 40),
            Message = Cut(message ?? "", 300),
            Level = level,
        };
        var scope = Scope.Current;
        var list = scope?.Breadcrumbs ?? _crumbs;
        lock (scope?.Gate ?? _gate)
        {
            list.Add(crumb);
            if (list.Count > MaxBreadcrumbs) list.RemoveAt(0);
        }
    }

    public bool Flush(TimeSpan timeout)
    {
        try
        {
            return Transport.Flush(timeout);
        }
        catch
        {
            return false;
        }
    }

    // ---- sinal de vida ----

    /// <summary>
    /// Primeiro sinal de vida logo depois do Init (numa thread do pool, nunca em quem chamou) e depois a cada
    /// 5 min. O Timer do .NET não segura o processo vivo.
    /// </summary>
    public void StartHeartbeat()
    {
        try
        {
            _heartbeat = new System.Threading.Timer(_ => Heartbeat(), null, TimeSpan.Zero, Options.HeartbeatInterval);
        }
        catch
        {
            // sem timer: segue sem sinal de vida
        }
    }

    private int _beating;

    private void Heartbeat()
    {
        if (_closed || Transport.Disabled) return;
        if (System.Threading.Interlocked.Exchange(ref _beating, 1) == 1) return;
        try
        {
            Transport.SendHeartbeat(HeartbeatJson());
        }
        catch
        {
            // nunca derruba o app
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _beating, 0);
        }
    }

    internal string HeartbeatJson()
    {
        var sb = new StringBuilder(256);
        sb.Append('{');
        void Field(string name, string? value)
        {
            if (value == null) return;
            if (sb.Length > 1) sb.Append(',');
            EventJson.Quote(sb, name);
            sb.Append(':');
            EventJson.Quote(sb, value);
        }
        Field("instance", Instance());
        Field("release", string.IsNullOrEmpty(Options.Release) ? null : Options.Release);
        Field("environment", string.IsNullOrEmpty(Options.Environment) ? "production" : Options.Environment);
        Field("host", HostName());
        sb.Append(",\"runtime\":{\"name\":\".NET\",\"version\":");
        EventJson.Quote(sb, SafeFramework());
        sb.Append("},\"sdk\":{\"name\":");
        EventJson.Quote(sb, BfocusMonitor.SdkName);
        sb.Append(",\"version\":");
        EventJson.Quote(sb, BfocusMonitor.Version);
        sb.Append("}}");
        return sb.ToString();
    }

    private static string? HostName()
    {
        try
        {
            var h = System.Environment.MachineName;
            return string.IsNullOrEmpty(h) ? null : h;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeFramework()
    {
        try
        {
            return RuntimeInformation.FrameworkDescription;
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>Id estável do processo: hash curto de hostname + pid.</summary>
    internal static string Instance()
    {
        int pid;
        try
        {
            pid = Process.GetCurrentProcess().Id;
        }
        catch
        {
            pid = 0;
        }
        using var sha = System.Security.Cryptography.SHA256.Create();
        var digest = sha.ComputeHash(Encoding.UTF8.GetBytes((HostName() ?? "") + ":" + pid.ToString(CultureInfo.InvariantCulture)));
        var hex = new StringBuilder(16);
        for (var i = 0; i < 8; i++) hex.Append(digest[i].ToString("x2"));
        return hex.ToString();
    }

    public void Close(TimeSpan timeout)
    {
        if (_closed) return;
        try
        {
            _heartbeat?.Dispose();
        }
        catch
        {
            // segue fechando
        }
        try
        {
            Transport.Flush(timeout);
        }
        catch
        {
            // segue fechando
        }
        _closed = true;
        Transport.Dispose();
    }

    // ---- utilitários ----

    internal static string Iso(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Cut(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

    private static IDictionary<string, IDictionary<string, string>>? _contexts;

    private static IDictionary<string, IDictionary<string, string>> Contexts()
    {
        var cached = _contexts;
        if (cached == null)
        {
            var built = new Dictionary<string, IDictionary<string, string>>();
            try
            {
                built["runtime"] = new Dictionary<string, string>
                {
                    ["name"] = ".NET",
                    ["version"] = RuntimeInformation.FrameworkDescription,
                };
                built["os"] = new Dictionary<string, string>
                {
                    ["name"] = RuntimeInformation.OSDescription,
                    ["arch"] = RuntimeInformation.OSArchitecture.ToString(),
                };
            }
            catch
            {
                // sem contexto
            }
            _contexts = cached = built;
        }
        // cópia: o beforeSend pode mexer à vontade
        return cached.ToDictionary(kv => kv.Key, kv => (IDictionary<string, string>)new Dictionary<string, string>(kv.Value));
    }
}
