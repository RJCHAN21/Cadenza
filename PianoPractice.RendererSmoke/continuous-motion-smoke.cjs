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

function drainFrames(maxFrames = 400) {
  let timestamp = 16;
  let count = 0;
  const leftFrames = [];
  const transformFrames = [];
  while (queuedFrames.size && count < maxFrames) {
    runNextFrame(timestamp);
    timestamp += 16;
    count++;
    leftFrames.push(parseFloat(context.playhead.style.left));
    const match = String(context.notation.style.transform).match(/translate3d\(([-\d.]+)px/);
    if (match) transformFrames.push(Number(match[1]));
  }
  if (queuedFrames.size) throw new Error(`Animation did not settle after ${maxFrames} frames.`);
  return { count, leftFrames, transformFrames };
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function maxStep(values) {
  let result = 0;
  for (let index = 1; index < values.length; index++)
    result = Math.max(result, Math.abs(values[index] - values[index - 1]));
  return result;
}

const api = context.window.CadenzaNotation;

const beforeBeat = context.renderedBeats.length;
api.setCursorBeat(0.12, false);
assert(context.renderedBeats.length === beforeBeat + 1,
  'Authoritative cursor updates were delayed by the comfort layer.');

context.applyContinuousTransform();
drainFrames();
context.continuousOffsetX = -40;
context.applyContinuousTransform();
assert(!context.notation.style.transform.includes('-40.000px'),
  'Continuous transform snapped immediately.');
const transformRun = drainFrames();
assert(transformRun.count >= 4, `Expected multiple transform frames; got ${transformRun.count}.`);
assert(Math.abs(transformRun.transformFrames.at(-1) + 40) < 0.03,
  `Continuous transform did not converge: ${transformRun.transformFrames.at(-1)}.`);
assert(maxStep(transformRun.transformFrames) < 18,
  `Continuous transform made a large per-frame jump: ${maxStep(transformRun.transformFrames)}px.`);

context.timelineRunning = false;
context.setPixelStyle(context.playhead, 'left', 100);
context.setPixelStyle(context.playhead, 'top', 20);
context.setPixelStyle(context.playhead, 'height', 100);
drainFrames();
context.timelineRunning = true;
context.pixelSamples.length = 0;
context.setPixelStyle(context.playhead, 'left', 148);
context.setPixelStyle(context.playhead, 'top', 20);
context.setPixelStyle(context.playhead, 'height', 100);
const beforeBar = parseFloat(context.playhead.style.left);
const barRun = drainFrames();
assert(barRun.count >= 4, `Expected multiple barline frames; got ${barRun.count}.`);
assert(barRun.leftFrames[0] > beforeBar && barRun.leftFrames[0] < 148,
  `First barline frame did not move gradually: ${barRun.leftFrames[0]}.`);
assert(Math.abs(barRun.leftFrames.at(-1) - 148) < 0.03,
  `Barline motion did not converge: ${barRun.leftFrames.at(-1)}.`);
assert(maxStep(barRun.leftFrames) < 14,
  `Barline motion still contains a visible jump: ${maxStep(barRun.leftFrames)}px.`);

context.setPixelStyle(context.playhead, 'left', 194);
const secondBarRun = drainFrames();
assert(secondBarRun.count >= 4,
  `Expected multiple second-bar frames; got ${secondBarRun.count}.`);
assert(maxStep(secondBarRun.leftFrames) < 14,
  `Second bar transition contains a visible jump: ${maxStep(secondBarRun.leftFrames)}px.`);

api.setReadingMode('Page');
context.timelineRunning = false;
context.setPixelStyle(context.playhead, 'left', 60);
context.setPixelStyle(context.playhead, 'top', 30);
context.setPixelStyle(context.playhead, 'height', 110);
drainFrames();
context.timelineRunning = true;
context.setPixelStyle(context.playhead, 'left', 108);
context.setPixelStyle(context.playhead, 'top', 30);
context.setPixelStyle(context.playhead, 'height', 110);
const pageRun = drainFrames();
assert(pageRun.count >= 4, `Expected multiple Page-mode frames; got ${pageRun.count}.`);
assert(maxStep(pageRun.leftFrames) < 14,
  `Page-mode barline motion contains a visible jump: ${maxStep(pageRun.leftFrames)}px.`);
assert(Math.abs(pageRun.leftFrames.at(-1) - 108) < 0.03,
  `Page-mode motion did not converge: ${pageRun.leftFrames.at(-1)}.`);

context.setPixelStyle(context.playhead, 'left', 42);
context.setPixelStyle(context.playhead, 'top', 190);
context.setPixelStyle(context.playhead, 'height', 108);
const systemRun = drainFrames();
assert(systemRun.count === 1,
  `Cross-system relocation should settle in one frame; got ${systemRun.count}.`);
assert(Math.abs(parseFloat(context.playhead.style.left) - 42) < 0.03 &&
       Math.abs(parseFloat(context.playhead.style.top) - 190) < 0.03,
  'Cross-system relocation did not land directly on target geometry.');

const state = api.getState();
assert(state.comfortMotion?.installed === true, 'Comfort-motion telemetry is missing.');
assert(state.comfortMotion.pageModeVisualFrames >= 4,
  'Page-mode visual frames were not recorded.');
assert(state.comfortMotion.continuousTransformFrames >= 4,
  'Continuous transform frames were not recorded.');
assert(state.comfortMotion.smoothedPlayheadTargets >= 3,
  'Smoothed target telemetry was not recorded.');
assert(state.comfortMotion.geometrySnapCount >= 1,
  'Cross-system snap telemetry was not recorded.');
assert(state.comfortMotion.maximumObservedPlayheadStepPx < 20,
  `Smoothed playhead motion exceeded the step limit: ${state.comfortMotion.maximumObservedPlayheadStepPx}px.`);

console.log(
  `Cadenza comfort motion smoke passed: transformFrames=${transformRun.count}, ` +
  `barFrames=${barRun.count}, secondBarFrames=${secondBarRun.count}, ` +
  `pageFrames=${pageRun.count}, systemFrames=${systemRun.count}, ` +
  `maxBarStep=${maxStep(barRun.leftFrames).toFixed(2)}px.`
);