const fs = require("fs");
const path = require("path");

globalThis.HintDisplayPreviewCore = require("./src/preview-core.js");
const core = globalThis.HintDisplayPreviewCore;
const fixtures = require("./fixtures.js");
const backgrounds = require("./backgrounds.js");

function requireCheck(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

let parsedEntries = 0;
let staticIssues = 0;

requireCheck(core.xToPercent(-500) === 0, "x=-500 should map to left edge");
requireCheck(core.xToPercent(0) === 50, "x=0 should map to center");
requireCheck(core.xToPercent(500) === 100, "x=500 should map to right edge");
requireCheck(core.yToPercent(0) === 0, "y=0 should map to top edge");
requireCheck(core.yToPercent(1080) === 100, "y=1080 should map to bottom edge");
requireCheck(core.hsmHalfWidthForAspect(16 / 9) === 1920, "16:9 HSM half-width should match observed 1080p centered-position domain");
// CENTER-X is the in-game-measured linear fit (HSM_X_CAL): X=-600 -> px622, X=+600 -> px1290,
// X=0 -> px956 (midpoint; ~px960 measured), ~0.556 px per HSM unit (header gotcha #2). Edge-to-edge
// is reached via center + a large X (~+/-1745 -> px0/px1920), NOT via left/right alignment (#3/#6).
requireCheck(core.hsmXToPixel(-600) === 622, "HSM center X=-600 should land on the measured px622");
requireCheck(core.hsmXToPixel(600) === 1290, "HSM center X=600 should land on the measured px1290");
requireCheck(core.hsmXToPixel(0) === 956, "HSM center X=0 should land on the measured screen centre (~px956)");
requireCheck(Math.abs(core.hsmXToPixel(1745) - 1920) < 12, "HSM center X~=1745 should reach the right screen edge (~px1920)");
requireCheck(Math.round(core.hsmXToPercent(0, 16 / 9) * 100) / 100 === 49.79, "HSM center X=0 percent should match px956/1920");
requireCheck(core.HSM_TEXT_SAFE_LEFT_PX === 76 && core.HSM_TEXT_SAFE_RIGHT_PX === 1620, "wrap-safe band constants should match the in-game-measured px76..1620");
requireCheck(Math.round(core.hsmAlignedXToPercent({ x: -290, alignment: "left" }, 1920, 16 / 9) * 10) / 10 === 3.6, "HSM left-aligned x=-290 should land near the left HUD lane");
requireCheck(core.hsmYToPercent(0) === 0, "HSM y=0 should map to top edge");
requireCheck(core.hsmYToPercent(1080) === 100, "HSM y=1080 should map to bottom edge");
requireCheck(core.HSM_ALIGNMENT_CANVAS_HALF_WIDTH === 1200, "HSM alignment canvas half-width should match HintServiceMeow CoordinateTools");
requireCheck(backgrounds.length === 15, "native background registry should include none plus fourteen screenshots");
requireCheck(backgrounds.some((background) => background.id === "announcements-and-stat-bar"), "announcement background id should be typo-free");
requireCheck(backgrounds.some((background) => background.id === "inventory-highlighted"), "inventory highlighted background id should be typo-free");
requireCheck(backgrounds.some((background) => background.id === "scp-106-minimap"), "SCP-106 minimap background id should be typo-free");
requireCheck(backgrounds.some((background) => background.id === "scp-abilities-highlighted"), "SCP abilities highlighted background id should be typo-free");
const spectatorBackground = backgrounds.find((background) => background.id === "spectator-highlighted");
requireCheck(spectatorBackground && spectatorBackground.collisionZones.length >= 3, "spectator highlighted background should register native collision zones");
const spectatorZones = core.backgroundCollisionZonesForViewport(spectatorBackground, { width: 1920, height: 1080 });
requireCheck(
  spectatorZones.some((zone) => zone.id === "native.spectator-team-bars" && zone.left === 455 && zone.top === 3),
  "spectator team bars collision zone should match the highlighted screenshot"
);

for (const background of backgrounds.filter((item) => item.path)) {
  const imagePath = path.resolve(__dirname, background.path);
  requireCheck(fs.existsSync(imagePath), `native background missing: ${background.path}`);
  const bytes = fs.readFileSync(imagePath);
  requireCheck(bytes.readUInt32BE(16) === 1920 && bytes.readUInt32BE(20) === 1080, `${background.path} should be 1920x1080`);
}

const percentageSize = core.parseUnityRichText("<size=50%>small</size>", 20);
requireCheck(percentageSize.html.includes("font-size:0.5em"), "percentage size tags should be relative");

const unsupportedFont = core.parseUnityRichText("<font=\"微软雅黑\">中文</font>", 20);
requireCheck(unsupportedFont.html.includes("&lt;font"), "font tags should render as unsupported escaped text in preview");
requireCheck(unsupportedFont.unsupported.length === 2, "font tags should be reported as unsupported");

const unsupportedNested = core.parseUnityRichText("<color=red><sprite=1>Hi</sprite> after</color>", 20);
requireCheck(
  unsupportedNested.html === "<span style=\"color:red\">&lt;sprite=1&gt;Hi&lt;/sprite&gt; after</span>",
  "unsupported close tags should not pop supported spans");

const cspaceEm = core.parseUnityRichText("<cspace=0.25em>S P A C E D</cspace>", 20);
requireCheck(cspaceEm.html.includes("letter-spacing:0.25em"), "cspace em tags should map to em letter-spacing");
requireCheck(cspaceEm.unsupported.length === 0, "cspace should be a supported tag");

const cspacePx = core.parseUnityRichText("<cspace=6px>tight</cspace>", 20);
requireCheck(cspacePx.html.includes("letter-spacing:6px"), "cspace px tags should map to px letter-spacing");

const voffsetUp = core.parseUnityRichText("<voffset=0.3em>up</voffset>", 20);
requireCheck(voffsetUp.html.includes("translateY(-0.3em)"), "positive voffset should raise text (negative translateY)");
requireCheck(voffsetUp.unsupported.length === 0, "voffset should be a supported tag");

const farLeftAlignmentIssues = core.validateEntries([
  { id: "bad-left", owner: "test", system: "hsm", text: "bad", x: -1900, y: 60, alignment: "left", textSize: 18 }
]).filter((issue) => issue.severity === "warn" && issue.message.includes("center alignment"));
requireCheck(farLeftAlignmentIssues.length === 1, "far-left HSM entries should warn when using left alignment");

// --- Wrap-safe band validation (header gotcha #4). A wrappable center-aligned line whose estimated
// box leaves px76..1620 must warn (it WORD-WRAPs/garbles in-game); an in-band label and a single-token
// caret-only marker past the edge must NOT warn. Scoped to center alignment (the measured X path). ---
function wrapBandWarnings(entry) {
  return core
    .validateEntries([entry])
    .filter((issue) => issue.severity === "warn" && issue.message.includes("wrap-safe band"));
}

const wrapBandBad = wrapBandWarnings({
  id: "wrap-bad",
  owner: "test",
  system: "hsm",
  text: "REALITY ANCHOR FIELD STATUS NOMINAL ALL SYSTEMS GREEN",
  x: 600,
  y: 300,
  alignment: "center",
  textSize: 28
});
requireCheck(wrapBandBad.length === 1, "a wide center-aligned label pushed toward the right edge should warn about the HSM wrap-safe band");
requireCheck(wrapBandBad[0].message.includes("right"), "the wide right-edge label should report a right-side overflow");

const wrapBandOk = wrapBandWarnings({
  id: "wrap-ok",
  owner: "test",
  system: "hsm",
  text: "ALL CLEAR",
  x: 0,
  y: 300,
  alignment: "center",
  textSize: 28
});
requireCheck(wrapBandOk.length === 0, "a short in-band center-aligned label should NOT warn about the wrap-safe band");

const wrapBandCaret = wrapBandWarnings({
  id: "wrap-caret",
  owner: "test",
  system: "hsm",
  text: "▼",
  x: 1200,
  y: 300,
  alignment: "center",
  textSize: 28
});
requireCheck(wrapBandCaret.length === 0, "a single-token caret past px1620 should NOT warn (it renders intact in-game, header gotcha #4)");

requireCheck(core.estimateTextWidthPx("ALL CLEAR", 28) < 300, "short label width estimate should be small");
requireCheck(core.estimateTextWidthPx("REALITY ANCHOR FIELD STATUS NOMINAL ALL SYSTEMS GREEN", 28) > 700, "wide label width estimate should be large");

for (const fixture of fixtures) {
  const issues = core.validateEntries(fixture.entries).filter((issue) => issue.severity !== "ok");
  const model = core.buildPreviewModel(fixture.entries, { width: 1920, height: 1080 });
  requireCheck(model.entries.length === fixture.entries.length, `model entry count mismatch for ${fixture.name}`);
  requireCheck(model.metrics.hsmXDomain[0] === -1920 && model.metrics.hsmXDomain[1] === 1920, `model HSM x domain mismatch for ${fixture.name}`);
  staticIssues += issues.length;

  for (const entry of fixture.entries) {
    const normalized = core.normalizeEntry(entry);
    const parsed = core.parseUnityRichText(normalized.text, normalized.textSize);
    requireCheck(!parsed.html.includes("<script"), `unsafe parsed output for ${fixture.name}/${entry.id}`);
    parsedEntries++;
  }

  console.log(`[UIHarness] fixture="${fixture.name}" entries=${fixture.entries.length} issues=${issues.length}`);
}

console.log(`[UIHarness] parsed_entries=${parsedEntries} static_issues=${staticIssues}`);
