(() => {
  "use strict";

  function install() {
    if (typeof eventViewportCenter !== "function" || typeof post !== "function") {
      setTimeout(install, 10);
      return;
    }
    if (window.__cadenzaRuntimeEdgePatchInstalled) return;
    window.__cadenzaRuntimeEdgePatchInstalled = true;

    const originalEventViewportCenter = eventViewportCenter;
    eventViewportCenter = function cadenzaEventViewportCenter(event) {
      if (event?.measureOn && !event.on?.length && !event.restsOn?.length) {
        const measureElement = elementForVerovioId(event.measureOn);
        const measure = measureElement?.closest?.("g.measure") || measureElement;
        if (measure) {
          const stageRect = stage.getBoundingClientRect();
          const barlines = [...measure.querySelectorAll("g.barLine, .barLine, path[class*='barLine']")]
            .map(node => node.getBoundingClientRect())
            .filter(rect => rect.width > 0 || rect.height > 0)
            .sort((left, right) => left.left - right.left);
          const left = barlines[0]?.left ?? measure.getBoundingClientRect().left;
          return left - stageRect.left;
        }
      }
      return originalEventViewportCenter(event);
    };

    const originalPost = post;
    post = function cadenzaSafePost(type, payload = {}) {
      const safePayload = payload && typeof payload === "object" ? { ...payload } : {};
      if (Object.prototype.hasOwnProperty.call(safePayload, "mode")) {
        safePayload.mode = String(safePayload.mode || "unknown")
          .replace(/[^a-z0-9_-]/gi, "-")
          .slice(0, 40) || "unknown";
      }
      return originalPost(String(type || "unknown").slice(0, 64), safePayload);
    };
  }

  install();
})();
