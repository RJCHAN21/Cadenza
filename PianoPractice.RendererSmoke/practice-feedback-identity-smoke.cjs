const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const assetRoot = path.join(root, "PianoPractice.Desktop", "Assets", "Verovio");
const playerSource = fs.readFileSync(path.join(assetRoot, "player.html"), "utf8");
const mainWindowXaml = fs.readFileSync(path.join(root, "PianoPractice.Desktop", "MainWindow.xaml"), "utf8");
const verovio = require(path.join(assetRoot, "verovio-toolkit-wasm.js"));

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function asTimemap(value) {
  return typeof value === "string" ? JSON.parse(value) : value;
}

function addTie(index, startId, endId) {
  if (!index.has(startId)) index.set(startId, new Set());
  if (!index.has(endId)) index.set(endId, new Set());
  index.get(startId).add(endId);
  index.get(endId).add(startId);
}

function connectedIds(index, noteId) {
  const result = [];
  const visited = new Set([noteId]);
  const queue = [noteId];
  while (queue.length) {
    const current = queue.shift();
    for (const connected of index.get(current) || []) {
      if (visited.has(connected)) continue;
      visited.add(connected);
      queue.push(connected);
      result.push(connected);
    }
  }
  return result;
}

const crossPageTieScore = `<?xml version="1.0"?>
<score-partwise version="4.0">
  <part-list><score-part id="P1"><part-name>Piano</part-name></score-part></part-list>
  <part id="P1">
    <measure number="1">
      <attributes><divisions>1</divisions><time><beats>4</beats><beat-type>4</beat-type></time><clef><sign>G</sign><line>2</line></clef></attributes>
      <note id="tie-start"><pitch><step>D</step><octave>5</octave></pitch><duration>4</duration><tie type="start"/><voice>1</voice><type>whole</type><notations><tied type="start"/></notations></note>
    </measure>
    <measure number="2">
      <print new-page="yes"/>
      <note id="tie-stop"><pitch><step>D</step><octave>5</octave></pitch><duration>4</duration><tie type="stop"/><voice>1</voice><type>whole</type><notations><tied type="stop"/></notations></note>
    </measure>
  </part>
</score-partwise>`;

