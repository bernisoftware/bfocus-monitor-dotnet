using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bfocus.Monitor.Internal;
using Xunit;

// O monitor é estático (um por processo): nada de testes em paralelo.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Bfocus.Monitor.Tests;

/// <summary>Roda TODOS os casos de monitor/conformance/cases.json (cópia gerada ao lado deste arquivo).</summary>
public class ConformanceTests : IDisposable
{
    internal static readonly JsonObject Cases = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cases.json")))!;

    public static IEnumerable<object[]> SendCaseNames() =>
        ((JsonArray)Cases["send"]!).Select(c => new object[] { (string)c!["name"]! });

    public static IEnumerable<object[]> UserHashIndexes() =>
        Enumerable.Range(0, ((JsonArray)Cases["user_hash"]!).Count).Select(i => new object[] { i });

    public void Dispose() => BfocusMonitor.Close();

    [Theory]
    [MemberData(nameof(UserHashIndexes))]
    public void UserHash(int index)
    {
        var v = Cases["user_hash"]![index]!;
        var got = Signing.UserHash((string)v["secret"]!, (long)v["ts"]!, (string)v["user_external_id"]!, (string)v["customer_external_id"]!);
        Assert.Equal((string)v["expected"]!, got);
    }

    [Fact]
    public void Frames()
    {
        foreach (var c in (JsonArray)Cases["frames"]!)
        {
            // Rastro neutro → frames como o .NET daria: biblioteca vira namespace Microsoft.*, o resto, do app.
            var raw = ((JsonArray)c!["runtime_order"]!).Select(f => new RawFrame
            {
                File = (string?)f!["file"],
                Function = ((bool)f["library"]! ? "Microsoft.AspNetCore.Routing." : "Acme.Loja.") + (string)f["function"]!,
                Line = (int?)f["line"] ?? 0,
            }).ToList();
            var got = StackFrames.Convert(raw, new List<string>());
            var expected = (JsonArray)c["expected"]!;
            Assert.Equal(expected.Count, got.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                var e = expected[i]!;
                Assert.Equal((string?)e["file"], got[i].File);
                Assert.EndsWith("." + (string)e["function"]!, got[i].Function);
                Assert.Equal((int?)e["line"], got[i].Line);
                Assert.Equal((bool)e["inApp"]!, got[i].InApp);
            }
        }
    }

    public static IEnumerable<object[]> HeartbeatCaseNames() =>
        ((JsonArray)Cases["heartbeat"]!).Select(c => new object[] { (string)c!["name"]! });

    [Theory]
    [MemberData(nameof(HeartbeatCaseNames))]
    public void Heartbeat(string name)
    {
        var c = ((JsonArray)Cases["heartbeat"]!).Single(x => (string)x!["name"]! == name)!;
        using var server = new FakeServer();
        server.HeartbeatScript.Enqueue((int)c["respond"]!["status"]!);
        var init = c["init"]!;
        BfocusMonitor.Init(new MonitorOptions
        {
            Key = (string)init["key"]!,
            Release = (string?)init["release"],
            Environment = (string?)init["environment"] ?? "production",
            BaseUrl = server.BaseUrl,
            AutoCapture = false,
        });

        var got = Assert.Single(server.WaitHeartbeats(1));
        var expect = c["expect"]!;
        Assert.Equal((string)expect["method"]!, got.Method);
        Assert.Equal((string)expect["path"]!, got.Path);
        foreach (var (k, v) in (JsonObject)expect["headers"]!)
            Assert.Equal((string)v!, got.Header(k));
        foreach (var (k, v) in (JsonObject)expect["header_prefix"]!)
            Assert.StartsWith((string)v!, got.Header(k) ?? "");
        Assert.Equal("bfocus-monitor-dotnet/" + BfocusMonitor.Version, got.Header("X-bFocus-Client"));

        var body = JsonNode.Parse(got.Body)!;
        AssertNoNulls(body);
        foreach (var (path, want) in (JsonObject)expect["body"]!)
        {
            var expected = want is JsonValue jv && jv.TryGetValue<string>(out var s) && s == "$version"
                ? JsonValue.Create(BfocusMonitor.Version)
                : want;
            Assert.True(JsonNode.DeepEquals(expected, At(body, path)), $"{path}: esperado {expected?.ToJsonString()}, veio {At(body, path)?.ToJsonString()}");
        }
        foreach (var path in (JsonArray)expect["body_present"]!)
        {
            var v = At(body, (string)path!);
            Assert.True(v != null && v.ToJsonString() != "\"\"", $"heartbeat sem {path}: {got.Body}");
        }
        Assert.Equal("bfocus-monitor-dotnet", (string)body["sdk"]!["name"]!);
        Assert.Equal(".NET", (string)body["runtime"]!["name"]!);
        Assert.False(BfocusMonitor.Current!.Disabled);
        Assert.Empty(server.Requests); // heartbeat não é evento
    }

