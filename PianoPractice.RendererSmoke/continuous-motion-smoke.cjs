const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const projectRoot = path.resolve(__dirname, "..");
const patchPath = path.join(
  projectRoot,
  "PianoPractice.Desktop",
  "Assets",
  "Verovio",
  "cadenza-continuous-motion-patch.js");
const patchSource = fs.readFileSync(patchPath, "utf8");

let readingMode = "Continuous";
let nextFrameId = 1;
const queuedFrames = new Map();
const renderedBeats = [];

const stage = {
  classList: {
    contains(name) {
      return name === "continuous-mode" && readingMode === "Continuous";
    }
  }
};

const api = {
  setCursorBeat(beat, reset = false) {
    renderedBeats.push({ beat, reset });
  },
  setReadingMode(mode) {
    readingMode = mode;
  },
  loadScore() {},
  beginTimeline() {},
  endTimeline() {},
  stopPlayback() {},
  getState() {
    return { readingMode };
  }
};

const context = {
  window: { CadenzaNotation: api },
  document: {
    getElementById(id) {
      return id === "stage" ? stage : null;
    }
  },
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
  },
  console,
  Math,
  Number,
  Object
};
context.globalThis = context;
vm.createContext(context);
vm.runInContext(patchSource, context, { filename: patchPath });

function runFrame(timestamp) {
  const entry = queuedFrames.entries().next().value;
  if (!entry) return false;
  const [id, callback] = entry;
  queuedFrames.delete(id);
  callback(timestamp);
  return true;
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

api.setCursorBeat(0, true);
const beforeSmooth = renderedBeats.length;
api.setCursorBeat(0.12, false);
assert(renderedBeats.length === beforeSmooth,
  "A small Continuous-mode update was applied immediately instead of being interpolated.");

for (let timestamp = 16; timestamp <= 400 && runFrame(timestamp); timestamp += 16) {}
const smoothSamples = renderedBeats.slice(beforeSmooth);
assert(smoothSamples.length >= 3,
  `Expected at least three interpolated samples, got ${smoothSamples.length}.`);
assert(smoothSamples.every((sample, index) =>
  index === 0 || sample.beat + 0.000001 >= smoothSamples[index - 1].beat),
  "Interpolated Continuous-mode beats moved backward.");
assert(Math.abs(smoothSamples.at(-1).beat - 0.12) < 0.0002,
  `Continuous interpolation did not converge to 0.12 beats; got ${smoothSamples.at(-1).beat}.`);

const beforeSeek = renderedBeats.length;
api.setCursorBeat(4, false);
assert(renderedBeats.length === beforeSeek + 1 &&
       Math.abs(renderedBeats.at(-1).beat - 4) < 0.000001,
  "A large seek was animated instead of snapping immediately.");

api.setReadingMode("Page");
const beforePage = renderedBeats.length;
api.setCursorBeat(4.1, false);
assert(renderedBeats.length === beforePage + 1 &&
       Math.abs(renderedBeats.at(-1).beat - 4.1) < 0.000001,
  "Page mode cursor updates must remain immediate.");

const state = api.getState();
assert(state.comfortMotion?.installed === true,
  "Comfort-motion telemetry was not attached to renderer state.");
assert(state.comfortMotion.frameSamples >= 3,
  "Comfort-motion telemetry did not record animation frames.");
assert(state.comfortMotion.largeSeekThresholdBeats === 0.75,
  "The large-seek snap threshold changed unexpectedly.");

console.log(
  `Continuous comfort-motion smoke passed: samples=${smoothSamples.length}, ` +
  `finalBeat=${smoothSamples.at(-1).beat.toFixed(4)}, snaps=${state.comfortMotion.immediateSnapCount}.`
);