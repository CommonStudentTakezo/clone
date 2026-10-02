using Microsoft.Win32;

namespace ColdTurkeyClone.Services;

/// <summary>
/// Políticas de navegador confirmadas na documentação oficial (Chrome
/// Enterprise, Mozilla) em setembro de 2026:
///  - Modo anônimo/InPrivate desativado.
///  - DNS-over-HTTPS DESLIGADO na raiz (mais confiável que bloquear por
///    Firewall, porque desliga o recurso em vez de tentar adivinhar IPs de
///    provedores de DoH, que mudam). Isso é o que resolve o pedido de
///    "Firewall que bloqueie navegadores DoH" — não precisa de Firewall.
///  - SafeSearch/Modo Restrito reforçado nativamente no Chrome/Edge, além do
///    hosts (ForceGoogleSafeSearch, ForceYouTubeRestrict, SafeSitesFilterBehavior).
/// </summary>
public static class BrowserPolicyManager
{
    private static readonly (string KeyPath, string ValueName, int DisabledValue)[] IncognitoTargets =
    {
        (@"SOFTWARE\Policies\Google\Chrome", "IncognitoModeAvailability", 1),
        (@"SOFTWARE\Policies\Microsoft\Edge", "InPrivateModeAvailability", 1),
        (@"SOFTWARE\Policies\BraveSoftware\Brave", "IncognitoModeAvailability", 1),
        (@"SOFTWARE\Policies\Mozilla\Firefox", "DisablePrivateBrowsing", 1),
    };

    // Chaves Chromium (Chrome/Edge/Brave compartilham o mesmo esquema de política).
    private static readonly string[] ChromiumPolicyKeys =
    {
        @"SOFTWARE\Policies\Google\Chrome",
        @"SOFTWARE\Policies\Microsoft\Edge",
        @"SOFTWARE\Policies\BraveSoftware\Brave",
    };

    public static void ApplyRestrictions()
    {
        foreach (var (keyPath, valueName, value) in IncognitoTargets)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(keyPath);
                key?.SetValue(valueName, value, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Logger.LogException($"BrowserPolicyManager.ApplyRestrictions ({keyPath})", ex);
            }
        }
    }

    public static void RemoveRestrictions()
    {
        foreach (var (keyPath, valueName, _) in IncognitoTargets)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
                key?.DeleteValue(valueName, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Logger.LogException($"BrowserPolicyManager.RemoveRestrictions ({keyPath})", ex);
            }
        }
    }

    /// <summary>Desliga DNS-over-HTTPS e reforça SafeSearch nativo no Chrome/Edge/Brave.</summary>
    public static void ApplyPermanentChromiumPolicies()
    {
        foreach (var keyPath in ChromiumPolicyKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(keyPath);
                key?.SetValue("DnsOverHttpsMode", "off", RegistryValueKind.String);
                key?.SetValue("BuiltInDnsClientEnabled", 0, RegistryValueKind.DWord);
                key?.SetValue("ForceGoogleSafeSearch", 1, RegistryValueKind.DWord);
                key?.SetValue("ForceYouTubeRestrict", 2, RegistryValueKind.DWord); // 2 = estrito
                key?.SetValue("SafeSitesFilterBehavior", 1, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Logger.LogException($"BrowserPolicyManager.ApplyPermanentChromiumPolicies ({keyPath})", ex);
            }
        }

        ApplyFirefoxDoh(enabled: false, locked: true);
    }

    public static void RemovePermanentChromiumPolicies()
    {
        var names = new[]
        {
            "DnsOverHttpsMode", "BuiltInDnsClientEnabled",
            "ForceGoogleSafeSearch", "ForceYouTubeRestrict", "SafeSitesFilterBehavior"
        };
        foreach (var keyPath in ChromiumPolicyKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
                foreach (var name in names)
                    key?.DeleteValue(name, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Logger.LogException($"BrowserPolicyManager.RemovePermanentChromiumPolicies ({keyPath})", ex);
            }
        }

        ApplyFirefoxDoh(enabled: true, locked: false); // volta ao padrão do Firefox (DoH ligado, destravado)
    }

    private static void ApplyFirefoxDoh(bool enabled, bool locked)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS");
            key?.SetValue("Enabled", enabled ? 1 : 0, RegistryValueKind.DWord);
            key?.SetValue("Locked", locked ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Logger.LogException("BrowserPolicyManager.ApplyFirefoxDoh", ex);
        }
    }
}
