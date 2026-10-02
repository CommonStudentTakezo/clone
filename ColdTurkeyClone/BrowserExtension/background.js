// Consulta periodicamente o app local (http://127.0.0.1:57411/state) para saber
// se o bloqueio está ativo e quais são os domínios/palavras-chave bloqueados.
// Esse endpoint é somente leitura — a extensão nunca escreve nada nele.

const STATE_URL = "http://127.0.0.1:57411/state";
const POLL_INTERVAL_MS = 4000;

let cachedState = { locked: false, blockedKeywords: [], blockedWebsites: [], permanentProtectionsEnabled: false };

async function pollState() {
  try {
    const res = await fetch(STATE_URL, { cache: "no-store" });
    if (res.ok) {
      cachedState = await res.json();
      await chrome.storage.local.set({ ctcState: cachedState });
    }
  } catch (e) {
    // App/serviço pode estar fechado — nesse caso não há o que bloquear.
    cachedState = { locked: false, blockedKeywords: [], blockedWebsites: [], permanentProtectionsEnabled: false };
    await chrome.storage.local.set({ ctcState: cachedState });
  }
}

setInterval(pollState, POLL_INTERVAL_MS);
pollState();

// Reforço de SafeSearch por parâmetro de URL. O hosts já força Google/Bing/
// YouTube por IP (mais forte, funciona pra qualquer app, não só navegador).
// Isso aqui é uma camada extra: cobre o DuckDuckGo (que não tem um jeito
// confiável de travar por IP) e serve de rede de segurança pro Google/Bing
// caso o navegador esteja com DNS-over-HTTPS ignorando o hosts.
function enforceSafeSearchParam(url) {
  if (!cachedState.permanentProtectionsEnabled) return null;

  try {
    const u = new URL(url);
    const host = u.hostname.toLowerCase();

    if (host.includes("duckduckgo.com") && u.searchParams.get("kp") !== "1") {
      u.searchParams.set("kp", "1"); // 1 = SafeSearch estrito no DuckDuckGo
      return u.toString();
    }
    if (host.includes("google.") && u.pathname.startsWith("/search") && u.searchParams.get("safe") !== "active") {
      u.searchParams.set("safe", "active");
      return u.toString();
    }
    if (host.includes("bing.com") && u.searchParams.get("adlt") !== "strict") {
      u.searchParams.set("adlt", "strict");
      return u.toString();
    }
  } catch (e) { /* URL inválida, ignora */ }

  return null;
}

function urlMatchesBlock(url) {
  if (!cachedState.locked) return false;
  const lowerUrl = url.toLowerCase();

  for (const domain of cachedState.blockedWebsites || []) {
    if (domain && lowerUrl.includes(domain.toLowerCase())) return true;
  }
  for (const keyword of cachedState.blockedKeywords || []) {
    if (keyword && lowerUrl.includes(keyword.toLowerCase())) return true;
  }
  return false;
}

chrome.webNavigation.onBeforeNavigate.addListener((details) => {
  if (details.frameId !== 0) return; // só o frame principal da aba

  if (urlMatchesBlock(details.url)) {
    chrome.tabs.update(details.tabId, {
      url: chrome.runtime.getURL("blocked.html") + "?reason=url"
    });
    return;
  }

  const safeUrl = enforceSafeSearchParam(details.url);
  if (safeUrl) {
    chrome.tabs.update(details.tabId, { url: safeUrl });
  }
});

// Recebe avisos do content script quando ele encontra uma palavra-chave no
// TEXTO da página (útil para buscas que só mostram o termo depois de carregar).
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "CTC_KEYWORD_FOUND" && sender.tab?.id) {
    chrome.tabs.update(sender.tab.id, {
      url: chrome.runtime.getURL("blocked.html") + "?reason=keyword"
    });
  }
  if (message?.type === "CTC_GET_STATE") {
    sendResponse(cachedState);
  }
  return true;
});
