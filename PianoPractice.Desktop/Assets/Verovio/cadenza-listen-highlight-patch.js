(() => {
  "use strict";

  const EPSILON = 0.0001;
  const ACTIVE_CLASS = "cadenza-listen-glow-active";
  const STAGE_CLASS = "cadenza-listen-feedback";
  const DURATION_PROPERTY = "--cadenza-listen-glow-duration";
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

    const originalCursor = api.setCursorBeat.bind(api);
    const originalClock = api.setPerformanceClock.bind(api);
    const originalState = api.getState?.bind(api);

    let clock = [{ performanceBeat: 0, bpm: 120 }];
    let scoreBpm = 120;
    let totalBeats = 0;
    let activeKey = "";
    let activeNotes = new Set();
    let pulseCount = 0;
    let lastDurationMs = 0;

    function installStyles() {
      if (document.getElementById("cadenza-listen-highlight-style")) return;
      const style = document.createElement("style");
      style.id = "cadenza-listen-highlight-style";
      style.textContent = `
        /* Keep the glow confined to noteheads. Applying a filter to the full
           g.note group also includes stems, beams, flags, ledger lines, and
           neighbouring geometry in the SVG filter bounds, causing artifacts. */
        #stage.${STAGE_CLASS} #notation g.note.playing .notehead,
        #stage.${STAGE_CLASS} #notation .notehead.playing {
          color:#73fbff!important;
          fill:#73fbff!important;
          stroke:#73fbff!important;
          opacity:1!important;
          filter:
            brightness(1.72)
            drop-shadow(0 0 2px rgba(255,255,255,1))
            drop-shadow(0 0 7px rgba(0,244,255,1))
            drop-shadow(0 0 15px rgba(56,189,248,.96))
            drop-shadow(0 0 23px rgba(0,229,255,.58));
          will-change:filter,opacity;
        }
        #stage.${STAGE_CLASS} #notation g.note.playing .notehead *,
        #stage.${STAGE_CLASS} #notation .notehead.playing * {
          color:#73fbff!important;
          fill:#73fbff!important;
          stroke:#73fbff!important;
        }
        #stage.${STAGE_CLASS} #notation g.note.playing.${ACTIVE_CLASS} .notehead,
        #stage.${STAGE_CLASS} #notation .notehead.playing.${ACTIVE_CLASS} {
          animation:cadenzaListenNoteheadGlow
            var(${DURATION_PROPERTY},500ms)
            cubic-bezier(.16,1,.3,1) both;
        }

        /* Lyrics are intentionally included. Their glow is independent from
           note geometry, so it remains readable without lighting stems/beams. */
        #stage.${STAGE_CLASS} #notation g.note.playing .syl,
        #stage.${STAGE_CLASS} #notation g.syl.playing,
        #stage.${STAGE_CLASS} #notation .syl.playing {
          color:#c8fdff!important;
          fill:#c8fdff!important;
          stroke:#c8fdff!important;
          opacity:1!important;
          filter:
            brightness(1.5)
            drop-shadow(0 0 3px rgba(225,255,255,1))
            drop-shadow(0 0 8px rgba(0,244,255,.98))
            drop-shadow(0 0 16px rgba(56,189,248,.72));
          animation:cadenzaListenLyricGlow
            var(${DURATION_PROPERTY},500ms)
            cubic-bezier(.16,1,.3,1) both;
          will-change:filter,opacity;
        }
        #stage.${STAGE_CLASS} #notation g.note.playing .syl text,
        #stage.${STAGE_CLASS} #notation g.note.playing .syl tspan,
        #stage.${STAGE_CLASS} #notation g.syl.playing text,
        #stage.${STAGE_CLASS} #notation g.syl.playing tspan,
        #stage.${STAGE_CLASS} #notation .syl.playing text,
        #stage.${STAGE_CLASS} #notation .syl.playing tspan {
          color:#c8fdff!important;
          fill:#c8fdff!important;
          stroke:#c8fdff!important;
        }

        @keyframes cadenzaListenNoteheadGlow {
          0% {
            filter:
              brightness(1.55)
              drop-shadow(0 0 2px rgba(255,255,255,1))
              drop-shadow(0 0 6px rgba(0,244,255,.98))
              drop-shadow(0 0 13px rgba(56,189,248,.88))
              drop-shadow(0 0 20px rgba(0,229,255,.48));
          }
          11% {
            filter:
              brightness(2.75)
              drop-shadow(0 0 4px rgba(255,255,255,1))
              drop-shadow(0 0 11px rgba(0,244,255,1))
              drop-shadow(0 0 21px rgba(56,189,248,1))
              drop-shadow(0 0 30px rgba(0,229,255,.78));
          }
          38% {
            filter:
              brightness(2.15)
              drop-shadow(0 0 3px rgba(255,255,255,1))
              drop-shadow(0 0 9px rgba(0,244,255,1))
              drop-shadow(0 0 18px rgba(56,189,248,.98))
              drop-shadow(0 0 26px rgba(0,229,255,.66));
          }
          100% {
            filter:
              brightness(1.72)
              drop-shadow(0 0 2px rgba(255,255,255,1))
              drop-shadow(0 0 7px rgba(0,244,255,1))
              drop-shadow(0 0 15px rgba(56,189,248,.96))
              drop-shadow(0 0 23px rgba(0,229,255,.58));
          }
        }
        @keyframes cadenzaListenLyricGlow {
          0% {
            filter:
              brightness(1.35)
              drop-shadow(0 0 2px rgba(225,255,255,.96))
              drop-shadow(0 0 6px rgba(0,244,255,.84))
              drop-shadow(0 0 12px rgba(56,189,248,.55));
          }
          11% {
            filter:
              brightness(2.15)
              drop-shadow(0 0 4px rgba(255,255,255,1))
              drop-shadow(0 0 11px rgba(0,244,255,1))
              drop-shadow(0 0 21px rgba(56,189,248,.88));
          }
          38% {
            filter:
              brightness(1.75)
              drop-shadow(0 0 3px rgba(240,255,255,1))
              drop-shadow(0 0 9px rgba(0,244,255,.98))
              drop-shadow(0 0 18px rgba(56,189,248,.76));
          }
          100% {
            filter:
              brightness(1.5)
              drop-shadow(0 0 3px rgba(225,255,255,1))
              drop-shadow(0 0 8px rgba(0,244,255,.98))
              drop-shadow(0 0 16px rgba(56,189,248,.72));
          }
        }
        @media (prefers-reduced-motion:reduce) {
          #stage.${STAGE_CLASS} #notation g.note.playing.${ACTIVE_CLASS} .notehead,
          #stage.${STAGE_CLASS} #notation .notehead.playing.${ACTIVE_CLASS},
          #stage.${STAGE_CLASS} #notation g.note.playing .syl,
          #stage.${STAGE_CLASS} #notation g.syl.playing,
          #stage.${STAGE_CLASS} #notation .syl.playing {
            animation:none!important;
          }
        }
      `;
      (document.head || document.documentElement).appendChild(style);
    }

    const number = (value, fallback = 0) => {
      const parsed = Number(value);
      return Number.isFinite(parsed) ? parsed : fallback;
    };
    const clamp = (value, minimum, maximum) =>
      Math.max(minimum, Math.min(maximum, value));

    function normalizeClock(changes, total, initial) {
      scoreBpm = Math.max(1, number(initial, 120));
      totalBeats = Math.max(0, number(total));
      clock = (Array.isArray(changes) ? changes : [])
        .map(item => ({
          performanceBeat: Math.max(0, number(item?.performanceBeat)),
          bpm: Math.max(1, number(item?.bpm, scoreBpm))
        }))
        .filter(item => Number.isFinite(item.performanceBeat) && Number.isFinite(item.bpm))
        .sort((left, right) => left.performanceBeat - right.performanceBeat);
      if (!clock.length || clock[0].performanceBeat > EPSILON)
        clock.unshift({ performanceBeat: 0, bpm: scoreBpm });
    }

    function secondsAtBeat(beat) {
      const target = clamp(
        Math.max(0, number(beat)),
        0,
        totalBeats || Number.MAX_SAFE_INTEGER);
      const scale = Math.max(
        .01,
        number(typeof bpm === "undefined" ? scoreBpm : bpm, scoreBpm) / scoreBpm);
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
      const value = Math.max(0, number(beat));
      for (let index = 0; index < performanceTimeline.length; index++) {
        const occurrence = performanceTimeline[index];
        const start = number(occurrence.performanceStartBeat);
        const end = start + Math.max(0, number(occurrence.durationBeats));
        const isLast = index === performanceTimeline.length - 1;
        if (value >= start - EPSILON &&
            (value < end - EPSILON || (isLast && value <= end + EPSILON))) {
          return occurrence;
        }
      }
      return performanceTimeline.at(-1) || null;
    }

    function sourceBeat(beat, occurrence) {
      const performanceStart = number(occurrence?.performanceStartBeat);
      const sourceStart = number(occurrence?.sourceStartBeat);
      const duration = Math.max(0, number(occurrence?.durationBeats));
      return sourceStart + clamp(number(beat) - performanceStart, 0, duration);
    }

    function activeEvent(atBeat, occurrence) {
      const start = number(occurrence?.sourceStartBeat);
      const end = start + Math.max(0, number(occurrence?.durationBeats));
      let result = null;
      for (const event of timemap) {
        const stamp = number(event?.qstamp, Number.NaN);
        if (!Number.isFinite(stamp) || stamp < start - EPSILON || stamp >= end - EPSILON)
          continue;
        if (stamp > atBeat + EPSILON) break;
        if (event.on?.length) result = event;
      }
      return result;
    }

    function nextBoundary(event, occurrence) {
      const active = number(event?.qstamp);
      const start = number(occurrence?.sourceStartBeat);
      const end = start + Math.max(0, number(occurrence?.durationBeats));
      for (const item of timemap) {
        const stamp = number(item?.qstamp, Number.NaN);
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
      const occurrenceKey = number(
        occurrence?.occurrenceIndex,
        performanceTimeline.indexOf(occurrence));
      const ids = (event?.on || []).map(String).sort().join("|") ||
        nodes
          .map(node => node.id || node.getAttribute?.("data-id") || "")
          .filter(Boolean)
          .sort()
          .join("|");
      return `${occurrenceKey}:${number(event?.qstamp).toFixed(4)}:${ids}`;
    }

    function clear() {
      for (const node of activeNotes)
        node.classList?.remove(ACTIVE_CLASS);
      activeNotes = new Set();
      activeKey = "";
      stage.style?.removeProperty?.(DURATION_PROPERTY);
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
      if (nextKey === activeKey && nodes.every(node => activeNotes.has(node)))
        return;

      const sourceStart = number(occurrence.sourceStartBeat);
      const performanceStart = number(occurrence.performanceStartBeat);
      const eventBeat = performanceStart + number(event.qstamp) - sourceStart;
      const boundaryBeat = performanceStart + nextBoundary(event, occurrence) - sourceStart;
      const durationMs = clamp(
        (secondsAtBeat(boundaryBeat) - secondsAtBeat(eventBeat)) * 1000,
        MINIMUM_DURATION_MS,
        MAXIMUM_DURATION_MS);

      clear();
      activeKey = nextKey;
      lastDurationMs = durationMs;
      activeNotes = new Set(nodes);
      stage.style?.setProperty?.(DURATION_PROPERTY, `${durationMs.toFixed(1)}ms`);

      for (const node of nodes)
        node.classList?.remove(ACTIVE_CLASS);
      nodes[0]?.getBoundingClientRect?.();
      for (const node of nodes)
        node.classList?.add(ACTIVE_CLASS);
      pulseCount++;
    }

    api.setPerformanceClock = function(changes, total, initial) {
      normalizeClock(changes, total, initial);
      return originalClock(changes, total, initial);
    };

    api.setCursorBeat = function(beat, reset = false) {
      const requested = Math.max(0, number(beat));
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
            renderMode: "contained-svg-notehead-and-lyrics",
            listenSelected: isListen(),
            playbackActive: isPlaybackActive(),
            activeNodeCount: activeNotes.size,
            haloNodeCount: 0,
            pulseCount,
            lastDurationMs,
            lastAnimatedKey: activeKey,
            artifactsContained: true,
            lyricsIncluded: true
          }
        };
      };
    }
  }

  install();
})();
