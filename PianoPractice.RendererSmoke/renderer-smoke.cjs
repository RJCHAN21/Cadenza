const fs = require("node:fs");
const path = require("node:path");

const projectRoot = path.resolve(__dirname, "..");
const assetRoot = path.join(projectRoot, "PianoPractice.Desktop", "Assets", "Verovio");
const fixtureRoot = path.join(projectRoot, "TestData", "Fixtures");
const scorePath = path.resolve(process.argv[2] || path.join(fixtureRoot, "cadenza-timeline.musicxml"));
const contractPath = path.resolve(process.argv[3] || path.join(fixtureRoot, "cadenza-timeline.expected.json"));
const playerPath = path.join(assetRoot, "player.html");
const runtimePatchPath = path.join(assetRoot, "cadenza-runtime-patch.js");
const hostPath = path.join(projectRoot, "PianoPractice.Desktop", "MainWindow.SecurityAndNotationPatch.cs");
const verovio = require(path.join(assetRoot, "verovio-toolkit-wasm.js"));
const epsilon = 0.0001;

for (const requiredPath of [scorePath, contractPath, playerPath, runtimePatchPath, hostPath]) {
  if (!fs.existsSync(requiredPath)) throw new Error(`Required renderer input is missing: ${requiredPath}`);
}

function optionsForMode(continuous) {
  return {
    pageWidth: 2200,
    pageHeight: continuous ? 520 : 1050,
    scale: 56,
    unit: 8.4,
    adjustPageHeight: continuous,
    adjustPageWidth: continuous,
    breaks: continuous ? "none" : "auto",
    spacingLinear: 0.30,
    spacingNonLinear: 0.68,
    minLastJustification: 0,
    footer: "none",
    header: "none",
    pageMarginTop: 70,
    pageMarginBottom: 90,
    pageMarginLeft: 35,
    pageMarginRight: 20,
    font: "Bravura",
    expandNever: true,
    svgViewBox: true,
    svgHtml5: true,
    svgAdditionalAttribute: [
      "note@pname", "note@oct", "note@pnum", "note@accid", "note@accid.ges", "measure@n"
    ]
  };
}

function occurrenceAtPerformanceBeat(timeline, beat) {
  const value = Math.max(0, Number(beat) || 0);
  for (let index = 0; index < timeline.length; index++) {
    const occurrence = timeline[index];
    const end = occurrence.performanceStartBeat + occurrence.durationBeats;
    const isLast = index === timeline.length - 1;
    if (value >= occurrence.performanceStartBeat - epsilon &&
        (value < end - epsilon || (isLast && value <= end + epsilon))) return occurrence;
  }
  return timeline.at(-1) || null;
}

function sourceBeatForOccurrence(performanceBeat, occurrence) {
  if (!occurrence) return Math.max(0, Number(performanceBeat) || 0);
  const offset = Math.max(0, Math.min(
    occurrence.durationBeats,
    Number(performanceBeat) - occurrence.performanceStartBeat));
  return occurrence.sourceStartBeat + offset;
}

function validateOccurrenceContract(contract) {
  const timeline = contract.occurrences;
  if (!Array.isArray(timeline) || timeline.length === 0) throw new Error("The fixture has no occurrence contract.");
  if (timeline.length !== contract.performanceMeasureNumbers.length) {
    throw new Error("Occurrence and performance-measure contracts have different lengths.");
  }

  let nextPerformanceBeat = 0;
  for (let index = 0; index < timeline.length; index++) {
    const occurrence = timeline[index];
    if (occurrence.occurrenceIndex !== index) throw new Error(`Occurrence index ${index} is not contiguous.`);
    if (Math.abs(occurrence.performanceStartBeat - nextPerformanceBeat) > epsilon) {
      throw new Error(`Occurrence ${index} does not begin at performance beat ${nextPerformanceBeat}.`);
    }
    if (occurrence.durationBeats <= 0 ||
        occurrence.sourceStartBeat < -epsilon ||
        occurrence.sourceStartBeat + occurrence.durationBeats > contract.writtenBeats + epsilon) {
      throw new Error(`Occurrence ${index} lies outside the written score.`);
    }
    nextPerformanceBeat += occurrence.durationBeats;
  }
  if (Math.abs(nextPerformanceBeat - contract.performanceBeats) > epsilon) {
    throw new Error(`Occurrence duration is ${nextPerformanceBeat}; expected ${contract.performanceBeats}.`);
  }

  for (const occurrence of timeline) {
    for (const offset of [0, occurrence.durationBeats / 2, occurrence.durationBeats]) {
      const performanceBeat = Math.min(contract.performanceBeats, occurrence.performanceStartBeat + offset);
      const selected = occurrenceAtPerformanceBeat(timeline, performanceBeat);
      if (!selected) throw new Error(`No occurrence resolves performance beat ${performanceBeat}.`);
      const expected = sourceBeatForOccurrence(performanceBeat, selected);
      if (expected < -epsilon || expected > contract.writtenBeats + epsilon) {
        throw new Error(`Performance beat ${performanceBeat} maps outside the written score.`);
      }
    }
  }

  const firstPass = sourceBeatForOccurrence(0, occurrenceAtPerformanceBeat(timeline, 0));
  const repeatPass = sourceBeatForOccurrence(12, occurrenceAtPerformanceBeat(timeline, 12));
  if (Math.abs(firstPass) > epsilon || Math.abs(repeatPass) > epsilon) {
    throw new Error("The second repeat pass does not map back to written beat zero.");
  }
}

