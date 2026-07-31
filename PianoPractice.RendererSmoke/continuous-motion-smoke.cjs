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

let nextFrameId = 1;
const queuedFrames = new Map();
let observerCallback = null;

function createStyle(initial = {}) {
  const priorities = new Map();
  return {
    ...initial,
    setProperty(property, value, priority = "") {
      this[property] = String(value);
      if (priority) priorities.set(property, priority);
      else priorities.delete(property);
    },
    getPropertyValue(property) {
      return String(this[property] ?? "");
    },
    getPropertyPriority(property) {
      return priorities.get(property) || "";
    }
  };
}

function createClassList(element) {
  return {
    add(name) {
      const values = new Set(String(element.className || "").split(/\s+/).filter(Boolean));
      values.add(name);
      element.className = [...values].join(" ");
    },
    contains(name) {
      return String(element.className || "").split(/\s+/).includes(name);
    }
  };
}

function createElement(styleValues = {}) {
  const attributes = new Map();
  const element = {
    style: createStyle(styleValues),
    className: "playhead-line",
    isConnected: false,
    parentElement: null,
    classList: null,
    setAttribute(name, value) {
      attributes.set(name, String(value));
    },
    removeAttribute(name) {
      attributes.delete(name);
    },
    getAttribute(name) {
      return attributes.get(name) ?? null;
    },
    cloneNode() {
      const clone = createElement({ ...this.style });
      clone.className = this.className;
      return clone;
    }
  };
  element.classList = createClassList(element);
  return element;
}

const playheadParent = {
  children: [],
  appendChild(element) {
    element.parentElement = this;
    element.isConnected = true;
    this.children.push(element);
    return element;
  }
};

const playheadElement = createElement({
  position: "absolute",
  left: "0px",
  top: "20px",
  height: "100px",
  width: "3px",
  opacity: "1",
  display: "block",
  background: "#00f0ff",
  "z-index": "20"
});
playheadElement.parentElement = playheadParent;
playheadElement.isConnected = true;

const stage = {
  clientWidth: 1000,
  classList: {
    contains(name) {
      return name === "continuous-mode" && context.readingMode === "Continuous";
    }
  }
};

const context = {
  console,
  Math,
  Number,
  Object,
  String,
  Boolean,
  readingMode: "Continuous",
  timelineRunning: true,
  continuousOffsetX: 24,
  continuousOffsetY: 0,
  userZoom: 1,
  playhead: playheadElement,
  notation: { style: createStyle({ transform: "" }) },
  practiceWrongNote: { style: createStyle({ transform: "" }) },
  renderedBeats: [],
  pixelWrites: [],
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
  MutationObserver: class {
    constructor(callback) {
      observerCallback = callback;
    }
    observe() {}
  },
  getComputedStyle(element) {
    return {
      getPropertyValue(property) {
        const value = element.style?.getPropertyValue?.(property);
        if (value) return value;
        const defaults = {
          position: "absolute",
          width: "3px",
          opacity: "1",
          display: "block",
          background: "#00f0ff",
          "z-index": "20"
        };
        return defaults[property] || "";
      }
    };
  },
  document: {
    getElementById(id) {
      return id === "stage" ? stage : null;
    }
  },
  setPixelStyle(element, property, value) {
    element.style.setProperty(property, `${Number(value).toFixed(2)}px`);
    this.pixelWrites.push({ element, property, value: Number(value) });
  },
  applyContinuousTransform() {
    const transform =
      `translate3d(${this.continuousOffsetX}px, ${this.continuousOffsetY}px, 0) scale(${this.userZoom})`;
    this.notation.style.setProperty("transform", transform);
    this.practiceWrongNote.style.setProperty(
      "transform",
      this.readingMode === "Continuous" ? transform : "");
  }
};

