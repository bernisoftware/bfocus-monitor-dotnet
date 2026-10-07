using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Bfocus.Monitor;

/// <summary>
/// Middleware do ASP.NET Core: abre o contexto da requisição (transaction <c>"METHOD /path"</c>, URL sem
/// query, identidade e marcadores só desta requisição), captura a exceção que escapar e RELANÇA — o
/// tratamento de erro do app continua exatamente como era.
/// </summary>
public sealed class BfocusMonitorMiddleware
{
    /// <summary>Chave em <c>HttpContext.Items</c> com o contexto da requisição.</summary>
    public const string ItemKey = "Bfocus.Monitor.Scope";

    private readonly RequestDelegate _next;

    /// <summary>Criado pelo pipeline do ASP.NET Core.</summary>
    public BfocusMonitorMiddleware(RequestDelegate next) => _next = next ?? throw new ArgumentNullException(nameof(next));

    /// <summary>Executa a requisição dentro do contexto do monitor.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        IDisposable? scope = null;
        try
        {
            var req = context.Request;
            var path = (req.PathBase + req.Path).ToString();
            if (string.IsNullOrEmpty(path)) path = "/";
            string? url = null;
            if (req.Host.HasValue) url = req.Scheme + "://" + req.Host.Value + path;
            scope = BfocusMonitor.BeginScope(req.Method + " " + path, url);
            context.Items[ItemKey] = scope;
        }
        catch
        {
            // sem contexto: a requisição segue igual
        }

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BfocusMonitor.CaptureException(ex);
            throw;
        }
        finally
        {
            try
            {
                scope?.Dispose();
            }
            catch
            {
                // nada
            }
        }
    }
}

/// <summary><c>app.UseBfocusMonitor()</c>.</summary>
public static class BfocusMonitorApplicationBuilderExtensions
{
    /// <summary>
    /// Captura os erros das requisições. Ponha logo no começo do pipeline (depois de
    /// <c>UseExceptionHandler</c>/<c>UseDeveloperExceptionPage</c>, se houver), antes de rotas e controllers.
    /// </summary>
    public static IApplicationBuilder UseBfocusMonitor(this IApplicationBuilder app)
    {
        if (app == null) throw new ArgumentNullException(nameof(app));
        return app.UseMiddleware<BfocusMonitorMiddleware>();
    }
}
