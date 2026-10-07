using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Bfocus.Monitor.Tests;

public class AspNetCoreTests : IDisposable
{
    public void Dispose() => BfocusMonitor.Close();

    private static DefaultHttpContext Ctx(string method, string path, string query = "")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("loja.test");
        ctx.Request.Path = path;
        ctx.Request.QueryString = new QueryString(query);
        return ctx;
    }

    [Fact]
    public async Task Middleware_CapturesAndRethrows_WithTransactionAndUrlWithoutQuery()
    {
        using var server = new FakeServer();
        BfocusMonitor.Init(UnitTests.Options(server));
        var mw = new BfocusMonitorMiddleware(_ =>
        {
            BfocusMonitor.SetUser("u-1", "c-1", "v2.1.abc");
            throw new InvalidOperationException("estoque negativo");
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => mw.InvokeAsync(Ctx("POST", "/pedidos/42", "?cpf=12345678900&token=x")));
        Assert.Equal("estoque negativo", ex.Message);

        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(5)));
        var ev = UnitTests.AllEvents(server).Single();
        Assert.Equal("POST /pedidos/42", (string)ev["transaction"]!);
        Assert.Equal("https://loja.test/pedidos/42", (string)ev["url"]!);
        Assert.Equal("System.InvalidOperationException", (string)ev["exception"]!["type"]!);
        Assert.Equal("u-1", (string)ev["user"]!["externalId"]!);
        Assert.Equal("v2.1.abc", (string)ev["user"]!["userHash"]!);
        Assert.DoesNotContain("cpf", ev.ToJsonString());

        // a identidade era só daquela requisição
        BfocusMonitor.CaptureMessage("depois");
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(5)));
        Assert.Null(UnitTests.AllEvents(server)[1]["user"]);
    }

    [Fact]
    public async Task Middleware_SuccessfulRequest_SendsNothing()
    {
        using var server = new FakeServer();
        BfocusMonitor.Init(UnitTests.Options(server));
        var mw = new BfocusMonitorMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        await mw.InvokeAsync(Ctx("GET", "/ok"));
        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(2)));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Middleware_ConcurrentRequests_KeepTheirOwnIdentity()
    {
        using var server = new FakeServer();
        BfocusMonitor.Init(UnitTests.Options(server));
        var both = new TaskCompletionSource();
        var arrived = 0;
        var mw = new BfocusMonitorMiddleware(async ctx =>
        {
            var who = ctx.Request.Path.Value!.Trim('/');
            BfocusMonitor.SetUser(who, "cliente-" + who);
            if (Interlocked.Increment(ref arrived) == 2) both.SetResult();
            await both.Task;
            await Task.Yield();
            throw new InvalidOperationException("erro de " + who);
        });

        var a = Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => mw.InvokeAsync(Ctx("GET", "/ana"))));
        var b = Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => mw.InvokeAsync(Ctx("GET", "/bia"))));
        await Task.WhenAll(a, b);

        Assert.True(BfocusMonitor.Flush(TimeSpan.FromSeconds(5)));
        var events = UnitTests.AllEvents(server);
        Assert.Equal(2, events.Count);
        foreach (var ev in events)
        {
            var who = ((string)ev["exception"]!["message"]!).Replace("erro de ", "");
            Assert.Equal(who, (string)ev["user"]!["externalId"]!);
            Assert.Equal("GET /" + who, (string)ev["transaction"]!);
        }
    }
}
