; ============================================================================
; Installer.iss - script do Inno Setup (https://jrsoftware.org/isinfo.php)
;
; Gera um instalador unico (ColdTurkeyClone-Setup.exe) que:
;  - Copia App, Watchdog e a extensao de navegador para Arquivos de Programas
;  - Cria atalhos no Menu Iniciar / Area de Trabalho
;  - Roda o app ao final (que se encarrega de instalar o Servico do Windows)
;  - Aparece normalmente em "Adicionar ou Remover Programas"
;  - MAS: o desinstalador gerado (unins000.exe) NAO apaga nada sozinho.
;    Antes de tudo, ele chama "ColdTurkeyClone.exe --uninstall" e so continua
;    se o app confirmar (senha correta / sem bloqueio ativo). Isso fecha a
;    brecha que existiria se alguem pudesse desinstalar direto pelo Painel de
;    Controle sem passar pela senha.
;
; NOTA: este arquivo usa apenas caracteres ASCII de proposito (sem acentos).
; Alguns leitores/editores podem interpretar UTF-8 sem BOM incorretamente
; dependendo da configuracao regional do Windows, corrompendo acentos -
; evitar acentos aqui elimina esse risco por completo.
;
; COMO COMPILAR (no Windows, com Inno Setup instalado):
;   1. Baixe e instale o Inno Setup em https://jrsoftware.org/isdl.php
;   2. Publique o projeto como self-contained (gera os .exe sem depender de
;      instalar o .NET separadamente):
;        dotnet publish App -c Release -r win-x64 --self-contained true
;        dotnet publish Watchdog -c Release -r win-x64 --self-contained true
;   3. Abra este arquivo (Installer.iss) no Inno Setup Compiler e clique em
;      Build > Compile (ou rode via linha de comando: iscc Installer.iss)
;   4. O instalador final sai em Output\ColdTurkeyClone-Setup.exe
; ============================================================================

#define MyAppName "Cold Turkey Clone"
#define MyAppVersion "1.1"
#define MyAppExeName "ColdTurkeyClone.exe"

[Setup]
AppId={{B7B3A8C1-4C2E-4B8B-9B7B-COLDTURKEYCLONE}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\ColdTurkeyClone
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=Output
OutputBaseFilename=ColdTurkeyClone-Setup
Compression=lzma
SolidCompression=yes
UninstallDisplayIcon={app}\App\{#MyAppExeName}
WizardStyle=modern

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Files]
Source: "App\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}\App"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Watchdog\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}\Watchdog"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "BrowserExtension\*"; DestDir: "{app}\BrowserExtension"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "ExtensionSigningKey\*"; DestDir: "{app}\ExtensionSigningKey"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\App\{#MyAppExeName}"
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\App\{#MyAppExeName}"

[Run]
Filename: "{app}\App\{#MyAppExeName}"; Description: "Abrir o {#MyAppName} agora"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\ColdTurkeyClone"

[Code]
function InitializeUninstall(): Boolean;
var
  AppExe: String;
  ResultCode: Integer;
begin
  Result := True;
  AppExe := ExpandConstant('{app}\App\{#MyAppExeName}');

  // Se o executavel ja nao existir mais (foi apagado manualmente por algum
  // motivo), nao tem como checar nada - deixa o Setup seguir normalmente.
  if not FileExists(AppExe) then
    exit;

  if not Exec(AppExe, '--uninstall', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Nao foi possivel iniciar a checagem de autorizacao. Desinstalacao cancelada.',
      mbError, MB_OK);
    Result := False;
    exit;
  end;

  // App.exe retorna 0 = autorizado (senha correta / sem bloqueio ativo).
  // Qualquer outro codigo = negado ou cancelado pelo usuario.
  if ResultCode <> 0 then
  begin
    Result := False;
  end;
end;