function loadScore(toolkit) {
  const extension = path.extname(scorePath).toLowerCase();
  const scoreBytes = fs.readFileSync(scorePath);
  const loaded = extension === ".mxl"
    ? toolkit.loadZipDataBase64(scoreBytes.toString("base64"))
    : toolkit.loadData(scoreBytes.toString("utf8"));
  if (!loaded) throw new Error(`Verovio could not load ${path.basename(scorePath)}.`);
}

function parseTimemap(toolkit) {
  const rendered = toolkit.renderToTimemap({ includeMeasures: true, includeRests: true });
  return typeof rendered === "string" ? JSON.parse(rendered) : rendered;
}

function renderAndIndex(toolkit) {
  const pages = toolkit.getPageCount();
  const elementPages = new Map();
  const pageStaffCounts = [];
  let systems = 0;
  for (let page = 1; page <= pages; page++) {
    const svg = toolkit.renderToSVG(page, {});
    const staffCount = [...svg.matchAll(/<g[^>]*class="[^"]*\bstaff\b[^"]*"[^>]*>/g)].length;
    const systemCount = [...svg.matchAll(/<g[^>]*class="[^"]*\bsystem\b[^"]*"/g)].length;
    pageStaffCounts.push(staffCount);
    systems += systemCount;
    for (const match of svg.matchAll(/data-id="([^"]+)"/g)) elementPages.set(match[1], page);
  }
  return { pages, systems, elementPages, pageStaffCounts };
}

function assertRuntimeUsesAuthoritativeTimeline() {
  const player = fs.readFileSync(playerPath, "utf8");
  const patch = fs.readFileSync(runtimePatchPath, "utf8");
  const host = fs.readFileSync(hostPath, "utf8");
  const requiredContracts = [
    [player, "expandAlways: false"],
    [player, "function setPerformanceTimeline(timeline)"],
    [patch, "options.expandNever = true"],
    [patch, "function occurrenceAtPerformanceBeat(beat)"],
    [patch, "function sourceBeatForOccurrence(performanceBeat, occurrence)"],
    [patch, "const sourceBeat = sourceBeatForOccurrence(performanceBeat, occurrence);"],
    [host, "score.PerformanceMeasures.Select(occurrence => new"],
    [host, ".setPerformanceTimeline("]
  ];
  for (const [source, contract] of requiredContracts) {
    if (!source.includes(contract)) throw new Error(`Authoritative renderer contract is missing: ${contract}`);
  }
  if (/expandAlways\s*:\s*true/.test(player) || /expandAlways\s*:\s*true/.test(patch)) {
    throw new Error("The runtime still asks Verovio to create an independent expanded timeline.");
  }
}

async function main() {
  await new Promise(resolve => {
    if (verovio.module.calledRun) resolve();
    else verovio.module.onRuntimeInitialized = resolve;
  });

  const contract = JSON.parse(fs.readFileSync(contractPath, "utf8"));
  validateOccurrenceContract(contract);
  assertRuntimeUsesAuthoritativeTimeline();

  const toolkit = new verovio.toolkit();
  toolkit.setOptions(optionsForMode(false));
  loadScore(toolkit);

  const timemap = parseTimemap(toolkit);
  let previousBeat = -Infinity;
  for (const event of timemap) {
    if (event.qstamp + epsilon < previousBeat) {
      throw new Error(`Written timemap moved backward from beat ${previousBeat} to ${event.qstamp}.`);
    }
    previousBeat = event.qstamp;
  }
  const maxBeat = timemap.at(-1)?.qstamp ?? 0;
  if (Math.abs(maxBeat - contract.writtenBeats) > 0.01) {
    throw new Error(`Verovio engraved ${maxBeat} written beats; expected ${contract.writtenBeats}.`);
  }

  const pageLayout = renderAndIndex(toolkit);
  const positionedEvents = timemap.filter(event => event.on?.length || event.restsOn?.length || event.measureOn);
  const unresolved = positionedEvents.filter(event => {
    const ids = [...(event.on || []), ...(event.restsOn || []), ...(event.measureOn ? [event.measureOn] : [])];
    return ids.length > 0 && !ids.some(id => pageLayout.elementPages.has(id));
  });
  if (unresolved.length) throw new Error(`${unresolved.length} written timemap event(s) do not resolve to SVG elements.`);
  for (let page = 0; page < pageLayout.pageStaffCounts.length; page++) {
    const count = pageLayout.pageStaffCounts[page];
    if (count < 2 || count % 2 !== 0) {
      throw new Error(`Page ${page + 1} cannot pair every visible grand staff: ${count} staff groups.`);
    }
  }

  toolkit.setOptions(optionsForMode(true));
  toolkit.redoLayout();
  const continuousLayout = renderAndIndex(toolkit);
  if (continuousLayout.pages !== 1 || continuousLayout.systems !== 1) {
    throw new Error(`Continuous mode produced ${continuousLayout.pages} page(s) and ${continuousLayout.systems} system(s).`);
  }

  console.log(
    `Renderer timeline regression passed for ${path.basename(scorePath)}: ` +
    `writtenBeats=${maxBeat}, performanceBeats=${contract.performanceBeats}, ` +
    `occurrences=${contract.occurrences.length}, pages=${pageLayout.pages}, unresolved=0, ` +
    `continuousPages=1, continuousSystems=1.`
  );
}

main().catch(error => {
  console.error(error.message || error);
  process.exitCode = 1;
});
