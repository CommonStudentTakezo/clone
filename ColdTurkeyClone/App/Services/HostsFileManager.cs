using System.Diagnostics;
using System.IO;

namespace ColdTurkeyClone.Services;

/// <summary>
/// Bloqueia sites redirecionando o domínio para 127.0.0.1 no arquivo hosts,
/// e protege esse arquivo contra edição — inclusive por um Bloco de Notas
/// aberto "Como Administrador".
///
/// IMPORTANTE — sincronização entre processos: o App e o serviço Watchdog
/// são DOIS PROCESSOS SEPARADOS que podem tentar ler/alterar/escrever o
/// mesmo arquivo hosts quase ao mesmo tempo (o Watchdog reforça a cada 5s
/// independente do app estar aberto). Sem coordenação, um processo pode
/// escrever uma versão desatualizada por cima do que o outro acabou de
/// gravar — cada operação abaixo agora usa um Mutex nomeado do Windows
/// ("Global\...") pra garantir que só um processo mexa no hosts por vez,
/// eliminando essa condição de corrida.
///
/// Proteção de permissão: usamos os SIDs bem-conhecidos do Windows (não os
/// nomes localizados dos grupos) pra negar escrita tanto pra "Usuários"
/// quanto pra "Administradores". O Watchdog roda como NT AUTHORITY\SYSTEM —
/// conta separada, não pertence ao grupo Administradores — então continua
/// com acesso total mesmo com essas negações. Um Bloco de Notas aberto por
/// um Administrador (mesmo elevado) carrega o SID de Administradores no
/// token, então a negação vale pra ele too.
///
/// Limite honesto: negar "escrita" (W) não é o mesmo que negar "alterar
/// permissões" (WRITE_DAC). Um Administrador que rode `icacls` manualmente
/// ainda consegue reverter — é o mesmo caminho que o próprio app usa
/// internamente (Unprotect) pra conseguir escrever antes de reproteger.
/// </summary>
public static class HostsFileManager
{
    public static readonly string HostsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    private const string MarkerStart = "# === ColdTurkeyClone START ===";
    private const string MarkerEnd = "# === ColdTurkeyClone END ===";

    private const string SafeSearchMarkerStart = "# === ColdTurkeyClone SAFESEARCH START ===";
    private const string SafeSearchMarkerEnd = "# === ColdTurkeyClone SAFESEARCH END ===";

    private const string SidAdministrators = "*S-1-5-32-544";
    private const string SidUsers = "*S-1-5-32-545";

    // "Global\" torna o mutex visível entre sessões/serviços, não só dentro
    // do mesmo processo — é o que sincroniza App (sessão do usuário) e
    // Watchdog (SYSTEM, sessão 0) entre si.
    private static readonly Mutex HostsMutex = new(false, "Global\\ColdTurkeyClone_HostsFileMutex");

    private static void WithHostsLock(Action action)
    {
        var acquired = false;
        try
        {
            acquired = HostsMutex.WaitOne(TimeSpan.FromSeconds(15));
            if (!acquired)
            {
                Logger.Log("AVISO: não consegui adquirir o lock do hosts em 15s — outro processo pode estar preso. Prosseguindo mesmo assim.");
            }
            action();
        }
        catch (AbandonedMutexException)
        {
            // Um processo anterior morreu sem liberar o mutex — o .NET ainda
            // nos dá posse dele aqui. Prossegue normalmente.
            acquired = true;
            action();
        }
        finally
        {
            if (acquired)
            {
                try { HostsMutex.ReleaseMutex(); } catch { }
            }
        }
    }

