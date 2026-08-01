(() => {
  "use strict";

  const EPSILON = 0.0001;
  const X_TOLERANCE = 1.5;

  function install() {
    const api = window.CadenzaNotation;
    if (!api?.setCursorBeat ||
        typeof performanceTimeline === "undefined" ||
        typeof timemap === "undefined" ||
        typeof stage === "undefined" || !stage ||
        typeof notation === "undefined" || !notation ||
        typeof playhead === "undefined" || !playhead ||
        typeof applyContinuousTransform !== "function") {
      setTimeout(install, 10);
      return;
    }
    if (window.__cadenzaPlayablePositionPatchInstalled) return;
    window.__cadenzaPlayablePositionPatchInstalled = true;

    const originalSetCursorBeat = api.setCursorBeat.bind(api);
    const originalGetState = api.getState?.bind(api);

    let restGeometryFallbackCount = 0;
    let preventedBackwardCount = 0;
    let unresolvedPlayableCount = 0;
    let lastPerformanceBeat = Number.NaN;
    let lastOccurrenceIndex = -1;
    let lastOccurrence = null;
    let lastSystem = null;
    let lastScoreX = Number.NaN;
    let lastTarget = null;

    const number = (value, fallback = 0) => {
      const parsed = Number(value);
      return Number.isFinite(parsed) ? parsed : fallback;
    };

    function isPlayable(event) {
      return Boolean(event && (event.on?.length || event.restsOn?.length));
    }

    function occurrenceAt(performanceBeat) {
      const value = Math.max(0, number(performanceBeat));
      if (!Array.isArray(performanceTimeline) || !performanceTimeline.length) return null;
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

    function sourceBeatFor(performanceBeat, occurrence) {
      if (!occurrence) return Math.max(0, number(performanceBeat));
      const performanceStart = number(occurrence.performanceStartBeat);
      const sourceStart = number(occurrence.sourceStartBeat);
      const duration = Math.max(0, number(occurrence.durationBeats));
      return sourceStart + Math.max(
        0,
        Math.min(duration, number(performanceBeat) - performanceStart));
    }

    function eventsForOccurrence(occurrence) {
      if (!occurrence) return [];
      const start = number(occurrence.sourceStartBeat);
      const end = start + Math.max(0, number(occurrence.durationBeats));
      return (Array.isArray(timemap) ? timemap : []).filter(event => {
        const stamp = number(event?.qstamp, Number.NaN);
        return Number.isFinite(stamp) &&
          stamp >= start - EPSILON &&
          stamp < end - EPSILON;
      });
    }

    function playableGroupAtOrBefore(sourceBeat, occurrence) {
      let stamp = Number.NEGATIVE_INFINITY;
      let group = [];
      for (const event of eventsForOccurrence(occurrence)) {
        const eventStamp = number(event?.qstamp, Number.NaN);
        if (!Number.isFinite(eventStamp) || eventStamp > sourceBeat + EPSILON) break;
        if (!isPlayable(event)) continue;
        if (eventStamp > stamp + EPSILON) {
          stamp = eventStamp;
          group = [event];
        } else if (Math.abs(eventStamp - stamp) <= EPSILON) {
          group.push(event);
        }
      }
      return group;
    }

    function playableGroupAfter(sourceBeat, occurrence) {
      let stamp = Number.POSITIVE_INFINITY;
      const group = [];
      for (const event of eventsForOccurrence(occurrence)) {
        const eventStamp = number(event?.qstamp, Number.NaN);
        if (!Number.isFinite(eventStamp) || eventStamp <= sourceBeat + EPSILON || !isPlayable(event))
          continue;
        if (eventStamp < stamp - EPSILON) {
          stamp = eventStamp;
          group.length = 0;
          group.push(event);
        } else if (Math.abs(eventStamp - stamp) <= EPSILON) {
          group.push(event);
        } else if (eventStamp > stamp + EPSILON) {
          break;
        }
      }
      return group;
    }

    function groupStamp(group, fallback) {
      return group.length
        ? number(group[0]?.qstamp, fallback)
        : fallback;
    }

    function exactElement(id) {
      if (!id) return null;
      const key = String(id);
      return document.getElementById(key) ||
        notation.querySelector?.(`[data-id="${CSS.escape(key)}"]`) || null;
    }

    function occurrenceStartEvent(occurrence) {
      const sourceStart = number(occurrence?.sourceStartBeat);
      return (Array.isArray(timemap) ? timemap : []).find(event =>
        event?.measureOn &&
        Math.abs(number(event.qstamp, Number.NaN) - sourceStart) <= EPSILON) || null;
    }

    function measureFor(occurrence) {
      const startEvent = occurrenceStartEvent(occurrence);
      const exact = exactElement(startEvent?.measureOn);
      const exactMeasure = exact?.closest?.("g.measure") ||
        (exact?.matches?.("g.measure") ? exact : null);
      if (exactMeasure) return exactMeasure;

      const measureNumber = String(
        occurrence?.measureNumber ?? occurrence?.measure ?? occurrence?.sourceMeasureNumber ?? "");
      if (measureNumber) {
        const escaped = CSS.escape(measureNumber);
        const byNumber = notation.querySelector?.(
          `g.measure[data-n="${escaped}"], g.measure[n="${escaped}"]`);
        if (byNumber) return byNumber;
      }
      return null;
    }

    function centerX(element) {
      const rect = element?.getBoundingClientRect?.();
      const stageRect = stage.getBoundingClientRect?.();
      if (!rect || !stageRect || !Number.isFinite(rect.left) || rect.width < 0) return null;
      return rect.left + rect.width / 2 - stageRect.left;
    }

    function measureStartX(measure) {
      const rect = measure?.getBoundingClientRect?.();
      const stageRect = stage.getBoundingClientRect?.();
      return rect && stageRect ? rect.left - stageRect.left : null;
    }

    function measureEndX(measure) {
      if (!measure) return null;
      const stageRect = stage.getBoundingClientRect?.();
      if (!stageRect) return null;
      const barlines = [...measure.querySelectorAll?.(
        "g.barLine, .barLine, path[class*='barLine']") || []]
        .map(node => node.getBoundingClientRect?.())
        .filter(rect => rect && (rect.width > 0 || rect.height > 0));
      if (barlines.length)
        return Math.max(...barlines.map(rect => rect.right)) - stageRect.left;
      const rect = measure.getBoundingClientRect?.();
      return rect ? rect.right - stageRect.left : null;
    }

    function uniqueSorted(values) {
      const sorted = values.filter(Number.isFinite).sort((left, right) => left - right);
      const result = [];
      for (const value of sorted) {
        if (!result.length || Math.abs(value - result.at(-1)) > X_TOLERANCE)
          result.push(value);
      }
      return result;
    }

    function renderedRestXs(measure) {
      if (!measure) return [];
      const roots = new Set();
      for (const candidate of measure.querySelectorAll?.("g.rest, .rest") || []) {
        const root = candidate.closest?.("g.rest") || candidate;
        roots.add(root);
      }
      return uniqueSorted([...roots].map(centerX));
    }

    function restStamps(occurrence) {
      return uniqueSorted(eventsForOccurrence(occurrence)
        .filter(event => event?.restsOn?.length)
        .map(event => number(event.qstamp, Number.NaN)));
    }

    function restXAt(stamp, occurrence, measure) {
      const exactValues = [];
      for (const event of eventsForOccurrence(occurrence)) {
        if (Math.abs(number(event?.qstamp, Number.NaN) - stamp) > EPSILON) continue;
        for (const id of event?.restsOn || []) {
          const value = centerX(exactElement(id));
          if (Number.isFinite(value)) exactValues.push(value);
        }
      }
      if (exactValues.length)
        return exactValues.reduce((sum, value) => sum + value, 0) / exactValues.length;

      const stamps = restStamps(occurrence);
      const positions = renderedRestXs(measure);
      const stampIndex = stamps.findIndex(value => Math.abs(value - stamp) <= EPSILON);
      if (stampIndex >= 0 && positions.length) {
        const positionIndex = stamps.length <= 1
          ? 0
          : Math.round(stampIndex * (positions.length - 1) / (stamps.length - 1));
        restGeometryFallbackCount++;
        return positions[Math.max(0, Math.min(positions.length - 1, positionIndex))];
      }

      const start = number(occurrence?.sourceStartBeat);
      const duration = Math.max(EPSILON, number(occurrence?.durationBeats));
      const left = measureStartX(measure);
      const right = measureEndX(measure);
      if (Number.isFinite(left) && Number.isFinite(right)) {
        restGeometryFallbackCount++;
        const progress = Math.max(0, Math.min(1, (stamp - start) / duration));
        return left + (right - left) * progress;
      }
      return null;
    }

    function resolveGroup(group, occurrence, measure) {
      if (!group.length) return null;
      const values = [];
      let system = null;
      let hasNotes = false;
      let hasRests = false;

      for (const event of group) {
        for (const id of event?.on || []) {
          const element = exactElement(id);
          const anchor = element?.matches?.("g.note")
            ? (element.querySelector?.(":scope > g.notehead") ||
               element.querySelector?.("g.notehead") || element)
            : element;
          const value = centerX(anchor);
          if (Number.isFinite(value)) values.push(value);
          system ||= element?.closest?.("g.system") || null;
          hasNotes = true;
        }
        if (event?.restsOn?.length) {
          const value = restXAt(number(event.qstamp), occurrence, measure);
          if (Number.isFinite(value)) values.push(value);
          hasRests = true;
        }
      }

      system ||= measure?.closest?.("g.system") || null;
      if (!values.length) {
        unresolvedPlayableCount++;
        return null;
      }
      return {
        x: values.reduce((sum, value) => sum + value, 0) / values.length,
        system,
        stamp: groupStamp(group, number(occurrence?.sourceStartBeat)),
        kind: hasNotes && hasRests ? "notes+rests" : hasNotes ? "notes" : "rests",
        noteIds: group.flatMap(event => event?.on || [])
      };
    }

    function isOrdinaryForwardContinuation(occurrence, occurrenceIndex, performanceBeat) {
      if (!lastOccurrence || !Number.isFinite(lastPerformanceBeat) || performanceBeat + EPSILON < lastPerformanceBeat)
        return false;
      if (occurrenceIndex === lastOccurrenceIndex) return true;
      if (occurrenceIndex !== lastOccurrenceIndex + 1) return false;

      const priorSourceEnd = number(lastOccurrence.sourceStartBeat) +
        Math.max(0, number(lastOccurrence.durationBeats));
      const priorPerformanceEnd = number(lastOccurrence.performanceStartBeat) +
        Math.max(0, number(lastOccurrence.durationBeats));
      return Math.abs(number(occurrence?.sourceStartBeat) - priorSourceEnd) <= EPSILON &&
        Math.abs(number(occurrence?.performanceStartBeat) - priorPerformanceEnd) <= EPSILON;
    }

    function applyPosition(performanceBeat, reset, previousOffsetX) {
      if ((typeof pendingPage !== "undefined" && pendingPage) || !Array.isArray(timemap) || !timemap.length)
        return;

      const occurrence = occurrenceAt(performanceBeat);
      if (!occurrence) return;
      const occurrenceIndex = performanceTimeline.indexOf(occurrence);
      const sourceBeat = sourceBeatFor(performanceBeat, occurrence);
      const measure = measureFor(occurrence);
      const previousGroup = playableGroupAtOrBefore(sourceBeat, occurrence);
      const nextGroup = playableGroupAfter(sourceBeat, occurrence);
      const firstGroup = previousGroup.length
        ? previousGroup
        : playableGroupAfter(number(occurrence.sourceStartBeat) - EPSILON, occurrence);
      const previousTarget = resolveGroup(firstGroup, occurrence, measure);
      const nextTarget = resolveGroup(nextGroup, occurrence, measure);
      if (!previousTarget && !nextTarget) return;

      let x1 = previousTarget?.x;
      let x2 = nextTarget?.x;
      const system = previousTarget?.system || nextTarget?.system || measure?.closest?.("g.system") || null;
      const nextSystem = nextTarget?.system || system;
      if (!Number.isFinite(x1)) x1 = Number.isFinite(x2) ? x2 : measureStartX(measure);
      if (!Number.isFinite(x2) || (system && nextSystem && system !== nextSystem))
        x2 = measureEndX(measure) ?? x1;
      if (!Number.isFinite(x1) || !Number.isFinite(x2)) return;

      const sourceStart = number(occurrence.sourceStartBeat);
      const sourceEnd = sourceStart + Math.max(0, number(occurrence.durationBeats));
      const previousStamp = previousTarget?.stamp ?? sourceStart;
      const nextStamp = nextTarget?.stamp ?? sourceEnd;
      const span = Math.max(EPSILON, nextStamp - previousStamp);
      const progress = Math.max(0, Math.min(1, (sourceBeat - previousStamp) / span));
      let visibleX = x1 + (x2 - x1) * progress;

      const continuous = typeof readingMode !== "undefined" && readingMode === "Continuous";
      const zoom = continuous && typeof userZoom !== "undefined"
        ? Math.max(0.01, number(userZoom, 1))
        : 1;
      let scoreX = continuous ? (visibleX - previousOffsetX) / zoom : visibleX;
      const ordinaryForward = !reset &&
        isOrdinaryForwardContinuation(occurrence, occurrenceIndex, performanceBeat);
      const sameSystem = Boolean(system && lastSystem && system === lastSystem);

      if (ordinaryForward && sameSystem && Number.isFinite(lastScoreX) && scoreX < lastScoreX - X_TOLERANCE) {
        scoreX = lastScoreX;
        visibleX = continuous ? scoreX * zoom + previousOffsetX : scoreX;
        preventedBackwardCount++;
      }

      if (continuous) {
        continuousOffsetX = previousOffsetX;
        applyContinuousTransform();
        visibleX = scoreX * zoom + previousOffsetX;
        const stageRect = stage.getBoundingClientRect();
        const anchorX = Math.max(180, number(stageRect?.width) * 0.32);
        const targetOffsetX = previousOffsetX + anchorX - visibleX;
        continuousOffsetX = ordinaryForward
          ? Math.min(previousOffsetX, targetOffsetX)
          : targetOffsetX;
        applyContinuousTransform();
        visibleX = scoreX * zoom + continuousOffsetX;
      }

      playhead.style.left = `${visibleX}px`;
      playhead.style.opacity = "1";

      document.querySelectorAll?.(".playing").forEach(node => node.classList.remove("playing"));
      for (const id of previousTarget?.noteIds || []) {
        const element = exactElement(id);
        if (!element) continue;
        element.classList?.add("playing");
        element.querySelectorAll?.(".syl, text, tspan")
          .forEach(child => child.classList.add("playing"));
      }

      lastPerformanceBeat = performanceBeat;
      lastOccurrenceIndex = occurrenceIndex;
      lastOccurrence = occurrence;
      lastSystem = system;
      lastScoreX = scoreX;
      lastTarget = {
        performanceBeat,
        sourceBeat,
        occurrenceIndex,
        previousStamp,
        nextStamp,
        kind: previousTarget?.kind || nextTarget?.kind || "unknown",
        scoreX,
        visibleX,
        ordinaryForward,
        sameSystem
      };
    }

    api.setCursorBeat = function cadenzaPlayablePositionSetCursorBeat(beat, reset = false) {
      const requestedBeat = Math.max(0, number(beat));
      const previousOffsetX = typeof continuousOffsetX === "undefined"
        ? 0
        : number(continuousOffsetX);
      if (reset) {
        lastPerformanceBeat = Number.NaN;
        lastOccurrenceIndex = -1;
        lastOccurrence = null;
        lastSystem = null;
        lastScoreX = Number.NaN;
      }

      const result = originalSetCursorBeat(requestedBeat, reset);
      applyPosition(requestedBeat, reset, previousOffsetX);
      return result;
    };

    if (originalGetState) {
      api.getState = function cadenzaPlayablePositionGetState(...args) {
        return {
          ...(originalGetState(...args) || {}),
          playablePositioning: {
            installed: true,
            structuralEventsAreTargets: false,
            restResolution: "measure-local-geometry",
            restGeometryFallbackCount,
            preventedBackwardCount,
            unresolvedPlayableCount,
            lastTarget
          }
        };
      };
    }
  }

  install();
})();
