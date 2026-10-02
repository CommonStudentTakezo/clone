# ColdTurkeyClone

Bloqueador de sites, palavras-chave, SafeSearch e programas para Windows 11,
com trava por tempo sem cancelamento, proteção contra desinstalação, hosts
sempre protegido contra edição, pasta protegida contra exclusão, app e
serviço iniciando sozinhos no boot, e proteções permanentes (SafeSearch, DoH
desligado, extensão auto-instalada).

## Instalação rápida pra testes (sem Inno Setup)

Pra iterar rápido sem compilar um instalador toda vez, use o
`quick-install.ps1` incluído: ele publica os dois projetos, copia tudo pra
uma pasta na estrutura certa (App\ e Watchdog\ lado a lado — o que resolve
de vez o problema de o app não achar o Watchdog.exe) e já abre o app
elevado.

```powershell
.\quick-install.ps1
```

Rodadas seguintes, se só mudou código e já publicou antes:
```powershell
.\quick-install.ps1 -SkipPublish
```

O Inno Setup (`Installer.iss`) continua existindo pra quando você quiser
gerar o instalador de verdade pra distribuir, mas não é mais necessário
durante os testes.

## Causa raiz real do bloqueio intermitente (encontrada via log)

O log mostrou **dois processos `Watchdog.exe` diferentes rodando ao mesmo
tempo**, cada um brigando pelo mesmo arquivo hosts sem coordenação — isso
explica os erros de "Acesso negado" e "arquivo sendo usado por outro
processo" espalhados pelo log, e o comportamento errático (funciona, depois
para, sem padrão claro).

**Por que isso acontecia:** toda vez que o app abre, ele reinstala o serviço
(`sc stop` + `sc delete` + `sc create` + `sc start`) pra garantir que está
usando o binário mais recente. Só que `sc stop` apenas *sinaliza* a parada —
não espera de verdade o processo terminar antes do `sc delete` seguir em
frente. Se o processo antigo demorasse um pouco pra encerrar, virava um
**órfão**: continuava rodando pra sempre, sem nenhum serviço registrado
apontando pra ele, e nada mais conseguia pará-lo via `sc stop`. Depois de
várias rodadas de teste, é esperado acumular vários desses.

**Correção (três camadas):**
1. `ServiceInstaller.Uninstall()` agora espera de verdade a confirmação de
   parada (até 5s) antes do `sc delete`.
2. Antes de qualquer instalação/reinstalação, o app agora mata **todo**
   processo chamado "Watchdog" pelo nome — inclusive os que o Windows já
   nem associa mais a um serviço registrado.
3. O próprio Watchdog, ao iniciar, mata qualquer OUTRA instância de si mesmo
   — importante pro caso dele se autorrecuperar sozinho (sem o app aberto)
   e por acaso um órfão antigo ainda estar de pé.

Além disso, com múltiplos processos mexendo no ACL do hosts ao mesmo tempo,
é esperado que ele tenha acumulado entradas conflitantes — por isso o app
agora roda um `icacls /reset` completo (limpa TODAS as entradas explícitas)
toda vez que reinstala o serviço, antes de reaplicar a proteção normal.

Se você quiser limpar a bagunça acumulada agora, sem esperar reabrir o app,
tem um botão novo na aba Status: **"Encerrar Watchdogs órfãos e resetar ACL
do hosts"**.



**Sites bloqueados não bloqueavam de fato, mas o SafeSearch sim.** Achei a
causa: o App e o Watchdog (serviço) são dois processos separados que liam e
escreviam no arquivo hosts cada um por conta própria, sem se coordenar. Como
o Watchdog reforça o hosts a cada 5 segundos, havia uma condição de corrida
real — se os dois escrevessem quase ao mesmo tempo, um podia sobrescrever o
outro com uma versão desatualizada do arquivo. Isso batia com o sintoma
relatado: SafeSearch é escrito por um fluxo mais isolado, enquanto sites
bloqueados dependiam de escritas simultâneas do App E do Watchdog. Corrigido
com um Mutex nomeado do Windows (`Global\ColdTurkeyClone_HostsFileMutex`),
que garante que só um processo mexa no hosts por vez.
Use o botão **"Testar escrita do hosts agora"** pra confirmar rapidinho que
a escrita está realmente funcionando antes de testar um bloqueio inteiro.

