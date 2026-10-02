using System.Diagnostics;
using System.Threading;

namespace ColdTurkeyClone.Services;

/// <summary>
/// Registra o Watchdog.exe como um Serviço do Windows (em vez de tarefa agendada),
/// o que traz uma vantagem importante: usando "sc failure", configuramos o Service
/// Control Manager para reiniciar o serviço automaticamente se o processo morrer
/// de forma inesperada (ex: alguém dá "Finalizar Tarefa" nele). Isso acontece em
/// segundos, sem depender do app principal estar aberto.
///
/// IMPORTANTE — limite honesto: um Administrador local sempre pode rodar
/// "sc stop ColdTurkeyCloneWatchdog" ou parar pelo services.msc com privilégio de
/// admin. Isso NÃO conta como "falha" para o Service Control Manager, então a
/// recuperação automática não dispara nesse caso — é uma parada "limpa". Por isso,
/// no app, a ação de parar/desinstalar só é exposta atrás da tela de senha
/// (ver PasswordManager), então mesmo sabendo o comando, é preciso saber a senha
/// definida por você mesmo. Ainda assim, tecnicamente, alguém com conhecimento de
/// Windows e acesso de admin consegue reverter isso rodando os comandos "sc" na mão
/// — não existe proteção de software 100% à prova disso.
/// </summary>
public static class ServiceInstaller
{
    private const string ServiceName = "ColdTurkeyCloneWatchdog";

    public static bool Install(string watchdogExePath)
    {
        RunSc($"create {ServiceName} binPath= \"{watchdogExePath}\" start= auto DisplayName= \"Cold Turkey Clone Watchdog\"");

        // Reinicia automaticamente após 5s, em até 3 falhas consecutivas, resetando o contador após 1 dia.
        RunSc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000");
        RunSc($"failureflag {ServiceName} 1");

        RunSc($"start {ServiceName}");

        return IsInstalled();
    }

    /// <summary>Só deve ser chamado depois que o app confirmar a senha do usuário.</summary>
    public static void Uninstall()
    {
        RunSc($"stop {ServiceName}");

        // "sc stop" só SINALIZA a parada — não garante que o processo já morreu.
        // Espera até ~5s por uma confirmação real antes de seguir em frente.
        for (var i = 0; i < 10 && IsRunning(); i++)
        {
            Thread.Sleep(500);
        }

        RunSc($"delete {ServiceName}");

        // Rede de segurança: mata QUALQUER Watchdog.exe ainda vivo, mesmo os que
        // já não pertencem mais a nenhum serviço registrado (órfãos de
        // reinstalações anteriores que "sc stop" sozinho não consegue mais
        // alcançar). É isso que evita múltiplos Watchdog.exe brigando pelo
        // mesmo arquivo hosts ao mesmo tempo.
        KillAllWatchdogProcesses();
    }

    /// <summary>
    /// Mata todo processo chamado "Watchdog", de qualquer origem/instalação.
    /// Chamado antes de (re)instalar o serviço, pra garantir que nunca existe
    /// mais de uma instância rodando ao mesmo tempo.
    /// </summary>
    public static void KillAllWatchdogProcesses()
    {
        foreach (var proc in Process.GetProcessesByName("Watchdog"))
        {
            try
            {
                Logger.Log($"Encerrando processo Watchdog.exe órfão/antigo (PID {proc.Id}).");
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Logger.LogException("ServiceInstaller.KillAllWatchdogProcesses", ex);
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    public static bool IsInstalled()
    {
        var psi = new ProcessStartInfo("sc", $"query {ServiceName}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        using var p = Process.Start(psi);
        var output = p?.StandardOutput.ReadToEnd() ?? "";
        p?.WaitForExit();
        return !output.Contains("1060"); // 1060 = serviço não existe
    }

    public static bool IsRunning()
    {
        var psi = new ProcessStartInfo("sc", $"query {ServiceName}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        using var p = Process.Start(psi);
        var output = p?.StandardOutput.ReadToEnd() ?? "";
        p?.WaitForExit();
        return output.Contains("RUNNING");
    }

    /// <summary>Lê o caminho do executável configurado no serviço registrado (ou null se não achar).</summary>
    public static string? GetConfiguredBinPath()
    {
        var psi = new ProcessStartInfo("sc", $"qc {ServiceName}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        using var p = Process.Start(psi);
        var output = p?.StandardOutput.ReadToEnd() ?? "";
        p?.WaitForExit();

        var line = output.Split('\n').FirstOrDefault(l => l.Contains("BINARY_PATH_NAME"));
        if (line == null) return null;

        var idx = line.IndexOf(':');
        return idx < 0 ? null : line[(idx + 1)..].Trim().Trim('"');
    }

    private static void RunSc(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("sc", arguments)
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
                Logger.Log($"sc {arguments} saiu com código {p.ExitCode}. stdout: {stdout} | stderr: {stderr}");
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("ServiceInstaller.RunSc", ex);
        }
    }
}
