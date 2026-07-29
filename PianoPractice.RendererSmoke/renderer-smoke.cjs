const fs = require("node:fs");
const path = require("node:path");

const projectRoot = path.resolve(__dirname, "..");
const assetRoot = path.join(projectRoot, "PianoPractice.Desktop", "Assets", "Verovio");
const playerPath = path.join(assetRoot, "player.html");
const verovio = require(path.join(assetRoot, "verovio-toolkit-wasm.js"));
const scorePath = process.argv[2];
const expectedTotalBeats = Number(process.argv[3] || 306);

if (!scorePath || !fs.existsSync(scorePath)) {
  throw new Error("Pass the MusicXML/MXL score path as the first argument.");
}

async function main() {
  await new Promise(resolve => {
    if (verovio.module.calledRun) resolve();
    else verovio.module.onRuntimeInitialized = resolve;
  });

  const toolkit = new verovio.toolkit();
  toolkit.setOptions({
    pageWidth: 2200,
    pageHeight: 1050,
    scale: 56,
    unit: 8.4,
    adjustPageHeight: false,
    breaks: "auto",
    spacingLinear: 0.30,
    spacingNonLinear: 0.68,
    minLastJustification: 0.0,
    footer: "none",
    header: "none",
    pageMarginTop: 70,
    pageMarginBottom: 90,
    pageMarginLeft: 35,
    pageMarginRight: 20,
    font: "Bravura",
    expandAlways: true,
    svgViewBox: true,
    svgHtml5: true,
    svgAdditionalAttribute: [
      "note@pname",
      "note@oct",
      "note@pnum",
      "note@accid",
      "note@accid.ges",
      "measure@n"
    ]
  });

  const extension = path.extname(scorePath).toLowerCase();
  const scoreBytes = fs.readFileSync(scorePath);
  const loaded = extension === ".mxl"
    ? toolkit.loadZipDataBase64(scoreBytes.toString("base64"))
    : toolkit.loadData(scoreBytes.toString("utf8"));
  if (!loaded) throw new Error("Verovio could not load the supplied score.");

  const renderedTimemap = toolkit.renderToTimemap({ includeMeasures: true, includeRests: true });
  const timemap = typeof renderedTimemap === "string"
    ? JSON.parse(renderedTimemap)
    : renderedTimemap;
  const pageCount = toolkit.getPageCount();
  const elementPage = new Map();
  const elementSystem = new Map();
  const pageStaffCounts = [];
  for (let page = 1; page <= pageCount; page++) {
    const svg = toolkit.renderToSVG(page, {});
    pageStaffCounts.push([...svg.matchAll(/<g[^>]*class="[^"]*\bstaff\b[^"]*"[^>]*>/g)].length);
    const systemStarts = [...svg.matchAll(/<g[^>]*class="[^"]*\bsystem\b[^"]*"/g)]
      .map(match => match.index);
    for (const match of svg.matchAll(/data-id="([^"]+)"/g)) {
      elementPage.set(match[1], page);
      const systemIndex = systemStarts.filter(index => index <= match.index).length;
      if (systemIndex > 0) elementSystem.set(match[1], `${page}:${systemIndex}`);
    }
  }

  const noteEvents = timemap.filter(event => event.on?.length);
  const unresolved = [];
  const backwards = [];
  let previousQstamp = -Infinity;
  for (const event of timemap) {
    if (event.qstamp + 0.0001 < previousQstamp) {
      throw new Error(`Timemap moved backward from beat ${previousQstamp} to ${event.qstamp}.`);
    }
    previousQstamp = event.qstamp;
  }
  previousQstamp = -Infinity;
  let currentPage = 1;
  let priorSystem = null;
  let samePageSystemBoundaries = 0;
  for (const event of noteEvents) {
    if (event.qstamp + 0.0001 < previousQstamp) {
      throw new Error(`Timemap moved backward from beat ${previousQstamp} to ${event.qstamp}.`);
    }
    previousQstamp = event.qstamp;

    const page = event.on.map(id => elementPage.get(id)).find(Boolean) ??
      (event.measureOn ? elementPage.get(event.measureOn) : null);
    if (!page) {
      unresolved.push({ qstamp: event.qstamp, ids: event.on, measureOn: event.measureOn });
      continue;
    }
    if (page < currentPage) {
      backwards.push({ qstamp: event.qstamp, from: currentPage, to: page });
    }
    const system = event.on.map(id => elementSystem.get(id)).find(Boolean) ?? null;
    if (priorSystem && system && priorSystem !== system &&
        priorSystem.split(":")[0] === system.split(":")[0]) {
      samePageSystemBoundaries++;
    }
    priorSystem = system;
    currentPage = page;
  }

  const maxQstamp = timemap.at(-1)?.qstamp ?? 0;
  if (Math.abs(maxQstamp - expectedTotalBeats) > 0.01) {
    throw new Error(`Renderer performance length is ${maxQstamp} beats; expected ${expectedTotalBeats}.`);
  }
  if (unresolved.length) {
    throw new Error(`${unresolved.length} timemap events do not resolve to engraved SVG elements; first beat ${unresolved[0].qstamp}.`);
  }
  if (backwards.length) {
    throw new Error(`Page mapping moved backward ${backwards.length} time(s); first at beat ${backwards[0].qstamp}.`);
  }
  if (!samePageSystemBoundaries) {
    throw new Error(
      `Page mode produced no same-page system boundary for the playhead handoff regression ` +
      `(pages=${pageCount}, mappedSystems=${elementSystem.size}).`
    );
  }

  toolkit.setOptions({
    pageWidth: 2200,
    pageHeight: 520,
    scale: 56,
    unit: 8.4,
    adjustPageHeight: true,
    adjustPageWidth: true,
    breaks: "none",
    spacingLinear: 0.30,
    spacingNonLinear: 0.68,
    minLastJustification: 0.0,
    footer: "none",
    header: "none",
    pageMarginTop: 70,
    pageMarginBottom: 90,
    pageMarginLeft: 35,
    pageMarginRight: 20,
    font: "Bravura",
    expandAlways: true,
    svgViewBox: true,
    svgHtml5: true
  });
  toolkit.redoLayout();
  const continuousPageCount = toolkit.getPageCount();
  if (continuousPageCount !== 1) {
    throw new Error(`Continuous mode must engrave one horizontal page; got ${continuousPageCount}.`);
  }
  const continuousElementPage = new Map();
  let continuousSystemCount = 0;
  for (let page = 1; page <= continuousPageCount; page++) {
    const svg = toolkit.renderToSVG(page, {});
    continuousSystemCount += [...svg.matchAll(/<g[^>]*class="[^"]*\bsystem\b[^"]*"/g)].length;
    for (const match of svg.matchAll(/data-id="([^"]+)"/g)) continuousElementPage.set(match[1], page);
  }
  if (continuousSystemCount !== 1) {
    throw new Error(`Continuous mode must engrave one horizontal system; got ${continuousSystemCount}.`);
  }
  const unresolvedContinuous = [];
  for (const event of noteEvents) {
    const page = event.on.map(id => continuousElementPage.get(id)).find(Boolean) ?? null;
    if (!page) unresolvedContinuous.push(event);
  }
  if (unresolvedContinuous.length) {
    throw new Error(`Continuous repeat expansion left ${unresolvedContinuous.length} note event(s) unresolved.`);
  }
  const restEvents = timemap.filter(event => event.restsOn?.length);
  if (!restEvents.length || !timemap.at(-1)?.restsOff?.length) {
    throw new Error("The repeat-aware timemap does not retain the final rest span through performance end.");
  }

  const playerSource = fs.readFileSync(playerPath, "utf8");
  const requiredContracts = [
    "expandAlways: true",
    "latestRequestedBeat",
    "pageTransitionGeneration",
    "if (lessonMode === \"WaitForYou\") setCursorBeat(beat);",
    "const crossesSystem = Boolean(previousSystem && nextSystem && previousSystem !== nextSystem);",
    "const x = crossesSystem ? x1 : x1 + (x2 - x1) * progress;",
    "const renderedSvg = toolkit.renderToSVG(page, {});",
    "breaks: continuous ? \"none\" : \"auto\"",
    "adjustPageWidth: continuous",
    "function centerPageSystems()",
    "function alignContinuousSystem()",
    "function sizeContinuousSvg()",
    "const pageViewBoxWidth = 2200 * 56 / 100",
    "if (notation.querySelector(\"svg\")) alignContinuousSystem();",
    "function eventWithPositionAtOrBefore(ms)",
    "function eventWithPositionAfter(ms)",
    "function elementScoreX(element, useLeftEdge = false)",
    "function eventScoreCenter(event)",
    "function createRuntimeTelemetry(startBeat)",
    "function recordRuntimeTelemetry(beat, previous, next, scoreX, visibleX, highlightedX)",
    "if (event.on?.length) return [...event.on]",
    "maxHighlightedPlayheadError",
    "function validateRendererLayout()",
    "function pitchPoint(event, midiNote, staffNumber = 0)",
    "function showPracticeWrongNote(event, midiNote, dispatchId = 0, preserveExpiry = false, staffNumber = 0)",
    "function validateFeedbackGeometry(beat = latestRequestedBeat)",
    "toolkit?.getElementAttr",
    "staffKind: staff.classList.contains(\"cadenza-bass\") ? \"bass\" : \"treble\"",
    "const persistentCorrectFeedback = new Map()",
    "function reapplyLessonFeedback()",
    "numericRunGeneration !== lessonGeneration",
    "post(\"feedbackAck\"",
    "post(\"feedbackReapplied\"",
    "#practiceWrongNote .correct-notehead { fill: #19f47b; stroke: #19f47b; }",
    "function expectedOverlayPoint(item)",
    "function renderPracticeCorrectNote(key, item)",
    "function clearPracticeCorrectNotes()",
    "const persistentKey = `${lessonGeneration}:${Number(occurrenceIndex || 0)}:${noteIdentity}`",
    "data-feedback-key",
    "duplicateOverlayCount",
    "renderedPracticeCorrectCount",
    "feedbackTelemetry.renderedCorrect += renderedKeys.length",
    "feedbackTelemetry.renderedWrong++",
    "post(\"feedbackTelemetry\"",
    "group.setAttribute(\"data-midi\", point.midi)",
    "if (lessonMode === \"WaitForYou\" && (kind === \"wrong\" || kind === \"extra\"))",
    "if (lessonMode === \"WaitForYou\" && kind === \"correct\") {",
    "className = \"audit-pitch-note\"",
    "runtimeTelemetry.backwardOffsetCount",
    "runtimeTelemetry.futureVisibilityShortfallCount",
    "runtimeTelemetry.completed = runtimeTelemetry.endBeat + .02 >= finalQstamp",
    "cadenzaEnd: true",
    "const anchorX = Math.max(180, stageRect.width * .32);",
    "let nextOffsetX = Math.min(24, previousOffsetX + anchorX - x);",
    "visibleX = x + continuousOffsetX - previousOffsetX;",
    "if (timelineRunning) nextOffsetX = Math.min(continuousOffsetX, nextOffsetX);",
    "notation.style.transition = \"none\";",
    "#stage.hints #notation { width: 100%; }",
    "const width = normalWidth;",
    "spacingLinear: 0.30",
    "spacingNonLinear: 0.68",
    "function setHandMode(mode)",
    "function applyHandFocus()",
    "#stage.hand-right #notation g.staff.cadenza-bass",
    "#stage.hand-left #notation g.staff.cadenza-treble",
    "const centers = staves.map",
    "const split = (Math.min(...centers) + Math.max(...centers)) / 2"
  ];
  for (const contract of requiredContracts) {
    if (!playerSource.includes(contract)) throw new Error(`Renderer contract is missing: ${contract}`);
  }
  const waitCorrectStart = playerSource.indexOf(
    "if (lessonMode === \"WaitForYou\" && kind === \"correct\") {");
  const waitCorrectEnd = playerSource.indexOf("\n      if (kind === \"release\")", waitCorrectStart);
  const waitCorrectBranch = playerSource.slice(waitCorrectStart, waitCorrectEnd);
  if (waitCorrectStart < 0 || waitCorrectEnd < 0 ||
      waitCorrectBranch.includes("classList.add(\"feedback-correct\")") ||
      waitCorrectBranch.includes("paintNotehead(node, \"#19f47b\")")) {
    throw new Error("Practice accepted feedback must use the visible persistent overlay, not tint an engraved SVG subtree.");
  }
  if (!playerSource.includes(
      "practiceWrongNote.querySelector(`:scope > g.correct-feedback[data-feedback-key=\"${CSS.escape(key)}\"]`)")) {
    throw new Error("Practice accepted overlays are not deduplicated by their run/occurrence/note identity key.");
  }
  if (playerSource.includes("setTimeout(() => updateCursor(ms") ||
      playerSource.includes("setTimeout(() => setCursorBeat(beat)") ||
      playerSource.includes("pageTransitionTimer = setTimeout")) {
    throw new Error("A stale timestamp retry remains in the page-transition path.");
  }
  if (playerSource.includes("instanceof SVGGraphicsElement")) {
    throw new Error("Renderer geometry must not depend on the optional SVGGraphicsElement WebView global.");
  }
  if (playerSource.includes("#stage.page-mode:not(.hints)") ||
      playerSource.includes("#stage.hints #notation { width: 92%; transform:") ||
      playerSource.includes("const width = hintMode ?") ||
      playerSource.includes("spacingLinear: hintMode ?") ||
      playerSource.includes("spacingNonLinear: hintMode ?") ||
      playerSource.includes("cadenza-staff-dim") ||
      playerSource.includes("system.appendChild(overlay)")) {
    throw new Error("Hint Page no longer shares the normal Page centering contract.");
  }

  for (let page = 1; page <= pageCount; page++) {
    const staffCount = pageStaffCounts[page - 1];
    if (staffCount < 2 || staffCount % 2 !== 0) {
      throw new Error(`Page ${page} cannot pair every visible grand staff for hand focus: ${staffCount} staff groups.`);
    }
  }

  console.log(
    `Renderer repeat/page regression passed: pages=${pageCount}, events=${noteEvents.length}, ` +
    `maxBeat=${maxQstamp}, unresolved=0, backwardPages=0, ` +
    `pageSystemHandoffs=${samePageSystemBoundaries}, continuousPages=1, ` +
    `continuousSystems=1, restEvents=${restEvents.length}.`
  );
}

main().catch(error => {
  console.error(error.message || error);
  process.exitCode = 1;
});
