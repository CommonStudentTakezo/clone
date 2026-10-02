// Roda em toda página carregada. Verifica o texto visível em busca de
// palavras-chave bloqueadas (cobre casos em que a palavra só aparece no
// conteúdo, não na URL — ex: resultados de busca carregados via JavaScript).
//
// Performance: com listas de palavras-chave grandes (centenas/milhares,
// como uma lista importada), checar keyword por keyword com .includes() em
// loop, a cada mudança de página, ficava visivelmente lento. Agora:
//  - monta UMA regex combinando todas as palavras-chave (cache, só reconstrói
//    se a lista mudar), em vez de N buscas separadas;
//  - limita quanto texto é varrido por vez (páginas muito longas não
//    precisam ser lidas inteiras pra achar uma palavra-chave perto do topo).

(function () {
  let lastCheckedLength = 0;
  let cachedRegex = null;
  let cachedKeywordsKey = null;
  const MAX_SCAN_LENGTH = 200000; // ~200k caracteres é mais que suficiente

  function escapeRegex(s) {
    return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  }

  function getKeywordRegex(keywords) {
    const key = keywords.join("\u0001"); // separador improvável de colidir
    if (cachedRegex && cachedKeywordsKey === key) return cachedRegex;

    const validKeywords = keywords.filter((k) => k && k.trim().length > 0);
    if (validKeywords.length === 0) {
      cachedRegex = null;
    } else {
      const pattern = validKeywords.map(escapeRegex).join("|");
      cachedRegex = new RegExp(pattern, "i");
    }
    cachedKeywordsKey = key;
    return cachedRegex;
  }

  function scanPage() {
    chrome.storage.local.get("ctcState", (data) => {
      const state = data?.ctcState;
      if (!state?.locked || !state.blockedKeywords?.length) return;

      const fullText = document.body?.innerText || "";
      if (fullText.length === lastCheckedLength) return;
      lastCheckedLength = fullText.length;

      const text = fullText.length > MAX_SCAN_LENGTH ? fullText.slice(0, MAX_SCAN_LENGTH) : fullText;

      const regex = getKeywordRegex(state.blockedKeywords);
      if (!regex) return;

      const match = regex.exec(text);
      if (match) {
        chrome.runtime.sendMessage({ type: "CTC_KEYWORD_FOUND", keyword: match[0] });
      }
    });
  }

  scanPage();

  // Reagenda a varredura quando a página muda dinamicamente (SPAs, resultados de busca, etc.)
  const observer = new MutationObserver(() => {
    clearTimeout(window.__ctcScanTimeout);
    window.__ctcScanTimeout = setTimeout(scanPage, 800);
  });
  observer.observe(document.documentElement, { childList: true, subtree: true });
})();
