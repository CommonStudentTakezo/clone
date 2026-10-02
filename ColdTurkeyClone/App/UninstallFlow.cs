using System.Windows;
using ColdTurkeyClone.Services;
using MessageBox = System.Windows.MessageBox;

namespace ColdTurkeyClone;

/// <summary>
/// Lógica única de "desinstalação protegida", usada em dois lugares:
///  1) Pelo botão "Desinstalar" dentro do próprio app.
///  2) Pelo desinstalador gerado pelo Inno Setup, que chama
///     "ColdTurkeyClone.exe --uninstall" ANTES de apagar qualquer arquivo
///     (ver Installer.iss, InitializeUninstall). Se essa chamada retornar
///     código de saída diferente de 0, o Setup aborta a desinstalação
///     inteira — isso fecha a brecha que existiria se o desinstalador
///     padrão do Windows pudesse remover tudo sem pedir senha.
/// </summary>
public static class UninstallFlow
{
    /// <returns>true se autorizado e a limpeza (hosts/serviço/políticas) foi concluída.</returns>
    public static bool TryPerformProtectedUninstall(Window? owner)
    {
        var state = StateManager.Load();

        if (state.IsLocked)
        {
            MessageBox.Show(
                "Não é possível desinstalar enquanto o bloqueio estiver ativo. Espere o tempo acabar.",
                "Bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (state.HasPassword)
        {
            var dialog = new PasswordDialog("Digite a senha para confirmar a desinstalação:");
            if (owner != null) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            if (dialog.ShowDialog() != true) return false;

            if (!PasswordManager.Verify(dialog.EnteredPassword, state.PasswordHash))
            {
                MessageBox.Show("Senha incorreta.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
        else
        {
            var confirm = MessageBox.Show(
                "Nenhuma senha foi definida. Deseja mesmo desinstalar?",
                "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return false;
        }

        // Senha certa não é o fim da história: uma sequência longa de
        // confirmações variadas vem depois, pra aumentar o atrito contra o
        // impulso do momento mesmo de quem sabe a senha (pedido explícito:
        // "é fácil demais desinstalar e só colocar a senha").
        if (!ConfirmationGauntlet.Run(owner, "Desinstalar"))
        {
            return false;
        }

        ServiceInstaller.Uninstall();
        AppStartupManager.UnregisterAutoStart();
        HostsFileManager.Unprotect();
        HostsFileManager.ClearBlockedSites();
        HostsFileManager.ClearSafeSearchEntries();
        BrowserPolicyManager.RemoveRestrictions();
        BrowserPolicyManager.RemovePermanentChromiumPolicies();
        ExtensionInstaller.Uninstall();

        var installRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, ".."));
        FolderProtection.Unprotect(installRoot);

        return true;
    }
}
