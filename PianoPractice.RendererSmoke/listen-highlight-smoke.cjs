const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const projectRoot = path.resolve(__dirname, "..");
const patchPath = path.join(
  projectRoot,
  "PianoPractice.Desktop",
  "Assets",
  "Verovio",
  "cadenza-listen-highlight-patch.js");
const patchSource = fs.readFileSync(patchPath, "utf8");

function createStyle() {
  const values = new Map();
  return {
    setProperty(name, value) { values.set(name, String(value)); },
    getPropertyValue(name) { return values.get(name) || ""; },
    removeProperty(name) {
      const value = values.get(name) || "";
      values.delete(name);
      return value;
    }
  };
}

function createClassList(initial = []) {
  const values = new Set(initial);
  return {
    add(...names) { names.forEach(name => values.add(name)); },
    remove(...names) { names.forEach(name => values.delete(name)); },
    contains(name) { return values.has(name); },
    toggle(name, force) {
      if (force === true) { values.add(name); return true; }
      if (force === false) { values.delete(name); return false; }
      if (values.has(name)) { values.delete(name); return false; }
      values.add(name);
      return true;
    }
  };
}

function createElement(tagName = "div") {
  const element = {
    tagName,
    id: "",
    textContent: "",
    classList: createClassList(),
    style: createStyle(),
    children: [],
    parentNode: null,
    attributes: new Map(),
    appendChild(child) {
      this.children.push(child);
      child.parentNode = this;
      return child;
    },
    setAttribute(name, value) { this.attributes.set(name, String(value)); },
    remove() {
      const index = this.parentNode?.children.indexOf(this) ?? -1;
      if (index >= 0) this.parentNode.children.splice(index, 1);
      this.parentNode = null;
    }
  };
  return element;
}

function createNote(id, initialRect) {
  let rect = { ...initialRect };
  const notehead = {
    getBoundingClientRect() { return { ...rect }; }
  };
  return {
    id,
    isConnected: true,
    style: createStyle(),
    classList: createClassList(),
    setRect(next) { rect = { ...next }; },
    matches(selector) { return selector === "g.note"; },
    closest(selector) { return selector === "g.note" ? this : null; },
    getAttribute(name) { return name === "data-id" ? id : null; },
    querySelector(selector) {
      return selector === ".notehead" || selector === "g.notehead" || selector === "use"
        ? notehead
        : null;
    },
    getBoundingClientRect() { return { ...rect }; }
  };
}

const note1 = createNote("n1", { left: 100, top: 200, width: 12, height: 10 });
const note2 = createNote("n2", { left: 160, top: 198, width: 12, height: 10 });
const note3 = createNote("n3", { left: 240, top: 202, width: 12, height: 10 });
const notes = [note1, note2, note3];
const stage = createElement("section");
stage.id = "stage";
stage.getBoundingClientRect = () => ({ left: 0, top: 0, width: 1000, height: 600 });
const head = createElement("head");
const frameCallbacks = new Map();
let nextFrameId = 1;

const context = {
  console,
  Math,
  Number,
  Object,
  Array,
  String,
  Boolean,
  Set,
  Map,
  Date,
  lessonMode: "Listen",
  lessonRunning: true,
  timelineRunning: true,
  playing: true,
  bpm: 120,
  performanceTimeline: [{
    occurrenceIndex: 0,
    sourceStartBeat: 0,
    performanceStartBeat: 0,
    durationBeats: 4
  }],
  timemap: [
    { qstamp: 0, on: ["n1"] },
    { qstamp: 1, on: ["n2"] },
    { qstamp: 2, restsOn: ["r1"] },
    { qstamp: 3, on: ["n3"] },
    { qstamp: 4, measureOn: "m2" }
  ],
  setTimeout(callback) { callback(); return 1; },
  clearTimeout() {},
  document: {
    head,
    documentElement: head,
    createElement,
    getElementById(id) {
      if (id === "stage") return stage;
      return head.children.find(node => node.id === id) ||
        stage.children.find(node => node.id === id) || null;
    },
    querySelectorAll(selector) {
      if (selector !== "#notation .playing") return [];
      return notes.filter(note => note.classList.contains("playing"));
    }
  }
};

function updateRawPlaying(beat) {
  notes.forEach(note => note.classList.remove("playing"));
  if (beat >= 0 && beat < 1) note1.classList.add("playing");
  else if (beat >= 1 && beat < 2) note2.classList.add("playing");
  else if (beat >= 3 && beat <= 4) note3.classList.add("playing");
}

context.window = {
  requestAnimationFrame(callback) {
    const id = nextFrameId++;
    frameCallbacks.set(id, callback);
    return id;
  },
  cancelAnimationFrame(id) { frameCallbacks.delete(id); },
  CadenzaNotation: {
    setPerformanceClock() {},
    setCursorBeat(beat) { updateRawPlaying(Number(beat)); },
    getState() { return { lessonMode: context.lessonMode }; }
  }
};
context.globalThis = context;
vm.createContext(context);
vm.runInContext(patchSource, context, { filename: patchPath });

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function duration(note) {
  return Number.parseFloat(
    note.style.getPropertyValue("--cadenza-listen-glow-duration"));
}

