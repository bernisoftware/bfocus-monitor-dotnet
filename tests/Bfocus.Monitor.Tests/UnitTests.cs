using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bfocus.Monitor.Internal;
using Xunit;

namespace Bfocus.Monitor.Tests;

public class UnitTests : IDisposable
{
    public void Dispose() => BfocusMonitor.Close();

    internal static MonitorOptions Options(FakeServer server, Action<MonitorOptions>? tweak = null)
    {
        var o = new MonitorOptions
        {
            Key = "bf_mon_test",
            Release = "1.4.2",
            BaseUrl = server.BaseUrl + "/",
            AutoCapture = false,
            RetryDelay = TimeSpan.FromMilliseconds(50),
            BatchInterval = TimeSpan.FromMilliseconds(100),
        };
        tweak?.Invoke(o);
        return o;
    }

    internal static List<JsonNode> AllEvents(FakeServer server) =>
        server.Requests.SelectMany(r => r.Events.Select(e => e!)).ToList();

    // ---- versão ----

    [Fact]
    public void Version_MatchesManifests()
    {
        var root = Path.GetFullPath(Path.Combine(ConformanceTests.SourceDir(), "..", ".."));
        foreach (var csproj in new[] { "src/Bfocus.Monitor/Bfocus.Monitor.csproj", "src/Bfocus.Monitor.AspNetCore/Bfocus.Monitor.AspNetCore.csproj" })
        {
            var text = File.ReadAllText(Path.Combine(root, csproj));
            var m = Regex.Match(text, "<Version>([^<]+)</Version>");
            Assert.True(m.Success, csproj + " sem <Version>");
            Assert.Equal(BfocusMonitor.Version, m.Groups[1].Value.Trim());
        }
        var informational = typeof(BfocusMonitor).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(BfocusMonitor.Version, informational.Split('+')[0]);

        // monitor/release.json só existe no monorepo (o espelho público não tem).
        var release = Path.Combine(root, "..", "release.json");
        if (File.Exists(release))
        {
            var manifest = (string)JsonNode.Parse(File.ReadAllText(release))!["version"]!;
            Assert.Equal(BfocusMonitor.Version, manifest);
        }
    }

    [Fact]
    public void VendoredCases_MatchMonorepo()
    {
        var mono = Path.Combine(ConformanceTests.SourceDir(), "..", "..", "..", "conformance", "cases.json");
        if (!File.Exists(mono)) return; // espelho público
        Assert.Equal(File.ReadAllText(mono), File.ReadAllText(Path.Combine(ConformanceTests.SourceDir(), "cases.json")));
    }

    // ---- init ----

    [Fact]
    public void Init_RequiresKey()
    {
        Assert.Throws<ArgumentException>(() => BfocusMonitor.Init(new MonitorOptions { Key = "  " }));
        Assert.Throws<ArgumentNullException>(() => BfocusMonitor.Init(null!));
    }