    [Theory]
    [MemberData(nameof(SendCaseNames))]
    public void Send(string name)
    {
        var c = ((JsonArray)Cases["send"]!).Single(x => (string)x!["name"]! == name)!;
        var expectedRequests = (JsonArray)c["requests"]!;
        var script = expectedRequests.Select(r => ((int)r!["respond"]!["status"]!, r["respond"]!["body"]!.ToJsonString()));
        using var server = new FakeServer(script);

        var init = c["init"]!;
        var options = new MonitorOptions
        {
            Key = (string)init["key"]!,
            Release = (string?)init["release"],
            Environment = (string?)init["environment"] ?? "production",
            BaseUrl = server.BaseUrl,
            SigningSecret = (string?)init["signing_secret"],
            AutoCapture = false,
            RetryDelay = TimeSpan.FromMilliseconds(50),
            BatchInterval = TimeSpan.FromMilliseconds(100),
        };
        if (init["ignore"] is JsonArray ignore)
            foreach (var s in ignore) options.Ignore.Add((string)s!);
        var setUser = c["set_user"];
        if (setUser?["ts"] is JsonNode ts)
        {
            var fixedTs = (long)ts;
            options.SigningClock = () => fixedTs;
        }
        BfocusMonitor.Init(options);

        if (setUser != null)
            BfocusMonitor.SetUser((string?)setUser["user_external_id"], (string?)setUser["customer_external_id"], (string?)setUser["user_hash"]);
        if (c["breadcrumbs"] is JsonArray crumbs)
            foreach (var b in crumbs)
                BfocusMonitor.AddBreadcrumb((string)b!["category"]!, (string)b["message"]!, Level((string?)b["level"]) ?? MonitorLevel.Info);

        var repeat = (int?)c["repeat"] ?? 1;
        for (var i = 0; i < repeat; i++) Capture(c["capture"]!);
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(10)), "flush não esvaziou a fila");

        var got = server.Requests;
        Assert.Equal(expectedRequests.Count, got.Count);
        for (var i = 0; i < expectedRequests.Count; i++)
            CheckRequest(expectedRequests[i]!["expect"]!, got[i]);
        if (expectedRequests.Count == 2)
            Assert.Equal(got[0].Body, got[1].Body); // nova tentativa = mesmo corpo

        if (c["then_capture"] is JsonNode then)
        {
            Capture(then);
            BfocusMonitor.Flush(TimeSpan.FromSeconds(5));
        }
        Thread.Sleep(250); // nada atrasado chegando
        Assert.Equal(expectedRequests.Count, server.Requests.Count);

        var after = (string)c["after"]!;
        Assert.Equal(after == "disabled", BfocusMonitor.Current!.Disabled);
    }

    private static void CheckRequest(JsonNode expect, Received got)
    {
        Assert.Equal((string)expect["method"]!, got.Method);
        Assert.Equal((string)expect["path"]!, got.Path);
        foreach (var (k, v) in (JsonObject)expect["headers"]!)
            Assert.Equal((string)v!, got.Header(k));
        foreach (var (k, v) in (JsonObject)expect["header_prefix"]!)
            Assert.StartsWith((string)v!, got.Header(k) ?? "");
        Assert.Equal("bfocus-monitor-dotnet/" + BfocusMonitor.Version, got.Header("X-bFocus-Client"));

        var events = got.Events;
        Assert.Single(events);
        var ev = events[0]!;
        AssertNoNulls(ev);
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$"), (string)ev["timestamp"]!);
        Assert.Equal("bfocus-monitor-dotnet", (string)ev["sdk"]!["name"]!);
        foreach (var (path, want) in (JsonObject)expect["event"]!)
        {
            var actual = At(ev, path);
            Assert.True(actual != null, $"evento sem o campo {path}: {ev.ToJsonString()}");
            var expected = want is JsonValue jv && jv.TryGetValue<string>(out var s) && s == "$version"
                ? JsonValue.Create(BfocusMonitor.Version)
                : want;
            Assert.True(JsonNode.DeepEquals(expected, actual), $"{path}: esperado {expected?.ToJsonString()}, veio {actual?.ToJsonString()}");
        }
    }

    internal static JsonNode? At(JsonNode node, string dotted)
    {
        JsonNode? cur = node;
        foreach (var part in dotted.Split('.'))
        {
            cur = cur switch
            {
                JsonArray arr when int.TryParse(part, out var i) => i < arr.Count ? arr[i] : null,
                JsonObject obj => obj[part],
                _ => null,
            };
            if (cur == null) return null;
        }
        return cur;
    }

    internal static void AssertNoNulls(JsonNode? node)
    {
        Assert.NotNull(node);
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, v) in o) AssertNoNulls(v);
                break;
            case JsonArray a:
                foreach (var v in a) AssertNoNulls(v);
                break;
        }
    }

    internal static MonitorLevel? Level(string? s) => s switch
    {
        "fatal" => MonitorLevel.Fatal,
        "error" => MonitorLevel.Error,
        "warning" => MonitorLevel.Warning,
        "info" => MonitorLevel.Info,
        _ => null,
    };

    private static void Capture(JsonNode cap)
    {
        var level = Level((string?)cap["level"]);
        if ((string)cap["kind"]! == "message")
        {
            BfocusMonitor.CaptureMessage((string)cap["message"]!, level ?? MonitorLevel.Info);
            return;
        }
        var ex = Thrown((string)cap["type"]!, (string)cap["message"]!);
        var tags = (cap["tags"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => (string)kv.Value!);
        var fingerprint = (cap["fingerprint"] as JsonArray)?.Select(x => (string)x!).ToList();
        BfocusMonitor.CaptureException(ex, level ?? MonitorLevel.Error, tags, fingerprint);
    }

    // ---- exceções com o nome que o caso pede (ValueError, KeyError, E…), lançadas de verdade ----

    private static readonly ModuleBuilder Module =
        AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ConformanceErrors"), AssemblyBuilderAccess.Run).DefineDynamicModule("m");
    private static readonly Dictionary<string, Type> Types = new();

    internal static Exception Thrown(string typeName, string message)
    {
        Type type;
        lock (Types)
        {
            if (!Types.TryGetValue(typeName, out type!))
            {
                var tb = Module.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class, typeof(Exception));
                var ctor = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(string) });
                var il = ctor.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, typeof(Exception).GetConstructor(new[] { typeof(string) })!);
                il.Emit(OpCodes.Ret);
                type = tb.CreateType()!;
                Types[typeName] = type;
            }
        }
        try
        {
            throw (Exception)Activator.CreateInstance(type, message)!;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    internal static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
