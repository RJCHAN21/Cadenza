(() => {
  "use strict";

  const EPSILON = 0.0001;
  const ACTIVE = "cadenza-listen-glow-active";
  const STAGE_CLASS = "cadenza-listen-feedback";
  const LAYER_ID = "cadenza-listen-halo-layer";
  const HALO = "cadenza-listen-screen-halo";
  const OUTER = "cadenza-listen-screen-halo-outer";
  const MIDDLE = "cadenza-listen-screen-halo-middle";
  const CORE = "cadenza-listen-screen-halo-core";
  const DURATION = "--cadenza-listen-glow-duration";
  const MINIMUM_DURATION_MS = 90;
  const MAXIMUM_DURATION_MS = 6000;

  function install() {
    const api = window.CadenzaNotation;
    const stage = document.getElementById("stage");
    if (!api?.setCursorBeat || !api?.setPerformanceClock || !stage ||
        typeof performanceTimeline === "undefined" ||
        typeof timemap === "undefined") {
      setTimeout(install, 10);
      return;
    }
    if (window.__cadenzaListenHighlightPatchInstalled) return;
    window.__cadenzaListenHighlightPatchInstalled = true;

    installStyles();
    const haloLayer = ensureHaloLayer(stage);
    const originalCursor = api.setCursorBeat.bind(api);
    const originalClock = api.setPerformanceClock.bind(api);
    const originalState = api.getState?.bind(api);
    const requestFrame = window.requestAnimationFrame?.bind(window) ??
      (callback => setTimeout(() => callback(Date.now()), 16));
    const cancelFrame = window.cancelAnimationFrame?.bind(window) ?? clearTimeout;

    let clock = [{ performanceBeat: 0, bpm: 120 }];
    let scoreBpm = 120;
    let totalBeats = 0;
    let activeKey = "";
    let activeNodes = new Set();
    let haloEntries = new Map();
    let trackingFrame = 0;
    let pulseCount = 0;
    let lastDurationMs = 0;

    function installStyles() {
      if (document.getElementById("cadenza-listen-highlight-style")) return;
      const style = document.createElement("style");
      style.id = "cadenza-listen-highlight-style";
      style.textContent = `
        #stage.${STAGE_CLASS} #notation g.note.playing,
        #stage.${STAGE_CLASS} #notation g.note.playing * {
          color:#8bffff!important;
          fill:#8bffff!important;
          stroke:#8bffff!important;
        }
        #stage.${STAGE_CLASS} #notation g.note.playing {
          opacity:1!important;
          filter:brightness(2.05)
            drop-shadow(0 0 4px rgba(255,255,255,1))
            drop-shadow(0 0 14px rgba(0,244,255,1))
            drop-shadow(0 0 34px rgba(56,189,248,1));
          will-change:filter,opacity;
        }
        #stage.${STAGE_CLASS} #notation g.note.playing.${ACTIVE} {
          animation:cadenzaListenSourceFlash var(${DURATION},500ms)
            cubic-bezier(.16,1,.3,1) both;
        }
        #${LAYER_ID} {
          position:absolute;
          inset:0;
          z-index:4;
          overflow:visible;
          pointer-events:none;
          contain:layout style;
        }
        #${LAYER_ID} .${HALO} {
          position:absolute;
          left:0;
          top:0;
          border-radius:50%;
          pointer-events:none;
          transform:translate(-50%,-50%);
          transform-origin:center;
          mix-blend-mode:screen;
          will-change:left,top,width,height,opacity,transform;
        }
        #${LAYER_ID} .${OUTER} {
          opacity:.96;
          background:radial-gradient(ellipse at center,
            rgba(185,255,255,.98) 0%,
            rgba(0,248,255,.94) 15%,
            rgba(56,189,248,.72) 38%,
            rgba(0,229,255,.38) 61%,
            rgba(0,229,255,0) 82%);
          filter:blur(7px);
          box-shadow:
            0 0 24px 12px rgba(0,248,255,.96),
            0 0 58px 26px rgba(56,189,248,.76),
            0 0 108px 44px rgba(0,229,255,.48);
          animation:cadenzaListenOuterBloom var(${DURATION},500ms)
            cubic-bezier(.16,1,.3,1) both;
        }
        #${LAYER_ID} .${MIDDLE} {
          opacity:1;
          background:radial-gradient(ellipse at center,
            rgba(255,255,255,1) 0%,
            rgba(130,255,255,1) 18%,
            rgba(0,244,255,.98) 43%,
            rgba(56,189,248,.62) 67%,
            rgba(56,189,248,0) 86%);
          filter:blur(3px);
          box-shadow:
            0 0 14px 8px rgba(0,244,255,1),
            0 0 36px 18px rgba(56,189,248,.92),
            0 0 72px 30px rgba(0,229,255,.62);
          animation:cadenzaListenMiddleBloom var(${DURATION},500ms)
            cubic-bezier(.16,1,.3,1) both;
        }
        #${LAYER_ID} .${CORE} {
          opacity:1;
          background:radial-gradient(ellipse at center,
            rgba(255,255,255,1) 0%,
            rgba(221,255,255,1) 20%,
            rgba(63,250,255,.98) 48%,
            rgba(0,229,255,.72) 69%,
            rgba(0,229,255,0) 88%);
          filter:blur(.8px);
          box-shadow:
            0 0 8px 5px rgba(255,255,255,.98),
            0 0 20px 11px rgba(0,244,255,1),
            0 0 42px 18px rgba(56,189,248,.94);
          animation:cadenzaListenCoreFlash var(${DURATION},500ms)
            cubic-bezier(.16,1,.3,1) both;
        }
        #stage.${STAGE_CLASS} #notation g.syl.playing,
        #stage.${STAGE_CLASS} #notation g.syl.playing *,
        #stage.${STAGE_CLASS} #notation .syl.playing text,
        #stage.${STAGE_CLASS} #notation .syl.playing tspan {
          color:#e9ffff!important;
          fill:#e9ffff!important;
          stroke:#e9ffff!important;
          filter:brightness(1.65)
            drop-shadow(0 0 9px rgba(66,245,255,1))
            drop-shadow(0 0 22px rgba(56,189,248,.86));
        }
        @keyframes cadenzaListenSourceFlash {
          0%{filter:brightness(1.8) drop-shadow(0 0 4px #fff)
            drop-shadow(0 0 14px #00f4ff) drop-shadow(0 0 34px #38bdf8)}
          12%{filter:brightness(3.4) drop-shadow(0 0 10px #fff)
            drop-shadow(0 0 28px #00f4ff) drop-shadow(0 0 64px #38bdf8)}
          42%{filter:brightness(2.6) drop-shadow(0 0 7px #fff)
            drop-shadow(0 0 22px #00f4ff) drop-shadow(0 0 50px #38bdf8)}
          100%{filter:brightness(2.05) drop-shadow(0 0 4px #fff)
            drop-shadow(0 0 14px #00f4ff) drop-shadow(0 0 34px #38bdf8)}
        }
        @keyframes cadenzaListenOuterBloom {
          0%{opacity:.58;transform:translate(-50%,-50%) scale(.64)}
          12%{opacity:1;transform:translate(-50%,-50%) scale(1.28)}
          42%{opacity:.96;transform:translate(-50%,-50%) scale(1.08)}
          100%{opacity:.82;transform:translate(-50%,-50%) scale(1)}
        }
        @keyframes cadenzaListenMiddleBloom {
          0%{opacity:.68;transform:translate(-50%,-50%) scale(.7)}
          12%{opacity:1;transform:translate(-50%,-50%) scale(1.2)}
          42%{opacity:1;transform:translate(-50%,-50%) scale(1.05)}
          100%{opacity:.9;transform:translate(-50%,-50%) scale(1)}
        }
        @keyframes cadenzaListenCoreFlash {
          0%{opacity:.78;transform:translate(-50%,-50%) scale(.72)}
          12%{opacity:1;transform:translate(-50%,-50%) scale(1.16)}
          42%{opacity:1;transform:translate(-50%,-50%) scale(1.04)}
          100%{opacity:.94;transform:translate(-50%,-50%) scale(1)}
        }
        @media (prefers-reduced-motion:reduce) {
          #stage.${STAGE_CLASS} #notation g.note.playing.${ACTIVE},
          #${LAYER_ID} .${HALO} {
            animation:none!important;
          }
          #${LAYER_ID} .${HALO} {
            opacity:.92!important;
            transform:translate(-50%,-50%)!important;
          }
        }
      `;
      (document.head || document.documentElement).appendChild(style);
    }

    function ensureHaloLayer(stageElement) {
      const existing = document.getElementById(LAYER_ID);
      if (existing) return existing;
      const layer = document.createElement("div");
      layer.id = LAYER_ID;
      layer.setAttribute?.("aria-hidden", "true");
      stageElement.appendChild(layer);
      return layer;
    }

    const num = (value, fallback = 0) => {
      const parsed = Number(value);
      return Number.isFinite(parsed) ? parsed : fallback;
    };
    const clamp = (value, minimum, maximum) =>
      Math.max(minimum, Math.min(maximum, value));

    function normalizeClock(changes, total, initial) {
      scoreBpm = Math.max(1, num(initial, 120));
      totalBeats = Math.max(0, num(total));
      clock = (Array.isArray(changes) ? changes : [])
        .map(item => ({
          performanceBeat: Math.max(0, num(item?.performanceBeat)),
          bpm: Math.max(1, num(item?.bpm, scoreBpm))
        }))
        .filter(item => Number.isFinite(item.performanceBeat) && Number.isFinite(item.bpm))
        .sort((left, right) => left.performanceBeat - right.performanceBeat);
      if (!clock.length || clock[0].performanceBeat > EPSILON)
        clock.unshift({ performanceBeat: 0, bpm: scoreBpm });
    }

    function secondsAtBeat(beat) {
      const target = clamp(
        Math.max(0, num(beat)),
        0,
        totalBeats || Number.MAX_SAFE_INTEGER);
      const scale = Math.max(
        .01,
        num(typeof bpm === "undefined" ? scoreBpm : bpm, scoreBpm) / scoreBpm);
      let seconds = 0;
      let previous = 0;
      let activeBpm = Math.max(1, clock[0].bpm * scale);
      for (let index = 1; index < clock.length; index++) {
        const change = clock[index];
        if (change.performanceBeat >= target - EPSILON) break;
        const next = Math.max(previous, change.performanceBeat);
        seconds += (next - previous) * 60 / activeBpm;
        previous = next;
        activeBpm = Math.max(1, change.bpm * scale);
      }
      return seconds + Math.max(0, target - previous) * 60 / activeBpm;
    }

    function occurrenceAt(beat) {
      const value = Math.max(0, num(beat));
      for (let index = 0; index < performanceTimeline.length; index++) {
        const occurrence = performanceTimeline[index];
        const start = num(occurrence.performanceStartBeat);
        const end = start + Math.max(0, num(occurrence.durationBeats));
        const isLast = index === performanceTimeline.length - 1;
        if (value >= start - EPSILON &&
            (value < end - EPSILON || (isLast && value <= end + EPSILON))) {
          return occurrence;
        }
      }
      return performanceTimeline.at(-1) || null;
    }

    function sourceBeat(beat, occurrence) {
      const performanceStart = num(occurrence?.performanceStartBeat);
      const sourceStart = num(occurrence?.sourceStartBeat);
      const duration = Math.max(0, num(occurrence?.durationBeats));
      return sourceStart + clamp(num(beat) - performanceStart, 0, duration);
    }

    function activeEvent(atBeat, occurrence) {
      const start = num(occurrence?.sourceStartBeat);
      const end = start + Math.max(0, num(occurrence?.durationBeats));
      let result = null;
      for (const event of timemap) {
        const stamp = num(event?.qstamp, Number.NaN);
        if (!Number.isFinite(stamp) || stamp < start - EPSILON || stamp >= end - EPSILON)
          continue;
        if (stamp > atBeat + EPSILON) break;
        if (event.on?.length) result = event;
      }
      return result;
    }

    function nextBoundary(event, occurrence) {
      const active = num(event?.qstamp);
      const start = num(occurrence?.sourceStartBeat);
      const end = start + Math.max(0, num(occurrence?.durationBeats));
      for (const item of timemap) {
        const stamp = num(item?.qstamp, Number.NaN);
        if (!Number.isFinite(stamp) || stamp < start - EPSILON || stamp > end + EPSILON)
          continue;
        if (stamp > active + EPSILON &&
            (item.on?.length || item.restsOn?.length || item.measureOn)) {
          return Math.min(end, stamp);
        }
      }
      return end;
    }

    function playingNotes() {
      const result = new Set();
      for (const node of document.querySelectorAll("#notation .playing")) {
        const note = node.matches?.("g.note") ? node : node.closest?.("g.note");
        if (note) result.add(note);
      }
      return [...result];
    }

    function eventKey(event, occurrence, nodes) {
      const occurrenceKey = num(
        occurrence?.occurrenceIndex,
        performanceTimeline.indexOf(occurrence));
      const ids = (event?.on || []).map(String).sort().join("|") ||
        nodes
          .map(node => node.id || node.getAttribute?.("data-id") || "")
          .filter(Boolean)
          .sort()
          .join("|");
      return `${occurrenceKey}:${num(event?.qstamp).toFixed(4)}:${ids}`;
    }

    function createHalo(className, durationMs) {
      const halo = document.createElement("div");
      halo.classList.add(HALO, className);
      halo.setAttribute?.("aria-hidden", "true");
      halo.style.setProperty(DURATION, `${durationMs.toFixed(1)}ms`);
      haloLayer.appendChild(halo);
      return halo;
    }

    function visualTarget(node) {
      return node.querySelector?.(".notehead") ||
        node.querySelector?.("g.notehead") ||
        node.querySelector?.("use") ||
        node;
    }

    function writeBox(element, centerX, centerY, width, height) {
      element.style.left = `${centerX.toFixed(2)}px`;
      element.style.top = `${centerY.toFixed(2)}px`;
      element.style.width = `${width.toFixed(2)}px`;
      element.style.height = `${height.toFixed(2)}px`;
    }

    function positionEntry(entry) {
      if (entry.node?.isConnected === false) return false;
      const target = visualTarget(entry.node);
      const rect = target?.getBoundingClientRect?.();
      const stageRect = stage.getBoundingClientRect?.();
      if (!rect || !stageRect ||
          !Number.isFinite(rect.left) || !Number.isFinite(rect.top) ||
          rect.width <= 0 || rect.height <= 0) {
        return false;
      }

      const centerX = rect.left - stageRect.left + rect.width / 2;
      const centerY = rect.top - stageRect.top + rect.height / 2;
      const outerWidth = clamp(Math.max(96, rect.width * 6.8), 96, 190);
      const outerHeight = clamp(Math.max(84, rect.height * 6.2), 84, 174);
      const middleWidth = clamp(Math.max(62, rect.width * 4.2), 62, 124);
      const middleHeight = clamp(Math.max(54, rect.height * 3.8), 54, 112);
      const coreWidth = clamp(Math.max(34, rect.width * 2.6), 34, 76);
      const coreHeight = clamp(Math.max(30, rect.height * 2.4), 30, 68);

      writeBox(entry.outer, centerX, centerY, outerWidth, outerHeight);
      writeBox(entry.middle, centerX, centerY, middleWidth, middleHeight);
      writeBox(entry.core, centerX, centerY, coreWidth, coreHeight);
      return true;
    }

    function removeEntry(entry) {
      entry.outer.remove?.();
      entry.middle.remove?.();
      entry.core.remove?.();
    }

    function trackHalos() {
      trackingFrame = 0;
      if (!haloEntries.size) return;
      for (const [node, entry] of haloEntries) {
        if (!positionEntry(entry)) {
          removeEntry(entry);
          haloEntries.delete(node);
        }
      }
      if (haloEntries.size)
        trackingFrame = requestFrame(trackHalos);
    }

    function startTracking() {
      if (!trackingFrame && haloEntries.size)
        trackingFrame = requestFrame(trackHalos);
    }

    function clear() {
      if (trackingFrame) {
        cancelFrame(trackingFrame);
        trackingFrame = 0;
      }
      for (const node of activeNodes) {
        node.classList?.remove(ACTIVE);
        node.style?.removeProperty?.(DURATION);
      }
      for (const entry of haloEntries.values()) removeEntry(entry);
      activeNodes = new Set();
      haloEntries = new Map();
      activeKey = "";
    }

    function isListen() {
      return typeof lessonMode !== "undefined" && lessonMode === "Listen";
    }

    function isPlaybackActive() {
      return isListen() &&
        ((typeof timelineRunning !== "undefined" && Boolean(timelineRunning)) ||
         (typeof playing !== "undefined" && Boolean(playing)));
    }

    function update(beat, reset) {
      stage.classList.toggle(STAGE_CLASS, isListen());
      const nodes = playingNotes();
      if (!isListen() || reset || !isPlaybackActive() || !nodes.length) {
        clear();
        return;
      }

      const occurrence = occurrenceAt(beat);
      const event = activeEvent(sourceBeat(beat, occurrence), occurrence);
      if (!occurrence || !event) {
        clear();
        return;
      }

      const nextKey = eventKey(event, occurrence, nodes);
      if (nextKey === activeKey && nodes.every(node => activeNodes.has(node)))
        return;

      const sourceStart = num(occurrence.sourceStartBeat);
      const performanceStart = num(occurrence.performanceStartBeat);
      const eventBeat = performanceStart + num(event.qstamp) - sourceStart;
      const boundaryBeat = performanceStart + nextBoundary(event, occurrence) - sourceStart;
      const durationMs = clamp(
        (secondsAtBeat(boundaryBeat) - secondsAtBeat(eventBeat)) * 1000,
        MINIMUM_DURATION_MS,
        MAXIMUM_DURATION_MS);

      clear();
      activeKey = nextKey;
      lastDurationMs = durationMs;
      activeNodes = new Set(nodes);
      for (const node of nodes) {
        node.style?.setProperty?.(DURATION, `${durationMs.toFixed(1)}ms`);
        node.classList?.remove(ACTIVE);
        const entry = {
          node,
          outer: createHalo(OUTER, durationMs),
          middle: createHalo(MIDDLE, durationMs),
          core: createHalo(CORE, durationMs)
        };
        haloEntries.set(node, entry);
        positionEntry(entry);
      }
      nodes[0]?.getBoundingClientRect?.();
      for (const node of nodes) node.classList?.add(ACTIVE);
      startTracking();
      pulseCount++;
    }

    api.setPerformanceClock = function(changes, total, initial) {
      normalizeClock(changes, total, initial);
      return originalClock(changes, total, initial);
    };

    api.setCursorBeat = function(beat, reset = false) {
      const requested = Math.max(0, num(beat));
      const result = originalCursor(requested, reset);
      update(requested, reset);
      return result;
    };

    if (originalState) {
      api.getState = function(...args) {
        return {
          ...(originalState(...args) || {}),
          listenHighlight: {
            installed: true,
            renderMode: "screen-space-overlay",
            listenSelected: isListen(),
            playbackActive: isPlaybackActive(),
            activeNodeCount: activeNodes.size,
            haloNodeCount: haloEntries.size * 3,
            trackingActive: trackingFrame !== 0,
            pulseCount,
            lastDurationMs,
            lastAnimatedKey: activeKey
          }
        };
      };
    }
  }

  install();
})();