    public static void ApplyBlockedSites(IEnumerable<string> domains)
    {
        var domainListSnapshot = domains.ToList(); // materializa antes de entrar no lock
        Exception? captured = null;

        WithHostsLock(() =>
        {
            try
            {
                var domainList = domainListSnapshot.Distinct().Where(d => !string.IsNullOrWhiteSpace(d)).ToList();

                var intendedBlock = BuildBlockLines(domainList);

                var currentLines = File.Exists(HostsPath) ? File.ReadAllLines(HostsPath).ToList() : new List<string>();
                var currentBlock = ExtractMarkedBlock(currentLines, MarkerStart, MarkerEnd);

                // Otimização importante pra listas grandes (dezenas de milhares
                // de domínios importados): se o bloco já está exatamente como
                // deveria estar, NÃO reescreve o arquivo (nem chama icacls, nem
                // ipconfig). Sem isso, o Watchdog reescrevia o hosts inteiro a
                // cada 5s, o tempo todo, mesmo sem nada ter mudado — com um
                // hosts de ~140 mil linhas (70 mil domínios x 2), isso pesava
                // bastante e deixava a navegação visivelmente mais lenta.
                if (currentBlock != null && currentBlock.SequenceEqual(intendedBlock))
                {
                    return;
                }

                UnprotectInternal();

                var lines = currentLines;
                RemoveOurBlock(lines);
                if (intendedBlock.Count > 0)
                {
                    lines.Add(MarkerStart);
                    lines.AddRange(intendedBlock);
                    lines.Add(MarkerEnd);
                }

                File.WriteAllLines(HostsPath, lines);
                FlushDnsCache();

                var afterContent = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : "";
                if (domainList.Count > 0 && !afterContent.Contains(MarkerStart))
                {
                    Logger.Log("AVISO: hosts foi escrito mas o marcador de bloqueio NÃO aparece na releitura logo em seguida. " +
                               "Sinal de que algo (antivírus, outro processo) reverteu a alteração. " +
                               $"Caminho: {HostsPath}");
                }
                else
                {
                    Logger.Log($"ApplyBlockedSites OK: {domainList.Count} domínio(s) escritos ({intendedBlock.Count} linhas).");
                }
            }
            catch (Exception ex)
            {
                Logger.LogException("HostsFileManager.ApplyBlockedSites", ex);
                captured = ex;
            }
        });

        if (captured != null) throw captured;
    }

    private static List<string> BuildBlockLines(List<string> domainList)
    {
        var result = new List<string>(domainList.Count * 2);
        foreach (var domain in domainList)
        {
            var d = domain.Trim().Replace("https://", "").Replace("http://", "").Trim('/');
            result.Add($"127.0.0.1 {d}");
            result.Add($"127.0.0.1 www.{d}");
        }
        return result;
    }

    /// <summary>Retorna as linhas ENTRE os marcadores (sem incluir os próprios marcadores), ou null se não existirem.</summary>
    private static List<string>? ExtractMarkedBlock(List<string> lines, string markerStart, string markerEnd)
    {
        var startIdx = lines.IndexOf(markerStart);
        var endIdx = lines.IndexOf(markerEnd);
        if (startIdx < 0 || endIdx <= startIdx) return null;
        return lines.GetRange(startIdx + 1, endIdx - startIdx - 1);
    }

    public static void ClearBlockedSites()
    {
        WithHostsLock(() =>
        {
            try
            {
                UnprotectInternal();
                if (!File.Exists(HostsPath)) return;
                var lines = File.ReadAllLines(HostsPath).ToList();
                RemoveOurBlock(lines);
                File.WriteAllLines(HostsPath, lines);
                FlushDnsCache();
            }
            catch (Exception ex)
            {
                Logger.LogException("HostsFileManager.ClearBlockedSites", ex);
            }
        });
    }

    /// <summary>Nega escrita p/ Usuários e Administradores. Seguro de chamar a qualquer momento.</summary>
    public static void Protect()
    {
        WithHostsLock(() =>
        {
            try
            {
                File.SetAttributes(HostsPath, FileAttributes.ReadOnly | FileAttributes.System);
                RunIcacls($"\"{HostsPath}\" /deny {SidAdministrators}:(W)");
                RunIcacls($"\"{HostsPath}\" /deny {SidUsers}:(W)");
            }
            catch (Exception ex)
            {
                Logger.LogException("HostsFileManager.Protect", ex);
            }
        });
    }

