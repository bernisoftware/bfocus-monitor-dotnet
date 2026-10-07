using System.Collections.Generic;

namespace Bfocus.Monitor;

/// <summary>Nível do evento.</summary>
public enum MonitorLevel
{
    /// <summary>O processo vai morrer (exceção não tratada no topo).</summary>
    Fatal,
    /// <summary>Erro (padrão das exceções).</summary>
    Error,
    /// <summary>Aviso.</summary>
    Warning,
    /// <summary>Informação (padrão de <see cref="BfocusMonitor.CaptureMessage"/>).</summary>
    Info,
}

/// <summary>
/// O evento como vai para o bFocus (<c>POST /api/v1/monitor/events</c>). É o que
/// <see cref="MonitorOptions.BeforeSend"/> recebe: altere à vontade ou devolva <c>null</c> para descartar.
/// Campo <c>null</c> (ou coleção vazia) não vai no corpo.
/// </summary>
public sealed class MonitorEvent
{
    /// <summary>ISO 8601 UTC com <c>Z</c>.</summary>
    public string Timestamp { get; set; } = "";
    /// <summary>Nível.</summary>
    public MonitorLevel Level { get; set; } = MonitorLevel.Error;
    /// <summary>Versão do sistema.</summary>
    public string? Release { get; set; }
    /// <summary>Ambiente.</summary>
    public string? Environment { get; set; }
    /// <summary>O erro (a causa raiz, quando encadeado).</summary>
    public MonitorException Exception { get; set; } = new MonitorException();
    /// <summary>Ex.: <c>POST /pedidos</c>.</summary>
    public string? Transaction { get; set; }
    /// <summary>URL da requisição, sem query string.</summary>
    public string? Url { get; set; }
    /// <summary>Quem foi afetado.</summary>
    public MonitorUser? User { get; set; }
    /// <summary>Cliente (empresa) afetado.</summary>
    public MonitorCustomer? Customer { get; set; }
    /// <summary>Marcadores.</summary>
    public IDictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
    /// <summary>Passos antes do erro (os 30 últimos).</summary>
    public IList<MonitorBreadcrumb> Breadcrumbs { get; set; } = new List<MonitorBreadcrumb>();
    /// <summary>Agrupamento manual (opcional).</summary>
    public IList<string>? Fingerprint { get; set; }
    /// <summary>Contextos (runtime, sistema operacional).</summary>
    public IDictionary<string, IDictionary<string, string>> Contexts { get; set; } = new Dictionary<string, IDictionary<string, string>>();
    /// <summary>Este pacote.</summary>
    public MonitorSdk Sdk { get; set; } = new MonitorSdk();
}

/// <summary>O erro.</summary>
public sealed class MonitorException
{
    /// <summary>Nome completo da classe (ex.: <c>System.NullReferenceException</c>).</summary>
    public string Type { get; set; } = "";
    /// <summary>Mensagem.</summary>
    public string Message { get; set; } = "";
    /// <summary>Frames de FORA para DENTRO: o último é onde estourou.</summary>
    public IList<MonitorFrame> Frames { get; set; } = new List<MonitorFrame>();
}

/// <summary>Um frame da pilha.</summary>
public sealed class MonitorFrame
{
    /// <summary>Arquivo (relativo ao diretório atual quando der).</summary>
    public string? File { get; set; }
    /// <summary><c>Namespace.Classe.Metodo</c>.</summary>
    public string? Function { get; set; }
    /// <summary>Linha (só com o .pdb).</summary>
    public int? Line { get; set; }
    /// <summary>Coluna.</summary>
    public int? Col { get; set; }
    /// <summary>Código do sistema (<c>true</c>) ou de biblioteca (<c>false</c>).</summary>
    public bool InApp { get; set; }
}

/// <summary>Pessoa afetada.</summary>
public sealed class MonitorUser
{
    /// <summary>Id da pessoa no SEU sistema.</summary>
    public string ExternalId { get; set; } = "";
    /// <summary>Assinatura v2 (a mesma do widget).</summary>
    public string? UserHash { get; set; }
}

/// <summary>Cliente afetado.</summary>
public sealed class MonitorCustomer
{
    /// <summary>Id do cliente no SEU sistema.</summary>
    public string ExternalId { get; set; } = "";
}

/// <summary>Passo antes do erro.</summary>
public sealed class MonitorBreadcrumb
{
    /// <summary>ISO 8601 UTC.</summary>
    public string Timestamp { get; set; } = "";
    /// <summary>Categoria (http, navigation, sql…).</summary>
    public string Category { get; set; } = "";
    /// <summary>O que aconteceu.</summary>
    public string Message { get; set; } = "";
    /// <summary>Nível.</summary>
    public MonitorLevel Level { get; set; } = MonitorLevel.Info;
}

/// <summary>Identificação do pacote.</summary>
public sealed class MonitorSdk
{
    /// <summary><c>bfocus-monitor-dotnet</c>.</summary>
    public string Name { get; set; } = BfocusMonitor.SdkName;
    /// <summary>Versão do pacote.</summary>
    public string Version { get; set; } = BfocusMonitor.Version;
}
