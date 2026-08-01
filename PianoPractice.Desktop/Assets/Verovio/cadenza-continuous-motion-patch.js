(() => {
  "use strict";

  const playheadResponseSeconds = 0.09;
  const playheadMaximumSpeedPxPerSecond = 900;
  const playheadMaximumAccelerationPxPerSecondSquared = 5600;
  const playheadMaximumVisibleStepPx = 7.5;
  const playheadVerticalMaximumVisibleStepPx = 6;
  const playheadHeightMaximumVisibleStepPx = 6;
  const opacitySpeedPerSecond = 16;

  const sheetResponseSeconds = 0.12;
  const sheetMaximumSpeedPxPerSecond = 1200;
  const sheetMaximumAccelerationPxPerSecondSquared = 7600;
  const sheetMaximumVisibleStepPx = 10;

  const verticalRelocationThresholdPx = 36;
  const backwardRelocationThresholdPx = 72;
  const settledPositionEpsilon = 0.015;
  const settledVelocityEpsilon = 0.3;

  function install() {
    const api = window.CadenzaNotation;
    const stageElement = document.getElementById("stage");
    if (!api?.setCursorBeat || !stageElement ||
        typeof playhead === "undefined" || !playhead ||
        typeof notation === "undefined" || !notation ||
        typeof applyContinuousTransform !== "function" ||
        typeof setPixelStyle !== "function" ||
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

    const proxy = createVisualPlayhead(playhead);
    if (!proxy) return;

    let frameHandle = 0;
    let lastFrameTimestamp = 0;
    let initialized = false;
    let relocationPhase = "none";

    let targetX = 0;
    let targetY = 0;
    let targetHeight = 1;
    let targetOpacity = 0;
    let currentX = 0;
    let currentY = 0;
    let currentHeight = 1;
    let currentOpacity = 0;
    let velocityX = 0;
    let velocityY = 0;
    let velocityHeight = 0;

    let sheetInitialized = false;
    let sheetTargetX = 0;
    let sheetTargetY = 0;
    let sheetCurrentX = 0;
    let sheetCurrentY = 0;
    let sheetVelocityX = 0;
    let sheetVelocityY = 0;

    let visualFrames = 0;
    let pageModeFrames = 0;
    let continuousModeFrames = 0;
    let relocationFadeCount = 0;
    let directWriteObservations = 0;
    let maximumVisiblePlayheadStepPx = 0;
    let previousVisibleX = null;

    function createVisualPlayhead(source) {
      const parent = source.parentElement;
      if (!parent || typeof source.cloneNode !== "function") return null;

      const visual = source.cloneNode(true);
      visual.removeAttribute?.("id");
      visual.setAttribute?.("aria-hidden", "true");
      visual.setAttribute?.("data-cadenza-comfort-playhead", "true");
      visual.classList?.add?.("cadenza-comfort-playhead");

      const computed = typeof getComputedStyle === "function" ? getComputedStyle(source) : null;
      for (const property of [
        "position", "width", "min-width", "max-width", "background",
        "background-color", "border", "border-radius", "box-shadow",
        "filter", "mix-blend-mode", "z-index"
      ]) {
        const value = computed?.getPropertyValue?.(property);
        if (value) visual.style.setProperty(property, value);
      }

      visual.style.setProperty("left", "0px", "important");
      visual.style.setProperty("top", "0px", "important");
      visual.style.setProperty("margin", "0", "important");
      visual.style.setProperty("pointer-events", "none", "important");
      visual.style.setProperty("transition", "none", "important");
      visual.style.setProperty("transform-origin", "0 0", "important");
      visual.style.setProperty("will-change", "transform,height,opacity", "important");
      visual.style.setProperty("visibility", "visible", "important");
      visual.style.setProperty("opacity", "0", "important");

      parent.appendChild(visual);
      source.setAttribute?.("aria-hidden", "true");
      source.style.setProperty("visibility", "hidden", "important");
      return visual;
    }

    function isContinuousMode() {
      return stageElement.classList.contains("continuous-mode");
    }

    function timelineIsRunning() {
      return typeof timelineRunning === "undefined" || Boolean(timelineRunning);
    }

    function clamp(value, minimum, maximum) {
      return Math.max(minimum, Math.min(maximum, value));
    }

    function moveTowards(current, target, maximumDelta) {
      const delta = target - current;
      if (Math.abs(delta) <= maximumDelta) return target;
      return current + Math.sign(delta) * maximumDelta;
    }

    function numericStyle(element, property, fallback = 0) {
      const inlineValue = Number.parseFloat(String(element.style?.[property] ?? ""));
      if (Number.isFinite(inlineValue)) return inlineValue;
      if (typeof getComputedStyle === "function") {
        const computedValue = Number.parseFloat(getComputedStyle(element).getPropertyValue(property));
        if (Number.isFinite(computedValue)) return computedValue;
      }
      return fallback;
    }

    function readAuthoritativeGeometry() {
      const computed = typeof getComputedStyle === "function" ? getComputedStyle(playhead) : null;
      const inlineOpacity = Number.parseFloat(String(playhead.style?.opacity ?? ""));
      const computedOpacity = Number.parseFloat(computed?.getPropertyValue?.("opacity") ?? "1");
      const display = computed?.getPropertyValue?.("display") || playhead.style?.display || "block";
      return {
        x: numericStyle(playhead, "left", targetX),
        y: numericStyle(playhead, "top", targetY),
        height: Math.max(1, numericStyle(playhead, "height", targetHeight || 1)),
        opacity: display === "none"
          ? 0
          : clamp(Number.isFinite(inlineOpacity) ? inlineOpacity :
              (Number.isFinite(computedOpacity) ? computedOpacity : 1), 0, 1)
      };
    }

    function advanceAxis(
      current,
      target,
      velocity,
      deltaSeconds,
      responseSeconds,
      maximumSpeed,
      maximumAcceleration,
      maximumVisibleStep) {
      const error = target - current;
      if (Math.abs(error) <= settledPositionEpsilon &&
          Math.abs(velocity) <= settledVelocityEpsilon) {
        return { position: target, velocity: 0 };
      }

      const desiredVelocity = clamp(
        error / Math.max(0.01, responseSeconds),
        -maximumSpeed,
        maximumSpeed);
      let nextVelocity = moveTowards(
        velocity,
        desiredVelocity,
        maximumAcceleration * deltaSeconds);
      let displacement = nextVelocity * deltaSeconds;
      displacement = clamp(displacement, -maximumVisibleStep, maximumVisibleStep);

      if (Math.abs(displacement) >= Math.abs(error))
        return { position: target, velocity: 0 };

      if (deltaSeconds > 0)
        nextVelocity = displacement / deltaSeconds;
      return { position: current + displacement, velocity: nextVelocity };
    }

    function applyVisualPlayhead() {
      proxy.style.setProperty(
        "transform",
        `translate3d(${currentX.toFixed(3)}px, ${currentY.toFixed(3)}px, 0)`,
        "important");
      proxy.style.setProperty("height", `${currentHeight.toFixed(3)}px`, "important");
      proxy.style.setProperty("opacity", currentOpacity.toFixed(4), "important");

      if (currentOpacity <= 0.08) {
        previousVisibleX = null;
        return;
      }
      if (previousVisibleX != null) {
        maximumVisiblePlayheadStepPx = Math.max(
          maximumVisiblePlayheadStepPx,
          Math.abs(currentX - previousVisibleX));
      }
      previousVisibleX = currentX;
    }

    function applySheetTransform() {
      const zoom = Math.max(0.01, Number(typeof userZoom === "undefined" ? 1 : userZoom) || 1);
      const transform =
        `translate3d(${sheetCurrentX.toFixed(3)}px, ${sheetCurrentY.toFixed(3)}px, 0) scale(${zoom})`;
      notation.style.transform = transform;
      if (typeof practiceWrongNote !== "undefined" && practiceWrongNote)
        practiceWrongNote.style.transform = isContinuousMode() ? transform : "";
    }

    function synchronizeImmediately() {
      const geometry = readAuthoritativeGeometry();
      targetX = currentX = geometry.x;
      targetY = currentY = geometry.y;
      targetHeight = currentHeight = geometry.height;
      targetOpacity = currentOpacity = geometry.opacity;
      velocityX = velocityY = velocityHeight = 0;
      relocationPhase = "none";
      initialized = true;
      previousVisibleX = null;
      applyVisualPlayhead();
    }

    function geometryRequiresRelocation(next) {
      const viewportWidth = Math.max(1, Number(stageElement.clientWidth) || 1);
      return Math.abs(next.y - currentY) >= verticalRelocationThresholdPx ||
        next.x < currentX - backwardRelocationThresholdPx ||
        Math.abs(next.x - currentX) >= Math.max(260, viewportWidth * 0.58);
    }

    function updateTargetFromAuthoritativeSource(allowRelocation = true) {
      const next = readAuthoritativeGeometry();
      if (!initialized) {
        targetX = currentX = next.x;
        targetY = currentY = next.y;
        targetHeight = currentHeight = next.height;
        targetOpacity = currentOpacity = next.opacity;
        initialized = true;
        previousVisibleX = null;
        applyVisualPlayhead();
        return;
      }

      if (allowRelocation && relocationPhase === "none" && timelineIsRunning() &&
          geometryRequiresRelocation(next)) {
        relocationPhase = "fadeOut";
        relocationFadeCount++;
      }

      targetX = next.x;
      targetY = next.y;
      targetHeight = next.height;
      targetOpacity = next.opacity;
    }

    function scheduleFrame() {
      if (!frameHandle) frameHandle = requestAnimationFrame(renderFrame);
    }

    function renderFrame(timestamp) {
      frameHandle = 0;
      const frameGapMilliseconds = lastFrameTimestamp > 0
        ? clamp(timestamp - lastFrameTimestamp, 1, 34)
        : 16.667;
      lastFrameTimestamp = timestamp;
      const deltaSeconds = frameGapMilliseconds / 1000;
      visualFrames++;
      if (isContinuousMode()) continuousModeFrames++;
      else pageModeFrames++;

      updateTargetFromAuthoritativeSource(true);

      if (isContinuousMode() && sheetInitialized) {
        const x = advanceAxis(
          sheetCurrentX,
          sheetTargetX,
          sheetVelocityX,
          deltaSeconds,
          sheetResponseSeconds,
          sheetMaximumSpeedPxPerSecond,
          sheetMaximumAccelerationPxPerSecondSquared,
          sheetMaximumVisibleStepPx);
        sheetCurrentX = x.position;
        sheetVelocityX = x.velocity;

        const y = advanceAxis(
          sheetCurrentY,
          sheetTargetY,
          sheetVelocityY,
          deltaSeconds,
          0.09,
          900,
          6200,
          8);
        sheetCurrentY = y.position;
        sheetVelocityY = y.velocity;
        applySheetTransform();
      }

      if (relocationPhase === "fadeOut") {
        currentOpacity = moveTowards(currentOpacity, 0, opacitySpeedPerSecond * deltaSeconds);
        if (currentOpacity <= 0.025) {
          currentOpacity = 0;
          currentX = targetX;
          currentY = targetY;
          currentHeight = targetHeight;
          velocityX = velocityY = velocityHeight = 0;
          previousVisibleX = null;
          relocationPhase = "fadeIn";
        }
      } else {
        const x = advanceAxis(
          currentX,
          targetX,
          velocityX,
          deltaSeconds,
          playheadResponseSeconds,
          playheadMaximumSpeedPxPerSecond,
          playheadMaximumAccelerationPxPerSecondSquared,
          playheadMaximumVisibleStepPx);
        currentX = x.position;
        velocityX = x.velocity;

        const y = advanceAxis(
          currentY,
          targetY,
          velocityY,
          deltaSeconds,
          0.075,
          900,
          6400,
          playheadVerticalMaximumVisibleStepPx);
        currentY = y.position;
        velocityY = y.velocity;

        const height = advanceAxis(
          currentHeight,
          targetHeight,
          velocityHeight,
          deltaSeconds,
          0.08,
          1000,
          6800,
          playheadHeightMaximumVisibleStepPx);
        currentHeight = height.position;
        velocityHeight = height.velocity;

        currentOpacity = moveTowards(
          currentOpacity,
          targetOpacity,
          opacitySpeedPerSecond * deltaSeconds);
        if (relocationPhase === "fadeIn" &&
            Math.abs(currentOpacity - targetOpacity) <= 0.005)
          relocationPhase = "none";
      }

      applyVisualPlayhead();

      const playheadPending = relocationPhase !== "none" ||
        Math.abs(targetX - currentX) > settledPositionEpsilon ||
        Math.abs(targetY - currentY) > settledPositionEpsilon ||
        Math.abs(targetHeight - currentHeight) > settledPositionEpsilon ||
        Math.abs(targetOpacity - currentOpacity) > 0.005 ||
        Math.abs(velocityX) > settledVelocityEpsilon ||
        Math.abs(velocityY) > settledVelocityEpsilon ||
        Math.abs(velocityHeight) > settledVelocityEpsilon;
      const sheetPending = isContinuousMode() && sheetInitialized &&
        (Math.abs(sheetTargetX - sheetCurrentX) > settledPositionEpsilon ||
         Math.abs(sheetTargetY - sheetCurrentY) > settledPositionEpsilon ||
         Math.abs(sheetVelocityX) > settledVelocityEpsilon ||
         Math.abs(sheetVelocityY) > settledVelocityEpsilon);

      if (playheadPending || sheetPending) scheduleFrame();
      else lastFrameTimestamp = 0;
    }

    setPixelStyle = function comfortSetPixelStyle(element, property, value) {
      const result = originalSetPixelStyle(element, property, value);
      if (element === playhead &&
          (property === "left" || property === "top" || property === "height")) {
        updateTargetFromAuthoritativeSource(true);
        if (!timelineIsRunning()) synchronizeImmediately();
        else scheduleFrame();
      }
      return result;
    };

    applyContinuousTransform = function comfortApplyContinuousTransform() {
      if (!isContinuousMode()) {
        sheetInitialized = false;
        sheetVelocityX = sheetVelocityY = 0;
        return originalApplyContinuousTransform();
      }

      const nextX = Number(continuousOffsetX) || 0;
      const nextY = Number(continuousOffsetY) || 0;
      if (!sheetInitialized) {
        sheetTargetX = sheetCurrentX = nextX;
        sheetTargetY = sheetCurrentY = nextY;
        sheetVelocityX = sheetVelocityY = 0;
        sheetInitialized = true;
        applySheetTransform();
        return;
      }

      const viewportWidth = Math.max(1, Number(stageElement.clientWidth) || 1);
      const repeatRewind = nextX > sheetCurrentX + backwardRelocationThresholdPx;
      const hugeReposition = Math.abs(nextX - sheetCurrentX) >= Math.max(300, viewportWidth * 0.6);
      sheetTargetX = nextX;
      sheetTargetY = nextY;
      if (!timelineIsRunning() || repeatRewind || hugeReposition) {
        sheetCurrentX = sheetTargetX;
        sheetCurrentY = sheetTargetY;
        sheetVelocityX = sheetVelocityY = 0;
        applySheetTransform();
        return;
      }
      scheduleFrame();
    };

    api.setCursorBeat = function comfortSetCursorBeat(beat, reset = false) {
      const result = originalSetCursorBeat(Math.max(0, Number(beat) || 0), reset);
      updateTargetFromAuthoritativeSource(!reset);
      if (reset || !timelineIsRunning()) synchronizeImmediately();
      else scheduleFrame();
      return result;
    };

    function resetMotionState() {
      if (frameHandle) cancelAnimationFrame(frameHandle);
      frameHandle = 0;
      lastFrameTimestamp = 0;
      initialized = false;
      sheetInitialized = false;
      relocationPhase = "none";
      velocityX = velocityY = velocityHeight = 0;
      sheetVelocityX = sheetVelocityY = 0;
      previousVisibleX = null;
    }

    if (originalSetReadingMode) {
      api.setReadingMode = function comfortSetReadingMode(mode) {
        resetMotionState();
        const result = originalSetReadingMode(mode);
        setTimeout(synchronizeImmediately, 0);
        return result;
      };
    }

    if (originalLoadScore) {
      api.loadScore = function comfortLoadScore(...args) {
        resetMotionState();
        const result = originalLoadScore(...args);
        setTimeout(synchronizeImmediately, 0);
        return result;
      };
    }

    if (originalBeginTimeline) {
      api.beginTimeline = function comfortBeginTimeline(...args) {
        resetMotionState();
        const result = originalBeginTimeline(...args);
        setTimeout(synchronizeImmediately, 0);
        return result;
      };
    }

    if (originalEndTimeline) {
      api.endTimeline = function comfortEndTimeline(...args) {
        const result = originalEndTimeline(...args);
        updateTargetFromAuthoritativeSource(false);
        scheduleFrame();
        return result;
      };
    }

    if (originalStopPlayback) {
      api.stopPlayback = function comfortStopPlayback(...args) {
        const result = originalStopPlayback(...args);
        updateTargetFromAuthoritativeSource(false);
        scheduleFrame();
        return result;
      };
    }

    if (typeof MutationObserver === "function") {
      const observer = new MutationObserver(() => {
        if (playhead.style.getPropertyValue("visibility") !== "hidden" ||
            playhead.style.getPropertyPriority("visibility") !== "important") {
          playhead.style.setProperty("visibility", "hidden", "important");
        }
        directWriteObservations++;
        updateTargetFromAuthoritativeSource(true);
        if (!timelineIsRunning()) synchronizeImmediately();
        else scheduleFrame();
      });
      observer.observe(playhead, {
        attributes: true,
        attributeFilter: ["style", "class"]
      });
    }

    if (originalGetState) {
      api.getState = function comfortGetState(...args) {
        const state = originalGetState(...args) || {};
        return {
          ...state,
          comfortMotion: {
            installed: true,
            visualProxyInstalled: Boolean(proxy?.isConnected ?? true),
            continuous: isContinuousMode(),
            relocationPhase,
            visualFrames,
            pageModeFrames,
            continuousModeFrames,
            relocationFadeCount,
            directWriteObservations,
            maximumVisiblePlayheadStepPx,
            targetX,
            currentX,
            targetY,
            currentY,
            playheadResponseSeconds,
            playheadMaximumVisibleStepPx,
            playheadMaximumSpeedPxPerSecond,
            playheadMaximumAccelerationPxPerSecondSquared,
            sheetResponseSeconds
          }
        };
      };
    }

    synchronizeImmediately();
  }

  install();
})();