    /// <summary>
    /// Reseta o ACL do hosts por completo (herda de novo do pai, descartando
    /// QUALQUER entrada explícita acumulada — inclusive lixo deixado por
    /// processos concorrentes/antigos de rodadas de teste anteriores). Use uma
    /// vez na inicialização pra garantir um estado limpo antes de reaplicar
    /// nossa proteção normal.
    /// </summary>
    /// <summary>
    /// Restaura o ACL do hosts pra um estado conhecido-bom, explicitamente
    /// (NUNCA usa "icacls /reset"). "/reset" faz o arquivo herdar permissões
    /// da pasta pai (drivers\etc), que NÃO garante direitos suficientes pro
    /// próprio SYSTEM — isso já causou uma falha real e séria: depois de um
    /// /reset, nem o Watchdog (rodando como SYSTEM) conseguia mais mexer no
    /// hosts, deixando o arquivo num estado quebrado que impedia o Windows
    /// de resolver QUALQUER site, bloqueio ativo ou não. Em vez de depender
    /// de herança, concede explicitamente os mesmos direitos que o Windows
    /// dá por padrão pro hosts (SYSTEM e Administradores com controle total,
    /// Usuários com leitura), e remove qualquer negação antiga que possa ter
    /// sobrado de uma execução anterior.
    /// </summary>
    public static void ResetAcl()
    {
        WithHostsLock(() =>
        {
            try
            {
                if (File.Exists(HostsPath))
                {
                    var attrs = File.GetAttributes(HostsPath);
                    attrs &= ~FileAttributes.ReadOnly;
                    attrs &= ~FileAttributes.System;
                    File.SetAttributes(HostsPath, attrs);
                }

                RunIcacls($"\"{HostsPath}\" /grant *S-1-5-18:F");       // SYSTEM: controle total
                RunIcacls($"\"{HostsPath}\" /grant {SidAdministrators}:F"); // Administradores: controle total
                RunIcacls($"\"{HostsPath}\" /grant {SidUsers}:RX");     // Usuários: leitura e execução
                RunIcacls($"\"{HostsPath}\" /remove:d {SidAdministrators}"); // remove negação antiga, se houver
                RunIcacls($"\"{HostsPath}\" /remove:d {SidUsers}");

                Logger.Log("ACL do hosts restaurado explicitamente pro padrão conhecido-bom (sem usar /reset).");
            }
            catch (Exception ex)
            {
                Logger.LogException("HostsFileManager.ResetAcl", ex);
            }
        });
    }

    public static void Unprotect() => WithHostsLock(UnprotectInternal);