async function main() {
  await new Promise(resolve => {
    if (verovio.module.calledRun) resolve();
    else verovio.module.onRuntimeInitialized = resolve;
  });

  assert(/if \(verovio\.module\.calledRun\) initializeToolkit\(\);[\s\S]*else verovio\.module\.onRuntimeInitialized = initializeToolkit;/.test(playerSource),
    "The renderer can miss its ready notification when cached WebAssembly initializes before DOMContentLoaded.");
  assert(mainWindowXaml.includes('Value="{Binding SightReadingProgressPercent, Mode=OneWay}"'),
    "The read-only sight-reading progress property must use a one-way binding at startup.");
  assert((mainWindowXaml.match(/HorizontalContentAlignment="Stretch"/g) || []).length >= 8 &&
         mainWindowXaml.includes('HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"'),
    "Sight-reading test icons and labels are not using one shared two-column alignment grid.");
  assert(playerSource.includes('html.sight-reading-test #playhead { display: none !important; }') &&
         playerSource.includes('get("mode") === "sight-reading"'),
    "Sight-reading tests can still show the lesson playhead.");
  assert(/function feedbackEventAtBeat[\s\S]*exactPerformanceEvent/.test(playerSource),
    "Practice feedback does not prefer the exact rendered performance occurrence.");
  assert(/const targetPage = elementPageIndex\.get\(item\.noteIdentity\)/.test(playerSource),
    "Persistent feedback is still gated by the source event page instead of its note identity.");
  assert(/for \(const continuationId of findTiedContinuationIds\(noteIdentity\)\)/.test(playerSource),
    "Accepted Practice notes do not persist tied continuation identities.");
  assert(/function clearPracticeWrongNote[\s\S]*?:scope > g\.wrong-feedback[\s\S]*?updatePracticeFeedbackOverlayVisibility\(\)/.test(playerSource),
    "Clearing an extra Practice note can still remove accepted green noteheads.");
  assert(/function clearPartialFeedback\(occurrenceIndex, beat\)[\s\S]*?Number\(item\.occurrenceIndex \|\| 0\) === idx &&[\s\S]*?Math\.abs\(Number\(item\.beat \|\| 0\) - targetBeat\) <= 0\.0001/.test(playerSource),
    "Resetting an incomplete Practice chord can still remove earlier correct notes from the same measure occurrence.");
  assert(/const partialChordFeedbackDurationMs = 1400/.test(playerSource) &&
         /function clearPartialFeedback[\s\S]*?persistentCorrectFeedback\.get\(key\) === item[\s\S]*?partialChordFeedbackDurationMs/.test(playerSource),
    "Incomplete Practice chord feedback is not retained safely long enough to inspect before a retry.");
  assert(/function beginTimeline[\s\S]*?if \(!lessonRunning\) clearLessonFeedbackForPlayback\(\)/.test(playerSource) &&
         /function clearLessonFeedbackForPlayback[\s\S]*?persistentCorrectFeedback\.clear\(\)[\s\S]*?clearPracticeCorrectNotes\(\)[\s\S]*?resetAudit\(\)/.test(playerSource),
    "Starting Listen does not clear retained correct and incorrect lesson feedback.");

  const repeatToolkit = new verovio.toolkit();
  repeatToolkit.setOptions({
    pageWidth: 2200,
    pageHeight: 1050,
    scale: 56,
    svgHtml5: true,
    expandAlways: true
  });
  repeatToolkit.loadData(fs.readFileSync(
    path.join(root, "TestData", "Fixtures", "cadenza-timeline.musicxml"), "utf8"));
  const timemap = asTimemap(repeatToolkit.renderToTimemap({ includeMeasures: true, includeRests: true }));
  const firstChord = timemap.find(event => Math.abs(event.qstamp) <= 0.0001 && event.on?.length >= 3);
  const repeatedChord = timemap.find(event => Math.abs(event.qstamp - 12) <= 0.0001 && event.on?.length >= 3);
  assert(firstChord && repeatedChord, "Repeat fixture did not expose both chord occurrences.");
  assert(repeatedChord.on.every(id => !firstChord.on.includes(id)),
    "Repeated chord did not receive occurrence-specific rendered note identities.");

  const tieToolkit = new verovio.toolkit();
  tieToolkit.setOptions({
    pageWidth: 1200,
    pageHeight: 800,
    scale: 56,
    breaks: "encoded",
    svgHtml5: true
  });
  tieToolkit.loadData(crossPageTieScore);
  assert(tieToolkit.getPageCount() === 2, "Cross-page tie fixture did not render as two pages.");

  const elementPages = new Map();
  const tieIndex = new Map();
  for (let page = 1; page <= tieToolkit.getPageCount(); page++) {
    const svg = tieToolkit.renderToSVG(page, {});
    for (const match of svg.matchAll(/data-id="([^"]+)"/g)) elementPages.set(match[1], page);
    for (const match of svg.matchAll(/<g[^>]*data-id="([^"]+)"[^>]*data-class="tie"[^>]*>/g)) {
      const attributes = tieToolkit.getElementAttr(match[1]);
      const startId = String(attributes?.startid || "").replace(/^#/, "");
      const endId = String(attributes?.endid || "").replace(/^#/, "");
      if (startId && endId) addTie(tieIndex, startId, endId);
    }
  }
  assert(elementPages.get("tie-start") === 1 && elementPages.get("tie-stop") === 2,
    "Tie endpoints were not indexed on their actual rendered pages.");
  assert(connectedIds(tieIndex, "tie-start").includes("tie-stop"),
    "The next-page tie continuation was not retained in the persistent identity chain.");

  console.log(
    "Practice feedback identity smoke passed: repeated chord uses distinct rendered IDs and cross-page tie continuation resolves on page 2.");
}

main().catch(error => {
  console.error(error.message || error);
  process.exitCode = 1;
});
