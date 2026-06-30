/*
 * Live Now web-tab inject (self-contained).
 *
 * Adds a working "Live Now" tab to the Jellyfin WEB client top bar: a button next to
 * Home/Favorites that, when clicked, shows an iframe of the Live Now warm-channel page
 * (/web/configurationpage?name=livenow).
 *
 * WHY this exists: the Custom Tabs plugin (0.2.10) on Jellyfin 10.11.8 injects only the
 * tab BUTTON, never a content PANE — clicking it shows a blank area (10.11.8's React home
 * tabs don't get the plugin's pane injection). This script does the whole job itself
 * (button + pane + show/hide), independent of Custom Tabs' broken pane logic.
 *
 * DELIVERY: injected into web/index.html by the File Transformation plugin via a single
 * search-replace transformation (anchor "</body></html>"). Custom Tabs' own tab config is
 * cleared so there is exactly one "Live Now" tab — this one. To REMOVE: delete the FT
 * transformation whose Id is "livenow-web-tab" (see web-tab/README.md).
 *
 * Self-contained, idempotent, reversible. No dependency on Custom Tabs at runtime.
 */
(function () {
  if (window.__liveNowTab) return;
  window.__liveNowTab = true;

  var LABEL = "Live Now";
  var PAGE = "/web/configurationpage?name=livenow"; // same-origin; base path handled by relative

  function onHome() {
    var h = location.hash;
    return h === "" || h === "#/home" || h === "#/home.html" ||
           h.indexOf("#/home?") === 0 || h.indexOf("#/home.html?") === 0;
  }

  // We render the page in a fixed OVERLAY appended to <body>, not as a Jellyfin
  // ".tabContent" pane. Jellyfin hides ".pageTabContent" with !important and the home
  // content is React-rendered, so a sibling pane can't be reliably shown. A self-owned
  // overlay with !important inline styles is fully under our control.
  var HIDE = "display:none !important;";
  var SHOW =
    "display:block !important;position:fixed !important;left:0 !important;right:0 !important;" +
    "bottom:0 !important;top:7.2em !important;z-index:1000 !important;background:#101010 !important;";

  function ensurePane() {
    var pane = document.querySelector("#liveNowTabPane");
    if (!pane) {
      pane = document.createElement("div");
      pane.id = "liveNowTabPane";
      pane.style.cssText = HIDE;
      pane.innerHTML =
        '<iframe id="liveNowTabFrame" ' +
        'style="position:absolute;top:0;left:0;width:100%;height:100%;border:0;" ' +
        'allow="fullscreen"></iframe>';
      document.body.appendChild(pane);
    }
    return pane;
  }

  function showLiveNow() {
    var pane = ensurePane();
    var frame = document.querySelector("#liveNowTabFrame");
    if (frame && !frame.getAttribute("src")) {
      var base = location.pathname.replace(/\/web\/.*$/, ""); // handles /jellyfin
      frame.setAttribute("src", base + PAGE);
    }
    pane.style.cssText = SHOW;
    document.querySelectorAll(".emby-tab-button").forEach(function (b) {
      b.classList.toggle("emby-tab-button-active", b.id === "liveNowTabButton");
    });
  }

  function hideLiveNow() {
    var pane = document.querySelector("#liveNowTabPane");
    if (pane) pane.style.cssText = HIDE;
    var btn = document.querySelector("#liveNowTabButton");
    if (btn) btn.classList.remove("emby-tab-button-active");
  }

  function build() {
    if (!onHome()) return;
    var slider = document.querySelector(".emby-tabs-slider");
    if (!slider || typeof ApiClient === "undefined") { return setTimeout(build, 200); }
    if (document.querySelector("#liveNowTabButton")) return; // already built

    var title = document.createElement("div");
    title.className = "emby-button-foreground";
    title.innerText = LABEL;

    var button = document.createElement("button");
    button.type = "button";
    button.setAttribute("is", "empty-button");
    button.className = "emby-tab-button emby-button";
    button.id = "liveNowTabButton";
    button.appendChild(title);
    button.addEventListener("click", function (e) {
      e.preventDefault();
      e.stopPropagation();
      showLiveNow();
    });
    slider.appendChild(button);

    // When the user clicks a NATIVE tab (Home/Favorites), hide our pane and let Jellyfin
    // show its own. Native tabs carry data-index; ours does not handle their click.
    slider.querySelectorAll(".emby-tab-button").forEach(function (b) {
      if (b.id === "liveNowTabButton") return;
      b.addEventListener("click", hideLiveNow);
    });
  }

  function init() { setTimeout(build, 300); }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
  window.addEventListener("popstate", init);
  window.addEventListener("hashchange", init);
  var _ps = history.pushState;
  history.pushState = function () { _ps.apply(history, arguments); init(); };
})();
