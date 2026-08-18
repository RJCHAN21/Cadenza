const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "..");
const patchPath = path.join(
  root,
  "PianoPractice.Desktop",
  "Assets",
  "Verovio",
  "cadenza-playable-position-patch.js");
const source = fs.readFileSync(patchPath, "utf8");

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function classList(initial = []) {
  const values = new Set(initial);
  return {
    add(...names) { names.forEach(name => values.add(name)); },
    remove(...names) { names.forEach(name => values.delete(name)); },
    contains(name) { return values.has(name); }
  };
}

const context = {
  console,
  Math,
  Number,
  Array,
  Object,
  String,
  Boolean,
  Set,
  Map,
  CSS: { escape(value) { return String(value); } },
  setTimeout(callback) { callback(); return 1; },
  clearTimeout() {},
  readingMode: "Continuous",
  userZoom: 1,
  continuousOffsetX: 24,
  pendingPage: 0,
  timelineRunning: true
};

function rect(baseLeft, width = 10, height = 12, top = 100) {
  const left = baseLeft + context.continuousOffsetX;
  return { left, right: left + width, width, top, bottom: top + height, height };
}

const system = {
  closest(selector) { return selector === "g.system" ? this : null; }
};

function visualNode(id, kind, baseLeft, width = 10) {
  const classes = classList(kind === "note" ? ["note"] : [kind]);
  const node = {
    id,
    classList: classes,
    matches(selector) {
      return (selector === "g.note" && kind === "note") ||
        (selector === "g.measure" && kind === "measure");
    },
    closest(selector) {
      if (selector === "g.system") return system;
      if (selector === "g.measure" && kind === "measure") return this;
      if (selector === "g.rest" && kind === "rest") return this;
      return null;
    },
    getBoundingClientRect() { return rect(baseLeft, width); },
    querySelector(selector) {
      if (kind === "note" && [":scope > g.notehead", "g.notehead"].includes(selector))
        return notehead;
      return null;
    },
    querySelectorAll() { return []; }
  };
  const notehead = {
    getBoundingClientRect() { return rect(baseLeft, width); }
  };
  return node;
}

function barline(baseLeft, width = 2) {
  return { getBoundingClientRect() { return rect(baseLeft, width, 80, 60); } };
}

function measureNode(id, number, left, right, rests, barlines) {
  return {
    id,
    number,
    matches(selector) { return selector === "g.measure"; },
    closest(selector) {
      if (selector === "g.measure") return this;
      if (selector === "g.system") return system;
      return null;
    },
    getBoundingClientRect() { return rect(left, right - left, 120, 60); },
    querySelectorAll(selector) {
      if (selector === "g.rest, .rest") return rests;
      if (selector.includes("barLine")) return barlines;
      return [];
    }
  };
}

const rest1 = visualNode("unaddressable-rest-a", "rest", 115, 10);
const rest2 = visualNode("unaddressable-rest-b", "rest", 175, 10);
const note1 = visualNode("note-1", "note", 425, 10);
// Deliberately place the first note after the double barline behind the prior
// accepted endpoint. The playable-position layer must stop this false rewind.
const note2 = visualNode("note-2", "note", 475, 10);
const note3 = visualNode("note-3", "note", 695, 10);
const measure1 = measureNode(
  "measure-1",
  "1",
  70,
  502,
  [rest1, rest2],
  [barline(492, 2), barline(499, 3)]);
const measure2 = measureNode(
  "measure-2",
  "2",
  502,
  900,
  [],
  [barline(898, 2)]);

const exact = new Map([
  ["measure-1", measure1],
  ["measure-2", measure2],
  ["note-1", note1],
  ["note-2", note2],
  ["note-3", note3]
]);

const allNotes = [note1, note2, note3];
const notation = {
  querySelector(selector) {
    const idMatch = selector.match(/^\[data-id="(.+)"\]$/);
    if (idMatch) return exact.get(idMatch[1]) || null;
    if (selector.includes("g.measure[data-n=\"1\"]")) return measure1;
    if (selector.includes("g.measure[data-n=\"2\"]")) return measure2;
    return null;
  },
  querySelectorAll(selector) {
    if (selector === ".playing")
      return allNotes.filter(note => note.classList.contains("playing"));
    return [];
  }
};

const stage = {
  getBoundingClientRect() {
    return { left: 0, right: 1000, top: 0, bottom: 600, width: 1000, height: 600 };
  }
};
const playhead = { style: { left: "0px", opacity: "0" } };

