(() => {
  "use strict";

  const playheadResponseSeconds = 0.11;
  const playheadMaximumSpeedPxPerSecond = 1100;
  const playheadMaximumAccelerationPxPerSecondSquared = 8200;
  const transformResponseSeconds = 0.13;
  const transformMaximumSpeedPxPerSecond = 1350;
  const transformMaximumAccelerationPxPerSecondSquared = 9200;
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

    let visualFrameHandle = 0;
    let visualLastFrameTimestamp = 0;
    let visualInitialized = false;
    let transformInitialized = false;

    let transformTargetX = 0;
    let transformTargetY = 0;
    let transformRenderedX = 0;
    let transformRenderedY = 0;
    let transformVelocityX = 0;
    let transformVelocityY = 0;

    let playheadTargetLeft = 0;
    let playheadTargetTop = 0;
    let playheadTargetHeight = 0;
    let playheadRenderedLeft = 0;
    let playheadRenderedTop = 0;
    let playheadRenderedHeight = 0;
    let playheadVelocityLeft = 0;
    let playheadVelocityTop = 0;
    let playheadVelocityHeight = 0;
    let playheadSnapPending = false;

    let visualFrameSamples = 0;
    let continuousTransformFrames = 0;
    let pageModeVisualFrames = 0;
    let smoothedPlayheadTargets = 0;
    let geometrySnapCount = 0;
    let maximumObservedPlayheadStepPx = 0;
    let maximumObservedTransformStepPx = 0;
    let previousAppliedPlayheadLeft = null;
    let previousAppliedTransformX = null;
    let visualSnapAppliedThisFrame = false;

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

    function cancelVisualFrame() {
      if (visualFrameHandle) cancelAnimationFrame(visualFrameHandle);
      visualFrameHandle = 0;
      visualLastFrameTimestamp = 0;
    }

    function scheduleVisualFrame() {
      if (!visualFrameHandle) visualFrameHandle = requestAnimationFrame(renderVisualFrame);
    }

    function moveTowards(current, target, maximumDelta) {
      const delta = target - current;
      if (Math.abs(delta) <= maximumDelta) return target;
      return current + Math.sign(delta) * maximumDelta;
    }

    function advanceAxis(
      current,
      target,
      velocity,
      deltaSeconds,
      responseSeconds,
      maximumSpeed,
      maximumAcceleration,
      positionEpsilon = 0.02) {
      const error = target - current;
      if (Math.abs(error) <= positionEpsilon && Math.abs(velocity) <= 0.5)
        return { position: target, velocity: 0 };

      const desiredVelocity = Math.max(
        -maximumSpeed,
        Math.min(maximumSpeed, error / Math.max(0.01, responseSeconds)));
      const nextVelocity = moveTowards(
        velocity,
        desiredVelocity,
        maximumAcceleration * deltaSeconds);
      const nextPosition = current + nextVelocity * deltaSeconds;

      if ((target - current) * (target - nextPosition) <= 0)
        return { position: target, velocity: 0 };

      return { position: nextPosition, velocity: nextVelocity };
    }

    function applyRenderedTransform() {
      const zoom = Math.max(0.01, Number(typeof userZoom === "undefined" ? 1 : userZoom) || 1);
      const transform =
        `translate3d(${transformRenderedX.toFixed(3)}px, ${transformRenderedY.toFixed(3)}px, 0) scale(${zoom})`;
      notation.style.transform = transform;
      if (typeof practiceWrongNote !== "undefined" && practiceWrongNote) {
        practiceWrongNote.style.transform = isContinuousMode() ? transform : "";
      }
      if (previousAppliedTransformX != null) {
        maximumObservedTransformStepPx = Math.max(
          maximumObservedTransformStepPx,
          Math.abs(transformRenderedX - previousAppliedTransformX));
      }
      previousAppliedTransformX = transformRenderedX;
    }

    function applyRenderedPlayhead() {
      originalSetPixelStyle(playhead, "left", playheadRenderedLeft);
      originalSetPixelStyle(playhead, "top", playheadRenderedTop);
      originalSetPixelStyle(playhead, "height", playheadRenderedHeight);
      if (!visualSnapAppliedThisFrame && previousAppliedPlayheadLeft != null) {
        maximumObservedPlayheadStepPx = Math.max(
          maximumObservedPlayheadStepPx,
          Math.abs(playheadRenderedLeft - previousAppliedPlayheadLeft));
      }
      previousAppliedPlayheadLeft = playheadRenderedLeft;
    }

    function initializeTransformState() {
      transformTargetX = Number(continuousOffsetX) || 0;
      transformTargetY = Number(continuousOffsetY) || 0;
      transformRenderedX = transformTargetX;
      transformRenderedY = transformTargetY;
      transformVelocityX = 0;
      transformVelocityY = 0;
      transformInitialized = true;
      previousAppliedTransformX = transformRenderedX;
    }

    function initializePlayheadState() {
      playheadTargetLeft = pixelValue(playhead.style.left);
      playheadTargetTop = pixelValue(playhead.style.top);
      playheadTargetHeight = pixelValue(playhead.style.height);
      playheadRenderedLeft = playheadTargetLeft;
      playheadRenderedTop = playheadTargetTop;
      playheadRenderedHeight = playheadTargetHeight;
      playheadVelocityLeft = 0;
      playheadVelocityTop = 0;
      playheadVelocityHeight = 0;
      playheadSnapPending = false;
      visualInitialized = true;
      previousAppliedPlayheadLeft = playheadRenderedLeft;
    }

    function snapVisualState() {
      cancelVisualFrame();
      initializeTransformState();
      initializePlayheadState();
      if (isContinuousMode()) applyRenderedTransform();
      applyRenderedPlayhead();
    }

    function renderVisualFrame(timestamp) {
      visualFrameHandle = 0;
      if (!visualInitialized) initializePlayheadState();
      if (isContinuousMode() && !transformInitialized) initializeTransformState();

      const frameGapMilliseconds = visualLastFrameTimestamp > 0
        ? Math.max(1, Math.min(34, timestamp - visualLastFrameTimestamp))
        : 16.667;
      visualLastFrameTimestamp = timestamp;
      const deltaSeconds = frameGapMilliseconds / 1000;
      visualFrameSamples++;
      visualSnapAppliedThisFrame = false;
      if (isContinuousMode()) continuousTransformFrames++;
      else pageModeVisualFrames++;

      if (isContinuousMode() && transformInitialized) {
        const x = advanceAxis(
          transformRenderedX,
          transformTargetX,
          transformVelocityX,
          deltaSeconds,
          transformResponseSeconds,
          transformMaximumSpeedPxPerSecond,
          transformMaximumAccelerationPxPerSecondSquared);
        transformRenderedX = x.position;
        transformVelocityX = x.velocity;

        const y = advanceAxis(
          transformRenderedY,
          transformTargetY,
          transformVelocityY,
          deltaSeconds,
          0.09,
          700,
          6400);
        transformRenderedY = y.position;
        transformVelocityY = y.velocity;
        applyRenderedTransform();
      }

      if (playheadSnapPending) {
        playheadRenderedLeft = playheadTargetLeft;
        playheadRenderedTop = playheadTargetTop;
        playheadRenderedHeight = playheadTargetHeight;
        playheadVelocityLeft = 0;
        playheadVelocityTop = 0;
        playheadVelocityHeight = 0;
        playheadSnapPending = false;
        visualSnapAppliedThisFrame = true;
      } else {
        const left = advanceAxis(
          playheadRenderedLeft,
          playheadTargetLeft,
          playheadVelocityLeft,
          deltaSeconds,
          playheadResponseSeconds,
          playheadMaximumSpeedPxPerSecond,
          playheadMaximumAccelerationPxPerSecondSquared);
        playheadRenderedLeft = left.position;
        playheadVelocityLeft = left.velocity;

        const top = advanceAxis(
          playheadRenderedTop,
          playheadTargetTop,
          playheadVelocityTop,
          deltaSeconds,
          0.085,
          650,
          6200);
        playheadRenderedTop = top.position;
        playheadVelocityTop = top.velocity;

        const height = advanceAxis(
          playheadRenderedHeight,
          playheadTargetHeight,
          playheadVelocityHeight,
          deltaSeconds,
          0.09,
          720,
          6600);
        playheadRenderedHeight = height.position;
        playheadVelocityHeight = height.velocity;
      }

      applyRenderedPlayhead();

      const transformPending = isContinuousMode() &&
        (Math.abs(transformTargetX - transformRenderedX) > 0.02 ||
         Math.abs(transformTargetY - transformRenderedY) > 0.02 ||
         Math.abs(transformVelocityX) > 0.5 ||
         Math.abs(transformVelocityY) > 0.5);
      const playheadPending =
        Math.abs(playheadTargetLeft - playheadRenderedLeft) > 0.02 ||
        Math.abs(playheadTargetTop - playheadRenderedTop) > 0.02 ||
        Math.abs(playheadTargetHeight - playheadRenderedHeight) > 0.02 ||
        Math.abs(playheadVelocityLeft) > 0.5 ||
        Math.abs(playheadVelocityTop) > 0.5 ||
        Math.abs(playheadVelocityHeight) > 0.5;
      if (transformPending || playheadPending)
        scheduleVisualFrame();
    }

    api.setCursorBeat = function comfortSetCursorBeat(beat, reset = false) {
      const requestedBeat = Math.max(0, Number(beat) || 0);
      if (reset) {
        playheadSnapPending = true;
        transformVelocityX = 0;
        transformVelocityY = 0;
      }
      return originalSetCursorBeat(requestedBeat, reset);
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
        transformVelocityX = 0;
        transformVelocityY = 0;
        return originalApplyContinuousTransform();
      }

      const nextX = Number(continuousOffsetX) || 0;
      const nextY = Number(continuousOffsetY) || 0;
      if (!transformInitialized) {
        transformTargetX = nextX;
        transformTargetY = nextY;
        transformRenderedX = nextX;
        transformRenderedY = nextY;
        transformVelocityX = 0;
        transformVelocityY = 0;
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
        transformVelocityX = 0;
        transformVelocityY = 0;
        geometrySnapCount++;
        applyRenderedTransform();
        return;
      }

      scheduleVisualFrame();
    };

    function resetMotionState() {
      cancelVisualFrame();
      visualInitialized = false;
      transformInitialized = false;
      playheadSnapPending = false;
      playheadVelocityLeft = 0;
      playheadVelocityTop = 0;
      playheadVelocityHeight = 0;
      transformVelocityX = 0;
      transformVelocityY = 0;
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
        playheadSnapPending = true;
        scheduleVisualFrame();
        return originalEndTimeline(...args);
      };
    }

    if (originalStopPlayback) {
      api.stopPlayback = function comfortStopPlayback(...args) {
        cancelVisualFrame();
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
            visualFrameSamples,
            continuousTransformFrames,
            pageModeVisualFrames,
            smoothedPlayheadTargets,
            geometrySnapCount,
            maximumObservedPlayheadStepPx,
            maximumObservedTransformStepPx,
            playheadResponseSeconds,
            playheadMaximumSpeedPxPerSecond,
            playheadMaximumAccelerationPxPerSecondSquared,
            transformResponseSeconds,
            transformMaximumSpeedPxPerSecond,
            transformMaximumAccelerationPxPerSecondSquared,
            verticalSystemJumpThresholdPx,
            backwardGeometrySnapThresholdPx
          }
        };
      };
    }
  }

  install();
})();