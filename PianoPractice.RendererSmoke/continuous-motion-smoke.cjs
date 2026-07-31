const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const projectRoot = path.resolve(__dirname, '..');
const patchPath = path.join(
  projectRoot,
  'PianoPractice.Desktop',
  'Assets',
  'Verovio',
  'cadenza-continuous-motion-patch.js');
const patchSource = fs.readFileSync(patchPath, 'utf8');
let nextFrameId = 1;
const queuedFrames = new Map();

const context = {
  console,
  Math,
  Number,
  Object,
  String,
  Boolean,
  setTimeout(callback) {
    callback();
    return 1;
  },
  requestAnimationFrame(callback) {
    const id = nextFrameId++;
    queuedFrames.set(id, callback);
    return id;
  },
  cancelAnimationFrame(id) {
    queuedFrames.delete(id);
  }
};
context.globalThis = context;
vm.createContext(context);
vm.runInContext(`
  var readingMode = "Continuous";
  var timelineRunning = true;
  var continuousOffsetX = 24;
  var continuousOffsetY = 0;
  var userZoom = 1;
  var renderedBeats = [];
  var pixelSamples = [];
  var transformSamples = [];
  var stage = {
    clientWidth: 1000,
    classList: {
      contains(name) { return name === "continuous-mode" && readingMode === "Continuous"; }
    }
  };
  var notation = { style: { transform: "" } };
  var practiceWrongNote = { style: { transform: "" } };
  var playhead = { style: { left: "0px", top: "20px", height: "100px", opacity: "1" } };
  var document = {
    getElementById(id) { return id === "stage" ? stage : null; }
  };
  function setPixelStyle(element, property, value) {
    element.style[property] = Number(value).toFixed(2) + "px";
    pixelSamples.push({ property, value: Number(value), mode: readingMode });
  }
  function applyContinuousTransform() {
    const transform = "translate3d(" + continuousOffsetX + "px, " + continuousOffsetY + "px, 0) scale(" + userZoom + ")";
    notation.style.transform = transform;
    practiceWrongNote.style.transform = readingMode === "Continuous" ? transform : "";
    transformSamples.push({ x: continuousOffsetX, y: continuousOffsetY });
  }
  var window = {
    CadenzaNotation: {
      setCursorBeat(beat, reset = false) { renderedBeats.push({ beat: Number(beat), reset }); },
      setReadingMode(mode) { readingMode = mode; },
      loadScore() {},
      beginTimeline() {},
      endTimeline() {},
      stopPlayback() {},
      getState() { return { readingMode }; }
    }
  };
`, context);
vm.runInContext(patchSource, context, { filename: patchPath });

function runNextFrame(timestamp) {
  const entry = queuedFrames.entries().next().value;
  if (!entry) return false;
  const [id, callback] = entry;
  queuedFrames.delete(id);
  callback(timestamp);
  return true;
}