context.performanceTimeline = [
  {
    occurrenceIndex: 0,
    measureNumber: "1",
    sourceStartBeat: 0,
    performanceStartBeat: 0,
    durationBeats: 4
  },
  {
    occurrenceIndex: 1,
    measureNumber: "2",
    sourceStartBeat: 4,
    performanceStartBeat: 4,
    durationBeats: 4
  },
  {
    occurrenceIndex: 2,
    measureNumber: "1",
    sourceStartBeat: 0,
    performanceStartBeat: 8,
    durationBeats: 4
  }
];
context.timemap = [
  { qstamp: 0, measureOn: "measure-1" },
  { qstamp: 0, restsOn: ["rest-missing-1"] },
  { qstamp: 0.5, restsOn: ["rest-missing-2"] },
  { qstamp: 3, on: ["note-1"] },
  // A structural event exists at the double barline, but it is not playable.
  { qstamp: 4, measureOn: "measure-2" },
  { qstamp: 4, on: ["note-2"] },
  { qstamp: 6, on: ["note-3"] }
];
context.stage = stage;
context.notation = notation;
context.playhead = playhead;
context.applyContinuousTransform = function applyContinuousTransform() {};
context.document = {
  getElementById(id) { return exact.get(String(id)) || null; },
  querySelectorAll(selector) { return notation.querySelectorAll(selector); }
};
context.window = {
  CadenzaNotation: {
    setCursorBeat(beat) {
      // Simulate the legacy failure: a structural lookup corrupts both the
      // sheet offset and playhead before the replacement layer runs.
      context.continuousOffsetX = 240;
      playhead.style.left = `${Number(beat) >= 4 ? 40 : 430}px`;
    },
    getState() { return {}; }
  }
};
context.globalThis = context;

vm.createContext(context);
vm.runInContext(source, context, { filename: patchPath });
const api = context.window.CadenzaNotation;

api.setCursorBeat(0, true);
let state = api.getState().playablePositioning;
assert(state.installed, "Playable-position patch did not install.");
assert(state.structuralEventsAreTargets === false,
  "Structural measure events are still eligible cursor targets.");
assert(state.lastTarget.kind === "rests",
  `Leading event should be a rest; got ${state.lastTarget.kind}.`);
assert(Math.abs(state.lastTarget.scoreX - 120) < 0.01,
  `Leading rest resolved to ${state.lastTarget.scoreX}px instead of 120px.`);
assert(Math.abs(Number.parseFloat(playhead.style.left) - 320) < 0.01,
  "Continuous playhead did not anchor the leading rest correctly.");

api.setCursorBeat(0.5, false);
state = api.getState().playablePositioning;
assert(Math.abs(state.lastTarget.scoreX - 180) < 0.01,
  `Closely spaced second rest collapsed to ${state.lastTarget.scoreX}px.`);

api.setCursorBeat(3.9, false);
const beforeBoundary = api.getState().playablePositioning.lastTarget.scoreX;
assert(beforeBoundary > 490 && beforeBoundary <= 502,
  `Pre-boundary cursor did not approach the rightmost double barline: ${beforeBoundary}px.`);
const offsetBeforeBoundary = context.continuousOffsetX;

api.setCursorBeat(4, false);
state = api.getState().playablePositioning;
assert(state.lastTarget.ordinaryForward,
  "Written continuation across the double barline was mistaken for navigation.");
assert(state.lastTarget.scoreX >= beforeBoundary - 0.01,
  `Double barline caused a backward cursor jump: ${beforeBoundary}px -> ${state.lastTarget.scoreX}px.`);
assert(state.preventedBackwardCount === 1,
  `Expected one prevented false rewind; got ${state.preventedBackwardCount}.`);
assert(context.continuousOffsetX <= offsetBeforeBoundary + 0.01,
  "Continuous sheet moved backward at an ordinary double barline.");

api.setCursorBeat(5, false);
state = api.getState().playablePositioning;
assert(state.lastTarget.scoreX > beforeBoundary,
  "Cursor failed to continue forward after the double barline.");

api.setCursorBeat(8, false);
state = api.getState().playablePositioning;
assert(state.lastTarget.ordinaryForward === false,
  "A real repeat rewind was incorrectly classified as ordinary continuation.");
assert(Math.abs(state.lastTarget.scoreX - 120) < 0.01,
  "A legitimate repeat rewind was blocked by the monotonic guard.");
assert(state.unresolvedPlayableCount === 0,
  `Playable targets remained unresolved: ${state.unresolvedPlayableCount}.`);

console.log(
  `Playable-position smoke passed: leadingRest=${120}px, secondRest=${180}px, ` +
  `doubleBarBackwardPrevented=${state.preventedBackwardCount}, repeatRewindAllowed=true.`);
