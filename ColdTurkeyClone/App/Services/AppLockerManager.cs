using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ColdTurkeyClone.Services;

/// <summary>
/// Gera e aplica regras do AppLocker (recurso nativo do Windows 11 Pro/
/// Enterprise/Education — NÃO existe no Windows 11 Home) que bloqueiam a
/// EXECUÇÃO de executáveis por caminho, o que é mais forte que o
/// ProcessBlocker: o AppLocker impede o processo de sequer iniciar (o
/// Windows recusa com uma mensagem própria), em vez de deixar abrir e matar
/// logo em seguida.
///
/// Limite honesto:
///  - Só funciona em Windows 11 Pro/Enterprise/Education. No Home, o método
///    IsSupported() retorna false e o app avisa o usuário — não tem como
///    contornar isso, é uma limitação de edição do Windows, não do app.
///  - Regras por CAMINHO (path rule) — como as que geramos aqui — podem ser
///    contornadas movendo o executável bloqueado para outra pasta. Regras
///    por HASH ou EDITOR (publisher) são mais fortes, mas exigem saber o
///    hash/assinatura exata de cada instalador com antecedência, o que não
///    dá pra automatizar de forma genérica. Path rules cobrem o caso comum
///    (instalação no local padrão) e são o que essa automação gera.
///  - O serviço "Application Identity" (AppIDSvc) precisa estar rodando
///    pro AppLocker funcionar — este método garante que ele esteja com
///    inicialização automática e ligado.
/// </summary>
public static class AppLockerManager
{
    // Caminhos padrão de instalação dos navegadores/ferramentas mais comuns
    // usados pra burlar bloqueios. Ajuste/adicione conforme necessário.
    private static readonly string[] DefaultBlockedPaths =
    {
        @"%OSDRIVE%\Users\*\Desktop\Tor Browser\Browser\firefox.exe",
        @"%OSDRIVE%\Users\*\AppData\Local\DuckDuckGo\*\DuckDuckGo.exe",
        @"%PROGRAMFILES%\DuckDuckGo\*\DuckDuckGo.exe",
        @"%OSDRIVE%\Users\*\AppData\Local\Programs\Epic Privacy Browser\Epic.exe",
    };

    public static bool IsSupported()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var editionId = key?.GetValue("EditionID") as string ?? "";
            return editionId.Contains("Professional") || editionId.Contains("Enterprise") || editionId.Contains("Education");
        }
        catch (Exception ex)
        {
            Logger.LogException("AppLockerManager.IsSupported", ex);
            return false;
        }
    }

    /// <returns>true se aplicado com sucesso; false se falhou (ver log de diagnóstico).</returns>
    public static bool Apply(IEnumerable<string>? extraPaths = null)
    {
        if (!IsSupported()) return false;

        var paths = DefaultBlockedPaths.Concat(extraPaths ?? Enumerable.Empty<string>()).Distinct().ToList();

        // Gera o XML de política do AppLocker com uma regra de "Negar Execução"
        // por caminho para cada item, aplicada a "Todos".
        var rulesXml = string.Join("\n", paths.Select((path, i) => $"""
              <FilePathRule Id="{Guid.NewGuid()}" Name="ColdTurkeyClone bloqueio {i}" Description="" UserOrGroupSid="S-1-1-0" Action="Deny">
                <Conditions>
                  <FilePathCondition Path="{path}" />
                </Conditions>
              </FilePathRule>
            """));

        var policyXml = $"""
            <AppLockerPolicy Version="1">
              <RuleCollection Type="Exe" EnforcementMode="Enabled">
            {rulesXml}
              </RuleCollection>
            </AppLockerPolicy>
            """;

        var tempXmlFile = Path.Combine(Path.GetTempPath(), "ColdTurkeyClone-AppLocker.xml");
        File.WriteAllText(tempXmlFile, policyXml);

        var script = $"""
            Set-AppLockerPolicy -XmlPolicy '{tempXmlFile}' -Merge
            Set-Service -Name AppIDSvc -StartupType Automatic
            Start-Service -Name AppIDSvc
            """;

        return RunPowerShell(script, "Apply");
    }

    public static bool RemoveAll()
    {
        // Substitui a política por uma vazia (não dá pra "remover só as nossas"
        // com Set-AppLockerPolicy sem também gerenciar regras de terceiros, então
        // documentamos essa limitação: isso reseta TODA a política de AppLocker
        // da máquina, não só as regras deste app).
        var script = """
            $empty = [xml]'<AppLockerPolicy Version="1"><RuleCollection Type="Exe" EnforcementMode="NotConfigured"/></AppLockerPolicy>'
            Set-AppLockerPolicy -XmlPolicy $empty
            """;
        return RunPowerShell(script, "RemoveAll");
    }

    /// <summary>
    /// Roda um script PowerShell escrevendo-o num arquivo .ps1 temporário e
    /// invocando via "-File" — muito mais confiável do que passar o script
    /// inteiro como argumento de linha de comando via "-Command" (que exige
    /// escapar aspas/quebras de linha manualmente e é frágil pra scripts
    /// multi-linha). O script embrulha o conteúdo original num try/catch que
    /// sempre termina com "exit 0" (sucesso) ou "exit 1" (falha), porque o
    /// código de saída padrão do PowerShell nem sempre reflete corretamente
    /// se um cmdlet como Set-AppLockerPolicy realmente funcionou.
    /// </summary>
    private static bool RunPowerShell(string innerScript, string context)
    {
        var scriptFile = Path.Combine(Path.GetTempPath(), $"ColdTurkeyClone-{Guid.NewGuid():N}.ps1");

        // Precisa de "$$" (dois cifrões) porque o script do PowerShell tem
        // chaves LITERAIS ({ e } do try/catch) misturadas com um trecho
        // interpolado (innerScript). Com um cifrão só, qualquer "{" abriria
        // interpolação — não daria pra escrever chave literal nenhuma. Com
        // dois cifrões, é preciso "{{" pra abrir interpolação, e "{"/"}"
        // sozinhos viram conteúdo literal.
        var wrappedScript = $$"""
            $ErrorActionPreference = 'Stop'
            try {
            {{innerScript}}
              exit 0
            } catch {
              Write-Error $_.Exception.Message
              exit 1
            }
            """;

        try
        {
            File.WriteAllText(scriptFile, wrappedScript);

            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptFile}\"")
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

            var ok = p?.ExitCode == 0;
            if (!ok)
            {
                Logger.Log($"AppLockerManager.{context} falhou (código {p?.ExitCode}). stdout: {stdout} | stderr: {stderr}");
            }
            else
            {
                Logger.Log($"AppLockerManager.{context} concluído com sucesso.");
            }
            return ok;
        }
        catch (Exception ex)
        {
            Logger.LogException($"AppLockerManager.{context}", ex);
            return false;
        }
        finally
        {
            try { File.Delete(scriptFile); } catch { }
        }
    }
}