const api = context.window.CadenzaNotation;
api.setPerformanceClock([{ performanceBeat: 0, bpm: 120 }], 4, 120);

const style = head.children.find(node => node.id === "cadenza-listen-highlight-style");
assert(style, "The Listen highlight stylesheet was not installed.");
assert(style.textContent.includes("@keyframes cadenzaListenSourceFlash"),
  "The tune-synced source-note keyframes are missing.");
assert(/@media\s*\(\s*prefers-reduced-motion\s*:\s*reduce\s*\)/.test(style.textContent),
  "The reduced-motion media query is missing.");
assert(style.textContent.includes("#cadenza-listen-halo-layer .cadenza-listen-screen-halo"),
  "Reduced-motion coverage does not include the screen-space halos.");
assert(style.textContent.includes("0 0 108px 44px"),
  "The intensified outer bloom is missing.");

api.setCursorBeat(0.1, false);
let state = api.getState();
let haloLayer = stage.children.find(node => node.id === "cadenza-listen-halo-layer");
assert(stage.classList.contains("cadenza-listen-feedback"),
  "Listen mode did not enable the stronger feedback styling.");
assert(note1.classList.contains("cadenza-listen-glow-active"),
  "The first sounding note did not receive the animated source glow.");
assert(Math.abs(duration(note1) - 500) < 0.2,
  `A one-beat note at 120 BPM should glow for 500ms; got ${duration(note1)}ms.`);
assert(state.listenHighlight.haloNodeCount === 3,
  `The first note should have three screen-space halo layers; got ${state.listenHighlight.haloNodeCount}.`);
assert(haloLayer?.children.length === 3,
  `The halo layer should contain three elements; got ${haloLayer?.children.length}.`);
assert(state.listenHighlight.pulseCount === 1,
  `The first note should create one pulse; got ${state.listenHighlight.pulseCount}.`);

api.setCursorBeat(0.45, false);
state = api.getState();
assert(state.listenHighlight.pulseCount === 1,
  "Repeated cursor updates retriggered the same sounding note.");
assert(haloLayer.children.length === 3,
  "Repeated cursor updates duplicated the halo elements.");

api.setCursorBeat(1.05, false);
state = api.getState();
assert(!note1.classList.contains("cadenza-listen-glow-active"),
  "The previous note retained its animated glow.");
assert(note2.classList.contains("cadenza-listen-glow-active"),
  "The second sounding note did not start its own glow.");
assert(Math.abs(duration(note2) - 500) < 0.2,
  `The second one-beat note should glow for 500ms; got ${duration(note2)}ms.`);
assert(state.listenHighlight.haloNodeCount === 3 && haloLayer.children.length === 3,
  "The note change did not replace the three halo layers cleanly.");
assert(state.listenHighlight.pulseCount === 2,
  `The note change should create a second pulse; got ${state.listenHighlight.pulseCount}.`);

api.setCursorBeat(2.1, false);
state = api.getState();
assert(state.listenHighlight.activeNodeCount === 0,
  "A rest did not clear the animated note glow.");
assert(state.listenHighlight.haloNodeCount === 0 && haloLayer.children.length === 0,
  "A rest did not remove the screen-space halos.");

context.bpm = 60;
api.setCursorBeat(3.05, false);
state = api.getState();
assert(note3.classList.contains("cadenza-listen-glow-active"),
  "The final note did not receive the animated glow.");
assert(Math.abs(duration(note3) - 1000) < 0.2,
  `A one-beat note at half tempo should glow for 1000ms; got ${duration(note3)}ms.`);
assert(state.listenHighlight.haloNodeCount === 3,
  "The half-tempo note did not receive all three halo layers.");

context.timelineRunning = false;
context.playing = false;
api.setCursorBeat(3.2, false);
state = api.getState();
assert(state.listenHighlight.activeNodeCount === 0 && haloLayer.children.length === 0,
  "Pausing Listen mode did not clear the glow and halo overlay.");

context.timelineRunning = true;
context.playing = true;
api.setCursorBeat(3.2, false);
state = api.getState();
assert(note3.classList.contains("cadenza-listen-glow-active"),
  "Resuming Listen mode did not restart the current note glow.");
assert(state.listenHighlight.pulseCount === 4,
  `Resume should create a fresh synchronized pulse; got ${state.listenHighlight.pulseCount}.`);

context.lessonMode = "TimedPlay";
api.setCursorBeat(3.3, false);
state = api.getState();
assert(!stage.classList.contains("cadenza-listen-feedback"),
  "Listen-only styling leaked into Timed Play.");
assert(state.listenHighlight.activeNodeCount === 0 && haloLayer.children.length === 0,
  "Listen halo state leaked into Timed Play.");

console.log(
  `Listen highlight smoke passed: pulses=${state.listenHighlight.pulseCount}, ` +
  `halosPerNote=3, normalDuration=500ms, halfTempoDuration=1000ms, ` +
  `restCleared=true, modeIsolation=true.`
);
