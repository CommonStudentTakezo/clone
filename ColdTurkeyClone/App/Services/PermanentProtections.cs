namespace ColdTurkeyClone.Services;

/// <summary>
/// Proteções que ficam ATIVAS O TEMPO TODO, independente de haver um
/// bloqueio temporizado rodando — diferente das listas de sites/apps/
/// palavras-chave, que só valem durante o "Iniciar bloqueio". Faz sentido
/// separar assim porque SafeSearch, DoH desligado, extensão instalada e
/// pasta protegida não atrapalham o uso normal do computador no dia a dia
/// (diferente de travar sites/programas específicos, que faria sentido
/// deixar sempre ligado só se você quisesse bloqueio permanente).
///
/// Habilitar não pede senha (é só aumentar proteção). Desabilitar pede senha
/// — ver UninstallFlow/MainWindow.
/// </summary>
public static class PermanentProtections
{
    public static void Enable(string installRootPath, string extensionFolderPath)
    {
        HostsFileManager.Unprotect();
        HostsFileManager.ApplySafeSearchEntries(SafeSearchDomains.GetHostsEntries());
        BrowserPolicyManager.ApplyPermanentChromiumPolicies();
        ExtensionInstaller.Install(extensionFolderPath);
        HostsFileManager.Protect();
        FolderProtection.Protect(installRootPath);
    }

    public static void Disable(string installRootPath)
    {
        FolderProtection.Unprotect(installRootPath);
        HostsFileManager.Unprotect();
        HostsFileManager.ClearSafeSearchEntries();
        HostsFileManager.Protect();
        BrowserPolicyManager.RemovePermanentChromiumPolicies();
        ExtensionInstaller.Uninstall();
    }
}
