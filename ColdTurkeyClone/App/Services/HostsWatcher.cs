using System.IO;
using ColdTurkeyClone.Models;

namespace ColdTurkeyClone.Services;

/// <summary>
/// Fica de olho no arquivo hosts em tempo real (em vez de esperar o próximo ciclo
/// de 5 segundos do Watchdog). Se detectar qualquer alteração enquanto o lock
/// estiver ativo E o bloco esperado não estiver mais lá, reaplica na hora.
///
/// Importante: isso reage a QUALQUER escrita no arquivo, inclusive as nossas
/// próprias (do ciclo normal do EnforcementWorker). O guard "ContainsOurBlock"
/// evita reentrância nesse caso (só reage quando o bloco realmente sumiu).
/// </summary>
public class HostsWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Func<BlockState> _loadState;

    public HostsWatcher(Func<BlockState> loadState)
    {
        _loadState = loadState;

        var dir = Path.GetDirectoryName(HostsFileManager.HostsPath)!;
        var file = Path.GetFileName(HostsFileManager.HostsPath);

        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Attributes | NotifyFilters.Size
        };
        _watcher.Changed += OnHostsChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnHostsChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            var state = _loadState();
            if (!state.IsLocked)
            {
                return;
            }

            var domains = (state.BlockedWebsites ?? new List<string>())
                .Concat(state.ImportedWebsites ?? new List<string>())
                .ToList();

            var present = HostsFileManager.ContainsOurBlock(domains);
            Logger.Log($"[HostsWatcher] mudança detectada no hosts. Locked=true, bloco presente={present}.");

            if (!present)
            {
                Logger.Log("[HostsWatcher] bloco ausente — reaplicando imediatamente.");
                HostsFileManager.ApplyBlockedSites(domains);
                HostsFileManager.Protect();
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("HostsWatcher.OnHostsChanged", ex);
        }
    }

    public void Dispose() => _watcher.Dispose();
}
