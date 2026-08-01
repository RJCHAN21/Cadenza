(() => {
  "use strict";

  const epsilon = 0.0001;
  const minimumGlowDurationMs = 90;
  const maximumGlowDurationMs = 6000;
  const activeClass = "cadenza-listen-glow-active";
  const stageClass = "cadenza-listen-feedback";
  const durationProperty = "--cadenza-listen-glow-duration";

  function install() {
    const api = window.CadenzaNotation;
    const stageElement = document.getElementById("stage");
    if (!api?.setCursorBeat || !api?.setPerformanceClock || !stageElement ||
        typeof performanceTimeline === "undefined" ||
        typeof timemap === "undefined") {
      setTimeout(install, 10);
      return;
    }
    if (window.__cadenzaListenHighlightPatchInstalled) return;
    window.__cadenzaListenHighlightPatchInstalled = true;

    installStyles();

    const originalSetCursorBeat = api.setCursorBeat.bind(api);
    const originalSetPerformanceClock = api.setPerformanceClock.bind(api);
    const originalGetState = api.getState?.bind(api);

    let tempoChanges = [{ performanceBeat: 0, bpm: 120 }];
    let initialBpm = 120;
    let totalPerformanceBeats = 0;
    let lastAnimatedKey = "";
    let animatedNodes = new Set();
    let pulseCount = 0;
    let lastDurationMs = 0;

    function installStyles() {
      if (document.getElementById("cadenza-listen-highlight-style")) return;
      const style = document.createElement("style");
      style.id = "cadenza-listen-highlight-style";
      style.textContent = `
        #stage.${stageClass} #notation g.note.playing,
        #stage.${stageClass} #notation g.note.playing * {
          color: #42f5ff !important;
          fill: #42f5ff !important;
          stroke: #42f5ff !important;
        }

        #stage.${stageClass} #notation g.note.playing {
          opacity: 1 !important;
          filter:
            brightness(1.22)
            drop-shadow(0 0 2px rgba(229, 254, 255, .98))
            drop-shadow(0 0 8px rgba(0, 240, 255, .96))
            drop-shadow(0 0 18px rgba(56, 189, 248, .78));
          will-change: filter, opacity;
        }

        #stage.${stageClass} #notation g.note.playing.${activeClass} {
          animation:
            cadenzaListenNoteGlow
            var(${durationProperty}, 500ms)
            cubic-bezier(.16, 1, .3, 1)
            both;
        }

        #stage.${stageClass} #notation g.syl.playing,
        #stage.${stageClass} #notation g.syl.playing *,
        #stage.${stageClass} #notation .syl.playing text,
        #stage.${stageClass} #notation .syl.playing tspan {
          color: #a9f8ff !important;
          fill: #a9f8ff !important;
          stroke: #a9f8ff !important;
          filter: drop-shadow(0 0 5px rgba(66, 245, 255, .58));
        }

        @keyframes cadenzaListenNoteGlow {
          0% {
            opacity: .96;
            filter:
              brightness(1.18)
              drop-shadow(0 0 2px rgba(229, 254, 255, .96))
              drop-shadow(0 0 7px rgba(0, 240, 255, .88))
              drop-shadow(0 0 15px rgba(56, 189, 248, .66));
          }
          14% {
            opacity: 1;
            filter:
              brightness(1.9)
              drop-shadow(0 0 4px rgba(255, 255, 255, 1))
              drop-shadow(0 0 13px rgba(0, 240, 255, 1))
              drop-shadow(0 0 27px rgba(56, 189, 248, .96));
          }
          42% {
            opacity: 1;
            filter:
              brightness(1.48)
              drop-shadow(0 0 3px rgba(229, 254, 255, .98))
              drop-shadow(0 0 10px rgba(0, 240, 255, .98))
              drop-shadow(0 0 22px rgba(56, 189, 248, .86));
          }
          100% {
            opacity: 1;
            filter:
              brightness(1.22)
              drop-shadow(0 0 2px rgba(229, 254, 255, .98))
              drop-shadow(0 0 8px rgba(0, 240, 255, .96))
              drop-shadow(0 0 18px rgba(56, 189, 248, .78));
          }
        }

        @media (prefers-reduced-motion: reduce) {
          #stage.${stageClass} #notation g.note.playing.${activeClass} {
            animation: none !important;
          }
        }
      `;
      (document.head || document.documentElement).appendChild(style);
    }

    function number(value, fallback = 0) {
      const parsed = Number(value);
      return Number.isFinite(parsed) ? parsed : fallback;
    }

    function clamp(value, minimum, maximum) {
      return Math.max(minimum, Math.min(maximum, value));
    }

    function normalizeClock(changes, totalBeats, scoreInitialBpm) {
      initialBpm = Math.max(1, number(scoreInitialBpm, 120));
      totalPerformanceBeats = Math.max(0, number(totalBeats));
      const normalized = (Array.isArray(changes) ? changes : [])
        .map(change => ({
          performanceBeat: Math.max(0, number(change?.performanceBeat)),
          bpm: Math.max(1, number(change?.bpm, initialBpm))
        }))
        .filter(change => Number.isFinite(change.performanceBeat) && Number.isFinite(change.bpm))
        .sort((left, right) => left.performanceBeat - right.performanceBeat);

      const deduplicated = [];
      for (const change of normalized) {
        const previous = deduplicated.at(-1);
        if (previous && Math.abs(previous.performanceBeat - change.performanceBeat) <= epsilon)
          deduplicated[deduplicated.length - 1] = change;
        else
          deduplicated.push(change);
      }
      if (!deduplicated.length || deduplicated[0].performanceBeat > epsilon)
        deduplicated.unshift({ performanceBeat: 0, bpm: initialBpm });
      tempoChanges = deduplicated;
    }

    function tempoScale() {
      const effectiveBpm = typeof bpm === "undefined" ? initialBpm : number(bpm, initialBpm);
      return Math.max(0.01, effectiveBpm / initialBpm);
    }

    function secondsAtPerformanceBeat(beat) {
      const target = clamp(
        Math.max(0, number(beat)),
        0,
        totalPerformanceBeats > 0 ? totalPerformanceBeats : Number.MAX_SAFE_INTEGER);
      const scale = tempoScale();
      let seconds = 0;
      let previousBeat = 0;
      let activeBpm = Math.max(1, tempoChanges[0].bpm * scale);

      for (let index = 1; index < tempoChanges.length; index++) {
        const change = tempoChanges[index];
        if (change.performanceBeat >= target - epsilon) break;
        const nextBeat = Math.max(previousBeat, change.performanceBeat);
        seconds += (nextBeat - previousBeat) * 60 / activeBpm;
        previousBeat = nextBeat;
        activeBpm = Math.max(1, change.bpm * scale);
      }
      seconds += Math.max(0, target - previousBeat) * 60 / activeBpm;
      return seconds;
    }

    function occurrenceAtPerformanceBeat(beat) {
      const value = Math.max(0, number(beat));
      if (!Array.isArray(performanceTimeline) || !performanceTimeline.length) return null;
      for (let index = 0; index < performanceTimeline.length; index++) {
        const occurrence = performanceTimeline[index];
        const start = number(occurrence?.performanceStartBeat);
        const end = start + Math.max(0, number(occurrence?.durationBeats));
        const isLast = index === performanceTimeline.length - 1;
        if (value >= start - epsilon &&
            (value < end - epsilon || (isLast && value <= end + epsilon))) {
          return occurrence;
        }
      }
      return performanceTimeline.at(-1) || null;
    }

    function sourceBeatForOccurrence(performanceBeat, occurrence) {
      if (!occurrence) return Math.max(0, number(performanceBeat));
      const performanceStart = number(occurrence.performanceStartBeat);
      const sourceStart = number(occurrence.sourceStartBeat);
      const duration = Math.max(0, number(occurrence.durationBeats));
      return sourceStart + clamp(number(performanceBeat) - performanceStart, 0, duration);
    }

    function positioned(event) {
      return Boolean(event && (event.on?.length || event.restsOn?.length || event.measureOn));
    }

    function activeNoteEvent(sourceBeat, occurrence) {
      const sourceStart = number(occurrence?.sourceStartBeat);
      const sourceEnd = sourceStart + Math.max(0, number(occurrence?.durationBeats));
      let result = null;
      for (const event of Array.isArray(timemap) ? timemap : []) {
        const qstamp = number(event?.qstamp, Number.NaN);
        if (!Number.isFinite(qstamp) || qstamp < sourceStart - epsilon || qstamp >= sourceEnd - epsilon)
          continue;
        if (qstamp > sourceBeat + epsilon) break;
        if (event.on?.length) result = event;
      }
      return result;
    }

    function nextHighlightBoundary(activeEvent, occurrence) {
      const activeBeat = number(activeEvent?.qstamp);
      const sourceStart = number(occurrence?.sourceStartBeat);
      const sourceEnd = sourceStart + Math.max(0, number(occurrence?.durationBeats));
      for (const event of Array.isArray(timemap) ? timemap : []) {
        const qstamp = number(event?.qstamp, Number.NaN);
        if (!Number.isFinite(qstamp) || qstamp < sourceStart - epsilon || qstamp > sourceEnd + epsilon)
          continue;
        if (qstamp > activeBeat + epsilon && positioned(event))
          return Math.min(sourceEnd, qstamp);
      }
      return sourceEnd;
    }

    function playingNoteNodes() {
      const result = new Set();
      for (const node of document.querySelectorAll("#notation .playing")) {
        const note = node.matches?.("g.note") ? node : node.closest?.("g.note");
        if (note) result.add(note);
      }
      return [...result];
    }

    function stableEventKey(event, occurrence, nodes) {
      const occurrenceKey = number(occurrence?.occurrenceIndex,
        Array.isArray(performanceTimeline) ? performanceTimeline.indexOf(occurrence) : 0);
      const ids = (event?.on || [])
        .map(String)
        .sort()
        .join("|");
      const nodeIds = nodes
        .map(node => node.id || node.getAttribute?.("data-id") || "")
        .filter(Boolean)
        .sort()
        .join("|");
      return `${occurrenceKey}:${number(event?.qstamp).toFixed(4)}:${ids || nodeIds}`;
    }

    function clearAnimation() {
      for (const node of animatedNodes) {
        node.classList?.remove(activeClass);
        node.style?.removeProperty?.(durationProperty);
      }
      animatedNodes = new Set();
      lastAnimatedKey = "";
    }

    function listenModeSelected() {
      return typeof lessonMode !== "undefined" && lessonMode === "Listen";
    }

    function listenPlaybackActive() {
      if (!listenModeSelected()) return false;
      const timelineActive = typeof timelineRunning !== "undefined" && Boolean(timelineRunning);
      const playbackActive = typeof playing !== "undefined" && Boolean(playing);
      return timelineActive || playbackActive;
    }

    function updateAnimatedHighlight(performanceBeat, reset) {
      const listenSelected = listenModeSelected();
      stageElement.classList.toggle(stageClass, listenSelected);
      if (!listenSelected) {
        clearAnimation();
        return;
      }

      const nodes = playingNoteNodes();
      if (reset || !listenPlaybackActive() || !nodes.length) {
        clearAnimation();
        return;
      }

      const occurrence = occurrenceAtPerformanceBeat(performanceBeat);
      const sourceBeat = sourceBeatForOccurrence(performanceBeat, occurrence);
      const event = activeNoteEvent(sourceBeat, occurrence);
      if (!occurrence || !event) {
        clearAnimation();
        return;
      }

      const key = stableEventKey(event, occurrence, nodes);
      if (key === lastAnimatedKey && nodes.every(node => animatedNodes.has(node)))
        return;

      const sourceStart = number(occurrence.sourceStartBeat);
      const performanceStart = number(occurrence.performanceStartBeat);
      const eventPerformanceBeat = performanceStart + number(event.qstamp) - sourceStart;
      const boundarySourceBeat = nextHighlightBoundary(event, occurrence);
      const boundaryPerformanceBeat = performanceStart + boundarySourceBeat - sourceStart;
      const durationMs = clamp(
        (secondsAtPerformanceBeat(boundaryPerformanceBeat) -
         secondsAtPerformanceBeat(eventPerformanceBeat)) * 1000,
        minimumGlowDurationMs,
        maximumGlowDurationMs);

      clearAnimation();
      lastAnimatedKey = key;
      lastDurationMs = durationMs;
      animatedNodes = new Set(nodes);
      for (const node of nodes) {
        node.style?.setProperty?.(durationProperty, `${durationMs.toFixed(1)}ms`);
        node.classList?.remove(activeClass);
      }
      nodes[0]?.getBoundingClientRect?.();
      for (const node of nodes)
        node.classList?.add(activeClass);
      pulseCount++;
    }

    api.setPerformanceClock = function cadenzaListenHighlightSetPerformanceClock(
      changes,
      totalBeats,
      scoreInitialBpm) {
      normalizeClock(changes, totalBeats, scoreInitialBpm);
      return originalSetPerformanceClock(changes, totalBeats, scoreInitialBpm);
    };

    api.setCursorBeat = function cadenzaListenHighlightSetCursorBeat(beat, reset = false) {
      const requestedBeat = Math.max(0, number(beat));
      const result = originalSetCursorBeat(requestedBeat, reset);
      updateAnimatedHighlight(requestedBeat, reset);
      return result;
    };

    if (originalGetState) {
      api.getState = function cadenzaListenHighlightGetState(...args) {
        const state = originalGetState(...args) || {};
        return {
          ...state,
          listenHighlight: {
            installed: true,
            listenSelected: listenModeSelected(),
            playbackActive: listenPlaybackActive(),
            activeNodeCount: animatedNodes.size,
            pulseCount,
            lastDurationMs,
            lastAnimatedKey
          }
        };
      };
    }
  }

  install();
})();