context.window = {
  CadenzaNotation: {
    setCursorBeat(beat, reset = false) {
      context.renderedBeats.push({ beat: Number(beat), reset });
    },
    setReadingMode(mode) {
      context.readingMode = mode;
    },
    loadScore() {},
    beginTimeline() {},
    endTimeline() {},
    stopPlayback() {},
    getState() {
      return { readingMode: context.readingMode };
    }
  }
};
context.globalThis = context;

vm.createContext(context);
vm.runInContext(patchSource, context, { filename: patchPath });

const api = context.window.CadenzaNotation;
const visualPlayhead = playheadParent.children.find(element =>
  element.getAttribute?.("data-cadenza-comfort-playhead") === "true");

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function parseTranslateX(element) {
  const match = String(element.style.transform || "").match(/translate3d\(([-\d.]+)px/);
  return match ? Number(match[1]) : NaN;
}

function parseTranslateY(element) {
  const match = String(element.style.transform || "").match(
    /translate3d\((?:[-\d.]+)px,\s*([-\d.]+)px/);
  return match ? Number(match[1]) : NaN;
}

function runFrames(maxFrames = 800) {
  let timestamp = 16;
  const samples = [];
  let count = 0;
  while (queuedFrames.size && count < maxFrames) {
    const entry = queuedFrames.entries().next().value;
    const [id, callback] = entry;
    queuedFrames.delete(id);
    callback(timestamp);
    timestamp += 16;
    count++;
    samples.push({
      x: parseTranslateX(visualPlayhead),
      y: parseTranslateY(visualPlayhead),
      opacity: Number.parseFloat(visualPlayhead.style.opacity || "0"),
      sheetX: (() => {
        const match = String(context.notation.style.transform || "").match(/translate3d\(([-\d.]+)px/);
        return match ? Number(match[1]) : NaN;
      })()
    });
  }
  if (queuedFrames.size)
    throw new Error(`Comfort motion did not settle after ${maxFrames} frames.`);
  return samples;
}

function maximumVisibleStep(samples) {
  let result = 0;
  for (let index = 1; index < samples.length; index++) {
    if (samples[index - 1].opacity <= 0.08 || samples[index].opacity <= 0.08) continue;
    result = Math.max(result, Math.abs(samples[index].x - samples[index - 1].x));
  }
  return result;
}

assert(visualPlayhead, "The separate visual playhead was not created.");
assert(context.playhead.style.getPropertyValue("visibility") === "hidden",
  "The renderer target playhead is still visible beneath the visual playhead.");

context.timelineRunning = false;
context.setPixelStyle(context.playhead, "left", 100);
context.setPixelStyle(context.playhead, "top", 20);
context.setPixelStyle(context.playhead, "height", 100);
api.setCursorBeat(0, true);
runFrames();
context.timelineRunning = true;

const authoritativeBefore = context.renderedBeats.length;
api.setCursorBeat(0.12, false);
assert(context.renderedBeats.length === authoritativeBefore + 1,
  "The visual smoothing layer delayed the authoritative cursor update.");

context.setPixelStyle(context.playhead, "left", 148);
const firstBar = runFrames();
assert(firstBar.length >= 5,
  `The playhead did not interpolate across enough frames: ${firstBar.length}.`);
assert(firstBar[0].x > 100 && firstBar[0].x < 148,
  `The first barline frame snapped to ${firstBar[0].x}.`);
assert(Math.abs(firstBar.at(-1).x - 148) < 0.04,
  `The playhead did not converge to the first bar target: ${firstBar.at(-1).x}.`);
assert(maximumVisibleStep(firstBar) < 10,
  `The first bar transition still contains a visible step of ${maximumVisibleStep(firstBar)}px.`);

context.setPixelStyle(context.playhead, "left", 194);
const secondBar = runFrames();
assert(secondBar.length >= 5,
  `The second bar transition used too few frames: ${secondBar.length}.`);
assert(maximumVisibleStep(secondBar) < 10,
  `The second bar transition contains a visible step of ${maximumVisibleStep(secondBar)}px.`);

context.playhead.style.left = "232px";
observerCallback?.([]);
const directWrite = runFrames();
assert(directWrite.length >= 5,
  "A direct playhead style write bypassed visual smoothing.");
assert(Math.abs(directWrite.at(-1).x - 232) < 0.04,
  `The visual playhead did not follow a direct style write: ${directWrite.at(-1).x}.`);
assert(maximumVisibleStep(directWrite) < 10,
  `A direct write caused a visible playhead jump of ${maximumVisibleStep(directWrite)}px.`);

api.setReadingMode("Page");
context.timelineRunning = false;
context.setPixelStyle(context.playhead, "left", 60);
context.setPixelStyle(context.playhead, "top", 30);
context.setPixelStyle(context.playhead, "height", 110);
runFrames();
context.timelineRunning = true;
context.setPixelStyle(context.playhead, "left", 116);
const pageBar = runFrames();
assert(pageBar.length >= 5,
  `Page-mode playhead used too few frames: ${pageBar.length}.`);
assert(maximumVisibleStep(pageBar) < 10,
  `Page mode contains a visible playhead jump of ${maximumVisibleStep(pageBar)}px.`);
assert(Math.abs(pageBar.at(-1).x - 116) < 0.04,
  `Page-mode playhead did not converge: ${pageBar.at(-1).x}.`);

context.setPixelStyle(context.playhead, "left", 42);
context.setPixelStyle(context.playhead, "top", 190);
context.setPixelStyle(context.playhead, "height", 108);
const relocation = runFrames();
const largeVerticalFrame = relocation.findIndex((sample, index) =>
  index > 0 && Math.abs(sample.y - relocation[index - 1].y) > 36);
assert(largeVerticalFrame >= 0,
  "The cross-system relocation did not move to its target system.");
assert(relocation[largeVerticalFrame].opacity <= 0.08 ||
       relocation[largeVerticalFrame - 1].opacity <= 0.08,
  "The playhead visibly teleported between systems instead of fading while repositioning.");
assert(Math.abs(relocation.at(-1).x - 42) < 0.04 &&
       Math.abs(relocation.at(-1).y - 190) < 0.04,
  "The cross-system playhead relocation did not settle at its destination.");

api.setReadingMode("Continuous");
context.continuousOffsetX = 24;
context.applyContinuousTransform();
runFrames();
context.continuousOffsetX = -48;
context.applyContinuousTransform();
const sheetMotion = runFrames();
assert(sheetMotion.length >= 5,
  `Continuous sheet movement used too few frames: ${sheetMotion.length}.`);
assert(Math.abs(sheetMotion.at(-1).sheetX + 48) < 0.04,
  `Continuous sheet movement did not converge: ${sheetMotion.at(-1).sheetX}.`);

const state = api.getState();
assert(state.comfortMotion?.installed === true,
  "Comfort-motion telemetry is missing.");
assert(state.comfortMotion.visualProxyInstalled === true,
  "Visual playhead proxy telemetry is missing.");
assert(state.comfortMotion.directWriteObservations >= 1,
  "Direct playhead writes were not observed.");
assert(state.comfortMotion.pageModeFrames >= 5,
  "Page-mode playhead frames were not recorded.");
assert(state.comfortMotion.continuousModeFrames >= 5,
  "Continuous-mode playhead frames were not recorded.");
assert(state.comfortMotion.relocationFadeCount >= 1,
  "Cross-system fade relocation was not recorded.");
assert(state.comfortMotion.maximumVisiblePlayheadStepPx < 12,
  `Visible playhead motion exceeded the comfort limit: ` +
  `${state.comfortMotion.maximumVisiblePlayheadStepPx}px.`);

console.log(
  `Cadenza visual-playhead comfort smoke passed: firstBarFrames=${firstBar.length}, ` +
  `secondBarFrames=${secondBar.length}, directWriteFrames=${directWrite.length}, ` +
  `pageFrames=${pageBar.length}, relocationFrames=${relocation.length}, ` +
  `maxVisibleStep=${state.comfortMotion.maximumVisiblePlayheadStepPx.toFixed(2)}px.`
);