const fs = require("fs");
const path = require("path");
const { chromium } = require("playwright");

globalThis.HintDisplayPreviewCore = require("./src/preview-core.js");
const core = globalThis.HintDisplayPreviewCore;
const fixtures = require("./fixtures.js");
const backgrounds = require("./backgrounds.js");

function argValue(name, fallback) {
  const index = process.argv.indexOf(name);
  if (index === -1 || index + 1 >= process.argv.length) {
    return fallback;
  }

  return process.argv[index + 1];
}

function hasArg(name) {
  return process.argv.includes(name);
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

function selectBackground(value) {
  if (!value || value === "none") {
    return null;
  }

  const lower = String(value).toLowerCase();
  const registered = backgrounds.find((background) => background.id.toLowerCase() === lower);
  const backgroundPath = registered ? registered.path : value;
  if (!backgroundPath) {
    return null;
  }

  return Object.assign({}, registered || { id: "custom", name: String(value), collisionZones: [] }, {
    path: path.resolve(__dirname, backgroundPath)
  });
}

function loadEntries() {
  const input = argValue("--input", null);
  if (!input) {
    return selectFixture(argValue("--fixture", "0")).entries;
  }

  const raw = input === "-"
    ? fs.readFileSync(0, "utf8")
    : fs.readFileSync(path.resolve(input), "utf8");
  const parsed = JSON.parse(raw.replace(/^\uFEFF/, ""));
  if (Array.isArray(parsed)) {
    return parsed;
  }

  if (Array.isArray(parsed.entries)) {
    return parsed.entries;
  }

  throw new Error("Input JSON must be an entry array or an object with an entries array.");
}

function escapeAttribute(value) {
  return String(value || "")
    .replace(/&/g, "&amp;")
    .replace(/"/g, "&quot;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

function chromeMode() {
  if (hasArg("--lobby-chrome")) {
    return "lobby";
  }

  if (hasArg("--game-chrome")) {
    return "minimal";
  }

  return "none";
}

function gameChromeHtml(mode) {
  if (mode === "none") {
    return "";
  }

  const minimal = [
    "<div class=\"nativeUi fps\" data-collision-zone=\"native.fps\">0 ms</div>",
    "<div class=\"nativeUi mute\" data-collision-zone=\"native.mute\">Mute pre-game lobby □</div>"
  ];

  if (mode !== "lobby") {
    return minimal.join("");
  }

  return [
    ...minimal,
    "<div class=\"nativeUi waitingTitle\" data-collision-zone=\"native.waiting-title\">WAITING FOR PLAYERS</div>",
    "<div class=\"nativeUi waitingSubtitle\" data-collision-zone=\"native.waiting-subtitle\">ROUND START IS <span>PAUSED</span></div>",
    "<div class=\"nativeUi connectedBox\" data-collision-zone=\"native.connected-box\"><strong>1</strong><small>connected</small></div>",
    "<div class=\"nativeUi optOut\" data-collision-zone=\"native.opt-out\">? Opt Out of SCP □</div>"
  ].join("");
}

function imageDataUrl(filePath) {
  const extension = path.extname(filePath).toLowerCase();
  const mime = extension === ".jpg" || extension === ".jpeg" ? "image/jpeg" : "image/png";
  return `data:${mime};base64,${fs.readFileSync(filePath).toString("base64")}`;
}

function backgroundZoneHtml(model, background) {
  return core.backgroundCollisionZonesForViewport(background, model.viewport).map((zone) => {
    const style = [
      `left:${zone.leftPercent}%`,
      `top:${zone.topPercent}%`,
      `width:${zone.widthPercent}%`,
      `height:${zone.heightPercent}%`
    ].join(";");
    return `<div class="backgroundCollisionZone" data-collision-zone="${escapeAttribute(zone.id)}" style="${style}"></div>`;
  }).join("");
}

function pageHtml(model, mode, background, backgroundSrc) {
  const chrome = gameChromeHtml(mode);
  const backgroundImage = backgroundSrc
    ? `<img class="nativeBackground" src="${escapeAttribute(backgroundSrc)}" alt="">`
    : "";
  const backgroundZones = backgroundZoneHtml(model, background);

  const entries = model.entries.map((entry) => {
    const style = [
      `left:${entry.leftPercent}%`,
      `top:${entry.topPercent}%`,
      `font-size:${entry.fontSizePx}px`,
      `color:${entry.color || "inherit"}`,
      `transform:${entry.transform}`
    ].join(";");
    return `<div class="hintEntry" data-id="${escapeAttribute(entry.id)}" data-system="${entry.system}" data-align="${entry.alignment}" data-vertical="${entry.verticalAlign}" style="${style}">${entry.html}</div>`;
  }).join("");

  return `<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <style>
    html, body { margin: 0; width: 100%; height: 100%; overflow: hidden; background: #000; }
    #stage { position: relative; width: ${model.viewport.width}px; height: ${model.viewport.height}px; overflow: hidden; background: #000; color: #fff; font-family: "Microsoft YaHei", "微软雅黑", "Segoe UI", Arial, sans-serif; text-shadow: 0 1px 2px #000, 0 0 4px rgba(0,0,0,.9); }
    .hintEntry { position: absolute; white-space: pre; line-height: 1.08; letter-spacing: 0; transform-origin: center center; }
    .hintEntry[data-align="left"] { text-align: left; }
    .hintEntry[data-align="center"] { text-align: center; }
    .hintEntry[data-align="right"] { text-align: right; }
    .nativeUi { position: absolute; z-index: 1; color: #e8e8e8; pointer-events: none; }
    .nativeBackground { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: fill; z-index: 0; }
    .backgroundCollisionZone { position: absolute; z-index: 1; opacity: 0; pointer-events: none; }
    .fps { left: 4px; top: 2px; font: 700 22px/1 Consolas, monospace; }
    .mute { right: 12px; bottom: 11px; font: 700 16px/1 "Segoe UI", Arial, sans-serif; }
    .waitingTitle { left: 50%; top: 19.2%; transform: translateX(-50%); font: 300 ${Math.round(model.viewport.height * 0.064)}px/1 Consolas, monospace; letter-spacing: .04em; white-space: nowrap; }
    .waitingSubtitle { left: 50%; top: 27.0%; transform: translateX(-50%); font: 300 ${Math.round(model.viewport.height * 0.035)}px/1 Consolas, monospace; letter-spacing: .02em; white-space: nowrap; }
    .waitingSubtitle span { color: #ff7b2f; }
    .connectedBox { left: 50%; top: 36.7%; width: ${Math.round(model.viewport.height * 0.292)}px; height: ${Math.round(model.viewport.height * 0.292)}px; transform: translateX(-50%); border: 4px solid #dedede; box-sizing: border-box; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 22px; }
    .connectedBox strong { font: 700 ${Math.round(model.viewport.height * 0.078)}px/1 "Segoe UI", Arial, sans-serif; }
    .connectedBox small { font: 700 ${Math.round(model.viewport.height * 0.025)}px/1 Consolas, monospace; }
    .optOut { left: 50%; top: 71.9%; transform: translateX(-50%); font: 700 ${Math.round(model.viewport.height * 0.024)}px/1 "Segoe UI", Arial, sans-serif; white-space: nowrap; }
    #hintLayer { position: absolute; inset: 0; z-index: 2; }
  </style>
</head>
<body>
  <div id="stage">${backgroundImage}${backgroundZones}${chrome}<div id="hintLayer">${entries}</div></div>
</body>
</html>`;
}

async function main() {
  const viewport = parseViewport(argValue("--viewport", "1920x1080"));
  const output = path.resolve(argValue("--output", "output/playwright/hint-render.png"));
  const entries = loadEntries();
  const model = core.buildPreviewModel(entries, viewport);
  const mode = chromeMode();
  const background = selectBackground(argValue("--background", null));
  if (background && !fs.existsSync(background.path)) {
    throw new Error(`Background image not found: ${background.path}`);
  }
  const backgroundSrc = background ? imageDataUrl(background.path) : null;

  fs.mkdirSync(path.dirname(output), { recursive: true });

  const browser = await chromium.launch();
  const page = await browser.newPage({
    viewport,
    deviceScaleFactor: Number(argValue("--device-scale", "1")) || 1
  });
  await page.setContent(pageHtml(model, mode, background, backgroundSrc), { waitUntil: "load" });
  const layout = await page.evaluate(() => {
    const stage = document.querySelector("#stage");
    const bounds = stage.getBoundingClientRect();
    const toBox = (element, type, id) => {
      const rect = element.getBoundingClientRect();
      return {
        id,
        type,
        left: rect.left - bounds.left,
        top: rect.top - bounds.top,
        right: rect.right - bounds.left,
        bottom: rect.bottom - bounds.top,
        width: rect.width,
        height: rect.height
      };
    };
    const hints = Array.from(document.querySelectorAll(".hintEntry"))
      .map((element) => toBox(element, "hint", element.dataset.id || "hint"));
    const native = Array.from(document.querySelectorAll("[data-collision-zone]"))
      .map((element) => toBox(element, "native", element.dataset.collisionZone || "native"));
    const issues = [];
    const boxes = [...hints, ...native];

    for (const box of hints) {
      const overflow = box.left < -1 ||
        box.right > bounds.width + 1 ||
        box.top < -1 ||
        box.bottom > bounds.height + 1;
      if (overflow && !String(box.id).startsWith("border.")) {
        issues.push({ severity: "warn", message: `${box.id} visually overflows the viewport.` });
      }
    }

    for (let i = 0; i < boxes.length; i++) {
      for (let j = i + 1; j < boxes.length; j++) {
        const first = boxes[i];
        const second = boxes[j];
        if (first.type === "native" && second.type === "native") {
          continue;
        }

        const width = Math.min(first.right, second.right) - Math.max(first.left, second.left);
        const height = Math.min(first.bottom, second.bottom) - Math.max(first.top, second.top);
        if (width > 2 && height > 2) {
          issues.push({
            severity: "error",
            message: `${first.id} overlaps ${second.id} (${Math.round(width)}x${Math.round(height)}px).`
          });
        }
      }
    }

    return { boxes, issues };
  });
  await page.locator("#stage").screenshot({ path: output });
  await browser.close();

  process.stdout.write(JSON.stringify({
    output,
    viewport,
    chrome: mode,
    background: background ? background.path : null,
    entries: model.entries.length,
    validation: model.validation,
    layout
  }, null, 2));

  if (hasArg("--fail-on-collision") && layout.issues.some((issue) => issue.severity === "error")) {
    process.exit(2);
  }
}

main().catch((error) => {
  console.error(error.stack || error.message);
  process.exit(1);
});