    [Fact]
    public void Calls_BeforeInit_AreNoOps()
    {
        BfocusMonitor.Close();
        BfocusMonitor.CaptureException(new InvalidOperationException("x"));
        BfocusMonitor.CaptureMessage("x");
        BfocusMonitor.SetUser("u", "c");
        BfocusMonitor.SetTag("k", "v");
        BfocusMonitor.AddBreadcrumb("c", "m");
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void Init_SendsNoEvents_HeartbeatGoesInBackground()
    {
        using var server = new FakeServer();
        BfocusMonitor.Init(Options(server));
        Assert.Single(server.WaitHeartbeats(1));
        Thread.Sleep(200);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void Heartbeat_RepeatsOnTheInterval_WithStableInstance()
    {
        using var server = new FakeServer();
        BfocusMonitor.Init(Options(server, o => o.HeartbeatInterval = TimeSpan.FromMilliseconds(150)));
        var beats = server.WaitHeartbeats(3);
        Assert.True(beats.Count >= 3, "esperava 3 heartbeats, vieram " + beats.Count);
        var instances = beats.Select(b => (string)JsonNode.Parse(b.Body)!["instance"]!).Distinct().ToList();
        Assert.Single(instances);
        Assert.Equal(MonitorClient.Instance(), instances[0]);
        Assert.Matches("^[0-9a-f]{16}$", instances[0]);
        Assert.Equal(Environment.MachineName, (string)JsonNode.Parse(beats[0].Body)!["host"]!);

        // close para o timer
        BfocusMonitor.Close();
        var after = server.Heartbeats.Count;
        Thread.Sleep(500);
        Assert.Equal(after, server.Heartbeats.Count);
    }

    [Fact]
    public void Heartbeat_401_DisablesSending()
    {
        using var server = new FakeServer();
        server.HeartbeatScript.Enqueue(401);
        BfocusMonitor.Init(Options(server, o => o.HeartbeatInterval = TimeSpan.FromMilliseconds(100)));
        server.WaitHeartbeats(1);
        var until = Environment.TickCount64 + 3000;
        while (!BfocusMonitor.Current!.Disabled && Environment.TickCount64 < until) Thread.Sleep(10);
        Assert.True(BfocusMonitor.Current!.Disabled);
        BfocusMonitor.CaptureMessage("não sai");
        BfocusMonitor.Flush(TimeSpan.FromSeconds(1));
        Thread.Sleep(300);
        Assert.Empty(server.Requests);
        Assert.Single(server.Heartbeats); // desligado: o timer também para de bater
    }

    [Fact]
    public void Heartbeat_NetworkFailure_IsIgnored()
    {
        BfocusMonitor.Init(new MonitorOptions { Key = "bf_mon_x", BaseUrl = "http://127.0.0.1:1", AutoCapture = false, HeartbeatInterval = TimeSpan.FromMilliseconds(50) });
        Thread.Sleep(300);
        Assert.False(BfocusMonitor.Current!.Disabled);
    }

    [Fact]
    public void FlushAndClose_DoNotSendHeartbeat()
    {
        using var server = new FakeServer();
        BfocusMonitor.Init(Options(server));
        server.WaitHeartbeats(1);
        Thread.Sleep(100);
        BfocusMonitor.Flush(TimeSpan.FromSeconds(1));
        BfocusMonitor.Close();
        Thread.Sleep(200);
        Assert.Single(server.Heartbeats);
    }

    // ---- envio ----

    [Fact]
    public void Headers_And_Body()
    {
        using var server = new FakeServer();
        BfocusMonitor.Init(Options(server));
        BfocusMonitor.CaptureException(ConformanceTests.Thrown("Boom", "x"));
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(5)));
        var req = Assert.Single(server.Requests);
        Assert.Equal("bfocus-monitor-dotnet/0.1.0", req.Header("X-bFocus-Client"));
        Assert.Equal("bfocus-monitor-dotnet/0.1.0", req.Header("User-Agent"));
        Assert.Equal("application/json", req.Header("Content-Type"));
        Assert.Equal("/api/v1/monitor/events", req.Path); // barra final do BaseUrl não duplica
        var ev = req.Events[0]!;
        Assert.Equal("production", (string)ev["environment"]!);
        Assert.Equal(".NET", (string)ev["contexts"]!["runtime"]!["name"]!);
        Assert.Null(ev["user"]);
        Assert.Null(ev["transaction"]);
    }

