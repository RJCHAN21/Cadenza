(() => {
  "use strict";

  const epsilon = 0.0001;
  const smoothingTimeConstantMs = 52;
  const maximumVisualLagBeats = 0.22;
  const largeSeekThresholdBeats = 0.75;

  function install() {
    const api = window.CadenzaNotation;
    const stage = document.getElementById("stage");
    if (!api?.setCursorBeat || !stage) {
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

    let targetBeat = 0;
    let renderedBeat = 0;
    let initialized = false;
    let frameHandle = 0;
    let lastFrameTimestamp = 0;
    let frameSamples = 0;
    let maximumFrameGapMs = 0;
    let maximumObservedLagBeats = 0;
    let immediateSnapCount = 0;

    function isContinuousMode() {
      return stage.classList.contains("continuous-mode");
    }

    function cancelFrame() {
      if (frameHandle) cancelAnimationFrame(frameHandle);
      frameHandle = 0;
      lastFrameTimestamp = 0;
    }

    function renderImmediately(beat, reset) {
      cancelFrame();
      targetBeat = beat;
      renderedBeat = beat;
      initialized = true;
      immediateSnapCount++;
      return originalSetCursorBeat(beat, reset);
    }

    function scheduleFrame() {
      if (!frameHandle) frameHandle = requestAnimationFrame(renderFrame);
    }

    function renderFrame(timestamp) {
      frameHandle = 0;
      if (!initialized || !isContinuousMode()) return;

      const frameGap = lastFrameTimestamp > 0
        ? Math.max(1, Math.min(50, timestamp - lastFrameTimestamp))
        : 16.667;
      lastFrameTimestamp = timestamp;
      frameSamples++;
      maximumFrameGapMs = Math.max(maximumFrameGapMs, frameGap);

      const error = targetBeat - renderedBeat;
      maximumObservedLagBeats = Math.max(maximumObservedLagBeats, Math.abs(error));
      if (error < -epsilon) {
        renderImmediately(targetBeat, true);
        return;
      }

      if (Math.abs(error) <= epsilon) {
        renderedBeat = targetBeat;
        originalSetCursorBeat(renderedBeat, false);
        return;
      }

      const interpolation = 1 - Math.exp(-frameGap / smoothingTimeConstantMs);
      let nextBeat = renderedBeat + error * interpolation;
      if (targetBeat - nextBeat > maximumVisualLagBeats)
        nextBeat = targetBeat - maximumVisualLagBeats;
      nextBeat = Math.max(renderedBeat, Math.min(targetBeat, nextBeat));

      renderedBeat = nextBeat;
      originalSetCursorBeat(renderedBeat, false);

      if (targetBeat - renderedBeat > epsilon)
        scheduleFrame();
    }

    api.setCursorBeat = function comfortSetCursorBeat(beat, reset = false) {
      const requestedBeat = Math.max(0, Number(beat) || 0);
      const jump = initialized ? Math.abs(requestedBeat - renderedBeat) : 0;

      if (reset || !isContinuousMode() || !initialized ||
          requestedBeat + epsilon < renderedBeat ||
          jump >= largeSeekThresholdBeats) {
        return renderImmediately(requestedBeat, reset);
      }

      targetBeat = requestedBeat;
      scheduleFrame();
      return undefined;
    };

    if (originalSetReadingMode) {
      api.setReadingMode = function comfortSetReadingMode(mode) {
        cancelFrame();
        initialized = false;
        return originalSetReadingMode(mode);
      };
    }

    if (originalLoadScore) {
      api.loadScore = function comfortLoadScore(...args) {
        cancelFrame();
        initialized = false;
        targetBeat = 0;
        renderedBeat = 0;
        return originalLoadScore(...args);
      };
    }

    if (originalBeginTimeline) {
      api.beginTimeline = function comfortBeginTimeline(...args) {
        cancelFrame();
        initialized = false;
        return originalBeginTimeline(...args);
      };
    }

    if (originalEndTimeline) {
      api.endTimeline = function comfortEndTimeline(...args) {
        if (initialized && isContinuousMode() && targetBeat > renderedBeat + epsilon)
          renderImmediately(targetBeat, false);
        else
          cancelFrame();
        return originalEndTimeline(...args);
      };
    }

    if (originalStopPlayback) {
      api.stopPlayback = function comfortStopPlayback(...args) {
        cancelFrame();
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
            initialized,
            targetBeat,
            renderedBeat,
            lagBeats: Math.max(0, targetBeat - renderedBeat),
            frameSamples,
            maximumFrameGapMs,
            maximumObservedLagBeats,
            immediateSnapCount,
            smoothingTimeConstantMs,
            maximumVisualLagBeats,
            largeSeekThresholdBeats
          }
        };
      };
    }
  }

  install();
})();