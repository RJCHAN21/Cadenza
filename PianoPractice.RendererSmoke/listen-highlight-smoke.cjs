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

function createNote(id) {
  return {
    id,
    style: createStyle(),
    classList: createClassList(),
    matches(selector) { return selector === "g.note"; },
    closest(selector) { return selector === "g.note" ? this : null; },
    getAttribute(name) { return name === "data-id" ? id : null; },
    getBoundingClientRect() { return { left: 0, top: 0, width: 12, height: 10 }; }
  };
}

const note1 = createNote("n1");
const note2 = createNote("n2");
const note3 = createNote("n3");
const notes = [note1, note2, note3];
const stage = { classList: createClassList(), style: createStyle() };
const styles = [];

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
  document: {
    head: { appendChild(node) { styles.push(node); return node; } },
    documentElement: { appendChild(node) { styles.push(node); return node; } },
    createElement() { return { id: "", textContent: "" }; },
    getElementById(id) {
      if (id === "stage") return stage;
      return styles.find(node => node.id === id) || null;
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

function duration() {
  return Number.parseFloat(
    stage.style.getPropertyValue("--cadenza-listen-glow-duration"));
}

const api = context.window.CadenzaNotation;
api.setPerformanceClock([{ performanceBeat: 0, bpm: 120 }], 4, 120);

const style = styles.find(node => node.id === "cadenza-listen-highlight-style");
assert(style, "The Listen highlight stylesheet was not installed.");
assert(style.textContent.includes("@keyframes cadenzaListenNoteheadGlow"),
  "The tune-synchronized notehead keyframes are missing.");
assert(style.textContent.includes("@keyframes cadenzaListenLyricGlow"),
  "The lyric glow keyframes are missing.");
assert(/@media\s*\(\s*prefers-reduced-motion\s*:\s*reduce\s*\)/.test(style.textContent),
  "The reduced-motion fallback is missing.");
assert(style.textContent.includes("g.note.playing .notehead"),
  "The notehead-targeted highlight selector is missing.");
assert(style.textContent.includes("g.syl.playing"),
  "The lyric highlight selector is missing.");
assert(!style.textContent.includes("cadenza-listen-halo-layer"),
  "The obsolete screen-space halo overlay is still present.");

api.setCursorBeat(0.1, false);
let state = api.getState();
assert(stage.classList.contains("cadenza-listen-feedback"),
  "Listen mode did not enable the feedback styling.");
assert(note1.classList.contains("cadenza-listen-glow-active"),
  "The first sounding note did not receive the active glow class.");
assert(Math.abs(duration() - 500) < 0.2,
  `A one-beat note at 120 BPM should glow for 500ms; got ${duration()}ms.`);
assert(state.listenHighlight.renderMode === "contained-svg-notehead-and-lyrics",
  `Unexpected render mode: ${state.listenHighlight.renderMode}.`);
assert(state.listenHighlight.activeNodeCount === 1,
  `Expected one active note; got ${state.listenHighlight.activeNodeCount}.`);
assert(state.listenHighlight.haloNodeCount === 0,
  "Contained rendering should not create overlay halo nodes.");
assert(state.listenHighlight.artifactsContained && state.listenHighlight.lyricsIncluded,
  "The contained-notehead and lyric guarantees are not reported.");
assert(state.listenHighlight.pulseCount === 1,
  `The first note should create one pulse; got ${state.listenHighlight.pulseCount}.`);

api.setCursorBeat(0.45, false);
state = api.getState();
assert(state.listenHighlight.pulseCount === 1,
  "Repeated cursor updates retriggered the same sounding note.");

api.setCursorBeat(1.05, false);
state = api.getState();
assert(!note1.classList.contains("cadenza-listen-glow-active"),
  "The previous note retained its active glow class.");
assert(note2.classList.contains("cadenza-listen-glow-active"),
  "The second sounding note did not start its own glow.");
assert(Math.abs(duration() - 500) < 0.2,
  `The second one-beat note should glow for 500ms; got ${duration()}ms.`);
assert(state.listenHighlight.pulseCount === 2,
  `The note change should create a second pulse; got ${state.listenHighlight.pulseCount}.`);

api.setCursorBeat(2.1, false);
state = api.getState();
assert(state.listenHighlight.activeNodeCount === 0,
  "A rest did not clear the animated note state.");
assert(!note2.classList.contains("cadenza-listen-glow-active"),
  "The note before a rest retained its active class.");
assert(stage.style.getPropertyValue("--cadenza-listen-glow-duration") === "",
  "A rest did not clear the inherited glow duration.");

context.bpm = 60;
api.setCursorBeat(3.05, false);
state = api.getState();
assert(note3.classList.contains("cadenza-listen-glow-active"),
  "The final note did not receive the animated glow.");
assert(Math.abs(duration() - 1000) < 0.2,
  `A one-beat note at half tempo should glow for 1000ms; got ${duration()}ms.`);
assert(state.listenHighlight.pulseCount === 3,
  `The third note should create the third pulse; got ${state.listenHighlight.pulseCount}.`);

context.timelineRunning = false;
context.playing = false;
api.setCursorBeat(3.2, false);
state = api.getState();
assert(state.listenHighlight.activeNodeCount === 0,
  "Pausing Listen mode did not clear the active glow.");

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
assert(state.listenHighlight.activeNodeCount === 0,
  "Listen animation state leaked into Timed Play.");

console.log(
  "Listen highlight smoke passed: pulses=4, render=contained-notehead-and-lyrics, " +
  "normalDuration=500ms, halfTempoDuration=1000ms, restCleared=true, modeIsolation=true."
);
