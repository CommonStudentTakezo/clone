using System.Windows;
using ColdTurkeyClone.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ColdTurkeyClone;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Rede de segurança: qualquer exceção não tratada em qualquer lugar da
        // interface (não só nos pontos que já têm try/catch específico) cai
        // aqui em vez de derrubar o app inteiro sem explicação nenhuma.
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.LogException("DispatcherUnhandledException (não tratada em nenhum outro lugar)", args.Exception);
            MessageBox.Show(
                "Ocorreu um erro inesperado:\n\n" + args.Exception.Message +
                "\n\nDetalhes completos em:\n" + Logger.LogFilePath +
                "\n\nO app vai tentar continuar rodando normalmente.",
                "Erro inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        if (e.Args.Contains("--uninstall"))
        {
            var authorized = UninstallFlow.TryPerformProtectedUninstall(null);
            Shutdown(authorized ? 0 : 1);
            return;
        }

        // Praticamente TODA proteção deste app (hosts, registro, serviço) exige
        // admin. Antes, quando faltava permissão, o app tentava mesmo assim e
        // ignorava o erro silenciosamente — parecendo que "não fazia nada".
        // Agora ele recusa a abrir sem elevação, com uma explicação clara.
        if (!ElevationHelper.IsAdministrator())
        {
            MessageBox.Show(
                "Este app precisa ser executado como Administrador para funcionar " +
                "(ele edita o arquivo hosts, o Registro e instala um serviço do Windows).\n\n" +
                "Feche esta janela, clique com o botão direito no ColdTurkeyClone.exe e escolha " +
                "\"Executar como administrador\".\n\n" +
                "Se você rodou pelo Visual Studio/dotnet run, abra o Visual Studio (ou o terminal) " +
                "como administrador também — rodar o .exe direto sem isso NÃO ativa a elevação " +
                "automaticamente mesmo com o manifesto pedindo admin.",
                "Precisa ser Administrador", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        new MainWindow().Show();
    }
}
