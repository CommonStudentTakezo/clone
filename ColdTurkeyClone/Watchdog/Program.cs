using ColdTurkeyClone.Watchdog;
using Microsoft.Extensions.Hosting;

// Quando iniciado pelo Service Control Manager do Windows, UseWindowsService()
// detecta isso automaticamente e roda como serviço real (sem janela, sem console).
// Se executado manualmente (ex: durante testes), roda como um processo de console normal.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ColdTurkeyCloneWatchdog";
});
builder.Services.AddHostedService<EnforcementWorker>();

var host = builder.Build();
host.Run();
