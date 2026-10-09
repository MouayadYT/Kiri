// The options page's behavior (PROJECT_SPEC §4.5, steps 88 and 90): shows whether "Selection + Nearby Context" is in force and lets the user
// choose between it and "Selection Only". Choosing the nearby mode makes the browser ask for the optional permission it needs, so the
// request is the first thing the click handler does. Every word on the page comes from _locales/en/messages.json.

import { MODE_NEARBY, chooseContextMode, onContextModeChanged, readContextMode } from "./settings.js";

for (const element of document.querySelectorAll("[data-i18n]")) {
  element.textContent = chrome.i18n.getMessage(element.dataset.i18n);
}
document.title = chrome.i18n.getMessage("optionsTitle");
document.documentElement.lang = chrome.i18n.getUILanguage();

const status = document.getElementById("status");
const choices = [...document.querySelectorAll('input[name="contextMode"]')];

function show(mode) {
  for (const choice of choices) {
    choice.checked = choice.value === mode;
  }
}

for (const choice of choices) {
  choice.addEventListener("change", async () => {
    try {
      // Nothing is waited for before this call: the browser only shows its permission prompt for a call made inside the user click.
      const wanted = choice.value;
      const result = await chooseContextMode(wanted);
      show(result);
      status.textContent = chrome.i18n.getMessage(
        wanted === MODE_NEARBY && result !== MODE_NEARBY ? "optionsNotAllowed" : "optionsSaved",
      );
    } catch {
      show(await readContextMode());
      status.textContent = chrome.i18n.getMessage("optionsNotSaved");
    }
  });
}

show(await readContextMode());

// The permission can also be given or taken back in the browser's own extension settings: the page follows.
onContextModeChanged(async () => show(await readContextMode()));