**Hosts agora é protegido o tempo todo.** Antes, a proteção contra edição
manual do hosts só valia durante um bloqueio ativo ou com "Proteções
permanentes" ligado. Agora o Watchdog protege o hosts em TODO ciclo, desde
que esteja rodando — não precisa de nada ligado antes pra isso valer.

**App e serviço agora iniciam sozinhos no boot/login.** O Watchdog (serviço)
já iniciava sozinho no boot (`sc create ... start= auto`) — se isso não
pareceu funcionar antes, o mais provável é o serviço nunca ter sido
instalado de fato (ver os problemas de caminho corrigidos nas rodadas
anteriores). Além disso, agora o app principal (a janela de controle) também
se registra pra abrir sozinho no login, via Agendador de Tarefas com
privilégio máximo — assim não aparece prompt do UAC toda vez que o Windows
liga (uma chave de Registro comum faria isso aparecer). O painel de status
mostra "App abre sozinho no login: sim/não".


## Resposta direta a cada problema relatado

**"É possível alterar a senha durante o bloqueio."**
Bug real, corrigido. `SetPasswordButton_Click` agora verifica `_state.IsLocked`
primeiro e recusa a troca.

**"Não desativa a guia anônima / não trava o SafeSearch / não bloqueia termos adultos / parece não iniciar sozinho / dá pra fechar pelo Task Manager / basta apagar a pasta."**
Isso quase certamente é consequência de **não ter rodado como Administrador**
(você mencionou isso no final da sua mensagem — é a causa mais provável).
Praticamente toda proteção deste app (hosts, Registro, serviço do Windows)
falhava **silenciosamente** sem elevação — parecia que "não fazia nada" mas na
real nunca tinha permissão pra fazer nada. Corrigi isso: o app agora recusa
abrir sem admin, com uma mensagem explicando exatamente o que fazer (botão
direito > Executar como administrador). Rode assim e teste de novo — a
expectativa é que a maioria desses pontos já resolva sozinha.
Além disso, mudei o SafeSearch de "só durante o bloqueio" para uma **proteção
permanente** de verdade (ver seção própria abaixo) e adicionei reforço nativo
no Chrome/Edge (`ForceGoogleSafeSearch`, `ForceYouTubeRestrict`), então agora
tem duas camadas independentes em vez de uma só.

**"Crasha ao importar uma lista grande (porn.txt)."**
Bug real, corrigido. O problema era jogar dezenas de milhares de domínios
dentro de uma caixa de texto do WPF (que não é feita pra isso e trava/estoura).
Agora a importação roda em segundo plano e listas grandes ficam guardadas à
parte (não aparecem mais na caixa de texto) — a interface só mostra um resumo
tipo "porn.txt (58.213 domínios)", com um botão pra remover a lista importada.

**"Dá pra fechar o app e ele para de funcionar / não roda em segundo plano."**
Isso reflete uma confusão de arquitetura que era esperada, não um bug: a
janela do app (ColdTurkeyClone.exe) é só o painel de controle — quem garante
o bloqueio rodando é o **Watchdog**, um Serviço do Windows separado, que deve
continuar rodando mesmo com o app fechado. Se ele não estava rodando, é a
mesma causa raiz da elevação (o serviço só é instalado na primeira vez que o
app roda como admin). Fiz duas coisas: (1) o app agora tem um painel de
"Status do sistema" mostrando se o serviço está instalado e rodando, pra você
conseguir verificar direto; (2) o "X" da janela agora minimiza pra bandeja em
vez de fechar de verdade (só "Sair" pelo menu da bandeja fecha), pra reduzir
a confusão — mas o que garante o bloqueio de verdade continua sendo o
serviço, não a janela.

