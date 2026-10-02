using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using ColdTurkeyClone.Models;
using ColdTurkeyClone.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace ColdTurkeyClone;

public partial class MainWindow : Window
{
    private BlockState _state = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _restrictionsAppliedByApp;
    private bool _reallyClosing;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    private static readonly string[] KnownBypassBrowserPaths =
    {
        "tor browser", "duckduckgo", "epic privacy browser", "ungoogled chromium"
    };

    // Cobre o caso de comprar/assinar (ex: Kindle Unlimited) pra acessar
    // conteúdo adulto em lojas que não são, no geral, sites adultos — a
    // Amazon inteira não dá pra bloquear (é loja normal), mas as páginas de
    // categoria/gênero desse tipo de conteúdo têm texto identificável.
    private static readonly string[] AdultContentKeywords =
    {
        "erótica", "erotica", "romance erótico", "livros eróticos",
        "conteúdo adulto", "adult content", "explicit content", "18+",
        "kindle unlimited", "hentai", "nsfw"
    };

    private string InstallRootPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    private string ExtensionFolderPath => Path.Combine(InstallRootPath, "BrowserExtension");

    private string WatchdogExePath =>
        (!string.IsNullOrEmpty(_state.WatchdogExePathOverride) && File.Exists(_state.WatchdogExePathOverride))
            ? _state.WatchdogExePathOverride!
            : Path.Combine(InstallRootPath, "Watchdog", "Watchdog.exe");

    public MainWindow()
    {
        InitializeComponent();
        _state = StateManager.Load();
        SitesBox.Text = string.Join(Environment.NewLine, _state.BlockedWebsites);
        KeywordsBox.Text = string.Join(Environment.NewLine, _state.BlockedKeywords);
        AppsBox.Text = string.Join(Environment.NewLine, _state.BlockedProcesses);
        PathKeywordsBox.Text = string.Join(Environment.NewLine, _state.BlockedProcessPathKeywords);
        UpdateImportedListLabel();
        PermanentProtectionsButton.Content = _state.PermanentProtectionsEnabled
            ? "Desativar proteções permanentes"
            : "Ativar proteções permanentes";

        SetupTrayIcon();

        _timer.Tick += Timer_Tick;
        _timer.Start();
        UpdateUiForLockState();

        EnsureServiceInstalled();
        RefreshStatus();
    }

    // ---------- Serviço / status ----------

    private void EnsureServiceInstalled()
    {
        if (!File.Exists(WatchdogExePath))
        {
            Logger.Log($"Watchdog.exe não encontrado em: {WatchdogExePath}");

            var choice = MessageBox.Show(
                "Não encontrei o Watchdog.exe no caminho esperado:\n\n" + WatchdogExePath + "\n\n" +
                "Isso é normal se você está rodando direto de uma pasta de build/publish (os caminhos do .NET " +
                "variam bastante: Debug/Release, self-contained, etc). Depois de instalado via Setup.exe isso se " +
                "resolve sozinho — mas se você quiser testar agora sem instalar, posso te deixar apontar onde está " +
                "o Watchdog.exe manualmente.\n\n" +
                "Quer localizar o arquivo agora?",
                "Serviço não encontrado", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (choice != MessageBoxResult.Yes) return;
            if (!LocateWatchdogManually()) return;
        }

        InstallServiceAt(WatchdogExePath);
    }

    private bool LocateWatchdogManually()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecione o Watchdog.exe",
            Filter = "Watchdog.exe|Watchdog.exe|Executáveis (*.exe)|*.exe",
            FileName = "Watchdog.exe"
        };
        if (dialog.ShowDialog() != true) return false;

