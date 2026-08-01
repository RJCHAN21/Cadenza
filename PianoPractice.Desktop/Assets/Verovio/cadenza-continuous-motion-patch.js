(() => {
  "use strict";

  const playheadSmoothTimeSeconds = 0.085;
  const playheadMaximumSpeedPxPerSecond = 2200;
  const playheadMaximumAccelerationPxPerSecondSquared = 18000;
  const playheadOpacitySmoothTimeSeconds = 0.045;
  const sheetSmoothTimeSeconds = 0.12;
  const sheetMaximumSpeedPxPerSecond = 1500;
  const sheetMaximumAccelerationPxPerSecondSquared = 10000;
  const verticalRelocationThresholdPx = 36;
  const backwardRelocationThresholdPx = 72;

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
    let targetHeight = 0;
    let targetOpacity = 0;
    let currentX = 0;
    let currentY = 0;
    let currentHeight = 0;
    let currentOpacity = 0;
    let velocityX = 0;
    let velocityY = 0;
    let velocityHeight = 0;
    let velocityOpacity = 0;

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
      const copiedProperties = [
        "position", "width", "min-width", "max-width", "background",
        "background-color", "border", "border-radius", "box-shadow",
        "filter", "mix-blend-mode", "z-index"
      ];
      for (const property of copiedProperties) {
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
          : Math.max(0, Math.min(1, Number.isFinite(inlineOpacity) ? inlineOpacity :
              (Number.isFinite(computedOpacity) ? computedOpacity : 1)))
      };
    }

    function moveTowards(current, target, maximumDelta) {
      const delta = target - current;
      if (Math.abs(delta) <= maximumDelta) return target;
      return current + Math.sign(delta) * maximumDelta;
    }

    function smoothDamp(current, target, velocity, smoothTime, maximumSpeed, deltaSeconds) {
      const safeSmoothTime = Math.max(0.0001, smoothTime);
      const omega = 2 / safeSmoothTime;
      const x = omega * deltaSeconds;
      const exponential = 1 / (1 + x + 0.48 * x * x + 0.235 * x * x * x);
      let change = current - target;
      const originalTarget = target;
      const maximumChange = maximumSpeed * safeSmoothTime;
      change = Math.max(-maximumChange, Math.min(maximumChange, change));
      target = current - change;
      const temporary = (velocity + omega * change) * deltaSeconds;
      let nextVelocity = (velocity - omega * temporary) * exponential;
      let output = target + (change + temporary) * exponential;

      if ((originalTarget - current > 0) === (output > originalTarget)) {
        output = originalTarget;
        nextVelocity = 0;
      }
      return { position: output, velocity: nextVelocity };
    }

    function applyVisualPlayhead() {
      proxy.style.setProperty(
        "transform",
        `translate3d(${currentX.toFixed(3)}px, ${currentY.toFixed(3)}px, 0)`,
        "important");
      proxy.style.setProperty("height", `${currentHeight.toFixed(3)}px`, "important");
      proxy.style.setProperty("opacity", currentOpacity.toFixed(4), "important");

      if (currentOpacity > 0.08 && previousVisibleX != null) {
        maximumVisiblePlayheadStepPx = Math.max(
          maximumVisiblePlayheadStepPx,
          Math.abs(currentX - previousVisibleX));
      }
      if (currentOpacity > 0.08) previousVisibleX = currentX;
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
      velocityX = velocityY = velocityHeight = velocityOpacity = 0;
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
        ? Math.max(1, Math.min(34, timestamp - lastFrameTimestamp))
        : 16.667;
      lastFrameTimestamp = timestamp;
      const deltaSeconds = frameGapMilliseconds / 1000;
      visualFrames++;
      if (isContinuousMode()) continuousModeFrames++;
      else pageModeFrames++;

      updateTargetFromAuthoritativeSource(true);

      if (isContinuousMode() && sheetInitialized) {
        const sheetX = smoothDamp(
          sheetCurrentX,
          sheetTargetX,
          sheetVelocityX,
          sheetSmoothTimeSeconds,
          sheetMaximumSpeedPxPerSecond,
          deltaSeconds);
        sheetCurrentX = sheetX.position;
        sheetVelocityX = moveTowards(
          sheetVelocityX,
          sheetX.velocity,
          sheetMaximumAccelerationPxPerSecondSquared * deltaSeconds);

        const sheetY = smoothDamp(
          sheetCurrentY,
          sheetTargetY,
          sheetVelocityY,
          0.09,
          900,
          deltaSeconds);
        sheetCurrentY = sheetY.position;
        sheetVelocityY = sheetY.velocity;
        applySheetTransform();
      }

      if (relocationPhase === "fadeOut") {
        const opacity = smoothDamp(
          currentOpacity,
          0,
          velocityOpacity,
          0.035,
          20,
          deltaSeconds);
        currentOpacity = opacity.position;
        velocityOpacity = opacity.velocity;
        if (currentOpacity <= 0.025) {
          currentX = targetX;
          currentY = targetY;
          currentHeight = targetHeight;
          velocityX = velocityY = velocityHeight = 0;
          relocationPhase = "fadeIn";
          previousVisibleX = null;
        }
      } else {
        const x = smoothDamp(
          currentX,
          targetX,
          velocityX,
          playheadSmoothTimeSeconds,
          playheadMaximumSpeedPxPerSecond,
          deltaSeconds);
        currentX = x.position;
        velocityX = moveTowards(
          velocityX,
          x.velocity,
          playheadMaximumAccelerationPxPerSecondSquared * deltaSeconds);

        const y = smoothDamp(
          currentY,
          targetY,
          velocityY,
          0.065,
          1100,
          deltaSeconds);
        currentY = y.position;
        velocityY = y.velocity;

        const height = smoothDamp(
          currentHeight,
          targetHeight,
          velocityHeight,
          0.075,
          1200,
          deltaSeconds);
        currentHeight = height.position;
        velocityHeight = height.velocity;

        const opacityTarget = relocationPhase === "fadeIn" ? targetOpacity : targetOpacity;
        const opacity = smoothDamp(
          currentOpacity,
          opacityTarget,
          velocityOpacity,
          playheadOpacitySmoothTimeSeconds,
          20,
          deltaSeconds);
        currentOpacity = opacity.position;
        velocityOpacity = opacity.velocity;

        if (relocationPhase === "fadeIn" &&
            Math.abs(currentOpacity - targetOpacity) < 0.01)
          relocationPhase = "none";
      }

      applyVisualPlayhead();

      const playheadPending = relocationPhase !== "none" ||
        Math.abs(targetX - currentX) > 0.015 ||
        Math.abs(targetY - currentY) > 0.015 ||
        Math.abs(targetHeight - currentHeight) > 0.015 ||
        Math.abs(targetOpacity - currentOpacity) > 0.005 ||
        Math.abs(velocityX) > 0.3 ||
        Math.abs(velocityY) > 0.3 ||
        Math.abs(velocityHeight) > 0.3 ||
        Math.abs(velocityOpacity) > 0.01;
      const sheetPending = isContinuousMode() && sheetInitialized &&
        (Math.abs(sheetTargetX - sheetCurrentX) > 0.015 ||
         Math.abs(sheetTargetY - sheetCurrentY) > 0.015 ||
         Math.abs(sheetVelocityX) > 0.3 ||
         Math.abs(sheetVelocityY) > 0.3);
      if (playheadPending || sheetPending) scheduleFrame();
      else lastFrameTimestamp = 0;
    }

    setPixelStyle = function comfortSetPixelStyle(element, property, value) {
      const result = originalSetPixelStyle(element, property, value);
      if (element === playhead &&
          (property === "left" || property === "top" || property === "height")) {
        updateTargetFromAuthoritativeSource(true);
        scheduleFrame();
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
      velocityX = velocityY = velocityHeight = velocityOpacity = 0;
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
        scheduleFrame();
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
            playheadSmoothTimeSeconds,
            playheadMaximumSpeedPxPerSecond,
            playheadMaximumAccelerationPxPerSecondSquared,
            sheetSmoothTimeSeconds
          }
        };
      };
    }

    synchronizeImmediately();
  }

  install();
})();