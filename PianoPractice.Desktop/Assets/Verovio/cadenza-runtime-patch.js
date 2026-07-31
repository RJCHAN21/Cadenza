(() => {
  "use strict";

  if (window.__cadenzaRuntimePatchInstalled) return;
  window.__cadenzaRuntimePatchInstalled = true;

  const epsilon = 0.0001;
  let performanceTempoChanges = [{ performanceBeat: 0, bpm: 120 }];
  let performanceTotalBeats = 0;
  let scoreInitialBpm = 120;

  function install() {
    if (typeof optionsForMode !== "function" ||
        typeof eventViewportCenter !== "function" ||
        typeof renderPage !== "function") {
      setTimeout(install, 10);
      return;
    }

    if (optionsForMode.__cadenzaPatched) return;

    const originalOptionsForMode = optionsForMode;
    optionsForMode = function cadenzaOptionsForMode(mode) {
      const options = { ...originalOptionsForMode(mode) };
      delete options.expandAlways;
      options.expandNever = true;
      return options;
    };
    optionsForMode.__cadenzaPatched = true;

    function normalizeTempoChanges(changes, totalBeats, initialBpm) {
      scoreInitialBpm = Math.max(1, Number(initialBpm) || 120);
      performanceTotalBeats = Math.max(0, Number(totalBeats) || 0);
      const normalized = (Array.isArray(changes) ? changes : [])
        .map(change => ({
          performanceBeat: Math.max(0, Number(change.performanceBeat) || 0),
          bpm: Math.max(1, Number(change.bpm) || scoreInitialBpm)
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
        deduplicated.unshift({ performanceBeat: 0, bpm: scoreInitialBpm });
      performanceTempoChanges = deduplicated;
    }

    function tempoScale() {
      return Math.max(0.01, Number(bpm || scoreInitialBpm) / scoreInitialBpm);
    }

    function secondsAtPerformanceBeat(beat) {
      const target = Math.max(0, Math.min(
        performanceTotalBeats > 0 ? performanceTotalBeats : Number.MAX_SAFE_INTEGER,
        Number(beat) || 0));
      const scale = tempoScale();
      let seconds = 0;
      let previousBeat = 0;
      let activeBpm = Math.max(1, performanceTempoChanges[0].bpm * scale);
      for (let index = 1; index < performanceTempoChanges.length; index++) {
        const change = performanceTempoChanges[index];
        if (change.performanceBeat >= target - epsilon) break;
        const nextBeat = Math.max(previousBeat, change.performanceBeat);
        seconds += (nextBeat - previousBeat) * 60 / activeBpm;
        previousBeat = nextBeat;
        activeBpm = Math.max(1, change.bpm * scale);
      }
      seconds += Math.max(0, target - previousBeat) * 60 / activeBpm;
      return seconds;
    }

    function performanceBeatAtMilliseconds(milliseconds) {
      let remaining = Math.max(0, Number(milliseconds) || 0) / 1000;
      const scale = tempoScale();
      let beat = 0;
      let activeBpm = Math.max(1, performanceTempoChanges[0].bpm * scale);
      for (let index = 1; index < performanceTempoChanges.length; index++) {
        const change = performanceTempoChanges[index];
        const segmentBeats = Math.max(0, change.performanceBeat - beat);
        const segmentSeconds = segmentBeats * 60 / activeBpm;
        if (remaining <= segmentSeconds + 1e-9)
          return Math.max(0, beat + remaining * activeBpm / 60);
        remaining -= segmentSeconds;
        beat = change.performanceBeat;
        activeBpm = Math.max(1, change.bpm * scale);
      }
      const result = beat + remaining * activeBpm / 60;
      return Math.max(0, Math.min(
        performanceTotalBeats > 0 ? performanceTotalBeats : result,
        result));
    }

    function occurrenceAtPerformanceBeat(beat) {
      const value = Math.max(0, Number(beat) || 0);
      if (!Array.isArray(performanceTimeline) || !performanceTimeline.length) return null;
      for (let index = 0; index < performanceTimeline.length; index++) {
        const occurrence = performanceTimeline[index];
        const start = Number(occurrence.performanceStartBeat) || 0;
        const end = start + Math.max(0, Number(occurrence.durationBeats) || 0);
        const isLast = index === performanceTimeline.length - 1;
        if (value >= start - epsilon && (value < end - epsilon || (isLast && value <= end + epsilon)))
          return occurrence;
      }
      return performanceTimeline.at(-1) || null;
    }

    function sourceBeatForOccurrence(performanceBeat, occurrence) {
      if (!occurrence) return Math.max(0, Number(performanceBeat) || 0);
      const start = Number(occurrence.performanceStartBeat) || 0;
      const sourceStart = Number(occurrence.sourceStartBeat) || 0;
      const duration = Math.max(0, Number(occurrence.durationBeats) || 0);
      return sourceStart + Math.max(0, Math.min(duration, Number(performanceBeat) - start));
    }

    function positioned(event) {
      return Boolean(event && (event.on?.length || event.restsOn?.length || event.measureOn));
    }

    function eventAtOrBeforeSourceBeat(sourceBeat, occurrence = null) {
      const minimum = occurrence ? Number(occurrence.sourceStartBeat) - epsilon : -Infinity;
      const maximum = occurrence
        ? Number(occurrence.sourceStartBeat) + Number(occurrence.durationBeats) + epsilon
        : Infinity;
      let result = null;
      for (const event of timemap) {
        const qstamp = Number(event.qstamp);
        if (!Number.isFinite(qstamp) || qstamp < minimum || qstamp > maximum) continue;
        if (qstamp > sourceBeat + epsilon) break;
        if (positioned(event)) result = event;
      }
      return result;
    }

    function eventAfterSourceBeat(sourceBeat, occurrence = null) {
      const minimum = occurrence ? Number(occurrence.sourceStartBeat) - epsilon : -Infinity;
      const occurrenceEnd = occurrence
        ? Number(occurrence.sourceStartBeat) + Number(occurrence.durationBeats)
        : Infinity;
      return timemap.find(event => {
        const qstamp = Number(event.qstamp);
        return positioned(event) &&
          Number.isFinite(qstamp) &&
          qstamp >= minimum &&
          qstamp > sourceBeat + epsilon &&
          qstamp < occurrenceEnd - epsilon;
      }) || null;
    }

    function eventNearestSourceBeat(sourceBeat, occurrence = null) {
      const previous = eventAtOrBeforeSourceBeat(sourceBeat, occurrence);
      const next = eventAfterSourceBeat(sourceBeat, occurrence);
      if (!previous) return next;
      if (!next) return previous;
      return Math.abs(Number(previous.qstamp) - sourceBeat) <=
        Math.abs(Number(next.qstamp) - sourceBeat)
        ? previous
        : next;
    }

    function occurrenceStartEvent(occurrence) {
      if (!occurrence) return null;
      const sourceStart = Number(occurrence.sourceStartBeat) || 0;
      return timemap.find(event =>
        Number.isFinite(Number(event.qstamp)) &&
        Math.abs(Number(event.qstamp) - sourceStart) < epsilon &&
        event.measureOn) || eventAtOrBeforeSourceBeat(sourceStart, occurrence) ||
        eventAfterSourceBeat(sourceStart - epsilon, occurrence);
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
      const rect = measure.getBoundingClientRect();
      return rect.left - stageRect.left;
    }

    function measureEndX(measure) {
      if (!measure) return null;
      const stageRect = stage.getBoundingClientRect();
      const barlines = [...measure.querySelectorAll("g.barLine, .barLine, path[class*='barLine']")];
      if (barlines.length) {
        const rectangles = barlines
          .map(node => node.getBoundingClientRect())
          .filter(rect => rect.width > 0 || rect.height > 0);
        if (rectangles.length)
          return Math.max(...rectangles.map(rect => rect.right)) - stageRect.left;
      }
      const rect = measure.getBoundingClientRect();
      return rect.right - stageRect.left;
    }

    eventAtBeat = function cadenzaEventAtBeat(performanceBeat) {
      const occurrence = occurrenceAtPerformanceBeat(performanceBeat);
      const sourceBeat = sourceBeatForOccurrence(performanceBeat, occurrence);
      return eventNearestSourceBeat(sourceBeat, occurrence);
    };

    elementsAtBeat = function cadenzaElementsAtBeat(performanceBeat) {
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

    function updateCursorAtBeat(performanceBeat, immediate) {
      if (!toolkit || !timemap.length) return;
      const occurrence = occurrenceAtPerformanceBeat(performanceBeat);
      const sourceBeat = sourceBeatForOccurrence(performanceBeat, occurrence);
      const previous = eventAtOrBeforeSourceBeat(sourceBeat, occurrence);
      const next = eventAfterSourceBeat(sourceBeat, occurrence);
      const startEvent = occurrenceStartEvent(occurrence);
      const desiredPage = pageForEvent(previous || next || startEvent);
      if (desiredPage !== currentPage) {
        if (!pendingPage || pendingPage !== desiredPage) {
          const direction = desiredPage > currentPage ? 1 : -1;
          renderPage(desiredPage, !immediate, direction, renderLatestCursor);
        }
        return;
      }
      if (pendingPage) return;

      const measure = measureForOccurrence(occurrence);
      const previousSystem = systemForEvent(previous || startEvent);
      const nextSystem = systemForEvent(next);
      let x1 = eventViewportCenter(previous) ?? measureStartX(measure);
      let x2 = next && (!previousSystem || !nextSystem || previousSystem === nextSystem)
        ? eventViewportCenter(next)
        : null;

      const occurrenceEnd = occurrence
        ? Number(occurrence.sourceStartBeat) + Number(occurrence.durationBeats)
        : sourceBeat;
      if (x2 == null) x2 = measureEndX(measure) ?? (previousSystem ? systemEndBarLineX(previousSystem) : x1);
      if (x1 == null) x1 = x2;
      if (x1 == null || x2 == null) return;

      const previousBeat = Number.isFinite(Number(previous?.qstamp))
        ? Number(previous.qstamp)
        : Number(occurrence?.sourceStartBeat) || sourceBeat;
      const nextBeat = next && Number.isFinite(Number(next.qstamp))
        ? Number(next.qstamp)
        : occurrenceEnd;
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

        const transformedX1 = eventViewportCenter(previous) ?? measureStartX(measure);
        const transformedX2 = next
          ? eventViewportCenter(next)
          : measureEndX(measure);
        if (transformedX1 != null && transformedX2 != null)
          visibleX = transformedX1 + (transformedX2 - transformedX1) * progress;
      }

      playhead.style.left = `${visibleX}px`;
      playhead.style.opacity = "1";
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
        playhead.style.top = `${top}px`;
        playhead.style.height = `${Math.max(24, bottom - top)}px`;

        const systems = [...notation.querySelectorAll("g.system")];
        const systemNumber = Math.max(1, systems.indexOf(system) + 1);
        const positionKey = `${currentPage}:${systemNumber}:${systems.length}`;
        if (positionKey !== lastPositionKey) {
          lastPositionKey = positionKey;
          post("position", {
            page: currentPage,
            pages: pageCount,
            system: systemNumber,
            systems: systems.length,
            occurrenceIndex: Number(occurrence?.occurrenceIndex ?? 0),
            repeatPass: Number(occurrence?.repeatPass ?? 1)
          });
        }
      }

      document.querySelectorAll(".playing").forEach(node => node.classList.remove("playing"));
      const active = previous || eventNearestSourceBeat(sourceBeat, occurrence);
      for (const id of active?.on || []) {
        const element = elementForVerovioId(id);
        if (element) {
          element.classList.add("playing");
          element.querySelectorAll(".syl, text, tspan").forEach(child => child.classList.add("playing"));
        }
      }
    }

    updateCursor = function cadenzaUpdateCursor(milliseconds, immediate) {
      updateCursorAtBeat(performanceBeatAtMilliseconds(milliseconds), immediate);
    };

    renderLatestCursor = function cadenzaRenderLatestCursor() {
      const beat = Math.max(0, Number(latestRequestedBeat) || 0);
      const occurrence = occurrenceAtPerformanceBeat(beat);
      const sourceBeat = sourceBeatForOccurrence(beat, occurrence);
      const event = eventNearestSourceBeat(sourceBeat, occurrence) || occurrenceStartEvent(occurrence);
      const desiredPage = pageForEvent(event);
      if (desiredPage !== currentPage) {
        if (!pendingPage || pendingPage !== desiredPage) {
          const direction = desiredPage > currentPage ? 1 : -1;
          renderPage(desiredPage, true, direction, renderLatestCursor);
        }
        return;
      }
      if (pendingPage) return;

      updateCursorAtBeat(beat, true);
      document.querySelectorAll(".expected").forEach(node => node.classList.remove("expected"));
      document.querySelectorAll(".active-measure-glow").forEach(node => node.classList.remove("active-measure-glow"));
      document.querySelectorAll(".hint-svg-badge").forEach(node => node.remove());
      for (const id of event?.on || []) {
        const element = elementForVerovioId(id);
        if (element) {
          element.classList.add("expected");
          element.querySelectorAll(".syl, text, tspan").forEach(child => child.classList.add("expected"));
        }
      }
      updateHintLane({ notes: event?.on || [], elements: [] }, beat);
    };

    findTiedContinuationElements = function cadenzaFindTiedContinuationElements(node) {
      if (!node || !notation) return [];
      const note = node.closest?.("g.note") || node;
      const noteId = note.id || note.getAttribute("data-id");
      if (!noteId) return [];

      const result = [];
      const visited = new Set([noteId]);
      const queue = [noteId];
      while (queue.length) {
        const currentId = queue.shift();
        for (const item of notation.querySelectorAll("g.tie, .tie")) {
          const itemId = item.id || item.getAttribute("data-id");
          let startId = item.getAttribute("data-startid") || item.getAttribute("startid") ||
            item.querySelector("[data-startid]")?.getAttribute("data-startid") ||
            item.querySelector("[startid]")?.getAttribute("startid") || "";
          let endId = item.getAttribute("data-endid") || item.getAttribute("endid") ||
            item.querySelector("[data-endid]")?.getAttribute("data-endid") ||
            item.querySelector("[endid]")?.getAttribute("endid") || "";
          if ((!startId || !endId) && toolkit?.getElementAttr && itemId) {
            try {
              const attributes = toolkit.getElementAttr(itemId);
              startId ||= attributes?.startid || attributes?.["data-startid"] || attributes?.["@startid"] || "";
              endId ||= attributes?.endid || attributes?.["data-endid"] || attributes?.["@endid"] || "";
            } catch { }
          }
          startId = String(startId || "").replace(/^#/, "");
          endId = String(endId || "").replace(/^#/, "");
          const startsHere = startId && (startId === currentId || currentId.endsWith(startId) || startId.endsWith(currentId));
          const endsHere = endId && (endId === currentId || currentId.endsWith(endId) || endId.endsWith(currentId));
          const candidate = startsHere ? endId : (endsHere ? startId : "");
          if (!candidate || visited.has(candidate)) continue;
          visited.add(candidate);
          queue.push(candidate);
          const target = elementForVerovioId(candidate);
          if (target) result.push(target.closest?.("g.note") || target);
        }
      }
      return result;
    };

    const originalReapplyLessonFeedback = reapplyLessonFeedback;
    reapplyLessonFeedback = function cadenzaReapplyLessonFeedback() {
      const wasRunning = lessonRunning;
      if (!wasRunning) lessonRunning = true;
      try {
        return originalReapplyLessonFeedback();
      } finally {
        lessonRunning = wasRunning;
      }
    };

    function setPerformanceClock(changes, totalBeats, initialBpm) {
      normalizeTempoChanges(changes, totalBeats, initialBpm);
    }

    window.CadenzaNotation = {
      ...window.CadenzaNotation,
      loadScore,
      setPerformanceTimeline,
      setPerformanceClock,
      setReadingMode,
      setHintMode,
      setHandMode,
      setScoreAppearance,
      startPlayback,
      stopPlayback,
      beginTimeline,
      endTimeline,
      setCursorBeat,
      setZoom,
      setTempo,
      changePage,
      resetAudit,
      beginLesson,
      finishLesson,
      showFeedback,
      showTemporaryLiveNoteFeedback,
      clearPartialFeedback,
      showCountdownStep,
      hideCountdown,
      showResultsModal,
      updateResultsAutoRepeat,
      hideResultsModal,
      showHoldProgress,
      hideHoldProgress,
      getState,
      elementsAtBeat,
      validateRendererLayout,
      validateFeedbackGeometry
    };
  }

  install();
})();
