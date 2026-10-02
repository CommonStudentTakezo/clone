using ColdTurkeyClone.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ColdTurkeyClone.Watchdog;

/// <summary>
/// Serviço em segundo plano com duas camadas independentes:
///  1) PERMANENTE (roda sempre, mesmo sem bloqueio ativo, enquanto
///     PermanentProtectionsEnabled=true): SafeSearch, DoH desligado, extensão
///     instalada, pasta protegida.
///  2) TEMPORIZADA (só enquanto IsLocked=true): sites/apps/palavras-chave da
///     lista, modo anônimo desativado.
/// Isso resolve o "SafeSearch não fica sempre ativo" — antes ele só era
/// aplicado durante o bloqueio e removido no final.
/// </summary>
public class EnforcementWorker : BackgroundService
{
    private readonly ILogger<EnforcementWorker> _logger;
    private HostsWatcher? _hostsWatcher;
    private LocalStateServer? _stateServer;
    private ProcessStartWatcher? _processWatcher;
    private bool _lockRestrictionsApplied;
    private int _cycleCount;
    private string InstallRootPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    private string ExtensionFolderPath => Path.Combine(InstallRootPath, "BrowserExtension");

    public EnforcementWorker(ILogger<EnforcementWorker> logger)
    {
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Logger.Log("Watchdog iniciado (StartAsync).");

        // Mata qualquer OUTRA instância de Watchdog.exe rodando (exceto esta),
        // caso alguma tenha sobrevivido de uma reinstalação anterior. Isso
        // complementa a limpeza que o App já faz — importante porque este
        // Watchdog pode ter sido reiniciado sozinho pela recuperação automática
        // do Windows, sem o App estar aberto pra fazer essa limpeza.
        var myPid = Environment.ProcessId;
        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("Watchdog"))
        {
            if (proc.Id != myPid)
            {
                try
                {
                    Logger.Log($"Encerrando outra instância de Watchdog.exe (PID {proc.Id}) encontrada ao iniciar.");
                    proc.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Logger.LogException("EnforcementWorker.StartAsync (limpeza de outras instâncias)", ex);
                }
            }
            proc.Dispose();
        }

        _hostsWatcher = new HostsWatcher(StateManager.Load);

        _stateServer = new LocalStateServer(StateManager.Load);
        _stateServer.Start();

        _processWatcher = new ProcessStartWatcher(StateManager.Load);
        _processWatcher.Start();

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _cycleCount++;
                var state = StateManager.Load();

                // --- Camada permanente ---
                if (state.PermanentProtectionsEnabled)
                {
                    HostsFileManager.ApplySafeSearchEntries(SafeSearchDomains.GetHostsEntries());
                    BrowserPolicyManager.ApplyPermanentChromiumPolicies();
                    if (Directory.Exists(ExtensionFolderPath))
                        ExtensionInstaller.Install(ExtensionFolderPath);
                    FolderProtection.Protect(InstallRootPath);
                }

                // --- Camada temporizada ---
                if (state.IsLocked)
                {
                    var domains = (state.BlockedWebsites ?? new List<string>())
                        .Concat(state.ImportedWebsites ?? new List<string>())
                        .ToList();

                    HostsFileManager.ApplyBlockedSites(domains);
                    ProcessBlocker.KillBlockedProcesses(state.BlockedProcesses, state.BlockedProcessPathKeywords);

                    if (!_lockRestrictionsApplied)
                    {
                        BrowserPolicyManager.ApplyRestrictions();
                        _lockRestrictionsApplied = true;
                    }

                    // Log por ciclo (não só heartbeat de 60s) enquanto travado —
                    // um bloqueio dura pouco tempo (minutos), então queremos
                    // visibilidade fina o bastante pra reconstruir a linha do
                    // tempo caso o bloqueio pare de funcionar antes da hora.
                    var stillThere = HostsFileManager.ContainsOurBlock(domains);
                    var remaining = state.LockedUntilUtc!.Value - DateTime.UtcNow;
                    Logger.Log($"[ciclo #{_cycleCount}] LOCKED restante={remaining:hh\\:mm\\:ss} domínios={domains.Count} " +
                               $"marcaAindaNoHosts={stillThere}");
                }
                else
                {
                    if (state.LockedUntilUtc.HasValue)
                    {
                        Logger.Log($"[ciclo #{_cycleCount}] Lock expirou (LockedUntilUtc={state.LockedUntilUtc:o}, agora={DateTime.UtcNow:o}) — limpando hosts.");
                        HostsFileManager.ClearBlockedSites();
                        state.LockedUntilUtc = null;
                        StateManager.Save(state);
                    }

                    if (_lockRestrictionsApplied)
                    {
                        BrowserPolicyManager.RemoveRestrictions();
                        _lockRestrictionsApplied = false;
                    }
                }

                // Só protege o hosts quando há algo de verdade sendo aplicado
                // (bloqueio ativo ou proteções permanentes). Antes disso rodava
                // sempre, incondicionalmente — e isso, combinado com uma falha
                // separada no ResetAcl, deixou o hosts num estado quebrado que
                // impedia QUALQUER acesso à internet, mesmo sem nada travado.
                // Proteger um arquivo que não tem nada pra proteger não traz
                // benefício real e só aumenta a superfície de risco.
                if (state.PermanentProtectionsEnabled || state.IsLocked)
                {
                    HostsFileManager.Protect();
                }
                else
                {
                    HostsFileManager.Unprotect();
                }

                // Heartbeat a cada ~60s (ciclo de 5s x 12), só pra confirmar no log
                // que o serviço está vivo e rodando mesmo sem bloqueio ativo.
                if (_cycleCount % 12 == 0)
                {
                    Logger.Log($"Watchdog vivo. Locked={state.IsLocked} PermanentProtections={state.PermanentProtectionsEnabled}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha em um ciclo de enforcement, tentando de novo no próximo.");
                Logger.LogException("Watchdog.EnforcementWorker.ExecuteAsync", ex);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _hostsWatcher?.Dispose();
        _stateServer?.Dispose();
        _processWatcher?.Dispose();
        return base.StopAsync(cancellationToken);
    }
}
