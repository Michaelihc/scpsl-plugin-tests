globalThis.HintDisplayPreviewCore = require("./src/preview-core.js");
const core = globalThis.HintDisplayPreviewCore;
const fixtures = require("./fixtures.js");

function argValue(name, fallback) {
  const index = process.argv.indexOf(name);
  if (index === -1 || index + 1 >= process.argv.length) {
    return fallback;
  }

  return process.argv[index + 1];
}

function parseViewport(value) {
  const [width, height] = String(value || "1920x1080").split("x").map(Number);
  return { width: width || 1920, height: height || 1080 };
}

function selectFixture(value) {
  if (!value) {
    return fixtures[0];
  }

  const numeric = Number(value);
  if (Number.isInteger(numeric) && fixtures[numeric]) {
    return fixtures[numeric];
  }

  const lower = String(value).toLowerCase();
  return fixtures.find((fixture) => fixture.name.toLowerCase() === lower) || fixtures[0];
}

const fixture = selectFixture(argValue("--fixture", "0"));
const viewport = parseViewport(argValue("--viewport", "1920x1080"));
const model = core.buildPreviewModel(fixture.entries, viewport);

process.stdout.write(JSON.stringify({
  fixture: fixture.name,
  summary: fixture.summary,
  ...model
}, null, 2));
