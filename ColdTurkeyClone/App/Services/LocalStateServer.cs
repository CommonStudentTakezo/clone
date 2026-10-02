using System.Net;
using System.Text;
using System.Text.Json;
using ColdTurkeyClone.Models;

namespace ColdTurkeyClone.Services;

/// <summary>
/// Expõe SOMENTE leitura, em http://127.0.0.1:57411/state, o estado atual de bloqueio,
/// para que a extensão de navegador (que não tem acesso ao sistema de arquivos)
/// possa saber quais palavras-chave e domínios bloquear, e se o lock está ativo.
///
/// Escuta apenas em loopback (127.0.0.1) — não é acessível pela rede/internet.
/// Não aceita escrita: a extensão nunca consegue alterar o estado, só consultar.
/// </summary>
public class LocalStateServer : IDisposable
{
    private const string Prefix = "http://127.0.0.1:57411/";
    private readonly HttpListener _listener = new();
    private readonly Func<BlockState> _loadState;
    private CancellationTokenSource? _cts;

    public LocalStateServer(Func<BlockState> loadState)
    {
        _loadState = loadState;
        _listener.Prefixes.Add(Prefix);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _ = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = HandleRequestAsync(ctx);
            }
            catch when (token.IsCancellationRequested)
            {
                break;
            }
            catch { /* segue escutando mesmo se uma requisição falhar */ }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");

        if (ctx.Request.Url?.AbsolutePath == "/state" && ctx.Request.HttpMethod == "GET")
        {
            var state = _loadState();
            var payload = new
            {
                locked = state.IsLocked,
                lockedUntilUtc = state.LockedUntilUtc,
                blockedKeywords = state.BlockedKeywords,
                blockedWebsites = state.BlockedWebsites,
                permanentProtectionsEnabled = state.PermanentProtectionsEnabled
            };
            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
        }
        else
        {
            ctx.Response.StatusCode = 404;
        }

        ctx.Response.Close();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener.Stop();
        _listener.Close();
    }
}
