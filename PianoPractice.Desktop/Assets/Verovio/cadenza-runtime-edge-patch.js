(() => {
  "use strict";

  const epsilon = 0.0001;
  let clockTempoChanges = [{ performanceBeat: 0, bpm: 120 }];
  let clockTotalBeats = 0;
  let clockInitialBpm = 120;
  let playingNodes = new Set();
  let expectedNodes = new Set();
  let lastHintKey = "";
  let lastPlayingKey = "";
  let lastExpectedKey = "";

  function install() {
    if (!window.CadenzaNotation?.setPerformanceClock ||
        typeof eventViewportCenter !== "function" ||
        typeof renderPage !== "function" ||
        typeof performanceTimeline === "undefined" ||
        typeof timemap === "undefined") {
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

    function normalizeClock(changes, totalBeats, initialBpm) {
      clockInitialBpm = Math.max(1, Number(initialBpm) || 120);
      clockTotalBeats = Math.max(0, Number(totalBeats) || 0);
      const normalized = (Array.isArray(changes) ? changes : [])
        .map(change => ({
          performanceBeat: Math.max(0, Number(change.performanceBeat) || 0),
          bpm: Math.max(1, Number(change.bpm) || clockInitialBpm)
        }))
        .filter(change => Number.isFinite(change.performanceBeat) && Number.isFinite(change.bpm))
        .sort((left, right) => left.performanceBeat - right.performanceBeat);

      const deduplicated = [];
      for (const change of normalized) {
        const previous = deduplicated.at(-1);
        if (previous && Math.abs(previous.performanceBeat - change.performanceBeat) < epsilon)
          deduplicated[deduplicated.length - 1] = change;
        else
          deduplicated.push(change);
      }
      if (!deduplicated.length || deduplicated[0].performanceBeat > epsilon)
        deduplicated.unshift({ performanceBeat: 0, bpm: clockInitialBpm });
      clockTempoChanges = deduplicated;
    }

    const originalSetPerformanceClock = window.CadenzaNotation.setPerformanceClock;
    window.CadenzaNotation.setPerformanceClock = function setPerformanceClock(changes, totalBeats, initialBpm) {
      normalizeClock(changes, totalBeats, initialBpm);
      return originalSetPerformanceClock(changes, totalBeats, initialBpm);
    };

    function tempoScale() {
      return Math.max(0.01, Number(bpm || clockInitialBpm) / clockInitialBpm);
    }

    function performanceBeatAtMilliseconds(milliseconds) {
      let remaining = Math.max(0, Number(milliseconds) || 0) / 1000;
      const scale = tempoScale();
      let beat = 0;
      let activeBpm = Math.max(1, clockTempoChanges[0].bpm * scale);
      for (let index = 1; index < clockTempoChanges.length; index++) {
        const change = clockTempoChanges[index];
        const segmentBeats = Math.max(0, change.performanceBeat - beat);
        const segmentSeconds = segmentBeats * 60 / activeBpm;
        if (remaining <= segmentSeconds + 1e-9)
          return Math.max(0, beat + remaining * activeBpm / 60);
        remaining -= segmentSeconds;
        beat = change.performanceBeat;
        activeBpm = Math.max(1, change.bpm * scale);
      }
      const result = beat + remaining * activeBpm / 60;
      return Math.max(0, Math.min(clockTotalBeats > 0 ? clockTotalBeats : result, result));
    }

    function occurrenceAtPerformanceBeat(beat) {
      const value = Math.max(0, Number(beat) || 0);
      if (!Array.isArray(performanceTimeline) || !performanceTimeline.length) return null;
      for (let index = 0; index < performanceTimeline.length; index++) {
        const occurrence = performanceTimeline[index];
        const start = Number(occurrence.performanceStartBeat) || 0;
        const duration = Math.max(0, Number(occurrence.durationBeats) || 0);
        const end = start + duration;
        const isLast = index === performanceTimeline.length - 1;
        if (value >= start - epsilon &&
            (value < end - epsilon || (isLast && value <= end + epsilon)))
          return occurrence;
      }
      return performanceTimeline.at(-1) || null;
    }

    function sourceBeatForOccurrence(performanceBeat, occurrence) {
      if (!occurrence) return Math.max(0, Number(performanceBeat) || 0);
      const performanceStart = Number(occurrence.performanceStartBeat) || 0;
      const sourceStart = Number(occurrence.sourceStartBeat) || 0;
      const duration = Math.max(0, Number(occurrence.durationBeats) || 0);
      return sourceStart + Math.max(0, Math.min(duration, Number(performanceBeat) - performanceStart));
    }

    function positioned(event) {
      return Boolean(event && (event.on?.length || event.restsOn?.length || event.measureOn));
    }

    function occurrenceSourceBounds(occurrence) {
      const start = occurrence ? Number(occurrence.sourceStartBeat) || 0 : -Infinity;
      const duration = occurrence ? Math.max(0, Number(occurrence.durationBeats) || 0) : Infinity;
      return { start, end: start + duration };
    }

    function eventBelongsToOccurrence(event, occurrence) {
      if (!occurrence) return true;
      const qstamp = Number(event?.qstamp);
      if (!Number.isFinite(qstamp)) return false;
      const bounds = occurrenceSourceBounds(occurrence);
      return qstamp >= bounds.start - epsilon && qstamp < bounds.end - epsilon;
    }

    function eventAtOrBeforeSourceBeat(sourceBeat, occurrence) {
      let result = null;
      for (const event of timemap) {
        const qstamp = Number(event.qstamp);
        if (!Number.isFinite(qstamp) || !eventBelongsToOccurrence(event, occurrence)) continue;
        if (qstamp > sourceBeat + epsilon) break;
        if (positioned(event)) result = event;
      }
      return result;
    }

    function eventAfterSourceBeat(sourceBeat, occurrence) {
      return timemap.find(event => {
        const qstamp = Number(event.qstamp);
        return positioned(event) && Number.isFinite(qstamp) &&
          qstamp > sourceBeat + epsilon && eventBelongsToOccurrence(event, occurrence);
      }) || null;
    }

    function occurrenceStartEvent(occurrence) {
      if (!occurrence) return null;
      const sourceStart = Number(occurrence.sourceStartBeat) || 0;
      return timemap.find(event =>
        eventBelongsToOccurrence(event, occurrence) &&
        Math.abs(Number(event.qstamp) - sourceStart) < epsilon && event.measureOn) ||
        eventAtOrBeforeSourceBeat(sourceStart, occurrence) ||
        eventAfterSourceBeat(sourceStart - epsilon, occurrence);
    }

    function eventNearestSourceBeat(sourceBeat, occurrence) {
      const previous = eventAtOrBeforeSourceBeat(sourceBeat, occurrence);
      const next = eventAfterSourceBeat(sourceBeat, occurrence);
      if (!previous) return next;
      if (!next) return previous;
      return Math.abs(Number(previous.qstamp) - sourceBeat) <=
        Math.abs(Number(next.qstamp) - sourceBeat) ? previous : next;
    }

    function measureForOccurrence(occurrence) {
      const startEvent = occurrenceStartEvent(occurrence);
      const measureNode = startEvent?.measureOn ? elementForVerovioId(startEvent.measureOn) : null;
      return measureNode?.closest?.("g.measure") || measureNode ||
        positionIds(startEvent).map(elementForVerovioId).find(Boolean)?.closest?.("g.measure") || null;
    }

    function measureStartX(measure) {
      if (!measure) return null;
      const stageRect = stage.getBoundingClientRect();
      return measure.getBoundingClientRect().left - stageRect.left;
    }

    function measureEndX(measure) {
      if (!measure) return null;
      const stageRect = stage.getBoundingClientRect();
      const barlines = [...measure.querySelectorAll("g.barLine, .barLine, path[class*='barLine']")]
        .map(node => node.getBoundingClientRect())
        .filter(rect => rect.width > 0 || rect.height > 0);
      if (barlines.length)
        return Math.max(...barlines.map(rect => rect.right)) - stageRect.left;
      return measure.getBoundingClientRect().right - stageRect.left;
    }

    function nodeSetForIds(ids) {
      const result = new Set();
      for (const id of ids || []) {
        const element = elementForVerovioId(id);
        if (element) result.add(element);
      }
      return result;
    }

    function replaceClassSet(current, next, className) {
      for (const node of current) {
        if (!next.has(node) || !node.isConnected)
          node.classList.remove(className);
      }
      for (const node of next) {
        if (!current.has(node))
          node.classList.add(className);
      }
      return next;
    }

    function stableKey(ids) {
      return [...new Set((ids || []).map(String))].sort().join("|");
    }

    function updatePlaying(ids) {
      const key = stableKey(ids);
      if (key === lastPlayingKey && [...playingNodes].every(node => node.isConnected)) return;
      lastPlayingKey = key;
      playingNodes = replaceClassSet(playingNodes, nodeSetForIds(ids), "playing");
    }

    function clearHintDecorations() {
      document.querySelectorAll(".hint-svg-badge").forEach(node => node.remove());
      document.querySelectorAll(".active-measure-glow").forEach(node => node.classList.remove("active-measure-glow"));
    }

    function updateExpected(ids, beat) {
      const filteredIds = hintIdsForSelectedHand(ids);
      const key = stableKey(filteredIds);
      if (key !== lastExpectedKey ||
          ![...expectedNodes].every(node => node.isConnected && node.classList.contains("expected"))) {
        lastExpectedKey = key;
        expectedNodes = replaceClassSet(expectedNodes, nodeSetForIds(filteredIds), "expected");
      }
      if (!hintMode) {
        if (lastHintKey) {
          lastHintKey = "";
          clearHintDecorations();
        }
        return;
      }
      const hintKey = `${handMode}:${key}`;
      const hintDecorationsExist = filteredIds.length === 0 ||
        document.querySelector(".hint-svg-badge") !== null;
      if (hintKey === lastHintKey && hintDecorationsExist) return;
      lastHintKey = hintKey;
      clearHintDecorations();
      updateHintLane({ notes: filteredIds, elements: [] }, beat);
    }

    function setPixelStyle(element, property, value) {
      const next = `${value.toFixed(2)}px`;
      if (element.style[property] !== next)
        element.style[property] = next;
    }

    function updateCursorAtBeat(performanceBeat, immediate) {
      if (!toolkit || !timemap.length) return null;
      const occurrence = occurrenceAtPerformanceBeat(performanceBeat);
      if (!occurrence) return null;
      const sourceBeat = sourceBeatForOccurrence(performanceBeat, occurrence);
      const startEvent = occurrenceStartEvent(occurrence);
      const previous = eventAtOrBeforeSourceBeat(sourceBeat, occurrence) || startEvent;
      const next = eventAfterSourceBeat(sourceBeat, occurrence);
      const desiredPage = pageForEvent(previous || next || startEvent);
      if (shouldHoldManualPage(desiredPage)) {
        playhead.style.opacity = "0";
        updatePlaying([]);
        return null;
      }
      if (desiredPage !== currentPage) {
        if (!pendingPage || pendingPage !== desiredPage) {
          const direction = desiredPage > currentPage ? 1 : -1;
          lastPlayingKey = "";
          lastExpectedKey = "";
          lastHintKey = "";
          renderPage(desiredPage, !immediate, direction, renderLatestCursor);
        }
        return null;
      }
      if (pendingPage) return null;

      const measure = measureForOccurrence(occurrence);
      const previousSystem = systemForEvent(previous || startEvent);
      const nextSystem = systemForEvent(next);
      let x1 = eventViewportCenter(previous || startEvent) ?? measureStartX(measure);
      let x2 = next && (!previousSystem || !nextSystem || previousSystem === nextSystem)
        ? eventViewportCenter(next)
        : null;
      const bounds = occurrenceSourceBounds(occurrence);
      if (x2 == null)
        x2 = measureEndX(measure) ?? (previousSystem ? systemEndBarLineX(previousSystem) : x1);
      if (x1 == null) x1 = x2;
      if (x1 == null || x2 == null) return null;

      const previousBeat = Number.isFinite(Number(previous?.qstamp)) ? Number(previous.qstamp) : bounds.start;
      const nextBeat = next && Number.isFinite(Number(next.qstamp)) ? Number(next.qstamp) : bounds.end;
      const span = Math.max(epsilon, nextBeat - previousBeat);
      const progress = Math.max(0, Math.min(1, (sourceBeat - previousBeat) / span));
      let visibleX = x1 + (x2 - x1) * progress;

      if (readingMode === "Continuous") {
        const stageRect = stage.getBoundingClientRect();
        const anchorX = Math.max(180, stageRect.width * .32);
        const targetOffsetX = continuousOffsetX + (anchorX - visibleX);
        if (targetOffsetX > continuousOffsetX + 150 ||
            (window.lastPlayheadX != null && visibleX < window.lastPlayheadX - 150)) {
          continuousOffsetX = targetOffsetX;
        } else if (timelineRunning) {
          continuousOffsetX = Math.min(continuousOffsetX, targetOffsetX);
        } else {
          continuousOffsetX = targetOffsetX;
        }
        window.lastPlayheadX = visibleX;
        applyContinuousTransform();
        const transformedX1 = eventViewportCenter(previous || startEvent) ?? measureStartX(measure);
        const transformedX2 = next ? eventViewportCenter(next) : measureEndX(measure);
        if (transformedX1 != null && transformedX2 != null)
          visibleX = transformedX1 + (transformedX2 - transformedX1) * progress;
      }

      setPixelStyle(playhead, "left", visibleX);
      if (playhead.style.opacity !== "1") playhead.style.opacity = "1";
      const system = previousSystem || nextSystem || measure?.closest?.("g.system");
      if (system) {
        const stageRect = stage.getBoundingClientRect();
        const staves = [...system.querySelectorAll("g.staff")];
        let top;
        let bottom;
        if (staves.length) {
          const firstRect = staves[0].getBoundingClientRect();
          const lastRect = staves.at(-1).getBoundingClientRect();
          let extraTopSpace = 44;
          const topMarking = system.querySelector("g.tempo, g.harm, g.dynam, g.dir");
          if (topMarking) {
            const markRect = topMarking.getBoundingClientRect();
            extraTopSpace = Math.max(extraTopSpace, firstRect.top - markRect.top + 14);
          }
          top = Math.max(6, firstRect.top - stageRect.top - extraTopSpace);
          bottom = Math.min(stageRect.height - 6, lastRect.bottom - stageRect.top + 16);
        } else {
          const systemRect = system.getBoundingClientRect();
          top = Math.max(6, systemRect.top - stageRect.top - 36);
          bottom = Math.min(stageRect.height - 6, systemRect.bottom - stageRect.top + 12);
        }
        setPixelStyle(playhead, "top", top);
        setPixelStyle(playhead, "height", Math.max(24, bottom - top));
      }

      updatePlaying(previous?.on || []);
      return { occurrence, event: previous || startEvent, sourceBeat };
    }

    eventAtBeat = function cadenzaBoundarySafeEventAtBeat(performanceBeat) {
      const occurrence = occurrenceAtPerformanceBeat(performanceBeat);
      return eventNearestSourceBeat(sourceBeatForOccurrence(performanceBeat, occurrence), occurrence);
    };

    elementsAtBeat = function cadenzaBoundarySafeElementsAtBeat(performanceBeat) {
      const occurrence = occurrenceAtPerformanceBeat(performanceBeat);
      const sourceBeat = sourceBeatForOccurrence(performanceBeat, occurrence);
      const event = eventNearestSourceBeat(sourceBeat, occurrence);
      return {
        toolkit: null,
        indexedPage: pageForEvent(event),
        qstamp: event?.qstamp ?? null,
        occurrenceIndex: Number(occurrence?.occurrenceIndex ?? 0),
        sourceBeat
      };
    };

    updateCursor = function cadenzaBoundarySafeUpdateCursor(milliseconds, immediate) {
      updateCursorAtBeat(performanceBeatAtMilliseconds(milliseconds), immediate);
    };

    renderLatestCursor = function cadenzaBoundarySafeRenderLatestCursor() {
      const beat = Math.max(0, Number(latestRequestedBeat) || 0);
      const result = updateCursorAtBeat(beat, true);
      if (!result) return;
      updateExpected(result.event?.on || [], beat);
    };

    window.CadenzaNotation.elementsAtBeat = elementsAtBeat;

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
