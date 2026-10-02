using System.Diagnostics;

namespace ColdTurkeyClone.Services;

/// <summary>
/// Encerra processos por nome exato (ex: "steam") OU por um trecho do caminho
/// completo do executável (ex: "tor browser"). O segundo modo existe porque
/// alguns navegadores usados para burlar bloqueios têm nome de processo que
/// não ajuda em nada — o Tor Browser, por exemplo, roda internamente como
/// "firefox.exe" (é uma versão customizada do Firefox), então só dá pra
/// identificá-lo checando a pasta de instalação no caminho do executável.
///
/// Limite honesto: isso derruba o processo depois que ele tenta abrir — não
/// impede a instalação do programa em si. Para bloquear a instalação de
/// programas específicos de forma mais definitiva, o Windows tem o AppLocker
/// (edições Pro/Enterprise), mencionado no README como opção avançada.
/// </summary>
public static class ProcessBlocker
{
    public static void KillBlockedProcesses(IEnumerable<string> processNames, IEnumerable<string> pathKeywords)
    {
        var names = BuildNameSet(processNames);
        var keywords = BuildKeywordList(pathKeywords);
        if (names.Count == 0 && keywords.Count == 0) return;

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (IsBlocked(proc, names, keywords))
                    proc.Kill(entireProcessTree: true);
            }
            catch { /* processo do sistema protegido, ou já encerrado — ignora e segue */ }
        }
    }

    /// <summary>Checa e mata um único processo (usado pelo watcher de eventos em tempo real).</summary>
    public static void KillIfBlockedById(int processId, IEnumerable<string> processNames, IEnumerable<string> pathKeywords)
    {
        var names = BuildNameSet(processNames);
        var keywords = BuildKeywordList(pathKeywords);
        if (names.Count == 0 && keywords.Count == 0) return;

        try
        {
            using var proc = Process.GetProcessById(processId);
            if (IsBlocked(proc, names, keywords))
                proc.Kill(entireProcessTree: true);
        }
        catch { /* processo pode já ter fechado sozinho entre o evento e essa checagem */ }
    }

    private static HashSet<string> BuildNameSet(IEnumerable<string> processNames) =>
        new(processNames.Select(n => n.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

    private static List<string> BuildKeywordList(IEnumerable<string> pathKeywords) =>
        pathKeywords.Select(k => k.Trim().ToLowerInvariant()).Where(k => k.Length > 0).ToList();

    private static bool IsBlocked(Process proc, HashSet<string> names, List<string> keywords)
    {
        if (names.Contains(proc.ProcessName)) return true;

        if (keywords.Count > 0)
        {
            string? path = null;
            try { path = proc.MainModule?.FileName; } catch { }

            if (!string.IsNullOrEmpty(path))
            {
                var lowerPath = path.ToLowerInvariant();
                if (keywords.Any(k => lowerPath.Contains(k))) return true;
            }
        }

        return false;
    }
}
