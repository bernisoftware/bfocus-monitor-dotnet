# bFocus Monitor — .NET

Captura os erros não tratados do seu sistema .NET e manda para o **bFocus**, onde o mesmo erro é agrupado
entre todos os clientes e vira demanda para a equipe (módulo Monitoramento). Sem dependências; alvos
`netstandard2.0` (.NET Framework 4.6.2+, .NET 6/7…) e `net8.0`.

## Instalar

```bash
dotnet add package Bfocus.Monitor
dotnet add package Bfocus.Monitor.AspNetCore   # só em app ASP.NET Core
```

## Ligar (uma linha)

```csharp
using Bfocus.Monitor;

BfocusMonitor.Init(new MonitorOptions { Key = "bf_mon_…", Release = "1.4.2", Environment = "production" });
```

A chave está em **Monitoramento → Agentes** no bFocus. O `Init` não faz chamada de rede na sua thread (o primeiro sinal de vida sai em segundo plano): instala os ganchos
de `AppDomain.CurrentDomain.UnhandledException` (nível `fatal`, com até 2 s para enviar antes de o processo
cair) e `TaskScheduler.UnobservedTaskException`, e o envio de tudo que estiver na fila quando o processo sai.
O app quebra exatamente como quebraria sem o monitor.

## ASP.NET Core

```csharp
var app = builder.Build();
app.UseExceptionHandler("/erro");   // se houver, antes
app.UseBfocusMonitor();             // logo no começo do pipeline
app.MapControllers();
```

O middleware captura a exceção que escapar da requisição e **relança** (o seu tratamento de erro continua
igual). O evento leva `transaction` (`"POST /pedidos/42"`) e a URL **sem a query string**. Erro que um
`ExceptionHandler`/filtro de MVC já tratou não chega até ele — capture à mão onde tratar.

## Quem foi afetado (identidade assinada)

O bFocus só liga o erro a cliente e pessoa com assinatura válida — a mesma do `userHash` do widget. No
servidor, dê o segredo da chave de assinatura do sistema e o pacote assina sozinho:

```csharp
BfocusMonitor.Init(new MonitorOptions
{
    Key = "bf_mon_…",
    Release = "1.4.2",
    SigningSecret = builder.Configuration["Bfocus:SigningSecret"],   // só no servidor
});

// no login / no começo da requisição:
BfocusMonitor.SetUser(usuario.Id, usuario.ClienteId);
```

Dentro de uma requisição (`UseBfocusMonitor`), `SetUser`, `SetTag` e `AddBreadcrumb` valem **só para ela**
(`AsyncLocal`): dois usuários simultâneos nunca trocam de identidade. Em app desktop, quem tem o segredo é o
seu servidor — passe o hash pronto: `BfocusMonitor.SetUser(userId, clienteId, userHashDoServidor)`.

Fora do ASP.NET Core (filas, jobs), abra o contexto à mão:

```csharp
using (BfocusMonitor.BeginScope("job FecharCaixa"))
{
    BfocusMonitor.SetUser(job.UsuarioId, job.ClienteId);
    …
}
```

## Captura manual

```csharp
try { … }
catch (Exception ex)
{
    BfocusMonitor.CaptureException(ex, MonitorLevel.Error,
        tags: new Dictionary<string, string> { ["modulo"] = "fiscal" },
        fingerprint: new[] { "nota-fiscal", "timeout" });
}

BfocusMonitor.CaptureMessage("estoque negativo", MonitorLevel.Warning);
BfocusMonitor.SetTag("filial", "POA");
BfocusMonitor.AddBreadcrumb("http", "GET /api/estoque 500", MonitorLevel.Error);

BfocusMonitor.Flush(TimeSpan.FromSeconds(2));   // CLI/serverless: espera o envio (até o teto)
BfocusMonitor.Close();                          // envia o que falta e desliga
```

Exceção encadeada (`InnerException`): vai o tipo e a mensagem da mais **interna** (o grupo é da causa
raiz), com `" (dentro de: <TipoExterno>: <mensagem externa>)"` no fim — só o tipo externo quando a
mensagem externa já contém a interna.

## Opções

| Opção | Padrão | |
| --- | --- | --- |
| `Key` | — | Obrigatória (`bf_mon_…`). Vazia → `ArgumentException`. |
| `Release` | — | Versão do seu sistema; liga o erro às notas de versão. |
| `Environment` | `production` | |
| `BaseUrl` | `https://api.bfocus.com.br` | |
| `SampleRate` | `1.0` | Fração enviada (0 a 1). |
| `Ignore` / `IgnorePatterns` | vazio | Mensagens a ignorar (texto contido / `Regex`). |
| `BeforeSend` | — | Recebe o `MonitorEvent`; devolva-o alterado ou `null` para descartar. |
| `SigningSecret` | — | Só servidor: assina a identidade no `SetUser` (renova depois de 6 dias). |
| `AutoCapture` | `true` | Instalar os ganchos globais. |
| `InAppPrefixes` | vazio | Namespaces que são do seu sistema quando a heurística (tudo que não é `System.`/`Microsoft.`) não basta. |

## Como envia

- Fila em memória (até 100 eventos; cheia → descarta o mais novo) e uma thread de fundo que manda em lote a
  cada 1 s ou 20 eventos. Nada aqui lança exceção para o seu código.
- 429, 5xx e erro de rede: uma nova tentativa depois de 2 s. 401/403 (chave errada, agente desligado):
  para de enviar até o próximo `Init`.
- O mesmo erro sai no máximo 1 vez a cada 30 s, e no máximo 100 eventos por minuto.
- Sinal de vida: logo depois do `Init` (em segundo plano) e a cada 5 min, um `POST /api/v1/monitor/heartbeat`
  com a instância (hash curto de máquina + pid), a máquina, release e ambiente — é como o painel mostra o
  agente **vivo** mesmo sem erro nenhum. O timer não segura o processo; `Flush`/`Close` não mandam sinal.
- Não manda corpo de requisição, cookies, headers nem query string. Linhas e arquivos nos frames precisam
  do `.pdb` ao lado do `.dll` (o padrão `portable` já basta).

## Licença

MIT — Berni Software.
