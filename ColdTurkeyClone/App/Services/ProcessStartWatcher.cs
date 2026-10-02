using System.Management;
using ColdTurkeyClone.Models;

namespace ColdTurkeyClone.Services;

/// <summary>
/// Escuta eventos de "novo processo iniciado" no Windows via WMI
/// (Win32_ProcessStartTrace) e mata na hora, se estiver na lista de bloqueio.
/// Isso reduz bastante a janela de tempo entre "o app abriu" e "o Watchdog
/// percebe" — antes dependíamos só de checagens a cada poucos segundos. Exige
/// privilégio elevado/SYSTEM para funcionar, o que o Watchdog já tem.
/// </summary>
public class ProcessStartWatcher : IDisposable
{
    private ManagementEventWatcher? _watcher;
    private readonly Func<BlockState> _loadState;

    public ProcessStartWatcher(Func<BlockState> loadState)
    {
        _loadState = loadState;
    }

    public void Start()
    {
        try
        {
            var query = new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
            _watcher = new ManagementEventWatcher(query);
            _watcher.EventArrived += OnProcessStarted;
            _watcher.Start();
        }
        catch (Exception ex)
        {
            // Se o WMI não estiver disponível por algum motivo, o loop de 5s
            // do EnforcementWorker continua funcionando como rede de segurança.
            Logger.LogException("ProcessStartWatcher.Start", ex);
        }
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var state = _loadState();
            if (!state.IsLocked) return;

            var pid = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);
            ProcessBlocker.KillIfBlockedById(pid, state.BlockedProcesses, state.BlockedProcessPathKeywords);
        }
        catch (Exception ex)
        {
            Logger.LogException("ProcessStartWatcher.OnProcessStarted", ex);
        }
    }

    public void Dispose()
    {
        _watcher?.Stop();
        _watcher?.Dispose();
    }
}
