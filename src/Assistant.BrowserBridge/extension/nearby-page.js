// The part of "Selection + Nearby Context" that runs inside the page (PROJECT_SPEC §4.5, step 88). The service worker injects
// `collectNearbyText` into the one frame the user chose the menu entry in, at that moment and no other, and it returns two
// strings: some of the page's text just before the selection and some just after it. It reads no more than that.
//
// It is injected as source text, so it must stand alone: nothing outside the function (no constant, no import, no browser API
// of the extension) may be used inside it. It runs in the extension's isolated world, so the page's own scripts cannot change
// what it calls, and it changes nothing in the page.
//
// What it reads, and what it leaves out:
//   - Only text nodes, walked outward from the selection's two ends, never the whole document. It stops at `maxChars` characters
//     on a side, and after looking at `maxNodes` nodes on a side, whichever comes first.
//   - Not what the user cannot see: script, style and template text, hidden, transparent or tiny elements, and anything the page
//     marks aria-hidden or inert.
//   - Not form controls (input, textarea, select, option, button): what is typed there is the user's own. A selection made inside
//     one gets no nearby text at all.
// The result is raw (it has the page's own characters), so the worker cleans it before it goes anywhere (nearby-context.js).

/**
 * Returns `{ before, after }`: the page's text just before and just after the current selection, each at most `maxChars`
 * characters, with a line break where the page has a new block of text. Both are empty when nothing is selected, the selection is
 * in a form control, or the page has nothing readable next to it. Never throws.
 */
