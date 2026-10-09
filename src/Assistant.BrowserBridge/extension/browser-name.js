// What the browser calls itself, for the selection request (PROJECT_SPEC §4.5, §5.7): "Microsoft Edge",
// "Google Chrome", "Brave" and so on, so the Assistant can say which browser a selection came from. The
// name is read from the browser's own brand list (`navigator.userAgentData`) or, for a browser without
// one, from its user-agent string. This module has no side effects and reads nothing but what it is given.

// Chromium-based browsers list a generic engine brand and a deliberately odd "GREASE" brand beside their
// own, so those two are skipped when looking for the browser's name.
const ENGINE_BRANDS = ["Chromium"];

// A brand is the browser's own when it is not the engine's and is made of ordinary words: the GREASE brand
// is built from punctuation ("Not A;Brand", "Not/A)Brand").
function isGrease(brand) {
  return /^not[^a-z]/i.test(brand) || /[;)(/=?_:]/.test(brand);
}

// Brands in the order a name is preferred when a browser lists several of its own (Edge and Brave both
// also list "Google Chrome").
const PREFERRED = ["Microsoft Edge", "Brave", "Opera", "Vivaldi", "Google Chrome"];

/**
 * Returns the browser's name from `navigator`-like input ({ userAgentData?: { brands }, userAgent? }), or
 * an empty string when it cannot be told.
 */
export function detectBrowserName(nav) {
  const brands = nav && nav.userAgentData && Array.isArray(nav.userAgentData.brands) ? nav.userAgentData.brands : [];
  const names = brands
    .map((entry) => (entry && typeof entry.brand === "string" ? entry.brand.trim() : ""))
    .filter((brand) => brand !== "" && !isGrease(brand));

  for (const preferred of PREFERRED) {
    if (names.includes(preferred)) {
      return preferred;
    }
  }
  const own = names.find((brand) => !ENGINE_BRANDS.includes(brand));
  if (own !== undefined) {
    return own;
  }
  if (names.includes("Chromium")) {
    return "Chromium";
  }

  const agent = nav && typeof nav.userAgent === "string" ? nav.userAgent : "";
  if (/\bEdg(e|A|iOS)?\//.test(agent)) {
    return "Microsoft Edge";
  }
  if (/\bOPR\//.test(agent)) {
    return "Opera";
  }
  if (/\bVivaldi\//.test(agent)) {
    return "Vivaldi";
  }
  if (/\bChrome\//.test(agent)) {
    return "Google Chrome";
  }
  return "";
}
