(() => {
  "use strict";

  const epsilon = 0.0001;

  function install() {
    const api = window.CadenzaNotation;
    if (!api?.setCursorBeat ||
        typeof performanceTimeline === "undefined" ||
        typeof timemap === "undefined" ||
        typeof eventViewportCenter !== "function" ||
        typeof systemForEvent !== "function" ||
        typeof pageForEvent !== "function" ||
        typeof applyContinuousTransform !== "function" ||
        typeof continuousOffsetX === "undefined" ||
        typeof playhead === "undefined" || !playhead ||
        typeof stage === "undefined" || !stage) {
      setTimeout(install, 10);
      return;
    }
    if (window.__cadenzaBarBoundaryBridgeInstalled) return;
    window.__cadenzaBarBoundaryBridgeInstalled = true;

    const originalSetCursorBeat = api.setCursorBeat.bind(api);
    const originalGetState = api.getState?.bind(api);
    let appliedBridgeCount = 0;
    let lastBridge = null;

    function number(value, fallback = 0) {
      const parsed = Number(value);
      return Number.isFinite(parsed) ? parsed : fallback;
    }

    function isPlayable(event) {
      return Boolean(event && (event.on?.length || event.restsOn?.length));
    }

    function isPositioned(event) {
      return Boolean(isPlayable(event) || event?.measureOn);
    }

    function occurrenceAtPerformanceBeat(beat) {
      const value = Math.max(0, number(beat));
      if (!Array.isArray(performanceTimeline) || !performanceTimeline.length) return null;
      for (let index = 0; index < performanceTimeline.length; index++) {
        const occurrence = performanceTimeline[index];
        const start = number(occurrence.performanceStartBeat);
        const end = start + Math.max(0, number(occurrence.durationBeats));
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
      return sourceStart + Math.max(
        0,
        Math.min(duration, number(performanceBeat) - performanceStart));
    }

    function eventAtOrBefore(sourceBeat, occurrence) {
      const sourceStart = number(occurrence?.sourceStartBeat);
      const sourceEnd = sourceStart + Math.max(0, number(occurrence?.durationBeats));
      let result = null;
      for (const event of Array.isArray(timemap) ? timemap : []) {
        const qstamp = number(event?.qstamp, Number.NaN);
        if (!Number.isFinite(qstamp) ||
            qstamp < sourceStart - epsilon ||
            qstamp > sourceEnd + epsilon) {
          continue;
        }
        if (qstamp > sourceBeat + epsilon) break;
        if (!isPositioned(event)) continue;
        if (!result ||
            qstamp > number(result.qstamp) + epsilon ||
            (Math.abs(qstamp - number(result.qstamp)) <= epsilon &&
             isPlayable(event) && !isPlayable(result))) {
          result = event;
        }
      }
      return result;
    }

    function eventAfter(sourceBeat, occurrence) {
      const sourceStart = number(occurrence?.sourceStartBeat);
      const sourceEnd = sourceStart + Math.max(0, number(occurrence?.durationBeats));
      return (Array.isArray(timemap) ? timemap : []).find(event => {
        const qstamp = number(event?.qstamp, Number.NaN);
        return Number.isFinite(qstamp) &&
          isPositioned(event) &&
          qstamp >= sourceStart - epsilon &&
          qstamp > sourceBeat + epsilon &&
          qstamp < sourceEnd - epsilon;
      }) || null;
    }

    function playableEventAtOccurrenceStart(occurrence) {
      const sourceStart = number(occurrence?.sourceStartBeat);
      return (Array.isArray(timemap) ? timemap : []).find(event => {
        const qstamp = number(event?.qstamp, Number.NaN);
        return Number.isFinite(qstamp) &&
          Math.abs(qstamp - sourceStart) <= epsilon &&
          isPlayable(event);
      }) || null;
    }

    function buildBridgePlan(performanceBeat) {
      const occurrence = occurrenceAtPerformanceBeat(performanceBeat);
      if (!occurrence) return null;

      const occurrenceIndex = performanceTimeline.indexOf(occurrence);
      if (occurrenceIndex < 0 || occurrenceIndex >= performanceTimeline.length - 1)
        return null;

      const nextOccurrence = performanceTimeline[occurrenceIndex + 1];
      const sourceStart = number(occurrence.sourceStartBeat);
      const duration = Math.max(0, number(occurrence.durationBeats));
      const sourceEnd = sourceStart + duration;
      const performanceStart = number(occurrence.performanceStartBeat);
      const performanceEnd = performanceStart + duration;
      const nextSourceStart = number(nextOccurrence.sourceStartBeat);
      const nextPerformanceStart = number(nextOccurrence.performanceStartBeat);

      // Only bridge ordinary written continuation. Repeats, volta jumps, seeks,
      // and other navigation boundaries must retain their authoritative jump.
      if (Math.abs(nextSourceStart - sourceEnd) > epsilon ||
          Math.abs(nextPerformanceStart - performanceEnd) > epsilon) {
        return null;
      }

      const sourceBeat = sourceBeatForOccurrence(performanceBeat, occurrence);
      const previous = eventAtOrBefore(sourceBeat, occurrence);
      if (!previous || eventAfter(sourceBeat, occurrence)) return null;

      const nextStart = playableEventAtOccurrenceStart(nextOccurrence);
      if (!nextStart) return null;

      const currentPageValue = typeof currentPage === "undefined"
        ? pageForEvent(previous)
        : currentPage;
      if (pageForEvent(previous) !== currentPageValue ||
          pageForEvent(nextStart) !== currentPageValue) {
        return null;
      }

      const previousSystem = systemForEvent(previous);
      const nextSystem = systemForEvent(nextStart);
      if (!previousSystem || !nextSystem || previousSystem !== nextSystem)
        return null;

      const x1 = number(eventViewportCenter(previous), Number.NaN);
      const x2 = number(eventViewportCenter(nextStart), Number.NaN);
      if (!Number.isFinite(x1) || !Number.isFinite(x2) || x2 <= x1 + 1)
        return null;

      const previousBeat = number(previous.qstamp, sourceStart);
      const span = sourceEnd - previousBeat;
      if (span <= epsilon) return null;

      const progress = Math.max(0, Math.min(1, (sourceBeat - previousBeat) / span));
      return {
        occurrenceIndex,
        nextOccurrenceIndex: occurrenceIndex + 1,
        previousBeat,
        boundaryBeat: sourceEnd,
        progress,
        x1,
        x2,
        visibleX: x1 + (x2 - x1) * progress
      };
    }

    api.setCursorBeat = function cadenzaBoundaryBridgeSetCursorBeat(beat, reset = false) {
      const requestedBeat = Math.max(0, number(beat));
      const previousOffsetX = number(continuousOffsetX);
      const bridge = reset ? null : buildBridgePlan(requestedBeat);
      const result = originalSetCursorBeat(requestedBeat, reset);

      if (!bridge || (typeof pendingPage !== "undefined" && pendingPage)) {
        lastBridge = null;
        return result;
      }

      let finalVisibleX = bridge.visibleX;
      if (typeof readingMode !== "undefined" && readingMode === "Continuous") {
        const stageRect = stage.getBoundingClientRect();
        const anchorX = Math.max(180, number(stageRect?.width) * 0.32);
        let correctedOffsetX = previousOffsetX + anchorX - bridge.visibleX;
        if (typeof timelineRunning === "undefined" || timelineRunning)
          correctedOffsetX = Math.min(previousOffsetX, correctedOffsetX);

        continuousOffsetX = correctedOffsetX;
        applyContinuousTransform();
        finalVisibleX = bridge.visibleX + correctedOffsetX - previousOffsetX;
      }

      playhead.style.left = `${finalVisibleX}px`;
      appliedBridgeCount++;
      lastBridge = {
        ...bridge,
        finalVisibleX,
        mode: typeof readingMode === "undefined" ? "Unknown" : readingMode
      };
      return result;
    };

    if (originalGetState) {
      api.getState = function cadenzaBoundaryBridgeGetState(...args) {
        const state = originalGetState(...args) || {};
        return {
          ...state,
          boundaryBridge: {
            installed: true,
            appliedBridgeCount,
            lastBridge
          }
        };
      };
    }
  }

  install();
})();