function drainFrames(maxFrames = 200) {
  let timestamp = 16;
  let count = 0;
  while (queuedFrames.size && count < maxFrames) {
    runNextFrame(timestamp);
    timestamp += 16;
    count++;
  }
  if (queuedFrames.size) throw new Error(`Animation did not settle after ${maxFrames} frames.`);
  return count;
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

const api = context.window.CadenzaNotation;

api.setCursorBeat(0, true);
const beforeBeatSmooth = context.renderedBeats.length;
api.setCursorBeat(0.12, false);
assert(context.renderedBeats.length === beforeBeatSmooth,
  'A small Continuous-mode beat update was applied immediately.');
const beatFrames = drainFrames();
const smoothedBeats = context.renderedBeats.slice(beforeBeatSmooth);
assert(beatFrames >= 3 && smoothedBeats.length >= 3,
  `Expected multiple beat interpolation frames; frames=${beatFrames}, samples=${smoothedBeats.length}.`);
assert(smoothedBeats.every((sample, index) =>
  index === 0 || sample.beat + 0.000001 >= smoothedBeats[index - 1].beat),
  'Continuous beat interpolation moved backward.');
assert(Math.abs(smoothedBeats.at(-1).beat - 0.12) < 0.0002,
  `Beat interpolation did not converge to 0.12; got ${smoothedBeats.at(-1).beat}.`);

context.applyContinuousTransform();
drainFrames();
context.transformSamples.length = 0;
context.continuousOffsetX = -40;
context.applyContinuousTransform();
assert(!context.notation.style.transform.includes('-40.000px'),
  'Continuous notation transform snapped immediately instead of interpolating.');
const transformFrames = drainFrames();
assert(transformFrames >= 2, `Expected multiple transform frames; got ${transformFrames}.`);
assert(context.notation.style.transform.includes('-40.000px'),
  `Continuous transform did not converge: ${context.notation.style.transform}`);

context.timelineRunning = false;
context.setPixelStyle(context.playhead, 'left', 100);
context.setPixelStyle(context.playhead, 'top', 20);
context.setPixelStyle(context.playhead, 'height', 100);
drainFrames();
context.timelineRunning = true;
context.pixelSamples.length = 0;
context.setPixelStyle(context.playhead, 'left', 138);
context.setPixelStyle(context.playhead, 'top', 20);
context.setPixelStyle(context.playhead, 'height', 100);
assert(Math.abs(parseFloat(context.playhead.style.left) - 138) > 0.1,
  'A same-system bar-boundary movement snapped immediately.');
const barFrames = drainFrames();
const barLeftSamples = context.pixelSamples.filter(sample => sample.property === 'left');
assert(barFrames >= 2 && barLeftSamples.length >= 2,
  `Expected a smoothed bar-boundary transition; frames=${barFrames}, samples=${barLeftSamples.length}.`);
assert(Math.abs(parseFloat(context.playhead.style.left) - 138) < 0.03,
  `Bar-boundary playhead did not converge to 138px; got ${context.playhead.style.left}.`);

api.setReadingMode('Page');
context.timelineRunning = false;
context.setPixelStyle(context.playhead, 'left', 50);
context.setPixelStyle(context.playhead, 'top', 30);
context.setPixelStyle(context.playhead, 'height', 110);
drainFrames();
context.timelineRunning = true;
context.pixelSamples.length = 0;
context.setPixelStyle(context.playhead, 'left', 92);
context.setPixelStyle(context.playhead, 'top', 30);
context.setPixelStyle(context.playhead, 'height', 110);
assert(Math.abs(parseFloat(context.playhead.style.left) - 92) > 0.1,
  'Page-mode playhead movement snapped immediately.');
const pageFrames = drainFrames();
assert(pageFrames >= 2,
  `Expected Page-mode requestAnimationFrame smoothing; got ${pageFrames} frame(s).`);
assert(Math.abs(parseFloat(context.playhead.style.left) - 92) < 0.03,
  `Page-mode playhead did not converge to 92px; got ${context.playhead.style.left}.`);

context.pixelSamples.length = 0;
context.setPixelStyle(context.playhead, 'left', 42);
context.setPixelStyle(context.playhead, 'top', 190);
context.setPixelStyle(context.playhead, 'height', 108);
const systemFrames = drainFrames();
assert(systemFrames === 1,
  `A cross-system transition should snap in one visual frame, got ${systemFrames}.`);
assert(Math.abs(parseFloat(context.playhead.style.left) - 42) < 0.03 &&
       Math.abs(parseFloat(context.playhead.style.top) - 190) < 0.03,
  'Cross-system transition did not land directly on its target geometry.');

const state = api.getState();
assert(state.comfortMotion?.installed === true,
  'Comfort-motion telemetry was not attached.');
assert(state.comfortMotion.pageModeVisualFrames >= 2,
  'Page-mode visual frame telemetry was not recorded.');
assert(state.comfortMotion.continuousTransformFrames >= 2,
  'Continuous transform telemetry was not recorded.');
assert(state.comfortMotion.smoothedPlayheadTargets >= 2,
  'Smoothed playhead target telemetry was not recorded.');
assert(state.comfortMotion.geometrySnapCount >= 1,
  'Geometry snap telemetry did not record the cross-system transition.');

console.log(
  `Cadenza comfort motion smoke passed: beatFrames=${beatFrames}, ` +
  `transformFrames=${transformFrames}, barFrames=${barFrames}, ` +
  `pageFrames=${pageFrames}, systemFrames=${systemFrames}.`
);
