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
  return {
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
const note2 = createNote("n2", { left: 300, top: 260, width: 14, height: 11 });
const notes = [note1, note2];
const stage = createElement("section");
stage.id = "stage";
stage.getBoundingClientRect = () => ({ left: 20, top: 40, width: 1000, height: 600 });
const head = createElement("head");
const frames = new Map();
let frameId = 1;

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
    durationBeats: 3
  }],
  timemap: [
    { qstamp: 0, on: ["n1"] },
    { qstamp: 1, on: ["n2"] },
    { qstamp: 2, restsOn: ["r1"] },
    { qstamp: 3, measureOn: "m2" }
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
}

context.window = {
  requestAnimationFrame(callback) {
    const id = frameId++;
    frames.set(id, callback);
    return id;
  },
  cancelAnimationFrame(id) { frames.delete(id); },
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

function flushFrame() {
  const pending = [...frames.entries()];
  frames.clear();
  pending.forEach(([, callback]) => callback(16.67));
}

function px(element, property) {
  return Number.parseFloat(element.style[property]);
}

const api = context.window.CadenzaNotation;
api.setPerformanceClock([{ performanceBeat: 0, bpm: 120 }], 3, 120);
api.setCursorBeat(0.1, false);

const layer = stage.children.find(node => node.id === "cadenza-listen-halo-layer");
let state = api.getState().listenHighlight;
assert(layer, "The screen-space halo layer was not created.");
assert(state.renderMode === "screen-space-overlay",
  `Expected screen-space overlay rendering; got ${state.renderMode}.`);
assert(state.haloNodeCount === 3 && layer.children.length === 3,
  `Expected three halo layers; state=${state.haloNodeCount}, DOM=${layer.children.length}.`);

const outer = layer.children.find(node => node.classList.contains("cadenza-listen-screen-halo-outer"));
const middle = layer.children.find(node => node.classList.contains("cadenza-listen-screen-halo-middle"));
const core = layer.children.find(node => node.classList.contains("cadenza-listen-screen-halo-core"));
assert(outer && middle && core, "The outer, middle, and core halo layers are incomplete.");
assert(Math.abs(px(outer, "left") - 86) < 0.01,
  `Outer halo X should track the note center relative to the stage; got ${px(outer, "left")}.`);
assert(Math.abs(px(outer, "top") - 165) < 0.01,
  `Outer halo Y should track the note center relative to the stage; got ${px(outer, "top")}.`);
assert(px(outer, "width") >= 96 && px(outer, "height") >= 84,
  `Outer halo is too small to be clearly visible: ${px(outer, "width")}x${px(outer, "height")}.`);
assert(px(middle, "width") >= 62 && px(core, "width") >= 34,
  "The middle or core glow lacks the required visible body.");
assert(Math.abs(Number.parseFloat(
  outer.style.getPropertyValue("--cadenza-listen-glow-duration")) - 500) < 0.2,
  "The screen-space halo duration is not synchronized to one beat at 120 BPM.");

note1.setRect({ left: 180, top: 250, width: 12, height: 10 });
flushFrame();
assert(Math.abs(px(outer, "left") - 166) < 0.01,
  `The halo did not follow horizontal sheet movement; got ${px(outer, "left")}.`);
assert(Math.abs(px(outer, "top") - 215) < 0.01,
  `The halo did not follow vertical sheet movement; got ${px(outer, "top")}.`);

api.setCursorBeat(0.45, false);
state = api.getState().listenHighlight;
assert(state.pulseCount === 1,
  "Repeated cursor frames retriggered the same halo pulse.");
assert(layer.children.length === 3,
  "Repeated cursor frames duplicated the screen-space halos.");

api.setCursorBeat(1.05, false);
state = api.getState().listenHighlight;
assert(state.haloNodeCount === 3 && layer.children.length === 3,
  "The next note did not receive exactly three replacement halos.");
const nextOuter = layer.children.find(node => node.classList.contains("cadenza-listen-screen-halo-outer"));
assert(Math.abs(px(nextOuter, "left") - 287) < 0.01,
  `The replacement halo did not center on note 2; got ${px(nextOuter, "left")}.`);
assert(state.pulseCount === 2,
  `The note change should create the second pulse; got ${state.pulseCount}.`);

api.setCursorBeat(2.1, false);
state = api.getState().listenHighlight;
assert(state.haloNodeCount === 0 && layer.children.length === 0,
  "The rest did not clear all screen-space halo layers.");

const style = head.children.find(node => node.id === "cadenza-listen-highlight-style");
assert(style.textContent.includes("0 0 108px 44px"),
  "The 108px outer bloom is missing.");
assert(style.textContent.includes("cadenzaListenOuterBloom"),
  "The outer bloom animation is missing.");
assert(style.textContent.includes("cadenzaListenCoreFlash"),
  "The core flash animation is missing.");

console.log(
  "Listen highlight halo smoke passed: halos=3, outer=96x84 minimum, " +
  "peakShadow=108px, tracking=true, cleanup=true, retrigger=false."
);
