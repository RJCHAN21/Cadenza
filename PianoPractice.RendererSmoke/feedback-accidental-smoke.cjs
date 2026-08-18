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

function sourceBetween(start, end) {
  const startIndex = playerSource.indexOf(start);
  const endIndex = playerSource.indexOf(end, startIndex);
  if (startIndex < 0 || endIndex < 0)
    throw new Error(`Could not extract renderer source between ${start} and ${end}.`);
  return playerSource.slice(startIndex, endIndex);
}

const context = {
  keySignature: "C major",
  toolkit: null
};
vm.createContext(context);
vm.runInContext(
  sourceBetween("    function keyAccidentalForPname", "    function pitchSpelling"),
  context,
  { filename: playerPath });

function createNote(pname, octave, glyph = "", pnum = null, accid = "") {
  const accidentalUse = glyph
    ? { getAttribute(name) { return name === "href" ? `#${glyph}-fixture` : null; } }
    : null;
  const note = {
    id: "fixture-note",
    measure: null,
    closest(selector) {
      if (selector === "g.note") return this;
      if (selector === "g.measure") return this.measure;
      return null;
    },
    getAttribute(name) {
      return name === "data-pname" ? pname :
        name === "data-oct" ? String(octave) :
        name === "data-pnum" && pnum != null ? String(pnum) :
        name === "data-accid" && accid ? accid : null;
    },
    querySelector(selector) {
      return selector === ".accid use" ? accidentalUse : null;
    }
  };
  return note;
}

function createMeasure(...notes) {
  const measure = { querySelectorAll(selector) { return selector === "g.note" ? notes : []; } };
  notes.forEach(note => { note.measure = measure; });
  return measure;
}

function assertMidi(note, expected, label) {
  const actual = context.midiFromElement(note);
  if (actual !== expected)
    throw new Error(`${label} resolved to MIDI ${actual}; expected ${expected}.`);
}

assertMidi(createNote("c", 5), 72, "C5");
assertMidi(createNote("c", 5, "E262"), 73, "C-sharp5");
assertMidi(createNote("b", 4, "E260"), 70, "B-flat4");
assertMidi(createNote("f", 4, "E261"), 65, "F-natural4");
assertMidi(createNote("c", 5, "E263"), 74, "C-double-sharp5");
assertMidi(createNote("d", 5, "E264"), 72, "D-double-flat5");

context.keySignature = "G major";
assertMidi(createNote("f", 4), 66, "key-signature F-sharp4");

if (context.hintPitchName(createNote("c", 5, "E262", 73)) !== "C♯5")
  throw new Error("Hint guide did not preserve an explicitly rendered sharp.");
if (context.hintPitchName(createNote("b", 4, "E260", 70)) !== "B♭4")
  throw new Error("Hint guide did not preserve an explicitly rendered flat.");
if (context.hintPitchName(createNote("f", 4, "", 66)) !== "F♯4")
  throw new Error("Hint guide did not apply the G-major key signature.");
if (context.hintPitchName(createNote("f", 4, "E261", 65, "n")) !== "F♮4")
  throw new Error("Hint guide did not show an explicit natural against the key signature.");

context.keySignature = "C major";
const carriedSharp = createNote("f", 4, "", 66);
createMeasure(createNote("f", 4, "E262", 66, "s"), carriedSharp);
if (context.hintPitchName(carriedSharp) !== "F♯4")
  throw new Error("Hint guide did not carry a sharp through its measure.");

const carriedNatural = createNote("f", 4, "", 65);
createMeasure(
  createNote("f", 4, "E262", 66, "s"),
  createNote("f", 4, "E261", 65, "n"),
  carriedNatural);
if (context.hintPitchName(carriedNatural) !== "F♮4")
  throw new Error("Hint guide did not carry a natural through its measure.");

console.log(
  "Feedback and hint accidental smoke passed: explicit, key-signature, and measure-carried accidentals resolved.");