    [Fact]
    public void Batch_GroupsEventsOfTheSameSecond()
    {
        using var server = new FakeServer();
        BfocusMonitor.Init(Options(server, o => o.BatchInterval = TimeSpan.FromMilliseconds(400)));
        for (var i = 0; i < 5; i++) BfocusMonitor.CaptureMessage("lote " + i, MonitorLevel.Warning);
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(5)));
        var req = Assert.Single(server.Requests);
        Assert.Equal(5, req.Events.Count);
    }

    [Fact]
    public void ServerError_RetriesOnce_ThenDrops_WithoutDisabling()
    {
        using var server = new FakeServer(new[] { (500, "{}"), (503, "{}") });
        BfocusMonitor.Init(Options(server));
        BfocusMonitor.CaptureMessage("cai");
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, server.Requests.Count);
        Assert.False(BfocusMonitor.Current!.Disabled);
        BfocusMonitor.CaptureMessage("depois");
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, server.Requests.Count);
    }

    [Fact]
    public void NetworkError_RetriesOnce_AndNeverThrows()
    {
        BfocusMonitor.Init(new MonitorOptions { Key = "bf_mon_x", BaseUrl = "http://127.0.0.1:1", AutoCapture = false, RetryDelay = TimeSpan.FromMilliseconds(10) });
        BfocusMonitor.CaptureMessage("sem rede");
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Forbidden_Disables_UntilNextInit()
    {
        using var server = new FakeServer(new[] { (403, "{\"error\":\"MONITOR_AGENT_DISABLED\"}") });
        BfocusMonitor.Init(Options(server));
        BfocusMonitor.CaptureMessage("um");
        BfocusMonitor.Flush(TimeSpan.FromSeconds(5));
        Assert.True(BfocusMonitor.Current!.Disabled);
        BfocusMonitor.CaptureMessage("dois");
        BfocusMonitor.Flush(TimeSpan.FromSeconds(1));
        Assert.Single(server.Requests);

        BfocusMonitor.Init(Options(server));
        BfocusMonitor.CaptureMessage("três");
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public void RateLimit_100PerMinute()
    {
        using var server = new FakeServer();
        BfocusMonitor.Init(Options(server));
        for (var i = 0; i < 150; i++) BfocusMonitor.CaptureMessage("distinto " + i);
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(10)));
        Assert.Equal(100, AllEvents(server).Count);
        Assert.All(server.Requests, r => Assert.True(r.Events.Count <= Transport.MaxBatch));
    }

    [Fact]
    public void Queue_IsBounded_DropsNewest()
    {
        var t = new Transport("http://127.0.0.1:1", "k", TimeSpan.Zero, TimeSpan.FromHours(1)) { HoldForTest = true };
        try
        {
            var accepted = 0;
            for (var i = 0; i < 300; i++) if (t.Enqueue("{}")) accepted++;
            Assert.Equal(Transport.MaxQueue, accepted);
            Assert.Equal(Transport.MaxQueue, t.QueueCount);
            Assert.False(t.Flush(TimeSpan.FromMilliseconds(50))); // segura: o teto do flush vale
        }
        finally
        {
            t.Dispose();
        }
    }

    // ---- evento ----

    [Fact]
    public void ChainedException_SendsRootCause()
    {
        using var server = new FakeServer();
        BfocusMonitor.Init(Options(server));
        Exception wrapped = null!;
        try
        {
            try
            {
                Raiz();
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException("Falha ao fechar o pedido", inner);
            }
        }
        catch (Exception e)
        {
            wrapped = e;
        }
        BfocusMonitor.CaptureException(wrapped);
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(5)));
        var ev = AllEvents(server).Single();
        Assert.Equal("System.DivideByZeroException", (string)ev["exception"]!["type"]!);
        Assert.Equal(new DivideByZeroException().Message + " (dentro de: System.InvalidOperationException: Falha ao fechar o pedido)",
            (string)ev["exception"]!["message"]!);
        var frames = ((JsonArray)ev["exception"]!["frames"]!).Where(f => (bool)f!["inApp"]!).ToList();
        Assert.EndsWith("UnitTests.Raiz", (string)frames[^1]!["function"]!); // onde estourou no código do app
    }

    [Fact]
    public void ChainedException_OuterMessageContainingInner_KeepsOnlyOuterType()
    {
        using var server = new FakeServer();
        BfocusMonitor.Init(Options(server));
        var agg = new AggregateException(ConformanceTests.Thrown("TarefaFalhou", "conexão caiu"));
        BfocusMonitor.CaptureException(agg);
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(5)));
        var ev = AllEvents(server).Single();
        Assert.Equal("TarefaFalhou", (string)ev["exception"]!["type"]!);
        Assert.Equal("conexão caiu (dentro de: System.AggregateException)", (string)ev["exception"]!["message"]!);
    }

    private static int _zero = int.Parse("0");

    private static int Raiz() => 10 / _zero;

    [Fact]
    public void RealStackTrace_OutsideIn_WithInApp()
    {
        Exception ex;
        try
        {
            NivelUm();
            return;
        }
        catch (Exception e)
        {
            ex = e;
        }
        var frames = StackFrames.FromException(ex, new List<string>());
        Assert.True(frames.Count >= 3);
        Assert.EndsWith("UnitTests.NivelDois", frames[^1].Function);
        Assert.EndsWith("UnitTests.NivelUm", frames[^2].Function);
        Assert.True(frames[^1].InApp);
        Assert.True(frames[^1].Line > 0); // .pdb portátil ao lado
        Assert.EndsWith("UnitTests.cs", frames[^1].File);
    }

    private static void NivelUm() => NivelDois();

    private static void NivelDois() => throw new InvalidOperationException("dentro");

    [Fact]
    public void InApp_Heuristic()
    {
        var none = new List<string>();
        Assert.False(StackFrames.IsInApp("System.Linq.Enumerable.First", none));
        Assert.False(StackFrames.IsInApp("Microsoft.AspNetCore.Routing.EndpointMiddleware.Invoke", none));
        Assert.True(StackFrames.IsInApp("Acme.Loja.Pedido.Fechar", none));
        Assert.True(StackFrames.IsInApp("Microsoft.Acme.Loja.Pedido", new List<string> { "Microsoft.Acme." }));
        // frame do próprio monitor (pelo assembly) nunca é do sistema, nem com prefixo
        var own = StackFrames.Convert(new List<RawFrame> { new RawFrame { Function = "Bfocus.Monitor.BfocusMonitor.CaptureException", Own = true } }, new List<string> { "Bfocus." });
        Assert.False(own[0].InApp);
    }

    [Fact]
    public async Task AsyncFrames_UseTheWrittenMethodName()
    {
        Exception? ex = null;
        try
        {
            await FecharAsync();
        }
        catch (Exception e)
        {
            ex = e;
        }
        var frames = StackFrames.FromException(ex!, new List<string>());
        Assert.Contains(frames, f => f.Function != null && f.Function.EndsWith("UnitTests.FecharAsync"));
    }

    private static async Task FecharAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("async");
    }

    [Fact]
    public void Ignore_Sample_BeforeSend()
    {
        using var server = new FakeServer();
        BfocusMonitor.Init(Options(server, o =>
        {
            o.IgnorePatterns.Add(new Regex("^ruído \\d+$"));
            o.BeforeSend = e =>
            {
                if (e.Exception.Message == "descartar") return null;
                if (e.Exception.Message == "explode") throw new Exception("bug no beforeSend");
                e.Tags["alterado"] = "sim";
                return e;
            };
        }));
        BfocusMonitor.CaptureMessage("ruído 42");
        BfocusMonitor.CaptureMessage("descartar");
        BfocusMonitor.CaptureMessage("explode");
        BfocusMonitor.CaptureMessage("fica");
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(5)));
        var events = AllEvents(server);
        Assert.Equal(new[] { "explode", "fica" }, events.Select(e => (string)e["exception"]!["message"]!).ToArray());
        Assert.Null(events[0]["tags"]); // beforeSend que lança: vai como estava
        Assert.Equal("sim", (string)events[1]["tags"]!["alterado"]!);

        BfocusMonitor.Init(Options(server, o => o.SampleRate = 0));
        var before = server.Requests.Count;
        BfocusMonitor.CaptureMessage("amostra zero");
        BfocusMonitor.Flush(TimeSpan.FromSeconds(1));
        Assert.Equal(before, server.Requests.Count);
    }

    [Fact]
    public void Signature_IsRenewed_After6Days()
    {
        using var server = new FakeServer();
        long now = 1760000000;
        BfocusMonitor.Init(Options(server, o =>
        {
            o.SigningSecret = "whs_secret_A";
            o.SigningClock = () => now;
        }));
        BfocusMonitor.SetUser("u-123", "cliente-9");
        BfocusMonitor.CaptureMessage("primeiro");
        now += 3600; // 1 h: mantém
        BfocusMonitor.CaptureMessage("segundo");
        now = 1760000000 + Signing.MaxAgeSeconds + 1; // passou de 6 dias: assina de novo
        BfocusMonitor.CaptureMessage("terceiro");
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(5)));
        var hashes = AllEvents(server).Select(e => (string)e["user"]!["userHash"]!).ToList();
        Assert.Equal(Signing.UserHash("whs_secret_A", 1760000000, "u-123", "cliente-9"), hashes[0]);
        Assert.Equal(hashes[0], hashes[1]);
        Assert.Equal(Signing.UserHash("whs_secret_A", now, "u-123", "cliente-9"), hashes[2]);
    }

    [Fact]
    public async Task Scope_IsolatesIdentity_BetweenConcurrentRequests()
    {
        using var server = new FakeServer();
        BfocusMonitor.Init(Options(server));
        BfocusMonitor.SetTag("global", "1");
        var gate = new Barrier(2);

        Task Request(string user) => Task.Run(() =>
        {
            using (BfocusMonitor.BeginScope("GET /pedidos", "https://loja.test/pedidos?cpf=123"))
            {
                BfocusMonitor.SetUser(user, "cliente-" + user);
                BfocusMonitor.SetTag("quem", user);
                gate.SignalAndWait();
                BfocusMonitor.CaptureMessage("erro de " + user);
            }
        });

        await Task.WhenAll(Request("ana"), Request("bia"));
        BfocusMonitor.CaptureMessage("fora de requisição");
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(5)));
        var events = AllEvents(server);
        Assert.Equal(3, events.Count);
        foreach (var who in new[] { "ana", "bia" })
        {
            var ev = events.Single(e => (string)e["exception"]!["message"]! == "erro de " + who);
            Assert.Equal(who, (string)ev["user"]!["externalId"]!);
            Assert.Equal("cliente-" + who, (string)ev["customer"]!["externalId"]!);
            Assert.Equal(who, (string)ev["tags"]!["quem"]!);
            Assert.Equal("1", (string)ev["tags"]!["global"]!);
            Assert.Equal("https://loja.test/pedidos", (string)ev["url"]!);
            Assert.Equal("GET /pedidos", (string)ev["transaction"]!);
        }
        var outside = events.Single(e => (string)e["exception"]!["message"]! == "fora de requisição");
        Assert.Null(outside["user"]);
        Assert.Null(outside["url"]);
    }

    [Fact]
    public void Breadcrumbs_KeepTheLast30()
    {
        using var server = new FakeServer();
        BfocusMonitor.Init(Options(server));
        for (var i = 0; i < 40; i++) BfocusMonitor.AddBreadcrumb("passo", "p" + i);
        BfocusMonitor.CaptureMessage("com passos");
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(5)));
        var crumbs = (JsonArray)AllEvents(server).Single()["breadcrumbs"]!;
        Assert.Equal(30, crumbs.Count);
        Assert.Equal("p39", (string)crumbs[29]!["message"]!);
        Assert.Equal("info", (string)crumbs[0]!["level"]!);
    }

    [Fact]
    public void Event_IsCutTo64KB()
    {
        var ev = new MonitorEvent
        {
            Timestamp = "2026-10-06T12:00:00.000Z",
            Exception = new MonitorException
            {
                Type = "X",
                Message = new string('m', 2000),
                Frames = Enumerable.Range(0, 60).Select(i => new MonitorFrame { File = new string('f', 2000), Function = "F" + i, Line = i + 1, InApp = true }).ToList(),
            },
        };
        var json = MonitorClient.Fit(ev);
        Assert.NotNull(json);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json!) <= MonitorClient.MaxEventBytes);
        var node = JsonNode.Parse(json!)!;
        var frames = (JsonArray)node["exception"]!["frames"]!;
        Assert.Equal("F59", (string)frames[frames.Count - 1]!["function"]!); // o frame onde estourou fica
    }

    [Fact]
    public void Json_EscapesAndOmitsNulls()
    {
        var json = EventJson.Serialize(new MonitorEvent
        {
            Timestamp = "t",
            Exception = new MonitorException { Type = "T", Message = "aspas \" barra \\ linha\n ctrl \u0001 ção" },
        });
        var node = JsonNode.Parse(json)!;
        Assert.Equal("aspas \" barra \\ linha\n ctrl \u0001 ção", (string)node["exception"]!["message"]!);
        Assert.Null(node["release"]);
        Assert.DoesNotContain("null", json);
    }

    [Fact]
    public void UnhandledHooks_AreInstalled_OnlyWithAutoCapture()
    {
        using var server = new FakeServer();
        BfocusMonitor.Init(Options(server, o => o.AutoCapture = true));
        var handler = typeof(BfocusMonitor).GetMethod("OnUnhandledException", BindingFlags.NonPublic | BindingFlags.Static)!;
        handler.Invoke(null, new object?[] { null, new UnhandledExceptionEventArgs(ConformanceTests.Thrown("Fatal1", "morreu"), true) });
        // o gancho já fez flush (até 2 s)
        var ev = AllEvents(server).Single();
        Assert.Equal("fatal", (string)ev["level"]!);
        Assert.Equal("Fatal1", (string)ev["exception"]!["type"]!);

        var unobserved = typeof(BfocusMonitor).GetMethod("OnUnobservedTaskException", BindingFlags.NonPublic | BindingFlags.Static)!;
        unobserved.Invoke(null, new object?[] { null, new UnobservedTaskExceptionEventArgs(new AggregateException(ConformanceTests.Thrown("TarefaPerdida", "sem await"))) });
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(5)));
        var second = AllEvents(server)[1];
        Assert.Equal("error", (string)second["level"]!);
        Assert.Equal("TarefaPerdida", (string)second["exception"]!["type"]!);

        // AutoCapture=false: o gancho (já instalado no processo) não captura para este monitor.
        BfocusMonitor.Init(Options(server, o => o.AutoCapture = false));
        handler.Invoke(null, new object?[] { null, new UnhandledExceptionEventArgs(ConformanceTests.Thrown("Fatal2", "x"), true) });
        BfocusMonitor.Flush(TimeSpan.FromSeconds(1));
        Assert.Equal(2, AllEvents(server).Count);
    }
}
