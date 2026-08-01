(() => {
  "use strict";

  const EPSILON = 0.0001;
  const ACTIVE = "cadenza-listen-glow-active";
  const STAGE = "cadenza-listen-feedback";
  const HALO = "cadenza-listen-halo";
  const OUTER = "cadenza-listen-halo-outer";
  const INNER = "cadenza-listen-halo-inner";
  const DURATION = "--cadenza-listen-glow-duration";

  function install() {
    const api = window.CadenzaNotation;
    const stage = document.getElementById("stage");
    if (!api?.setCursorBeat || !api?.setPerformanceClock || !stage ||
        typeof performanceTimeline === "undefined" || typeof timemap === "undefined") {
      setTimeout(install, 10);
      return;
    }
    if (window.__cadenzaListenHighlightPatchInstalled) return;
    window.__cadenzaListenHighlightPatchInstalled = true;

    installStyles();
    const originalCursor = api.setCursorBeat.bind(api);
    const originalClock = api.setPerformanceClock.bind(api);
    const originalState = api.getState?.bind(api);

    let clock = [{ performanceBeat: 0, bpm: 120 }];
    let scoreBpm = 120;
    let totalBeats = 0;
    let key = "";
    let activeNodes = new Set();
    let haloNodes = new Set();
    let pulseCount = 0;
    let lastDurationMs = 0;

    function installStyles() {
      if (document.getElementById("cadenza-listen-highlight-style")) return;
      const style = document.createElement("style");
      style.id = "cadenza-listen-highlight-style";
      style.textContent = `
        #stage.${STAGE} #notation g.note.playing,
        #stage.${STAGE} #notation g.note.playing * {
          color:#66fbff!important;fill:#66fbff!important;stroke:#66fbff!important;
        }
        #stage.${STAGE} #notation g.note.playing {
          opacity:1!important;will-change:filter,opacity;
          filter:brightness(1.45)
            drop-shadow(0 0 3px rgba(255,255,255,1))
            drop-shadow(0 0 12px rgba(0,244,255,1))
            drop-shadow(0 0 27px rgba(56,189,248,.98))
            drop-shadow(0 0 48px rgba(0,229,255,.86));
        }
        #stage.${STAGE} #notation g.note.playing.${ACTIVE} {
          animation:cadenzaListenNoteGlow var(${DURATION},500ms) cubic-bezier(.16,1,.3,1) both;
        }
        #stage.${STAGE} #notation .${HALO} {
          pointer-events:none!important;mix-blend-mode:screen;transform-box:fill-box;
          transform-origin:center;will-change:filter,opacity;
        }
        #stage.${STAGE} #notation .${HALO},
        #stage.${STAGE} #notation .${HALO} * {
          color:#00efff!important;fill:#00efff!important;stroke:#00efff!important;
        }
        #stage.${STAGE} #notation .${OUTER} {
          opacity:.78;filter:blur(4.6px) brightness(1.8)
            drop-shadow(0 0 16px rgba(0,244,255,1))
            drop-shadow(0 0 36px rgba(56,189,248,1))
            drop-shadow(0 0 66px rgba(0,229,255,.94));
          animation:cadenzaListenOuterHalo var(${DURATION},500ms) cubic-bezier(.16,1,.3,1) both;
        }
        #stage.${STAGE} #notation .${INNER} {
          opacity:.98;filter:blur(1.5px) brightness(2.2)
            drop-shadow(0 0 8px rgba(255,255,255,1))
            drop-shadow(0 0 22px rgba(0,244,255,1))
            drop-shadow(0 0 42px rgba(56,189,248,1));
          animation:cadenzaListenInnerHalo var(${DURATION},500ms) cubic-bezier(.16,1,.3,1) both;
        }
        #stage.${STAGE} #notation g.syl.playing,
        #stage.${STAGE} #notation g.syl.playing *,
        #stage.${STAGE} #notation .syl.playing text,
        #stage.${STAGE} #notation .syl.playing tspan {
          color:#d5fdff!important;fill:#d5fdff!important;stroke:#d5fdff!important;
          filter:brightness(1.3) drop-shadow(0 0 8px rgba(66,245,255,.92))
            drop-shadow(0 0 18px rgba(56,189,248,.68));
        }
        @keyframes cadenzaListenNoteGlow {
          0%{filter:brightness(1.4) drop-shadow(0 0 3px #fff)
            drop-shadow(0 0 12px #00f4ff) drop-shadow(0 0 27px rgba(56,189,248,.98))
            drop-shadow(0 0 48px rgba(0,229,255,.86));}
          12%{filter:brightness(2.7) drop-shadow(0 0 8px #fff)
            drop-shadow(0 0 24px #00f4ff) drop-shadow(0 0 50px #38bdf8)
            drop-shadow(0 0 86px rgba(0,229,255,1));}
          40%{filter:brightness(2.05) drop-shadow(0 0 6px #fff)
            drop-shadow(0 0 18px #00f4ff) drop-shadow(0 0 40px #38bdf8)
            drop-shadow(0 0 70px rgba(0,229,255,.94));}
          100%{filter:brightness(1.45) drop-shadow(0 0 3px #fff)
            drop-shadow(0 0 12px #00f4ff) drop-shadow(0 0 27px rgba(56,189,248,.98))
            drop-shadow(0 0 48px rgba(0,229,255,.86));}
        }
        @keyframes cadenzaListenOuterHalo {0%{opacity:.58}12%{opacity:1}40%{opacity:.92}100%{opacity:.72}}
        @keyframes cadenzaListenInnerHalo {0%{opacity:.8}12%{opacity:1}40%{opacity:.98}100%{opacity:.88}}
        @media (prefers-reduced-motion:reduce) {
          #stage.${STAGE} #notation g.note.playing.${ACTIVE},
          #stage.${STAGE} #notation .${HALO}{animation:none!important}
        }
      `;
      (document.head || document.documentElement).appendChild(style);
    }

    const num = (value, fallback = 0) => {
      const parsed = Number(value);
      return Number.isFinite(parsed) ? parsed : fallback;
    };
    const clamp = (value, minimum, maximum) => Math.max(minimum, Math.min(maximum, value));

    function normalizeClock(changes, total, initial) {
      scoreBpm = Math.max(1, num(initial, 120));
      totalBeats = Math.max(0, num(total));
      clock = (Array.isArray(changes) ? changes : [])
        .map(item => ({
          performanceBeat: Math.max(0, num(item?.performanceBeat)),
          bpm: Math.max(1, num(item?.bpm, scoreBpm))
        }))
        .filter(item => Number.isFinite(item.performanceBeat) && Number.isFinite(item.bpm))
        .sort((a, b) => a.performanceBeat - b.performanceBeat);
      if (!clock.length || clock[0].performanceBeat > EPSILON)
        clock.unshift({ performanceBeat: 0, bpm: scoreBpm });
    }

    function secondsAtBeat(beat) {
      const target = clamp(Math.max(0, num(beat)), 0, totalBeats || Number.MAX_SAFE_INTEGER);
      const scale = Math.max(.01, num(typeof bpm === "undefined" ? scoreBpm : bpm, scoreBpm) / scoreBpm);
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
        if (value >= start - EPSILON &&
            (value < end - EPSILON || (index === performanceTimeline.length - 1 && value <= end + EPSILON)))
          return occurrence;
      }
      return performanceTimeline.at(-1) || null;
    }

    function sourceBeat(beat, occurrence) {
      const start = num(occurrence?.performanceStartBeat);
      const source = num(occurrence?.sourceStartBeat);
      return source + clamp(num(beat) - start, 0, Math.max(0, num(occurrence?.durationBeats)));
    }

    function activeEvent(atBeat, occurrence) {
      const start = num(occurrence?.sourceStartBeat);
      const end = start + Math.max(0, num(occurrence?.durationBeats));
      let result = null;
      for (const event of timemap) {
        const stamp = num(event?.qstamp, Number.NaN);
        if (!Number.isFinite(stamp) || stamp < start - EPSILON || stamp >= end - EPSILON) continue;
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
        if (!Number.isFinite(stamp) || stamp < start - EPSILON || stamp > end + EPSILON) continue;
        if (stamp > active + EPSILON && (item.on?.length || item.restsOn?.length || item.measureOn))
          return Math.min(end, stamp);
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
      const occurrenceKey = num(occurrence?.occurrenceIndex, performanceTimeline.indexOf(occurrence));
      const ids = (event?.on || []).map(String).sort().join("|") ||
        nodes.map(node => node.id || node.getAttribute?.("data-id") || "").filter(Boolean).sort().join("|");
      return `${occurrenceKey}:${num(event?.qstamp).toFixed(4)}:${ids}`;
    }

    function stripIdentity(node) {
      node.removeAttribute?.("id");
      node.removeAttribute?.("data-id");
      node.setAttribute?.("aria-hidden", "true");
      for (const child of node.querySelectorAll?.("[id], [data-id]") || []) {
        child.removeAttribute?.("id");
        child.removeAttribute?.("data-id");
      }
    }

    function cloneHalo(source, className, durationMs) {
      if (!source?.parentNode || typeof source.cloneNode !== "function") return null;
      const clone = source.cloneNode(true);
      stripIdentity(clone);
      clone.classList?.remove("playing", ACTIVE);
      clone.classList?.add(HALO, className);
      clone.style?.setProperty?.(DURATION, `${durationMs.toFixed(1)}ms`);
      source.parentNode.insertBefore?.(clone, source);
      return clone;
    }

    function clear() {
      for (const node of activeNodes) {
        node.classList?.remove(ACTIVE);
        node.style?.removeProperty?.(DURATION);
      }
      for (const halo of haloNodes) halo.remove?.();
      activeNodes = new Set();
      haloNodes = new Set();
      key = "";
    }

    function isListen() {
      return typeof lessonMode !== "undefined" && lessonMode === "Listen";
    }

    function isPlaying() {
      return isListen() &&
        ((typeof timelineRunning !== "undefined" && Boolean(timelineRunning)) ||
         (typeof playing !== "undefined" && Boolean(playing)));
    }

    function update(beat, reset) {
      stage.classList.toggle(STAGE, isListen());
      const nodes = playingNotes();
      if (!isListen() || reset || !isPlaying() || !nodes.length) {
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
      if (nextKey === key && nodes.every(node => activeNodes.has(node))) return;

      const sourceStart = num(occurrence.sourceStartBeat);
      const performanceStart = num(occurrence.performanceStartBeat);
      const eventBeat = performanceStart + num(event.qstamp) - sourceStart;
      const boundaryBeat = performanceStart + nextBoundary(event, occurrence) - sourceStart;
      const durationMs = clamp((secondsAtBeat(boundaryBeat) - secondsAtBeat(eventBeat)) * 1000, 90, 6000);

      clear();
      key = nextKey;
      lastDurationMs = durationMs;
      activeNodes = new Set(nodes);
      for (const node of nodes) {
        node.style?.setProperty?.(DURATION, `${durationMs.toFixed(1)}ms`);
        node.classList?.remove(ACTIVE);
        const outer = cloneHalo(node, OUTER, durationMs);
        const inner = cloneHalo(node, INNER, durationMs);
        if (outer) haloNodes.add(outer);
        if (inner) haloNodes.add(inner);
      }
      nodes[0]?.getBoundingClientRect?.();
      for (const node of nodes) node.classList?.add(ACTIVE);
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
            listenSelected: isListen(),
            playbackActive: isPlaying(),
            activeNodeCount: activeNodes.size,
            haloNodeCount: haloNodes.size,
            pulseCount,
            lastDurationMs,
            lastAnimatedKey: key
          }
        };
      };
    }
  }

  install();
})();
