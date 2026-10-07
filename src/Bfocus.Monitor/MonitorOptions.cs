using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Bfocus.Monitor;

/// <summary>Opções de <see cref="BfocusMonitor.Init"/>.</summary>
public sealed class MonitorOptions
{
    /// <summary>Chave de envio do agente (<c>bf_mon_…</c>), em Monitoramento → Agentes. Obrigatória.</summary>
    public string Key { get; set; } = "";

    /// <summary>Versão do SEU sistema (ex.: <c>1.4.2</c>) — ligada às notas de versão do produto.</summary>
    public string? Release { get; set; }

    /// <summary>Ambiente (<c>production</c>, <c>staging</c>…). Padrão <c>production</c>.</summary>
    public string Environment { get; set; } = "production";

    /// <summary>Endereço da API do bFocus, sem barra final. Padrão <c>https://api.bfocus.com.br</c>.</summary>
    public string BaseUrl { get; set; } = "https://api.bfocus.com.br";

    /// <summary>Fração dos erros enviada, de 0 a 1. Padrão 1 (todos).</summary>
    public double SampleRate { get; set; } = 1.0;

    /// <summary>Mensagens a ignorar: o erro cuja mensagem CONTÉM um destes textos não é enviado.</summary>
    public IList<string> Ignore { get; set; } = new List<string>();

    /// <summary>Mensagens a ignorar por expressão regular.</summary>
    public IList<Regex> IgnorePatterns { get; set; } = new List<Regex>();

    /// <summary>Última chance de alterar o evento ou descartá-lo (devolva <c>null</c>).</summary>
    public Func<MonitorEvent, MonitorEvent?>? BeforeSend { get; set; }

    /// <summary>
    /// SÓ NO SERVIDOR: segredo da chave de assinatura do sistema (o mesmo do <c>userHash</c> do widget).
    /// Com ele, <see cref="BfocusMonitor.SetUser"/> assina a identidade sozinho. Nunca em app desktop/móvel.
    /// </summary>
    public string? SigningSecret { get; set; }

    /// <summary>Instalar os ganchos globais (AppDomain e tarefas não observadas). Padrão <c>true</c>.</summary>
    public bool AutoCapture { get; set; } = true;

    /// <summary>
    /// Prefixos de namespace que são do SEU sistema (ex.: <c>Acme.</c>) quando a heurística não basta —
    /// por padrão tudo que não é <c>System.</c>/<c>Microsoft.</c> conta como do sistema.
    /// </summary>
    public IList<string> InAppPrefixes { get; set; } = new List<string>();

    // Ajustes internos (testes).
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan BatchInterval { get; set; } = TimeSpan.FromSeconds(1);
    internal Func<long>? SigningClock { get; set; }
    internal TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(5);
}
