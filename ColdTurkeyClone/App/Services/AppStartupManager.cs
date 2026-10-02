using System.Diagnostics;

namespace ColdTurkeyClone.Services;

/// <summary>
/// Registra o ColdTurkeyClone.exe (a janela principal, não o Watchdog) pra
/// abrir sozinho no login do Windows, via Agendador de Tarefas com
/// "/RL HIGHEST" (privilégio máximo). Isso é importante: um atalho comum na
/// pasta de Inicializar, ou uma chave de Registro "Run", dispararia um
/// prompt do UAC TODA VEZ que o Windows liga, porque o app exige admin. Uma
/// tarefa agendada configurada com privilégio máximo já nasce autorizada,
/// então abre direto, sem prompt.
///
/// Importante: isso é só conveniência de interface (abrir o painel sozinho).
/// A proteção de verdade (hosts, bloqueio, SafeSearch) já roda mesmo sem
/// isso, porque é o Watchdog — um Serviço do Windows — quem faz esse
/// trabalho, e serviços já iniciam sozinhos no boot independente do login de
/// qualquer usuário (ver ServiceInstaller.cs).
/// </summary>
public static class AppStartupManager
{
    private const string TaskName = "ColdTurkeyCloneApp";

    public static void RegisterAutoStart(string appExePath)
    {
        var args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{appExePath}\\\"\" /SC ONLOGON /RL HIGHEST /F";
        RunSchtasks(args);
    }

    public static void UnregisterAutoStart()
    {
        RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
    }

    public static bool IsRegistered()
    {
        var psi = new ProcessStartInfo("schtasks", $"/Query /TN \"{TaskName}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi);
        p?.WaitForExit();
        return p?.ExitCode == 0;
    }

    private static void RunSchtasks(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks", arguments)
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
                Logger.Log($"schtasks {arguments} saiu com código {p.ExitCode}. stdout: {stdout} | stderr: {stderr}");
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("AppStartupManager.RunSchtasks", ex);
        }
    }
}