**"Dá pra finalizar pelo Task Manager."**
O Watchdog (serviço) já tinha recuperação automática configurada — se
alguém finalizar o processo, o Windows deveria reiniciá-lo em segundos. Se
isso não aconteceu no seu teste, o mais provável é, de novo, o serviço nunca
ter sido instalado (sem admin). Com o painel de status novo, dá pra confirmar
isso diretamente. Se o serviço aparecer como "instalado e rodando" e mesmo
assim sobreviver a um "Finalizar Tarefa" sem voltar, aí sim seria um bug pra
investigar — me avise.

**"Basta deletar a pasta (e pede pra instalar o .NET)."**
Duas coisas diferentes aqui, ambas resolvidas:
- O aviso de "instale o .NET" não tinha relação com apagar a pasta — é porque
  o app era publicado no modo "dependente de framework" (precisa do .NET
  Desktop Runtime instalado à parte no Windows). Mudei os projetos pra
  publicarem como **self-contained** (`dotnet publish -r win-x64
  --self-contained true`) — o executável passa a carregar o runtime junto,
  sem depender de nada instalado à parte.
- Apagar a pasta: implementei sua sugestão (ver abaixo).

**"DuckDuckGo ainda acessível pelo Chrome."**
Porque a extensão nunca foi carregada nesse navegador (você mesmo disse que
não testou a pasta `BrowserExtension`) — sem ela, nenhuma proteção de
palavra-chave/DuckDuckGo vale naquele navegador específico. Implementei a
instalação automática (ver seção própria) pra isso não depender mais de você
carregar manualmente.

## Conteúdo adulto em lojas "normais" (ex: livros eróticos via Kindle Unlimited)

A Amazon inteira não dá pra bloquear (é loja normal, bloquear tudo seria
overkill pra maioria das pessoas) — mas as páginas de categoria/gênero desse
tipo de conteúdo têm texto identificável ("Erótica", "Romance Erótico",
etc.), que a extensão de navegador já pega (ela varre tanto a URL quanto o
texto da página, não só a URL). Botão novo: **"Sugerir termos de conteúdo
adulto"**, do lado da caixa de palavras-chave, preenche termos comuns
(erótica, romance erótico, kindle unlimited, hentai, nsfw, etc.) — o mesmo
padrão do botão de navegadores "anti-bloqueio".

**Limite honesto:** isso depende da extensão estar instalada no navegador
usado (ver seção da extensão) e de a página ter texto categorizando o
conteúdo — uma capa de livro isolada, sem nenhum texto de categoria/gênero
por perto, pode escapar da varredura por palavra-chave.

**Se quiser algo mais forte:** dá pra bloquear a leitura do Kindle inteira
(não só a busca/loja) adicionando `read.amazon.com` (Kindle Cloud Reader) na
lista de sites bloqueados — isso impede ler qualquer livro do Kindle pelo
navegador durante o bloqueio, não só conteúdo adulto. Se você tiver o app
oficial do Kindle instalado no Windows, também dá pra adicionar `Kindle` na
lista de programas bloqueados, pra cobrir a leitura fora do navegador.



Implementado em `FolderProtection.cs`, usando o mesmo truque de SID que já
protege o hosts: nega a permissão de **Excluir** (não de ler/executar) pra
Usuários e Administradores na pasta de instalação inteira, recursivamente. O
Watchdog (conta SYSTEM) não é afetado. Faz parte das "proteções permanentes"
abaixo. Limite honesto de sempre: um Administrador que rode `icacls`/`takeown`
na mão ainda reverte — não tem como fechar essa brecha 100% sem tirar do
Windows a capacidade normal de administração.

## Proteções permanentes (novo)

