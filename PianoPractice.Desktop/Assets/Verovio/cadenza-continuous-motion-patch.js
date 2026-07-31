(() => {
  "use strict";

  const epsilon = 0.0001;
  const beatSmoothingTimeConstantMs = 48;
  const maximumVisualLagBeats = 0.18;
  const largeSeekThresholdBeats = 0.9;
  const geometrySmoothingTimeConstantMs = 44;
  const maximumPlayheadLagPx = 18;
  const maximumTransformLagPx = 22;
  const verticalSystemJumpThresholdPx = 34;
  const backwardGeometrySnapThresholdPx = 72;

  function install() {
    const api = window.CadenzaNotation;
    const stageElement = document.getElementById("stage");
    if (!api?.setCursorBeat || !stageElement ||
        typeof setPixelStyle !== "function" ||
        typeof applyContinuousTransform !== "function" ||
        typeof playhead === "undefined" ||
        typeof notation === "undefined" ||
        typeof continuousOffsetX === "undefined" ||
        typeof continuousOffsetY === "undefined") {
      setTimeout(install, 10);
      return;
    }
    if (window.__cadenzaContinuousComfortMotionInstalled) return;
    window.__cadenzaContinuousComfortMotionInstalled = true;

    const originalSetCursorBeat = api.setCursorBeat.bind(api);
    const originalSetReadingMode = api.setReadingMode?.bind(api);
    const originalLoadScore = api.loadScore?.bind(api);
    const originalBeginTimeline = api.beginTimeline?.bind(api);
    const originalEndTimeline = api.endTimeline?.bind(api);
    const originalStopPlayback = api.stopPlayback?.bind(api);
    const originalGetState = api.getState?.bind(api);
    const originalSetPixelStyle = setPixelStyle;
    const originalApplyContinuousTransform = applyContinuousTransform;

    let targetBeat = 0;
    let renderedBeat = 0;
    let beatInitialized = false;
    let beatFrameHandle = 0;
    let beatLastFrameTimestamp = 0;

    let visualFrameHandle = 0;
    let visualLastFrameTimestamp = 0;
    let visualInitialized = false;
    let transformInitialized = false;
    let transformTargetX = 0;
    let transformTargetY = 0;
    let transformRenderedX = 0;
    let transformRenderedY = 0;
    let playheadTargetLeft = 0;
    let playheadTargetTop = 0;
    let playheadTargetHeight = 0;
    let playheadRenderedLeft = 0;
    let playheadRenderedTop = 0;
    let playheadRenderedHeight = 0;
    let playheadSnapPending = false;

    let beatFrameSamples = 0;
    let visualFrameSamples = 0;
    let continuousTransformFrames = 0;
    let pageModeVisualFrames = 0;
    let smoothedPlayheadTargets = 0;
    let geometrySnapCount = 0;
    let immediateBeatSnapCount = 0;
    let maximumObservedBeatLag = 0;
    let maximumObservedPlayheadLagPx = 0;
    let maximumObservedTransformLagPx = 0;

    function isContinuousMode() {
      return stageElement.classList.contains("continuous-mode");
    }

    function timelineIsRunning() {
      return typeof timelineRunning === "undefined" || Boolean(timelineRunning);
    }

    function pixelValue(value, fallback = 0) {
      const parsed = Number.parseFloat(String(value ?? ""));
      return Number.isFinite(parsed) ? parsed : fallback;
    }

    function cancelBeatFrame() {
      if (beatFrameHandle) cancelAnimationFrame(beatFrameHandle);
      beatFrameHandle = 0;
      beatLastFrameTimestamp = 0;
    }

    function cancelVisualFrame() {
      if (visualFrameHandle) cancelAnimationFrame(visualFrameHandle);
      visualFrameHandle = 0;
      visualLastFrameTimestamp = 0;
    }

    function cancelAllMotion() {
      cancelBeatFrame();
      cancelVisualFrame();
    }

    function applyRenderedTransform() {
      const zoom = Math.max(0.01, Number(typeof userZoom === "undefined" ? 1 : userZoom) || 1);
      const transform =
        `translate3d(${transformRenderedX.toFixed(3)}px, ${transformRenderedY.toFixed(3)}px, 0) scale(${zoom})`;
      notation.style.transform = transform;
      if (typeof practiceWrongNote !== "undefined" && practiceWrongNote) {
        practiceWrongNote.style.transform = isContinuousMode() ? transform : "";
      }
    }

    function initializeTransformState() {
      transformTargetX = Number(continuousOffsetX) || 0;
      transformTargetY = Number(continuousOffsetY) || 0;
      transformRenderedX = transformTargetX;
      transformRenderedY = transformTargetY;
      transformInitialized = true;
    }

    function initializePlayheadState() {
      playheadTargetLeft = pixelValue(playhead.style.left);
      playheadTargetTop = pixelValue(playhead.style.top);
      playheadTargetHeight = pixelValue(playhead.style.height);
      playheadRenderedLeft = playheadTargetLeft;
      playheadRenderedTop = playheadTargetTop;
      playheadRenderedHeight = playheadTargetHeight;
      playheadSnapPending = false;
      visualInitialized = true;
    }

    function snapVisualState() {
      cancelVisualFrame();
      initializeTransformState();
      initializePlayheadState();
      if (isContinuousMode()) applyRenderedTransform();
      originalSetPixelStyle(playhead, "left", playheadRenderedLeft);
      originalSetPixelStyle(playhead, "top", playheadRenderedTop);
      originalSetPixelStyle(playhead, "height", playheadRenderedHeight);
    }

    function scheduleBeatFrame() {
      if (!beatFrameHandle) beatFrameHandle = requestAnimationFrame(renderBeatFrame);
    }

    function scheduleVisualFrame() {
      if (!visualFrameHandle) visualFrameHandle = requestAnimationFrame(renderVisualFrame);
    }

    function boundedInterpolate(current, target, alpha, maximumLag) {
      let next = current + (target - current) * alpha;
      const remaining = target - next;
      if (Math.abs(remaining) > maximumLag)
        next = target - Math.sign(remaining) * maximumLag;
      if (Math.abs(target - next) < 0.02) next = target;
      return next;
    }

    function renderBeatImmediately(beat, reset) {
      cancelBeatFrame();
      targetBeat = beat;
      renderedBeat = beat;
      beatInitialized = true;
      immediateBeatSnapCount++;
      return originalSetCursorBeat(beat, reset);
    }

    function renderBeatFrame(timestamp) {
      beatFrameHandle = 0;
      if (!beatInitialized || !isContinuousMode()) return;

      const frameGap = beatLastFrameTimestamp > 0
        ? Math.max(1, Math.min(50, timestamp - beatLastFrameTimestamp))
        : 16.667;
      beatLastFrameTimestamp = timestamp;
      beatFrameSamples++;

      const error = targetBeat - renderedBeat;
      maximumObservedBeatLag = Math.max(maximumObservedBeatLag, Math.abs(error));
      if (error < -epsilon) {
        renderBeatImmediately(targetBeat, true);
        return;
      }

      if (Math.abs(error) <= epsilon) {
        renderedBeat = targetBeat;
        originalSetCursorBeat(renderedBeat, false);
        return;
      }

      const interpolation = 1 - Math.exp(-frameGap / beatSmoothingTimeConstantMs);
      let nextBeat = renderedBeat + error * interpolation;
      if (targetBeat - nextBeat > maximumVisualLagBeats)
        nextBeat = targetBeat - maximumVisualLagBeats;
      nextBeat = Math.max(renderedBeat, Math.min(targetBeat, nextBeat));

      renderedBeat = nextBeat;
      originalSetCursorBeat(renderedBeat, false);

      if (targetBeat - renderedBeat > epsilon)
        scheduleBeatFrame();
    }

    function renderVisualFrame(timestamp) {
      visualFrameHandle = 0;
      if (!visualInitialized) initializePlayheadState();
      if (isContinuousMode() && !transformInitialized) initializeTransformState();

      const frameGap = visualLastFrameTimestamp > 0
        ? Math.max(1, Math.min(50, timestamp - visualLastFrameTimestamp))
        : 16.667;
      visualLastFrameTimestamp = timestamp;
      visualFrameSamples++;
      if (isContinuousMode()) continuousTransformFrames++;
      else pageModeVisualFrames++;

      const interpolation = 1 - Math.exp(-frameGap / geometrySmoothingTimeConstantMs);

      if (isContinuousMode() && transformInitialized) {
        maximumObservedTransformLagPx = Math.max(
          maximumObservedTransformLagPx,
          Math.abs(transformTargetX - transformRenderedX));
        transformRenderedX = boundedInterpolate(
          transformRenderedX,
          transformTargetX,
          interpolation,
          maximumTransformLagPx);
        transformRenderedY = boundedInterpolate(
          transformRenderedY,
          transformTargetY,
          interpolation,
          10);
        applyRenderedTransform();
      }

      if (playheadSnapPending) {
        playheadRenderedLeft = playheadTargetLeft;
        playheadRenderedTop = playheadTargetTop;
        playheadRenderedHeight = playheadTargetHeight;
        playheadSnapPending = false;
      } else {
        maximumObservedPlayheadLagPx = Math.max(
          maximumObservedPlayheadLagPx,
          Math.abs(playheadTargetLeft - playheadRenderedLeft));
        playheadRenderedLeft = boundedInterpolate(
          playheadRenderedLeft,
          playheadTargetLeft,
          interpolation,
          maximumPlayheadLagPx);
        playheadRenderedTop = boundedInterpolate(
          playheadRenderedTop,
          playheadTargetTop,
          interpolation,
          8);
        playheadRenderedHeight = boundedInterpolate(
          playheadRenderedHeight,
          playheadTargetHeight,
          interpolation,
          12);
      }

      originalSetPixelStyle(playhead, "left", playheadRenderedLeft);
      originalSetPixelStyle(playhead, "top", playheadRenderedTop);
      originalSetPixelStyle(playhead, "height", playheadRenderedHeight);

      const transformPending = isContinuousMode() &&
        (Math.abs(transformTargetX - transformRenderedX) > 0.02 ||
         Math.abs(transformTargetY - transformRenderedY) > 0.02);
      const playheadPending =
        Math.abs(playheadTargetLeft - playheadRenderedLeft) > 0.02 ||
        Math.abs(playheadTargetTop - playheadRenderedTop) > 0.02 ||
        Math.abs(playheadTargetHeight - playheadRenderedHeight) > 0.02;
      if (transformPending || playheadPending)
        scheduleVisualFrame();
    }

    api.setCursorBeat = function comfortSetCursorBeat(beat, reset = false) {
      const requestedBeat = Math.max(0, Number(beat) || 0);
      const jump = beatInitialized ? Math.abs(requestedBeat - renderedBeat) : 0;

      if (reset || !isContinuousMode() || !beatInitialized ||
          requestedBeat + epsilon < renderedBeat ||
          jump >= largeSeekThresholdBeats) {
        return renderBeatImmediately(requestedBeat, reset);
      }

      targetBeat = requestedBeat;
      scheduleBeatFrame();
      return undefined;
    };

    setPixelStyle = function comfortSetPixelStyle(element, property, value) {
      if (element !== playhead ||
          (property !== "left" && property !== "top" && property !== "height")) {
        return originalSetPixelStyle(element, property, value);
      }

      const numericValue = Number(value);
      if (!Number.isFinite(numericValue))
        return originalSetPixelStyle(element, property, value);

      if (!visualInitialized) initializePlayheadState();

      const previousLeftTarget = playheadTargetLeft;
      const previousTopTarget = playheadTargetTop;
      if (property === "left") playheadTargetLeft = numericValue;
      else if (property === "top") playheadTargetTop = numericValue;
      else playheadTargetHeight = numericValue;

      const verticalJump = property === "top" &&
        Math.abs(numericValue - playheadRenderedTop) >= verticalSystemJumpThresholdPx;
      const backwardJump = property === "left" &&
        numericValue < playheadRenderedLeft - backwardGeometrySnapThresholdPx;
      const viewportWidth = Math.max(1, Number(stageElement.clientWidth) || 1);
      const hugeHorizontalJump = property === "left" &&
        Math.abs(numericValue - playheadRenderedLeft) >= Math.max(240, viewportWidth * 0.55);
      const shouldSnap = !timelineIsRunning() || verticalJump || backwardJump || hugeHorizontalJump;

      if (shouldSnap) {
        playheadSnapPending = true;
        geometrySnapCount++;
      } else if ((property === "left" && Math.abs(numericValue - previousLeftTarget) > 0.5) ||
                 (property === "top" && Math.abs(numericValue - previousTopTarget) > 0.5)) {
        smoothedPlayheadTargets++;
      }

      scheduleVisualFrame();
      return undefined;
    };

    applyContinuousTransform = function comfortApplyContinuousTransform() {
      if (!isContinuousMode()) {
        transformInitialized = false;
        return originalApplyContinuousTransform();
      }

      const nextX = Number(continuousOffsetX) || 0;
      const nextY = Number(continuousOffsetY) || 0;
      if (!transformInitialized) {
        transformTargetX = nextX;
        transformTargetY = nextY;
        transformRenderedX = nextX;
        transformRenderedY = nextY;
        transformInitialized = true;
        applyRenderedTransform();
        return;
      }

      transformTargetX = nextX;
      transformTargetY = nextY;
      const viewportWidth = Math.max(1, Number(stageElement.clientWidth) || 1);
      const backwardReposition = nextX > transformRenderedX + backwardGeometrySnapThresholdPx;
      const hugeReposition = Math.abs(nextX - transformRenderedX) >= Math.max(280, viewportWidth * 0.55);
      if (!timelineIsRunning() || backwardReposition || hugeReposition) {
        transformRenderedX = transformTargetX;
        transformRenderedY = transformTargetY;
        geometrySnapCount++;
        applyRenderedTransform();
        return;
      }

      scheduleVisualFrame();
    };

    function resetMotionState() {
      cancelAllMotion();
      beatInitialized = false;
      visualInitialized = false;
      transformInitialized = false;
      playheadSnapPending = false;
    }

    if (originalSetReadingMode) {
      api.setReadingMode = function comfortSetReadingMode(mode) {
        resetMotionState();
        const result = originalSetReadingMode(mode);
        setTimeout(snapVisualState, 0);
        return result;
      };
    }

    if (originalLoadScore) {
      api.loadScore = function comfortLoadScore(...args) {
        resetMotionState();
        targetBeat = 0;
        renderedBeat = 0;
        return originalLoadScore(...args);
      };
    }

    if (originalBeginTimeline) {
      api.beginTimeline = function comfortBeginTimeline(...args) {
        resetMotionState();
        return originalBeginTimeline(...args);
      };
    }

    if (originalEndTimeline) {
      api.endTimeline = function comfortEndTimeline(...args) {
        if (beatInitialized && isContinuousMode() && targetBeat > renderedBeat + epsilon)
          renderBeatImmediately(targetBeat, false);
        else
          cancelBeatFrame();
        if (visualInitialized) {
          playheadSnapPending = true;
          scheduleVisualFrame();
        } else {
          cancelVisualFrame();
        }
        return originalEndTimeline(...args);
      };
    }

    if (originalStopPlayback) {
      api.stopPlayback = function comfortStopPlayback(...args) {
        cancelAllMotion();
        return originalStopPlayback(...args);
      };
    }

    if (originalGetState) {
      api.getState = function comfortGetState(...args) {
        const state = originalGetState(...args) || {};
        return {
          ...state,
          comfortMotion: {
            installed: true,
            continuous: isContinuousMode(),
            beatInitialized,
            targetBeat,
            renderedBeat,
            beatLag: Math.max(0, targetBeat - renderedBeat),
            beatFrameSamples,
            visualFrameSamples,
            continuousTransformFrames,
            pageModeVisualFrames,
            smoothedPlayheadTargets,
            geometrySnapCount,
            immediateBeatSnapCount,
            maximumObservedBeatLag,
            maximumObservedPlayheadLagPx,
            maximumObservedTransformLagPx,
            beatSmoothingTimeConstantMs,
            geometrySmoothingTimeConstantMs,
            maximumVisualLagBeats,
            maximumPlayheadLagPx,
            maximumTransformLagPx,
            largeSeekThresholdBeats,
            verticalSystemJumpThresholdPx,
            backwardGeometrySnapThresholdPx
          }
        };
      };
    }
  }

  install();
})();