        _state.WatchdogExePathOverride = dialog.FileName;
        StateManager.Save(_state);
        Logger.Log($"Watchdog.exe localizado manualmente em: {dialog.FileName}");
        return true;
    }

    private void LocateWatchdogButton_Click(object sender, RoutedEventArgs e)
    {
        if (LocateWatchdogManually())
        {
            InstallServiceAt(WatchdogExePath);
            RefreshStatus();
        }
    }

    private void InstallServiceAt(string watchdogExePath)
    {
        if (!File.Exists(watchdogExePath)) return;

        var alreadyGood = ServiceInstaller.IsInstalled()
            && ServiceInstaller.IsRunning()
            && string.Equals(ServiceInstaller.GetConfiguredBinPath(), watchdogExePath, StringComparison.OrdinalIgnoreCase);

        if (alreadyGood)
        {
            // Já está tudo certo — não mexe em nada. Evitar reinstalar à toa
            // importa porque parar+recriar o serviço cria um intervalo de
            // alguns segundos sem ninguém reforçando o hosts.
            Logger.Log("Serviço já instalado e rodando com o binário correto — nada a fazer.");
        }
        else
        {
            // Sempre limpa qualquer Watchdog.exe órfão primeiro — inclusive os
            // que o Windows já não associa mais a nenhum serviço registrado (é
            // o que causava dois Watchdogs rodando ao mesmo tempo e brigando
            // pelo hosts).
            ServiceInstaller.KillAllWatchdogProcesses();

            // Com os órfãos mortos, reseta o ACL do hosts — pode ter ficado com
            // entradas conflitantes de quando havia mais de um processo
            // mexendo nele ao mesmo tempo.
            HostsFileManager.ResetAcl();

            if (ServiceInstaller.IsInstalled())
            {
                ServiceInstaller.Uninstall();
            }

            var ok = ServiceInstaller.Install(watchdogExePath);
            Logger.Log(ok ? $"Serviço instalado com sucesso apontando pra {watchdogExePath}."
                           : "Falha ao instalar o serviço — ver log do sc acima.");
            if (!ok)
            {
                MessageBox.Show(
                    "Encontrei o Watchdog.exe, mas a instalação do serviço falhou. Detalhes em:\n" + Logger.LogFilePath,
                    "Falha ao instalar serviço", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        if (!AppStartupManager.IsRegistered())
        {
            AppStartupManager.RegisterAutoStart(Path.Combine(AppContext.BaseDirectory, "ColdTurkeyClone.exe"));
        }
    }

    private void RefreshStatusButton_Click(object sender, RoutedEventArgs e) => RefreshStatus();

    private void TestHostsButton_Click(object sender, RoutedEventArgs e)
    {
        var (success, message) = HostsFileManager.SelfTest();
        Logger.Log($"Autoteste do hosts: {message}");
        MessageBox.Show(message, success ? "Autoteste OK" : "Autoteste FALHOU",
            MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Error);
    }

    private void VerifyCrxButton_Click(object sender, RoutedEventArgs e)
    {
        var crxPath = Path.Combine(ExtensionFolderPath, "extension.crx");
        var (ok, message) = ExtensionInstaller.VerifyExistingCrx(crxPath);
        Logger.Log($"Verificação manual do .crx: {message}");
        MessageBox.Show(message, ok ? "Verificação OK" : "Verificação FALHOU",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
    }

    private void CleanupOrphansButton_Click(object sender, RoutedEventArgs e)
    {
        var before = Process.GetProcessesByName("Watchdog").Length;
        ServiceInstaller.KillAllWatchdogProcesses();
        HostsFileManager.ResetAcl();

        MessageBox.Show(
            $"{before} processo(s) \"Watchdog\" encontrados e encerrados. ACL do hosts resetado.\n\n" +
            "Recomendo fechar e abrir o app de novo agora, pra ele reinstalar um único Watchdog limpo.",
            "Limpeza concluída", MessageBoxButton.OK, MessageBoxImage.Information);

        RefreshStatus();
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Logger.LogFilePath)!);
            if (!File.Exists(Logger.LogFilePath))
                File.WriteAllText(Logger.LogFilePath, "(log vazio até agora)\n");

            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Logger.LogFilePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não consegui abrir o log: {ex.Message}\n\nCaminho: {Logger.LogFilePath}",
                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshStatus()
    {
        var installed = ServiceInstaller.IsInstalled();
        var running = installed && ServiceInstaller.IsRunning();

        ServiceStatusText.Text = installed
            ? (running ? "Serviço de bloqueio: instalado e rodando ✔" : "Serviço de bloqueio: instalado, mas PARADO ✘ (clique em Atualizar de novo em alguns segundos — ele se autorrecupera)")
            : "Serviço de bloqueio: NÃO instalado ✘ — feche e abra o app como Administrador";
        ServiceStatusText.Text += $"\nApp abre sozinho no login: {(AppStartupManager.IsRegistered() ? "sim ✔" : "não")}";

        var crxExists = File.Exists(Path.Combine(ExtensionFolderPath, "extension.crx"));
        var appLockerLine = AppLockerManager.IsSupported() ? "disponível" : "indisponível nesta edição do Windows";

        var policyStatus = ExtensionInstaller.GetPolicyStatus();
        var policyLine = string.Join(" | ", policyStatus.Select(kv => $"{kv.Key}: {kv.Value}"));

        ProtectionsStatusText.Text =
            (_state.PermanentProtectionsEnabled ? "Proteções permanentes: ATIVAS ✔" : "Proteções permanentes: desativadas") +
            $"\nExtensão (.crx gerado automaticamente): {(crxExists ? "sim ✔" : "ainda não — ative as proteções permanentes")}" +
            $"\nPolítica de instalação forçada: {policyLine}" +
            $"\nAppLocker: {appLockerLine}";
    }

    // ---------- Importar lista grande (corrigido: assíncrono + fora da caixa de texto) ----------

    private async void ImportListButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsLocked)
        {
            MessageBox.Show("As listas não podem ser alteradas enquanto o bloqueio estiver ativo.",
                "Bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Selecione um arquivo de lista no formato hosts (ex: StevenBlack/hosts, Blocklist Project)",
            Filter = "Arquivos de texto (*.txt)|*.txt|Todos os arquivos (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        ImportListButton.IsEnabled = false;
        StatusText.Text = "Importando... (arquivos grandes podem levar alguns segundos)";

        try
        {
            // Faz o parsing em uma thread separada — listas com dezenas de
            // milhares de linhas (ex: porn.txt de blocklists públicas) travavam
            // a interface (e podiam até "crashar" o app) quando processadas na
            // thread da UI, ainda mais quando o resultado ia parar numa TextBox.
            var domains = await Task.Run(() => ParseHostsFormatFile(dialog.FileName));

            if (domains.Count == 0)
            {
                MessageBox.Show("Nenhum domínio reconhecido nesse arquivo (formato esperado: \"0.0.0.0 dominio.com\").",
                    "Nada importado", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Listas grandes NÃO vão para a caixa de texto (é isso que travava
            // a interface) — ficam guardadas à parte e só um resumo é mostrado.
            _state.ImportedWebsites = domains;
            _state.ImportedListLabel = $"{Path.GetFileName(dialog.FileName)} ({domains.Count:N0} domínios)";
            StateManager.Save(_state);
            UpdateImportedListLabel();

            StatusText.Text = $"{domains.Count:N0} domínio(s) importados de \"{Path.GetFileName(dialog.FileName)}\".";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Falha ao importar: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ImportListButton.IsEnabled = true;
        }
    }

    private void RemoveImportedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsLocked)
        {
            MessageBox.Show("Não é possível alterar listas durante o bloqueio.", "Bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _state.ImportedWebsites = new List<string>();
        _state.ImportedListLabel = null;
        StateManager.Save(_state);
        UpdateImportedListLabel();
    }

    private void UpdateImportedListLabel()
    {
        ImportedListLabelText.Text = string.IsNullOrEmpty(_state.ImportedListLabel)
            ? "Nenhuma lista importada."
            : $"Lista importada ativa: {_state.ImportedListLabel}";
    }

    private static List<string> ParseHostsFormatFile(string path)
    {
        var result = new List<string>(capacity: 4096);
        var regex = new Regex(@"^\s*(?:0\.0\.0\.0|127\.0\.0\.1)\s+([a-zA-Z0-9.-]+\.[a-zA-Z]{2,})\s*$");

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var match = regex.Match(line);
            if (match.Success)
            {
                var domain = match.Groups[1].Value.ToLowerInvariant();
                if (domain != "localhost" && !domain.EndsWith(".local"))
                    result.Add(domain);
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void SuggestBrowsersButton_Click(object sender, RoutedEventArgs e)
    {
        var existing = SplitLines(PathKeywordsBox.Text);
        var merged = existing.Concat(KnownBypassBrowserPaths).Distinct(StringComparer.OrdinalIgnoreCase);
        PathKeywordsBox.Text = string.Join(Environment.NewLine, merged);
    }

    private void SuggestAdultKeywordsButton_Click(object sender, RoutedEventArgs e)
    {
        var existing = SplitLines(KeywordsBox.Text);
        var merged = existing.Concat(AdultContentKeywords).Distinct(StringComparer.OrdinalIgnoreCase);
        KeywordsBox.Text = string.Join(Environment.NewLine, merged);
    }

    // ---------- Salvar / Lock ----------

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsLocked)
        {
            MessageBox.Show("As listas não podem ser alteradas enquanto o bloqueio estiver ativo.",
                "Bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _state.BlockedWebsites = SplitLines(SitesBox.Text);
        _state.BlockedKeywords = SplitLines(KeywordsBox.Text);
        _state.BlockedProcesses = SplitLines(AppsBox.Text);
        _state.BlockedProcessPathKeywords = SplitLines(PathKeywordsBox.Text);
        StateManager.Save(_state);
        StatusText.Text = "Listas salvas.";
    }

    private void LockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsLocked)
        {
            MessageBox.Show("Já existe um bloqueio ativo. Espere o tempo acabar — não há como cancelar antes.",
                "Bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(MinutesBox.Text, out var minutes) || minutes <= 0)
        {
            MessageBox.Show("Digite um número de minutos válido.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var confirm = MessageBox.Show(
            $"Isso vai travar tudo que está listado por {minutes} minuto(s). NÃO existe forma de cancelar pela interface antes do tempo acabar. Continuar?",
            "Confirmar bloqueio", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _state.BlockedWebsites = SplitLines(SitesBox.Text);
        _state.BlockedKeywords = SplitLines(KeywordsBox.Text);
        _state.BlockedProcesses = SplitLines(AppsBox.Text);
        _state.BlockedProcessPathKeywords = SplitLines(PathKeywordsBox.Text);
        _state.LockedUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
        StateManager.Save(_state);

        try
        {
            HostsFileManager.ApplyBlockedSites(_state.BlockedWebsites.Concat(_state.ImportedWebsites));
            HostsFileManager.Protect();
            ProcessBlocker.KillBlockedProcesses(_state.BlockedProcesses, _state.BlockedProcessPathKeywords);
            BrowserPolicyManager.ApplyRestrictions();
            _restrictionsAppliedByApp = true;
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.LockButton_Click", ex);
            MessageBox.Show(
                "O bloqueio foi marcado como ativo (o timer está rodando e não pode ser cancelado), mas houve um " +
                "erro ao aplicar o bloqueio de sites/apps:\n\n" + ex.Message + "\n\n" +
                "Detalhes completos em:\n" + Logger.LogFilePath + "\n\n" +
                "Isso pode ser causado por antivírus bloqueando alterações no arquivo hosts — confira o " +
                "Histórico de proteção em Windows Security.",
                "Erro ao aplicar bloqueio", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        UpdateUiForLockState();
    }

    // ---------- Proteções permanentes ----------

    private void PermanentProtectionsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_state.PermanentProtectionsEnabled)
            {
                PermanentProtections.Enable(InstallRootPath, ExtensionFolderPath);
                _state.PermanentProtectionsEnabled = true;
                StateManager.Save(_state);
                MessageBox.Show(
                    "Ativado: SafeSearch, DoH desligado, extensão instalada automaticamente no Chrome/Edge/Brave " +
                    "(sem precisar de nada manual) e pasta protegida contra exclusão.\n\n" +
                    "Se o navegador já estava aberto, pode ser preciso fechar e abrir de novo (ou esperar alguns " +
                    "minutos) pra ele reler as políticas e a extensão aparecer em chrome://extensions.",
                    "Proteções permanentes ativadas", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                if (_state.HasPassword)
                {
                    var pass = AskPassword("Digite a senha para desativar as proteções permanentes:");
                    if (pass == null) return;
                    if (!PasswordManager.Verify(pass, _state.PasswordHash))
                    {
                        MessageBox.Show("Senha incorreta.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }

                if (!ConfirmationGauntlet.Run(this, "Desativar proteções permanentes"))
                {
                    return;
                }

                PermanentProtections.Disable(InstallRootPath);
                _state.PermanentProtectionsEnabled = false;
                StateManager.Save(_state);
            }

            PermanentProtectionsButton.Content = _state.PermanentProtectionsEnabled
                ? "Desativar proteções permanentes"
                : "Ativar proteções permanentes";
            RefreshStatus();
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.PermanentProtectionsButton_Click", ex);
            MessageBox.Show($"Erro ao aplicar proteções permanentes: {ex.Message}\n\nDetalhes em:\n{Logger.LogFilePath}",
                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyAppLockerButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AppLockerManager.IsSupported())
        {
            MessageBox.Show(
                "O AppLocker só existe no Windows 11 Pro, Enterprise ou Education — não está disponível " +
                "nesta edição do Windows (Home). Isso é uma limitação do Windows, não do app.",
                "Não suportado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var pathKeywords = SplitLines(PathKeywordsBox.Text).Select(k => $"*{k}*");
        var ok = AppLockerManager.Apply(pathKeywords);
        MessageBox.Show(ok
            ? "Regras do AppLocker aplicadas. Pode levar um instante pro Windows aplicar de fato — " +
              "reinicie o computador se os apps ainda abrirem normalmente depois de alguns minutos."
            : $"Falha ao aplicar as regras. Detalhes em:\n{Logger.LogFilePath}",
            "AppLocker", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
    }

    // ---------- Senha / desinstalação ----------

    private void SetPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        // CORRIGIDO: antes dava pra trocar a senha com o bloqueio ativo,
        // o que anulava a proteção do timer. Agora não dá mais.
        if (_state.IsLocked)
        {
            MessageBox.Show("Não é possível alterar a senha enquanto o bloqueio estiver ativo.",
                "Bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_state.HasPassword)
        {
            var current = AskPassword("Digite a senha atual:");
            if (current == null) return;
            if (!PasswordManager.Verify(current, _state.PasswordHash))
            {
                MessageBox.Show("Senha atual incorreta.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        var newPassword = AskPassword("Digite a nova senha:");
        if (string.IsNullOrEmpty(newPassword))
        {
            MessageBox.Show("Senha não pode ser vazia.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var confirmPassword = AskPassword("Confirme a nova senha:");
        if (newPassword != confirmPassword)
        {
            MessageBox.Show("As senhas não coincidem.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _state.PasswordHash = PasswordManager.Hash(newPassword);
        StateManager.Save(_state);
        MessageBox.Show("Senha definida.", "Pronto", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsLocked)
        {
            MessageBox.Show("Não é possível desinstalar enquanto o bloqueio estiver ativo. Espere o tempo acabar.",
                "Bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!UninstallFlow.TryPerformProtectedUninstall(this)) return;

        MessageBox.Show("Serviço removido, hosts/políticas/pasta restaurados.\n\n" +
            "Se instalou pelo Setup, vá em \"Adicionar ou Remover Programas\" e remova normalmente " +
            "(a checagem de senha já foi feita). Se copiou os arquivos manualmente, já pode apagar a pasta.",
            "Desinstalado", MessageBoxButton.OK, MessageBoxImage.Information);

        _reallyClosing = true;
        _trayIcon?.Dispose();
        Application.Current.Shutdown();
    }

    private string? AskPassword(string prompt)
    {
        var dialog = new PasswordDialog(prompt) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.EnteredPassword : null;
    }

    // ---------- Bandeja do sistema ----------
    // O app em si é só um painel de controle — quem garante o bloqueio rodando
    // é o SERVIÇO do Windows (Watchdog), não esta janela. Ainda assim, fechar a
    // janela sem querer causava confusão ("parece que parou de funcionar").
    // Agora o "X" minimiza pra bandeja; só "Sair" pelo menu da bandeja fecha de
    // verdade (o serviço continua rodando de qualquer jeito, mesmo com o app
    // fechado por completo — isso aqui é só clareza de interface).

    private void SetupTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Shield,
            Visible = true,
            Text = "Cold Turkey Clone"
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); });
        menu.Items.Add("Sair (o serviço de bloqueio continua rodando)", null, (_, _) =>
        {
            _reallyClosing = true;
            _trayIcon?.Dispose();
            Application.Current.Shutdown();
        });
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); };
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyClosing) return;
        e.Cancel = true;
        Hide();
    }

    // ---------- Timer ----------

    private void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            _state = StateManager.Load();

            if (_state.IsLocked)
            {
                ProcessBlocker.KillBlockedProcesses(_state.BlockedProcesses, _state.BlockedProcessPathKeywords);
            }
            else if (_state.LockedUntilUtc.HasValue || _restrictionsAppliedByApp)
            {
                HostsFileManager.Unprotect();
                HostsFileManager.ClearBlockedSites();
                if (_state.PermanentProtectionsEnabled)
                {
                    HostsFileManager.ApplySafeSearchEntries(SafeSearchDomains.GetHostsEntries());
                    HostsFileManager.Protect();
                }
                BrowserPolicyManager.RemoveRestrictions();
                _restrictionsAppliedByApp = false;
                _state.LockedUntilUtc = null;
                StateManager.Save(_state);
            }

            UpdateUiForLockState();
        }
        catch (Exception ex)
        {
            // Loga sem popup — isso roda a cada segundo, um popup por ciclo seria
            // insuportável. O log de diagnóstico continua registrando o problema.
            Logger.LogException("MainWindow.Timer_Tick", ex);
        }
    }

    private void UpdateUiForLockState()
    {
        var locked = _state.IsLocked;
        SitesBox.IsEnabled = !locked;
        KeywordsBox.IsEnabled = !locked;
        AppsBox.IsEnabled = !locked;
        PathKeywordsBox.IsEnabled = !locked;
        MinutesBox.IsEnabled = !locked;
        LockButton.IsEnabled = !locked;
        SaveButton.IsEnabled = !locked;
        UninstallButton.IsEnabled = !locked;
        ImportListButton.IsEnabled = !locked;
        RemoveImportedButton.IsEnabled = !locked;
        SuggestBrowsersButton.IsEnabled = !locked;
        SetPasswordButton.IsEnabled = !locked;

        StatusText.Text = locked
            ? $"🔒 BLOQUEADO até {_state.LockedUntilUtc!.Value.ToLocalTime():HH:mm:ss} ({(_state.LockedUntilUtc.Value - DateTime.UtcNow):hh\\:mm\\:ss} restantes) — sem opção de cancelar."
            : "Sem bloqueio ativo.";

        StatusBanner.Background = locked
            ? (System.Windows.Media.Brush)FindResource("ColorDanger")
            : (System.Windows.Media.Brush)FindResource("ColorSurfaceAlt");
        StatusText.Foreground = locked
            ? System.Windows.Media.Brushes.White
            : (System.Windows.Media.Brush)FindResource("ColorTextPrimary");
    }

    private static List<string> SplitLines(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
}
