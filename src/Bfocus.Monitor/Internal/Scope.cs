using System;
using System.Collections.Generic;
using System.Threading;

namespace Bfocus.Monitor.Internal;

/// <summary>Identidade de quem foi afetado (assinada ou dada pelo servidor do cliente).</summary>
internal sealed class Identity
{
    public string? UserExternalId { get; set; }
    public string? CustomerExternalId { get; set; }
    /// <summary>Dado pelo cliente: vai como veio.</summary>
    public string? GivenHash { get; set; }
    /// <summary>Calculado aqui com o signingSecret (recalculado depois de 6 dias).</summary>
    public string? SignedHash { get; set; }
    public long SignedAt { get; set; }
}

/// <summary>
/// Contexto de UMA requisição (AsyncLocal): a identidade, os marcadores e os passos dela não vazam para
/// outra requisição simultânea. Fora de requisição vale o contexto global do processo.
/// </summary>
internal sealed class Scope : IDisposable
{
    private static readonly AsyncLocal<Scope?> CurrentSlot = new AsyncLocal<Scope?>();
    private readonly Scope? _previous;
    private bool _disposed;

    public readonly object Gate = new object();
    public Identity? Identity;
    public readonly Dictionary<string, string> Tags = new Dictionary<string, string>();
    public readonly List<MonitorBreadcrumb> Breadcrumbs = new List<MonitorBreadcrumb>();
    public string? Transaction;
    public string? Url;

    private Scope(Scope? previous) => _previous = previous;

    public static Scope? Current => CurrentSlot.Value;

    public static Scope Begin(string? transaction, string? url)
    {
        var scope = new Scope(CurrentSlot.Value) { Transaction = transaction, Url = StripQuery(url) };
        CurrentSlot.Value = scope;
        return scope;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(CurrentSlot.Value, this)) CurrentSlot.Value = _previous;
    }

    /// <summary>URL sem query string nem fragmento (é onde mora token, e-mail, CPF).</summary>
    public static string? StripQuery(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var cut = url!.IndexOfAny(new[] { '?', '#' });
        return cut >= 0 ? url.Substring(0, cut) : url;
    }
}