    /// <summary>Versão "crua", sem adquirir o lock de novo — usada pelos métodos que já estão dentro de WithHostsLock.</summary>
    private static void UnprotectInternal()
    {
        try
        {
            RunIcacls($"\"{HostsPath}\" /remove:d {SidAdministrators}");
            RunIcacls($"\"{HostsPath}\" /remove:d {SidUsers}");
            if (File.Exists(HostsPath))
            {
                var attrs = File.GetAttributes(HostsPath);
                attrs &= ~FileAttributes.ReadOnly;
                attrs &= ~FileAttributes.System;
                File.SetAttributes(HostsPath, attrs);
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("HostsFileManager.Unprotect", ex);
        }
    }

    public static void ApplySafeSearchEntries(IEnumerable<(string Domain, string Ip)> entries)
    {
        var entryListSnapshot = entries.ToList();

        WithHostsLock(() =>
        {
            try
            {
                var intendedBlock = entryListSnapshot.Select(e => $"{e.Ip} {e.Domain}").ToList();

                var currentLines = File.Exists(HostsPath) ? File.ReadAllLines(HostsPath).ToList() : new List<string>();
                var currentBlock = ExtractMarkedBlock(currentLines, SafeSearchMarkerStart, SafeSearchMarkerEnd);

                if (currentBlock != null && currentBlock.SequenceEqual(intendedBlock))
                {
                    return;
                }

                UnprotectInternal();

                var lines = currentLines;
                RemoveMarkedBlock(lines, SafeSearchMarkerStart, SafeSearchMarkerEnd);

                if (intendedBlock.Count > 0)
                {
                    lines.Add(SafeSearchMarkerStart);
                    lines.AddRange(intendedBlock);
                    lines.Add(SafeSearchMarkerEnd);
                }

                File.WriteAllLines(HostsPath, lines);
                FlushDnsCache();
            }
            catch (Exception ex)
            {
                Logger.LogException("HostsFileManager.ApplySafeSearchEntries", ex);
            }
        });
    }

    public static void ClearSafeSearchEntries()
    {
        WithHostsLock(() =>
        {
            try
            {
                UnprotectInternal();
                if (!File.Exists(HostsPath)) return;
                var lines = File.ReadAllLines(HostsPath).ToList();
                RemoveMarkedBlock(lines, SafeSearchMarkerStart, SafeSearchMarkerEnd);
                File.WriteAllLines(HostsPath, lines);
                FlushDnsCache();
            }
            catch (Exception ex)
            {
                Logger.LogException("HostsFileManager.ClearSafeSearchEntries", ex);
            }
        });
    }

    /// <summary>
    /// Escreve uma linha de teste isolada, confere se ela aparece numa
    /// releitura, e remove em seguida. Não mexe no bloqueio real.
    /// </summary>
    public static (bool Success, string Message) SelfTest()
    {
        const string testMarker = "# ColdTurkeyClone-SELFTEST";
        const string testDomain = "coldturkeyclone-selftest.invalid";
        var result = (Success: false, Message: "");

        WithHostsLock(() =>
        {
            try
            {
                UnprotectInternal();

                var lines = File.Exists(HostsPath) ? File.ReadAllLines(HostsPath).ToList() : new List<string>();
                lines.RemoveAll(l => l.Contains(testMarker) || l.Contains(testDomain));
                lines.Add($"127.0.0.1 {testDomain} {testMarker}");
                File.WriteAllLines(HostsPath, lines);

                var reread = File.ReadAllText(HostsPath);
                var wroteOk = reread.Contains(testDomain);

                var cleanLines = File.ReadAllLines(HostsPath).ToList();
                cleanLines.RemoveAll(l => l.Contains(testMarker) || l.Contains(testDomain));
                File.WriteAllLines(HostsPath, cleanLines);

                result = wroteOk
                    ? (true, $"OK: a linha de teste apareceu normalmente em {HostsPath} logo após escrever.")
                    : (false, $"FALHOU: escrevi em {HostsPath} sem erro, mas a linha de teste NÃO apareceu na " +
                              "releitura imediata. Sinal forte de algo revertendo a alteração por fora do app " +
                              "(antivírus, outro software de proteção do hosts, ou política de grupo).");
            }
            catch (Exception ex)
            {
                Logger.LogException("HostsFileManager.SelfTest", ex);
                result = (false, $"FALHOU com exceção: {ex.GetType().Name}: {ex.Message}");
            }
        });

        return result;
    }

    public static bool ContainsOurBlock(IEnumerable<string> expectedDomains)
    {
        if (!File.Exists(HostsPath)) return false;
        var content = File.ReadAllText(HostsPath);
        return expectedDomains.All(d => content.Contains(d.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static void RemoveOurBlock(List<string> lines) => RemoveMarkedBlock(lines, MarkerStart, MarkerEnd);

    private static void RemoveMarkedBlock(List<string> lines, string markerStart, string markerEnd)
    {
        var startIdx = lines.IndexOf(markerStart);
        var endIdx = lines.IndexOf(markerEnd);
        if (startIdx >= 0 && endIdx >= startIdx)
            lines.RemoveRange(startIdx, endIdx - startIdx + 1);
    }

    private static void FlushDnsCache()
    {
        try
        {
            var psi = new ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false };
            Process.Start(psi)?.WaitForExit();
        }
        catch { }
    }

    private static void RunIcacls(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("icacls", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            var stdout = p?.StandardOutput.ReadToEnd();
            var stderr = p?.StandardError.ReadToEnd();
            p?.WaitForExit();
            if (p != null && p.ExitCode != 0)
            {
                Logger.Log($"icacls \"{arguments}\" saiu com código {p.ExitCode}. stdout: {stdout} | stderr: {stderr}");
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("HostsFileManager.RunIcacls", ex);
        }
    }
}
