@echo off
REM emergency-fix-hosts.bat
REM Rode este arquivo como Administrador (botao direito > Executar como administrador)
REM se o PC ficou sem acessar sites depois de testar o ColdTurkeyClone.

echo Restaurando permissoes do arquivo hosts...

REM Remove os atributos que travavam o arquivo
attrib -r -s "%WINDIR%\System32\drivers\etc\hosts"

REM Restaura as permissoes padrao do Windows explicitamente (NAO usa /reset,
REM que depende de heranca da pasta e pode deixar o arquivo pior)
icacls "%WINDIR%\System32\drivers\etc\hosts" /grant *S-1-5-18:F
icacls "%WINDIR%\System32\drivers\etc\hosts" /grant *S-1-5-32-544:F
icacls "%WINDIR%\System32\drivers\etc\hosts" /grant *S-1-5-32-545:RX
icacls "%WINDIR%\System32\drivers\etc\hosts" /remove:d *S-1-5-32-544
icacls "%WINDIR%\System32\drivers\etc\hosts" /remove:d *S-1-5-32-545

REM Para o servico do Watchdog, que ficaria tentando mexer no arquivo e
REM re-quebrando tudo enquanto voce nao atualiza para a versao corrigida
sc stop ColdTurkeyCloneWatchdog
taskkill /F /IM Watchdog.exe
taskkill /F /IM ColdTurkeyClone.exe

ipconfig /flushdns

echo.
echo Pronto. Teste acessar um site agora. O servico do Watchdog foi parado
echo de proposito - so reinstale/rode o app de novo depois de pegar a
echo versao corrigida.
pause