Um novo botão **"Ativar proteções permanentes"** liga, de forma independente
do timer de bloqueio (fica sempre ativo, não só durante um "Iniciar
bloqueio"):
- SafeSearch forçado (hosts + `ForceGoogleSafeSearch`/`ForceYouTubeRestrict`
  nativos do Chrome/Edge + extensão reforçando DuckDuckGo).
- **DNS-over-HTTPS desligado** no Chrome/Edge/Brave (`DnsOverHttpsMode=off`,
  `BuiltInDnsClientEnabled=0`) e no Firefox (`DNSOverHTTPS\Enabled=0` +
  `Locked=1`, o que também impede a pessoa de reativar manualmente nas
  configurações). Isso é o que resolve o pedido de "Firewall que bloqueie
  DoH" — desligar o recurso na raiz via política oficial de cada navegador é
  mais confiável do que tentar bloquear IPs de provedores de DoH por
  Firewall (esses IPs mudam; a política, não).
- Extensão instalada automaticamente no Chrome/Edge/Brave, sem precisar de
  "Modo do desenvolvedor" — ver seção seguinte.
- Pasta protegida contra exclusão.

Ativar não pede senha (é só aumentar proteção). **Desativar pede a senha**
que você configurou, do mesmo jeito que desinstalar.

## A extensão instala automaticamente?

Sim. Usa `ExtensionInstallForcelist`, a política oficial do Chrome pra
instalar extensões sem interação do usuário e **sem que ele consiga
desativar/remover** pela tela `chrome://extensions` (documentado em
developer.chrome.com/docs/extensions/develop/concepts/extensions-update-lifecycle).
É o que fecha de vez a brecha do "DuckDuckGo acessível pelo Chrome" — uma vez
ativado, a extensão passa a estar sempre lá, em todo perfil do Chrome/Edge/
Brave da máquina, sem o usuário poder tirá-la.

**Não sobrou passo manual nenhum.** O `.crx` assinado (formato CRX3,
documentado publicamente pelo Chromium) é gerado sozinho em C#, usando a
chave em `ExtensionSigningKey/extension_key.pem` — sem precisar do Chrome
instalado pra empacotar. Tudo acontece automaticamente ao clicar em "Ativar
proteções permanentes".

**Validação:** o formato exato que uso (`extension_id;file:///caminho/
manifesto.xml` na política) é o mesmo documentado pela UiPath pra distribuir
a extensão Chrome deles via instalação offline por Política de Grupo
(docs.uipath.com/studio/standalone/latest/user-guide/chrome-group-policies)
— um caso real, em produção. Isso não substitui testar no seu navegador,
mas é uma confirmação forte de que o esquema funciona sem precisar de
políticas extras.

**Se não aparecer:** o Chrome só relê políticas nova em algumas situações
(abrir o navegador, ou periodicamente). Se ele já estava aberto quando você
ativou, feche e abra de novo. Pra conferir o que o Chrome está enxergando de
verdade, acesse `chrome://policy` — se `ExtensionInstallForcelist` aparecer
lá com nosso ID, a política chegou; se a extensão ainda não aparecer em
`chrome://extensions` depois disso, o problema é o Chrome não conseguir
baixar/validar o `.crx` (confira o log de diagnóstico do app).

**Coisas que podem bloquear isso, mesmo com tudo certo do nosso lado**
(raro numa máquina pessoal, mais comum em PC de empresa/gerenciado):
`ExtensionInstallBlocklist` com `*`, `ExtensionAllowedTypes` sem
`"extension"`, ou `BlockExternalExtensions` ligado. Se algo assim já estiver
configurado na sua máquina por outro motivo, nosso `ExtensionInstallForcelist`
pode não bastar sozinho.

Cobre Chrome/Edge/Brave. Não cobre Firefox (sistema de extensões diferente).

## AppLocker automatizado (novo)

Botão **"Aplicar regras AppLocker"** gera e aplica, via PowerShell
(`Set-AppLockerPolicy`), regras que impedem os navegadores "anti-bloqueio"
conhecidos de sequer **abrir** (o Windows recusa a execução, é mais forte que
matar o processo depois de aberto).
- **Só existe no Windows 11 Pro/Enterprise/Education** — o app detecta a
  edição e avisa se não for suportado (não tem contorno pra isso, é
  limitação do Windows Home mesmo).
- São regras por CAMINHO de instalação padrão — contornável movendo o
  executável de pasta. Regras por hash/editor seriam mais fortes, mas não dá
  pra gerar isso de forma genérica sem saber de antemão o instalador exato de
  cada programa.
- Não incluí um botão de "remover" no app: `Set-AppLockerPolicy` com uma
  política vazia reseta a política de AppLocker da máquina INTEIRA, não só as
  regras deste app — se sua organização/PC já tiver outras regras de
  AppLocker, isso as apagaria também. Se precisar reverter, faça manualmente
  pelo Editor de Política de Grupo Local (`gpedit.msc` >
  Configurações do Computador > Configurações do Windows > Configurações de
  Segurança > Políticas de Controle de Aplicativos > AppLocker) ou avise que
  posso te passar o comando exato pro seu caso.

## Como compilar e publicar
```
dotnet build -c Release
dotnet publish App -c Release -r win-x64 --self-contained true
dotnet publish Watchdog -c Release -r win-x64 --self-contained true
```
Saem em `App\bin\Release\net8.0-windows\win-x64\publish\` e o equivalente
pro Watchdog — são esses os caminhos que o `Installer.iss` já espera.

## Como gerar o instalador (Setup.exe)
Mesmo processo de antes — `Installer.iss` com Inno Setup
(https://jrsoftware.org/isdl.php), agora apontando pros caminhos de publish
self-contained. O desinstalador gerado continua chamando
`ColdTurkeyClone.exe --uninstall` antes de apagar qualquer coisa, então a
senha vale tanto pelo app quanto pelo "Adicionar ou Remover Programas".

## Como instalar
1. Publique (comandos acima) ou gere o Setup.
2. Rode como **Administrador** — o app recusa abrir sem isso agora, com
   instruções na tela.
3. Defina uma senha.
4. (Opcional) Gere o `extension.crx` (ver seção da extensão) e clique em
   "Ativar proteções permanentes".
5. (Opcional, Windows Pro/Enterprise) Clique em "Aplicar regras AppLocker".

## Tabela-resumo de proteções e limites reais

| Proteção | O que impede | Limite honesto |
|---|---|---|
| Elevação obrigatória | Falhas silenciosas sem admin | N/A — é um requisito, não uma trava |
| Sem botão de desbloqueio + HMAC | Editar JSON/Registro pra encurtar o tempo | Exige engenharia reversa do binário |
| Senha bloqueada durante o lock | Trocar/resetar senha no impulso | Fora do lock, ainda dá pra trocar sabendo a senha atual — é intencional |
| Serviço com auto-restart | Matar processo no Task Manager | `sc stop` administrativo limpo ainda funciona — por isso a senha no botão de proteções/desinstalar |
| Pasta protegida (ACL) | Apagar pelo Explorer, mesmo como admin | `icacls`/`takeown` manual ainda reverte |
| Hosts com ACL + watcher + Mutex entre processos, SEMPRE ativo | Edição manual, inclusive Notepad elevado; corrida de escrita App×Watchdog | `icacls`/`takeown` manual ainda reverte |
| SafeSearch permanente (hosts + prefs nativas + extensão) | Desligar SafeSearch no Google/Bing/YouTube; reforça DuckDuckGo | Cobre só navegador/PC |
| DoH desligado via política | Navegador ignorar o hosts via DNS-over-HTTPS | Só cobre Chrome/Edge/Brave/Firefox; outro navegador pode não respeitar a política |
| Extensão forçada (não removível) | Desativar/remover a extensão pela tela do navegador | Só Chrome/Edge/Brave; exige o passo manual do `.crx` |
| AppLocker | Abrir navegadores "anti-bloqueio" instalados em caminho padrão | Só Windows Pro/Enterprise; contornável movendo o executável |
| Watchdog + WMI mata processos na hora | Uso de apps/navegadores fora dos caminhos do AppLocker | — |

Como sempre: nada disso, junto ou separado, impede 100% um Administrador
local realmente decidido. O objetivo é atrito real o suficiente pra conter o
impulso do momento.

## Ideias de próximos passos
- Regras AppLocker por hash/editor em vez de só caminho.
- Empacotamento automático do `.crx` (hoje depende do Chrome real, manual).
- Suporte a Firefox na instalação forçada de extensão (sistema diferente).
