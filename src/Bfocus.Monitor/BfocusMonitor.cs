using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bfocus.Monitor.Internal;

namespace Bfocus.Monitor;

/// <summary>
/// Monitoramento de erros do bFocus. Uma linha liga tudo:
/// <code>BfocusMonitor.Init(new MonitorOptions { Key = "bf_mon_…", Release = "1.4.2", Environment = "production" });</code>
/// Nenhuma chamada de rede na thread do Init: o sinal de vida (no Init e a cada 5 min) e o envio em lote
/// saem em segundo plano; nada aqui derruba o app.
/// </summary>
public static class BfocusMonitor
{
    /// <summary>Versão deste pacote (a mesma do .csproj — um teste trava).</summary>
    public const string Version = "0.1.1";

    /// <summary>Nome no campo <c>sdk.name</c> e no header <c>X-bFocus-Client</c>.</summary>
    public const string SdkName = "bfocus-monitor-dotnet";

    private static readonly object Gate = new object();
    private static MonitorClient? _client;
    private static int _hooksInstalled;
    private static int _exitInstalled;

    internal static MonitorClient? Current => Volatile.Read(ref _client);

    /// <summary>
    /// Liga o monitor (ou religa com novas opções — e volta a enviar se a chave tinha sido recusada).
    /// Com <see cref="MonitorOptions.AutoCapture"/> instala os ganchos de AppDomain.UnhandledException
    /// (nível fatal, com flush de até 2 s) e TaskScheduler.UnobservedTaskException.
    /// </summary>
    /// <exception cref="ArgumentNullException">Sem opções.</exception>
    /// <exception cref="ArgumentException">Chave vazia.</exception>
    public static void Init(MonitorOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.Key)) throw new ArgumentException("A chave do agente (Key) é obrigatória.", nameof(options));
        if (double.IsNaN(options.SampleRate)) options.SampleRate = 1.0;
        options.SampleRate = Math.Max(0.0, Math.Min(1.0, options.SampleRate));

        var client = new MonitorClient(options);
        MonitorClient? previous;
        lock (Gate)
        {
            previous = _client;
            Volatile.Write(ref _client, client);
        }
        previous?.Close(TimeSpan.FromSeconds(2));
        client.StartHeartbeat(); // sinal de vida: em segundo plano, nunca nesta thread

        try
        {
            if (Interlocked.Exchange(ref _exitInstalled, 1) == 0)
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            if (options.AutoCapture && Interlocked.Exchange(ref _hooksInstalled, 1) == 0)
            {
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            }
        }
        catch
        {
            // ambiente sem AppDomain completo: segue só com a captura manual
        }
    }

    /// <summary>Manda a exceção (a causa raiz, quando encadeada).</summary>
    public static void CaptureException(Exception exception, MonitorLevel level = MonitorLevel.Error,
        IDictionary<string, string>? tags = null, IEnumerable<string>? fingerprint = null)
    {
        try
        {
            Current?.CaptureException(exception, level, tags, fingerprint);
        }
        catch
        {
            // nunca derruba o app
        }
    }

    /// <summary>Manda uma mensagem como evento (sem exceção).</summary>
    public static void CaptureMessage(string message, MonitorLevel level = MonitorLevel.Info)
    {
        try
        {
            Current?.CaptureMessage(message, level);
        }
        catch
        {
            // nunca derruba o app
        }
    }

    /// <summary>
    /// Quem foi afetado: o id da pessoa e do cliente no SEU sistema. Sem <paramref name="userHash"/> e com
    /// <see cref="MonitorOptions.SigningSecret"/>, o pacote assina sozinho (v2, a mesma do widget). Dentro de
    /// uma requisição (<c>app.UseBfocusMonitor()</c> ou <see cref="BeginScope"/>) vale só para ela.
    /// Passe <c>null</c> nos dois para limpar.
    /// </summary>
    public static void SetUser(string? userExternalId, string? customerExternalId, string? userHash = null)
    {
        try
        {
            Current?.SetUser(userExternalId, customerExternalId, userHash);
        }
        catch
        {
            // nunca derruba o app
        }
    }

    /// <summary>Marcador que vai em todos os eventos (ou só nos da requisição atual).</summary>
    public static void SetTag(string key, string value)
    {
        try
        {
            Current?.SetTag(key, value);
        }
        catch
        {
            // nunca derruba o app
        }
    }

    /// <summary>Passo antes do erro (os 30 últimos vão junto).</summary>
    public static void AddBreadcrumb(string category, string message, MonitorLevel level = MonitorLevel.Info)
    {
        try
        {
            Current?.AddBreadcrumb(category, message, level);
        }
        catch
        {
            // nunca derruba o app
        }
    }

    /// <summary>Envia o que está na fila e espera, até o teto (CLI, serverless). Devolve se esvaziou.</summary>
    public static bool Flush(TimeSpan timeout)
    {
        try
        {
            return Current?.Flush(timeout) ?? true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Envia o que falta (até 2 s) e desliga. Depois disso, só um novo Init volta a capturar.</summary>
    public static void Close()
    {
        MonitorClient? client;
        lock (Gate)
        {
            client = _client;
            Volatile.Write(ref _client, null);
        }
        try
        {
            client?.Close(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // nunca derruba o app
        }
    }

    /// <summary>
    /// Abre o contexto de UMA requisição/tarefa (identidade, marcadores e passos isolados, via AsyncLocal).
    /// A integração ASP.NET Core já faz isso; use em filas, jobs e frameworks sem integração pronta:
    /// <code>using (BfocusMonitor.BeginScope("job FecharCaixa")) { … }</code>
    /// </summary>
    /// <param name="transaction">Ex.: <c>POST /pedidos</c>.</param>
    /// <param name="url">URL da requisição — a query string é cortada.</param>
    public static IDisposable BeginScope(string? transaction = null, string? url = null) => Scope.Begin(transaction, url);

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var client = Current;
            if (client == null || !client.AutoCapture) return;
            if (e.ExceptionObject is Exception ex)
            {
                client.CaptureException(ex, e.IsTerminating ? MonitorLevel.Fatal : MonitorLevel.Error, null, null);
                client.Flush(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            // o processo segue o caminho normal dele
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            var client = Current;
            if (client == null || !client.AutoCapture || e.Exception == null) return;
            // AggregateException de uma só: manda a de dentro (o tipo que importa).
            Exception ex = e.Exception.InnerExceptions.Count == 1 ? e.Exception.InnerExceptions[0] : e.Exception;
            client.CaptureException(ex, MonitorLevel.Error, null, null);
        }
        catch
        {
            // nunca derruba o app
        }
    }

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        try
        {
            Current?.Flush(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // saindo de qualquer jeito
        }
    }
}
