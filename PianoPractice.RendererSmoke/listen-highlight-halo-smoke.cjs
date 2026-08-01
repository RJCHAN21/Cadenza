const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const projectRoot = path.resolve(__dirname, '..');
const patchPath = path.join(
  projectRoot,
  'PianoPractice.Desktop',
  'Assets',
  'Verovio',
  'cadenza-listen-highlight-patch.js');
const patchSource = fs.readFileSync(patchPath, 'utf8');

function createStyle() {
  const values = new Map();
  return {
    setProperty(name, value) { values.set(name, String(value)); },
    getPropertyValue(name) { return values.get(name) || ''; },
    removeProperty(name) { const value = values.get(name) || ''; values.delete(name); return value; }
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
      values.add(name); return true;
    },
    values() { return [...values]; }
  };
}

function createParent() {
  return {
    children: [],
    insertBefore(node, reference) {
      const index = this.children.indexOf(reference);
      if (index < 0) this.children.push(node);
      else this.children.splice(index, 0, node);
      node.parentNode = this;
      return node;
    }
  };
}

function createNote(id, parent = createParent()) {
  const node = {
    id,
    parentNode: parent,
    style: createStyle(),
    classList: createClassList(),
    attributes: new Map([['data-id', id]]),
    matches(selector) { return selector === 'g.note'; },
    closest(selector) { return selector === 'g.note' ? this : null; },
    getAttribute(name) { return this.attributes.get(name) || null; },
    setAttribute(name, value) { this.attributes.set(name, String(value)); },
    removeAttribute(name) { this.attributes.delete(name); if (name === 'id') this.id = ''; },
    querySelectorAll() { return []; },
    getBoundingClientRect() { return { left: 0, top: 0, width: 12, height: 12 }; },
    cloneNode() {
      const clone = createNote(this.id, createParent());
      clone.attributes = new Map(this.attributes);
      clone.classList = createClassList(this.classList.values());
      clone.remove = function remove() {
        const index = this.parentNode?.children.indexOf(this) ?? -1;
        if (index >= 0) this.parentNode.children.splice(index, 1);
        this.parentNode = null;
      };
      return clone;
    },
    remove() {
      const index = this.parentNode?.children.indexOf(this) ?? -1;
      if (index >= 0) this.parentNode.children.splice(index, 1);
      this.parentNode = null;
    }
  };
  if (!parent.children.includes(node)) parent.children.push(node);
  return node;
}

const note1 = createNote('n1');
const note2 = createNote('n2');
const notes = [note1, note2];
const stage = { classList: createClassList() };
const styles = [];

const context = {
  console, Math, Number, Object, Array, String, Boolean, Set, Map,
  lessonMode: 'Listen', lessonRunning: true, timelineRunning: true, playing: true, bpm: 120,
  performanceTimeline: [{ occurrenceIndex: 0, sourceStartBeat: 0, performanceStartBeat: 0, durationBeats: 3 }],
  timemap: [
    { qstamp: 0, on: ['n1'] },
    { qstamp: 1, on: ['n2'] },
    { qstamp: 2, restsOn: ['r1'] },
    { qstamp: 3, measureOn: 'm2' }
  ],
  setTimeout(callback) { callback(); return 1; },
  document: {
    head: { appendChild(node) { styles.push(node); return node; } },
    documentElement: { appendChild(node) { styles.push(node); return node; } },
    createElement() { return { id: '', textContent: '' }; },
    getElementById(id) { return id === 'stage' ? stage : styles.find(item => item.id === id) || null; },
    querySelectorAll(selector) {
      if (selector !== '#notation .playing') return [];
      return notes.filter(note => note.classList.contains('playing'));
    }
  }
};

function updateRawPlaying(beat) {
  notes.forEach(note => note.classList.remove('playing'));
  if (beat >= 0 && beat < 1) note1.classList.add('playing');
  else if (beat >= 1 && beat < 2) note2.classList.add('playing');
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

function assert(condition, message) { if (!condition) throw new Error(message); }
const api = context.window.CadenzaNotation;
api.setPerformanceClock([{ performanceBeat: 0, bpm: 120 }], 3, 120);

api.setCursorBeat(0.1, false);
let state = api.getState().listenHighlight;
assert(state.haloNodeCount === 2, `Expected two halo layers; got ${state.haloNodeCount}.`);
assert(note1.parentNode.children.length === 3, `Expected outer, inner, and source note; got ${note1.parentNode.children.length}.`);
const [outer, inner, source] = note1.parentNode.children;
assert(source === note1, 'Halo layers were not inserted behind the source note.');
assert(outer.classList.contains('cadenza-listen-halo-outer'), 'Outer halo class is missing.');
assert(inner.classList.contains('cadenza-listen-halo-inner'), 'Inner halo class is missing.');
assert(outer.id === '' && inner.id === '', 'Cloned halo IDs were not stripped.');
assert(Math.abs(Number.parseFloat(outer.style.getPropertyValue('--cadenza-listen-glow-duration')) - 500) < 0.2,
  'Halo duration is not synchronized to one beat at 120 BPM.');

api.setCursorBeat(0.45, false);
state = api.getState().listenHighlight;
assert(state.pulseCount === 1, 'Repeated cursor frames retriggered the halo.');
assert(note1.parentNode.children.length === 3, 'Repeated cursor frames duplicated halo layers.');

api.setCursorBeat(1.05, false);
state = api.getState().listenHighlight;
assert(note1.parentNode.children.length === 1, 'Old halo layers were not removed after the note changed.');
assert(note2.parentNode.children.length === 3, 'The next note did not receive two halo layers.');
assert(state.haloNodeCount === 2, 'The active note should retain exactly two halo layers.');

api.setCursorBeat(2.1, false);
state = api.getState().listenHighlight;
assert(state.haloNodeCount === 0, 'A rest did not clear the halo layers.');
assert(note2.parentNode.children.length === 1, 'Halo layers remained mounted during a rest.');

const style = styles.find(item => item.id === 'cadenza-listen-highlight-style');
assert(style.textContent.includes('drop-shadow(0 0 86px'), 'The intensified peak glow is missing.');
assert(style.textContent.includes('cadenzaListenOuterHalo'), 'The outer halo animation is missing.');
assert(style.textContent.includes('cadenzaListenInnerHalo'), 'The inner halo animation is missing.');

console.log(`Listen highlight halo smoke passed: halos=${2}, peak=86px, cleanup=true, retrigger=false.`);
