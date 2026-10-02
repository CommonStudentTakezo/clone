namespace ColdTurkeyClone.Services;

/// <summary>
/// Domínios de busca mapeados para os IPs "VIP" oficiais que forçam
/// SafeSearch / Modo Restrito, documentados pelo próprio Google
/// (support.google.com/websearch/answer/186669 para busca,
/// support.google.com/a/answer/6214622 para o YouTube) e usados de forma
/// equivalente para o Bing. Verificado em setembro de 2026.
///
/// Esses IPs raramente mudam (são endereços dedicados do Google/Microsoft
/// para essa finalidade específica), mas se o SafeSearch parar de ser
/// aplicado depois de algum tempo, confirme rodando em um prompt:
///   nslookup forcesafesearch.google.com
///   nslookup restrict.youtube.com
/// e atualize as constantes abaixo se o valor retornado for diferente.
/// </summary>
public static class SafeSearchDomains
{
    private const string GoogleSafeSearchIp = "216.239.38.120";
    private const string YoutubeStrictIp = "216.239.38.120";   // Modo Restrito ESTRITO
    private const string BingSafeSearchIp = "204.79.197.220";

    // Adicione outros domínios de país do Google aqui se você usar, ex: google.co.jp.
    private static readonly string[] GoogleDomains =
    {
        "google.com", "www.google.com",
        "google.com.br", "www.google.com.br",
        "google.co.uk", "www.google.co.uk",
        "google.ca", "www.google.ca",
        "google.com.au", "www.google.com.au",
        "google.de", "www.google.de",
        "google.fr", "www.google.fr",
        "google.es", "www.google.es",
        "google.com.mx", "www.google.com.mx",
        "google.co.in", "www.google.co.in",
    };

    // Lista oficial de hostnames que o Google recomenda redirecionar juntos.
    private static readonly string[] YoutubeDomains =
    {
        "www.youtube.com", "m.youtube.com",
        "youtubei.googleapis.com", "youtube.googleapis.com",
        "www.youtube-nocookie.com",
    };

    private static readonly string[] BingDomains =
    {
        "www.bing.com", "bing.com", "www2.bing.com", "www3.bing.com",
    };

    public static IEnumerable<(string Domain, string Ip)> GetHostsEntries()
    {
        foreach (var d in GoogleDomains) yield return (d, GoogleSafeSearchIp);
        foreach (var d in YoutubeDomains) yield return (d, YoutubeStrictIp);
        foreach (var d in BingDomains) yield return (d, BingSafeSearchIp);
    }
}
