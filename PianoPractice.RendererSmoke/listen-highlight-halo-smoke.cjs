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
    classList: createClassList(),
    style: createStyle(),
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
    durationBeats: 3
  }],
  timemap: [
    { qstamp: 0, on: ["n1", "n2"] },
    { qstamp: 1, on: ["n3"] },
    { qstamp: 2, restsOn: ["r1"] },
    { qstamp: 3, measureOn: "m2" }
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
  if (beat >= 0 && beat < 1) {
    note1.classList.add("playing");
    note2.classList.add("playing");
  } else if (beat >= 1 && beat < 2) {
    note3.classList.add("playing");
  }
}

context.window = {
  CadenzaNotation: {
    setPerformanceClock() {},
    setCursorBeat(beat) { updateRawPlaying(Number(beat)); },
    getState() { return {}; }
  }
};
context.globalThis = context;
vm.createContext(context);
vm.runInContext(patchSource, context, { filename: patchPath });

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

const api = context.window.CadenzaNotation;
api.setPerformanceClock([{ performanceBeat: 0, bpm: 120 }], 3, 120);

const style = styles.find(node => node.id === "cadenza-listen-highlight-style");
assert(style, "The Listen highlight stylesheet was not installed.");
const css = style.textContent;

assert(css.includes("g.note.playing .notehead"),
  "The visible glow is not targeted to noteheads.");
assert(css.includes("drop-shadow(0 0 30px"),
  "The notehead attack glow is not strong enough.");
assert(css.includes("cadenzaListenLyricGlow"),
  "The lyric glow animation is missing.");
assert(css.includes("drop-shadow(0 0 21px"),
  "The lyric attack glow is not visibly reinforced.");
assert(!css.includes("cadenza-listen-halo-layer"),
  "The ineffective screen-space halo overlay is still present.");
assert(!patchSource.includes("cloneNode("),
  "The patch still clones full SVG notes, which can reproduce glow artifacts.");
assert(!/#stage\.[^\n]+#notation\s+g\.note\.playing\s*\{[^}]*filter\s*:/s.test(css),
  "A filter is still applied to the entire note group.");
assert(!/g\.note\.playing\s*>?\s*\.stem[^}]*filter\s*:/s.test(css),
  "The glow leaks onto note stems.");
assert(!/g\.note\.playing\s*>?\s*\.beam[^}]*filter\s*:/s.test(css),
  "The glow leaks onto beams.");

api.setCursorBeat(0.1, false);
let state = api.getState().listenHighlight;
assert(state.renderMode === "contained-svg-notehead-and-lyrics",
  `Unexpected rendering approach: ${state.renderMode}.`);
assert(state.artifactsContained === true && state.lyricsIncluded === true,
  "The artifact containment or lyric guarantee is missing.");
assert(state.activeNodeCount === 2,
  `The chord should activate two note groups; got ${state.activeNodeCount}.`);
assert(note1.classList.contains("cadenza-listen-glow-active") &&
       note2.classList.contains("cadenza-listen-glow-active"),
  "The chord notes were not activated together.");
assert(state.haloNodeCount === 0,
  "Contained SVG rendering should not create detached halo elements.");
assert(state.pulseCount === 1,
  `The chord should create one synchronized pulse; got ${state.pulseCount}.`);
assert(Math.abs(Number.parseFloat(
  stage.style.getPropertyValue("--cadenza-listen-glow-duration")) - 500) < 0.2,
  "The chord glow duration is not synchronized to one beat at 120 BPM.");

api.setCursorBeat(0.45, false);
state = api.getState().listenHighlight;
assert(state.pulseCount === 1,
  "Repeated cursor frames retriggered the active chord.");

api.setCursorBeat(1.05, false);
state = api.getState().listenHighlight;
assert(!note1.classList.contains("cadenza-listen-glow-active") &&
       !note2.classList.contains("cadenza-listen-glow-active"),
  "The previous chord retained its glow class.");
assert(note3.classList.contains("cadenza-listen-glow-active"),
  "The next note did not receive the contained glow.");
assert(state.activeNodeCount === 1 && state.pulseCount === 2,
  "The note transition did not replace the active glow cleanly.");

api.setCursorBeat(2.1, false);
state = api.getState().listenHighlight;
assert(state.activeNodeCount === 0,
  "A rest did not clear the active glow.");
assert(!note3.classList.contains("cadenza-listen-glow-active"),
  "The note before the rest retained its glow class.");

console.log(
  "Listen contained-glow smoke passed: noteheads=strong, lyrics=enabled, " +
  "wholeNoteFilter=false, clonedHalos=false, chordSync=true, cleanup=true."
);
