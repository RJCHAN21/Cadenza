const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const projectRoot = path.resolve(__dirname, "..");
const patchPath = path.join(
  projectRoot,
  "PianoPractice.Desktop",
  "Assets",
  "Verovio",
  "cadenza-bar-boundary-bridge-patch.js");
const patchSource = fs.readFileSync(patchPath, "utf8");

const context = {
  console,
  Math,
  Number,
  Object,
  Array,
  String,
  Boolean,
  performanceTimeline: [
    {
      occurrenceIndex: 0,
      sourceStartBeat: 0,
      performanceStartBeat: 0,
      durationBeats: 4
    },
    {
      occurrenceIndex: 1,
      sourceStartBeat: 4,
      performanceStartBeat: 4,
      durationBeats: 4
    }
  ],
  timemap: [
    { qstamp: 0, measureOn: "m1", page: 1, system: "1:1", x: 40 },
    { qstamp: 3, on: ["n1"], page: 1, system: "1:1", x: 400 },
    { qstamp: 4, measureOn: "m2", page: 1, system: "1:1", x: 460 },
    { qstamp: 4, on: ["n2"], page: 1, system: "1:1", x: 520 },
    { qstamp: 7, on: ["n3"], page: 1, system: "1:1", x: 760 }
  ],
  currentPage: 1,
  pendingPage: null,
  readingMode: "Page",
  timelineRunning: true,
  continuousOffsetX: 24,
  applyTransformCalls: 0,
  playhead: { style: { left: "0px" } },
  stage: {
    getBoundingClientRect() {
      return { width: 1000 };
    }
  },
  setTimeout(callback) {
    callback();
    return 1;
  },
  eventViewportCenter(event) {
    return Number(event?.x);
  },
  systemForEvent(event) {
    return event?.system ?? null;
  },
  pageForEvent(event) {
    return event?.page ?? null;
  },
  applyContinuousTransform() {
    context.applyTransformCalls++;
  }
};

function rawTargetForBeat(beat) {
  if (beat < 4) return beat >= 3 ? 460 : 400;
  return 520;
}

context.window = {
  CadenzaNotation: {
    setCursorBeat(beat) {
      context.playhead.style.left = `${rawTargetForBeat(Number(beat))}px`;
    },
    getState() {
      return { readingMode: context.readingMode };
    }
  }
};
context.globalThis = context;
vm.createContext(context);
vm.runInContext(patchSource, context, { filename: patchPath });

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function left() {
  return Number.parseFloat(context.playhead.style.left);
}

const api = context.window.CadenzaNotation;

api.setCursorBeat(3.5, false);
const halfway = left();
assert(Math.abs(halfway - 460) < 0.001,
  `Halfway bridge should cross the barline at 460px; got ${halfway}px.`);

api.setCursorBeat(3.75, false);
const threeQuarter = left();
assert(Math.abs(threeQuarter - 490) < 0.001,
  `Three-quarter bridge should continue toward the next note at 490px; got ${threeQuarter}px.`);

api.setCursorBeat(3.9, false);
const nearBoundary = left();
assert(Math.abs(nearBoundary - 508) < 0.001,
  `Near-boundary bridge should reach 508px; got ${nearBoundary}px.`);

api.setCursorBeat(4, false);
const boundary = left();
assert(Math.abs(boundary - 520) < 0.001,
  `Boundary target should be the next note at 520px; got ${boundary}px.`);
assert(boundary - nearBoundary < 13,
  `The exact bar boundary still jumps ${boundary - nearBoundary}px.`);

context.readingMode = "Continuous";
context.continuousOffsetX = 24;
context.applyTransformCalls = 0;
api.setCursorBeat(3.75, false);
assert(Math.abs(context.continuousOffsetX + 146) < 0.001,
  `Continuous bridge offset should be -146px; got ${context.continuousOffsetX}px.`);
assert(Math.abs(left() - 320) < 0.001,
  `Continuous bridge should keep the playhead at the 320px anchor; got ${left()}px.`);
assert(context.applyTransformCalls === 1,
  `Continuous bridge should replace the raw sheet target once; calls=${context.applyTransformCalls}.`);

context.readingMode = "Page";
context.performanceTimeline = [
  {
    occurrenceIndex: 0,
    sourceStartBeat: 0,
    performanceStartBeat: 0,
    durationBeats: 4
  },
  {
    occurrenceIndex: 1,
    sourceStartBeat: 0,
    performanceStartBeat: 4,
    durationBeats: 4
  }
];
api.setCursorBeat(3.75, false);
assert(Math.abs(left() - 460) < 0.001,
  `A repeat rewind must retain the authoritative raw target; got ${left()}px.`);

context.performanceTimeline = [
  {
    occurrenceIndex: 0,
    sourceStartBeat: 0,
    performanceStartBeat: 0,
    durationBeats: 4
  },
  {
    occurrenceIndex: 1,
    sourceStartBeat: 4,
    performanceStartBeat: 4,
    durationBeats: 4
  }
];
context.timemap = context.timemap.map(event =>
  event.qstamp === 4 && event.on?.length
    ? { ...event, system: "1:2" }
    : event);
api.setCursorBeat(3.75, false);
assert(Math.abs(left() - 460) < 0.001,
  `A cross-system handoff must not sweep across systems; got ${left()}px.`);

const state = api.getState();
assert(state.boundaryBridge?.installed === true,
  "Boundary-bridge telemetry is missing.");
assert(state.boundaryBridge.appliedBridgeCount >= 4,
  `Expected at least four normal bridge applications; got ${state.boundaryBridge.appliedBridgeCount}.`);

console.log(
  `Bar-boundary bridge smoke passed: halfway=${halfway}px, ` +
  `threeQuarter=${threeQuarter}px, nearBoundary=${nearBoundary}px, ` +
  `boundary=${boundary}px, finalStep=${(boundary - nearBoundary).toFixed(2)}px.`
);