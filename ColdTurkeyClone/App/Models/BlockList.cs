namespace ColdTurkeyClone.Models;

public class BlockState
{
    public List<string> BlockedWebsites { get; set; } = new();

    // Domínios importados de um arquivo grande (ex: blocklist de terceiros).
    // Ficam separados de BlockedWebsites pra não travar a caixa de texto da
    // interface com dezenas de milhares de linhas.
    public List<string> ImportedWebsites { get; set; } = new();
    public string? ImportedListLabel { get; set; }

    public List<string> BlockedKeywords { get; set; } = new();

    // Nome exato de processo (sem .exe), ex: "steam", "discord"
    public List<string> BlockedProcesses { get; set; } = new();

    // Trecho do CAMINHO COMPLETO do executável (ex: "tor browser", "duckduckgo").
    // Usado para pegar programas cujo nome de processo sozinho não ajuda
    // (ex: o Tor Browser roda como "firefox.exe" por baixo dos panos).
    public List<string> BlockedProcessPathKeywords { get; set; } = new();

    // Caminho manual do Watchdog.exe, caso o app não consiga adivinhar (ex:
    // rodando de uma pasta de build/publish em vez da instalação via Setup).
    public string? WatchdogExePathOverride { get; set; }

    // Proteções permanentes: SafeSearch (hosts + prefs nativas), DoH desligado,
    // extensão instalada, pasta protegida contra deleção. Independentes do
    // timer de bloqueio — ver PermanentProtections.cs.
    public bool PermanentProtectionsEnabled { get; set; }

    public DateTime? LockedUntilUtc { get; set; }

    // Assinatura HMAC do LockedUntilUtc acima — ver StateManager.
    public string? LockSignature { get; set; }

    public string? PasswordHash { get; set; }

    public bool IsLocked => LockedUntilUtc.HasValue && LockedUntilUtc.Value > DateTime.UtcNow;
    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);
}
