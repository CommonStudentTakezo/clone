using System.Diagnostics;

namespace ColdTurkeyClone.Services;

/// <summary>
/// Nega a permissão de "Excluir" (não de leitura/execução) na pasta de
/// instalação para Usuários e Administradores, usando os mesmos SIDs
/// bem-conhecidos já usados no HostsFileManager. O Watchdog (SYSTEM) não é
/// afetado, então continua rodando o .exe normalmente.
///
/// Isso responde diretamente ao "basta apagar a pasta pra ele parar de
/// funcionar": apagar pelo Explorer (mesmo com privilégio de admin) passa a
/// dar "Acesso negado". Resolve também o efeito colateral de "pede pra
/// instalar o .NET": isso não tinha relação com apagar a pasta — era o app
/// rodando em modo "dependente de framework" sem o .NET Desktop Runtime
/// instalado à parte. Publicar como "self-contained" (ver App.csproj/README)
/// resolve isso independentemente da proteção de pasta.
///
/// Limite honesto, o de sempre: um Administrador que rode `icacls`/`takeown`
/// manualmente ainda consegue remover essa negação e apagar tudo depois.
/// </summary>
public static class FolderProtection
{
    private const string SidAdministrators = "*S-1-5-32-544";
    private const string SidUsers = "*S-1-5-32-545";

    public static void Protect(string folderPath)
    {
        RunIcacls($"\"{folderPath}\" /deny {SidAdministrators}:(DE,DC) /T");
        RunIcacls($"\"{folderPath}\" /deny {SidUsers}:(DE,DC) /T");
    }

    public static void Unprotect(string folderPath)
    {
        RunIcacls($"\"{folderPath}\" /remove:d {SidAdministrators} /T");
        RunIcacls($"\"{folderPath}\" /remove:d {SidUsers} /T");
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
                Logger.Log($"icacls (pasta) \"{arguments}\" saiu com código {p.ExitCode}. stdout: {stdout} | stderr: {stderr}");
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("FolderProtection.RunIcacls", ex);
        }
    }
}