export function collectNearbyText(maxChars, maxNodes) {
  const empty = { before: "", after: "" };
  try {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0 || selection.isCollapsed) {
      return empty;
    }

    const active = document.activeElement;
    if (active && (active.localName === "input" || active.localName === "textarea")) {
      return empty;
    }

    const range = selection.getRangeAt(0);

    const SKIPPED = new Set([
      "script", "style", "noscript", "template", "iframe", "frame", "object", "embed", "canvas", "svg", "video", "audio",
      "head", "textarea", "input", "select", "option", "datalist", "button",
    ]);
    const TAKE = 0;
    const ENTER = 1;
    const SKIP = 2;
    const BREAK = 3;
    const state = { visited: 0, done: false };
    const blocks = new Map();

    const styleOf = (element) => window.getComputedStyle(element);

    const isHidden = (element) => {
      if (element.hidden || element.getAttribute("aria-hidden") === "true" || element.hasAttribute("inert")) {
        return true;
      }
      const style = styleOf(element);
      return (
        style.display === "none" ||
        style.visibility === "hidden" ||
        style.visibility === "collapse" ||
        parseFloat(style.opacity) === 0 ||
        parseFloat(style.fontSize) < 2
      );
    };

    const isInline = (node) =>
      node !== null &&
      (node.nodeType === Node.TEXT_NODE ||
        (node.nodeType === Node.ELEMENT_NODE && styleOf(node).display.startsWith("inline")));

    // A text node of only spaces is a real space when it sits between two pieces of the same line (`<b>a</b> <i>b</i>`) or in
    // pre-formatted text; between blocks, or at the edge of one, it is the page's own layout and the page does not show it.
    const keepsSpace = (node) => {
      const parent = node.parentElement;
      if (parent && styleOf(parent).whiteSpace.startsWith("pre")) {
        return true;
      }
      return isInline(node.previousSibling) && isInline(node.nextSibling);
    };

    // What to do with a node met on the way out from the selection.
    const judge = (node) => {
      state.visited += 1;
      if (state.visited > maxNodes) {
        state.done = true;
        return SKIP;
      }
      if (node.nodeType === Node.TEXT_NODE) {
        return node.data.length > 0 && (/\S/.test(node.data) || keepsSpace(node)) ? TAKE : SKIP;
      }
      if (node.nodeType !== Node.ELEMENT_NODE) {
        return SKIP;
      }
      if (node.localName === "br") {
        return BREAK;
      }
      return SKIPPED.has(node.localName) || isHidden(node) ? SKIP : ENTER;
    };

    // The nearest ancestor that is a block of text rather than part of a line: text in two of them is on two lines.
    const blockOf = (start) => {
      for (let element = start; element; element = element.parentElement) {
        let inline = blocks.get(element);
        if (inline === undefined) {
          const display = styleOf(element).display;
          inline = display.startsWith("inline") || display === "contents";
          blocks.set(element, inline);
        }
        if (!inline) {
          return element;
        }
      }
      return document.documentElement;
    };

    // A line's own spaces are one space, as the page shows them, except where the page keeps them (pre-formatted text).
    const shown = (node, text) => {
      const parent = node.parentElement;
      return parent && styleOf(parent).whiteSpace.startsWith("pre") ? text : text.replace(/[ \t\r\n\f]+/g, " ");
    };

    const pieceOf = (node, text) => ({ text: shown(node, text), block: blockOf(node.parentElement) });

    function* subtree(node, forward) {
      if (state.done) {
        return;
      }
      const verdict = judge(node);
      if (verdict === TAKE) {
        yield pieceOf(node, node.data);
        return;
      }
      if (verdict === BREAK) {
        yield { text: "\n", block: null };
        return;
      }
      if (verdict !== ENTER) {
        return;
      }
      for (let child = forward ? node.firstChild : node.lastChild; child; child = forward ? child.nextSibling : child.previousSibling) {
        yield* subtree(child, forward);
        if (state.done) {
          return;
        }
      }
    }

    // Everything on one side of the point (container, offset), nearest first: the siblings on that side, then those of the
    // parent, and so on up to the page's body.
    function* outward(container, offset, forward) {
      let parent = container;
      let index = offset;
      while (parent && !state.done) {
        const children = parent.childNodes;
        if (forward) {
          for (let i = index; i < children.length && !state.done; i += 1) {
            yield* subtree(children[i], true);
          }
        } else {
          for (let i = Math.min(index, children.length) - 1; i >= 0 && !state.done; i -= 1) {
            yield* subtree(children[i], false);
          }
        }
        const up = parent.parentNode;
        if (!up || parent === document.body) {
          return;
        }
        index = Array.prototype.indexOf.call(up.childNodes, parent) + (forward ? 1 : 0);
        parent = up;
      }
    }

    const gather = (container, offset, forward) => {
      state.visited = 0;
      state.done = false;
      const pieces = [];
      let length = 0;
      const take = (piece) => {
        pieces.push(piece);
        length += piece.text.length;
        if (length >= maxChars) {
          state.done = true;
        }
      };

      let parent = container;
      let index = offset;
      if (container.nodeType === Node.TEXT_NODE) {
        // The part of the selection's own text node that is outside the selection comes first.
        const partial = forward ? container.data.slice(offset) : container.data.slice(0, offset);
        if (partial.length > 0) {
          take(pieceOf(container, partial));
        }
        parent = container.parentNode;
        index = Array.prototype.indexOf.call(parent.childNodes, container) + (forward ? 1 : 0);
      }
      for (const piece of outward(parent, index, forward)) {
        take(piece);
        if (state.done) {
          break;
        }
      }
      if (!forward) {
        pieces.reverse();
      }

      let text = "";
      let previous = null;
      for (const piece of pieces) {
        const newBlock = previous !== null && previous.block !== null && piece.block !== null && previous.block !== piece.block;
        text += (newBlock ? "\n" : "") + piece.text;
        previous = piece;
      }
      return forward ? text.slice(0, maxChars) : text.slice(Math.max(0, text.length - maxChars));
    };

    // Text next to a selection that is itself in something skipped or hidden (a form field's wrapper, a hidden panel) is not read.
    for (let element = range.commonAncestorContainer; element; element = element.parentNode) {
      if (element.nodeType === Node.ELEMENT_NODE && element !== document.body && element !== document.documentElement) {
        if (SKIPPED.has(element.localName) || isHidden(element)) {
          return empty;
        }
      }
    }

    return {
      before: gather(range.startContainer, range.startOffset, false),
      after: gather(range.endContainer, range.endOffset, true),
    };
  } catch {
    return empty;
  }
}
