const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const projectRoot = path.resolve(__dirname, "..");
const playerPath = path.join(
  projectRoot,
  "PianoPractice.Desktop",
  "Assets",
  "Verovio",
  "player.html");
const playerSource = fs.readFileSync(playerPath, "utf8");
const edgePatchSource = fs.readFileSync(
  path.join(projectRoot, "PianoPractice.Desktop", "Assets", "Verovio", "cadenza-runtime-edge-patch.js"),
  "utf8");

if (!edgePatchSource.includes('node.isConnected && node.classList.contains("expected")') ||
    !edgePatchSource.includes('document.querySelector(".hint-svg-badge") !== null') ||
    !edgePatchSource.includes('const hintKey = `${handMode}:${key}`'))
  throw new Error("Hint refresh caching does not recover removed decorations or react to hand changes.");

const renderPageSource = playerSource.slice(
  playerSource.indexOf("    function renderPage"),
  playerSource.indexOf("    function applyIvoryInk"));
const pendingClearedAt = renderPageSource.indexOf("pendingPage = 0;");
const restoredAt = renderPageSource.indexOf("restoreExpectedStateAfterRedraw();");
if (pendingClearedAt < 0 || restoredAt < pendingClearedAt)
  throw new Error("A score redraw does not restore expected-note hints immediately after the new SVG becomes active.");

const restoreSource = playerSource.slice(
  playerSource.indexOf("    function restoreExpectedStateAfterRedraw"),
  playerSource.indexOf("    function attachSvgHintBadge"));
if (!restoreSource.includes("updateExpectedState(atTime, beat);") ||
    !restoreSource.includes('document.querySelectorAll(".hint-svg-badge")'))
  throw new Error("Redraw restoration is not using the authoritative expected-note and badge refresh path.");

function sourceBetween(start, end) {
  const startIndex = playerSource.indexOf(start);
  const endIndex = playerSource.indexOf(end, startIndex);
  if (startIndex < 0 || endIndex < 0)
    throw new Error(`Could not extract renderer source between ${start} and ${end}.`);
  return playerSource.slice(startIndex, endIndex);
}

function createSvgNode() {
  return {
    attributes: {},
    children: [],
    textContent: "",
    setAttribute(name, value) { this.attributes[name] = String(value); },
    appendChild(child) { this.children.push(child); }
  };
}

const context = {
  document: { createElementNS() { return createSvgNode(); } }
};
vm.createContext(context);
vm.runInContext(
  sourceBetween("    function isHintElementForSelectedHand", "    function updateExpectedState"),
  context,
  { filename: playerPath });
vm.runInContext(
  sourceBetween("    function attachSvgHintBadge", "    function updateHintLane"),
  context,
  { filename: playerPath });

function createHandNote(staffClass) {
  const staff = { classList: { contains(value) { return value === staffClass; } } };
  return {
    closest(selector) {
      if (selector === "g.note") return this;
      if (selector === "g.staff") return staff;
      return null;
    }
  };
}

const handNotes = {
  right: createHandNote("cadenza-treble"),
  left: createHandNote("cadenza-bass")
};
context.elementForVerovioId = id => handNotes[id] || null;
context.handMode = "RightHand";
if (Array.from(context.hintIdsForSelectedHand(["right", "left"])).join(",") !== "right")
  throw new Error("Right-hand hints included a left-hand note.");
context.handMode = "LeftHand";
if (Array.from(context.hintIdsForSelectedHand(["right", "left"])).join(",") !== "left")
  throw new Error("Left-hand hints included a right-hand note.");
context.handMode = "BothHands";
if (Array.from(context.hintIdsForSelectedHand(["right", "left"])).join(",") !== "right,left")
  throw new Error("Both-hands hints did not retain both staves.");

function createNote(measureRight, top = 50) {
  const notehead = {
    getBoundingClientRect() {
      return { left: 100, right: 120, top, bottom: top + 20, width: 20, height: 20 };
    }
  };
  const svg = {
    createSVGPoint() {
      return {
        x: 0,
        y: 0,
        matrixTransform() { return { x: this.x / 2, y: this.y / 2 }; }
      };
    }
  };
  const measure = {
    appended: null,
    getBoundingClientRect() { return { right: measureRight }; },
    getScreenCTM() { return { inverse() { return {}; } }; },
    appendChild(child) { this.appended = child; }
  };
  return {
    ownerSVGElement: svg,
    appended: null,
    measure,
    querySelector(selector) {
      if (selector === ".notehead") return notehead;
      if (selector === ".hint-svg-badge") return null;
      return null;
    },
    closest(selector) {
      if (selector !== "g.measure") return null;
      return measure;
    },
    getScreenCTM() { return { inverse() { return {}; } }; },
    appendChild(child) { this.appended = child; }
  };
}

function assertNear(actual, expected, label) {
  if (Math.abs(Number(actual) - expected) > 0.001)
    throw new Error(`${label} was ${actual}; expected ${expected}.`);
}

const rightNote = createNote(220);
context.attachSvgHintBadge(rightNote, "C♯4");
if (rightNote.measure.appended?.attributes["data-side"] !== "right")
  throw new Error("Hint badge should prefer the right side of the notehead.");
if (rightNote.appended)
  throw new Error("Hint badge was nested under notation instead of appended above the measure content.");
const rightRect = rightNote.measure.appended.children[0];
const rightText = rightNote.measure.appended.children[1];
assertNear(rightRect.attributes.x, 63.5, "Right badge x");
assertNear(rightRect.attributes.y, 24.5, "Right badge y");
assertNear(rightRect.attributes.width, 18, "Right badge width");
assertNear(rightRect.attributes.height, 11, "Right badge height");
assertNear(rightText.attributes.x, 72.5, "Right badge text center");
assertNear(rightText.attributes["font-size"], 6, "Scaled 12-pixel font size");

const edgeNote = createNote(160);
context.attachSvgHintBadge(edgeNote, "C♯4");
if (edgeNote.measure.appended?.attributes["data-side"] !== "left")
  throw new Error("Hint badge should move left rather than cross the measure edge.");
assertNear(edgeNote.measure.appended.children[0].attributes.x, 28.5, "Left badge x");

const highChordNote = createNote(240, 40);
const middleChordNote = createNote(240, 48);
const lowChordNote = createNote(240, 56);
context.attachSvgChordHintBadge([
  { element: lowChordNote, text: "C4" },
  { element: highChordNote, text: "G4" },
  { element: middleChordNote, text: "E4" }
]);
const chordBadge = highChordNote.measure.appended;
if (!chordBadge?.attributes.class.includes("hint-chord-badge"))
  throw new Error("A multi-note chord did not render as one grouped chord guide.");
if (highChordNote.appended || middleChordNote.appended || lowChordNote.appended)
  throw new Error("A chord rendered overlapping individual note-name badges.");
const chordTexts = chordBadge.children.filter(child => child.textContent);
if (chordTexts.map(child => child.textContent).join(",") !== "G4,E4,C4")
  throw new Error("Chord labels are not stacked from the highest rendered note to the lowest.");
assertNear(
  Number(chordTexts[1].attributes.y) - Number(chordTexts[0].attributes.y),
  9.5,
  "Chord row spacing");

console.log("Hint-note badge smoke passed: single labels stay aligned and chord labels use one readable stacked guide.");
