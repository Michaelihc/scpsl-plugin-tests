/*
 * ============================================================================
 *  HSM RENDERING -- READ BEFORE POSITIONING  (in-game-verified gotchas)
 * ----------------------------------------------------------------------------
 *  Measured IN-GAME 2026-06-29 with `reinforcements hsmcal on` at 1920x1080
 *  (screenshots were 1999x1248 / 16:10, normalized onto the 1920x1080 virtual
 *  canvas that SCP:SL ALWAYS composes its UI on -- 4K and 16:10 are scaled to
 *  it). Every pixel number below is in that 1920x1080 space. These facts cost a
 *  long, painful in-game calibration saga; read them before you move any hint.
 *
 *  1. Y AXIS IS 1:1.  HSM Y=0 -> top edge (px0), Y=1080 -> bottom edge
 *     (px1080).  HSM_Y_CAL {0->0, 1080->1080} is correct -- leave it.
 *
 *  2. CENTER-X IS LINEAR & SYMMETRIC, ~0.556 px per HSM unit.  Measured
 *     X=0 -> px960 (exact screen centre), X=-600 -> px622, X=+600 -> px1290.
 *     So a CENTER-aligned hint lands at  pixel ~= 960 + 0.556 * X  (this is
 *     exactly what HSM_X_CAL encodes below).
 *
 *  3. EDGE-TO-EDGE IS REACHED ONLY VIA CENTER ALIGNMENT + A LARGE X (NOT via
 *     Left/Right alignment).  X ~= +/-1745 hits the literal screen edges
 *     (px0 / px1920); +/-1700 ~= px25 / px1895 (just inside); +/-1780 is
 *     OFF-screen and its label wraps back toward centre.
 *
 *  4. *** THE BIG TRAP (cost the most time) ***  THE HSM TEXT AREA IS
 *     ASYMMETRIC AND TEXT WRAPS PAST ITS RIGHT EDGE.  The wrap-safe horizontal
 *     band is roughly px76 (left) .. px1620 (right) -- it is NOT centred on the
 *     screen, and the right edge sits ~300px INSIDE the screen's right edge.
 *     Multi-character / multi-word text whose rendered box extends PAST ~px1620
 *     on the right gets TMP WORD-WRAPPED, and the overflow fragment scatters
 *     elsewhere on the row (a right-edge "X=1700" label once dumped its "1700"
 *     on top of the centre "X=0" marker).  The LEFT side is more forgiving
 *     (text renders out to ~px76).  A SINGLE token with NO wrap point (e.g. a
 *     lone caret) renders past px1620 WITHOUT wrapping -- which is why edge
 *     CARETS work but edge LABELS do not.  Encoded as HSM_TEXT_SAFE_LEFT_PX /
 *     HSM_TEXT_SAFE_RIGHT_PX; validateEntries WARNS on center-aligned text
 *     whose estimated box leaves the band.
 *
 *  5. <nobr> DOES NOT RELIABLY STOP THE WRAP (tested in-game -- HSM's parser /
 *     TMP still wrapped).  Do NOT rely on it.  The reliable fix is to keep
 *     wrappable text inside ~px76..1620, OR make outer/edge markers caret-only
 *     (a single token with no wrap point).
 *
 *  6. HSM LEFT/RIGHT ALIGNMENT CLAMPS the text edge at the hint-area boundary
 *     (~px1620 right) and IGNORES XCoordinate once pushed past it -- it CANNOT
 *     reach the true right screen edge.  (A HSM-fork attempt to render
 *     alignment via per-line <pos> was REVERTED: it caused exactly the wrap in
 *     #4.  The hint-area width is client-side and unchangeable from HSM.)
 *     For edge HUDs use CENTER + the right X (#3), never Left/Right alignment.
 *
 *  7. HintVerticalAnchor.Bottom RENDERS OFF-SCREEN in-game.  Never bottom-
 *     anchor; use Middle or Top only (validateEntries warns on Bottom).
 *
 *  8. The reinforcements HsmHintDisplayProvider forces Center + DefaultX unless
 *     you call its ShowPrompt(...alignment, anchor) overload.  Its real value
 *     is EXPOSING X: use Center alignment + a large X for edge positioning.
 * ============================================================================
 */
(function attachPreviewCore(root, factory) {
  const core = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = core;
  }
  root.HintDisplayPreviewCore = core;
})(typeof globalThis !== "undefined" ? globalThis : this, function createPreviewCore() {
  const MIN_X = -500;
  const MAX_X = 500;
  const MIN_Y = 0;
  const MAX_Y = 1080;
  const HSM_MIN_Y = 0;
  const HSM_MAX_Y = 1080;
  const HSM_BASE_HEIGHT = 1080;
  const HSM_ALIGNMENT_CANVAS_HALF_WIDTH = 1200;
  const DEFAULT_TEXT_SIZE = 20;
  const EDGE_OFFSET_BASE = 1079;
  const DISPLAY_AREA_WIDTH = 1200;

  // In-game-measured TMP wrap boundaries (2026-06-29, 1920x1080 virtual canvas).
  // The wrap-safe band is ASYMMETRIC (see header gotcha #4): wrappable text whose
  // rendered box leaves px76..px1620 gets TMP word-wrapped / scattered in-game.
  // These are NOT the screen edges (px0/px1920) -- the right edge sits ~300px
  // INSIDE the screen on the right; the left is more forgiving out to ~px76.
  const HSM_TEXT_SAFE_LEFT_PX = 76;
  const HSM_TEXT_SAFE_RIGHT_PX = 1620;
  // CSS font-size multiplier the renderer applies at 1080p (see fontSizeToPixels:
  // (size/1080)*viewportHeight*1.22, == size*1.22 at height 1080). Width estimates
  // use the same factor so the wrap-band check matches what gets rendered.
  const FONT_PX_PER_SIZE_UNIT = 1.22;

  function clamp(value, min, max) {
    const number = Number(value);
    if (!Number.isFinite(number)) {
      return min;
    }

    return Math.max(min, Math.min(max, number));
  }

  function normalizeAlignment(alignment) {
    const value = String(alignment || "center").toLowerCase();
    return value === "left" || value === "right" ? value : "center";
  }

  function normalizeVerticalAlign(verticalAlign) {
    const value = String(verticalAlign || "middle").toLowerCase();
    return value === "top" || value === "bottom" ? value : "middle";
  }

  function normalizeSystem(system) {
    return "hsm";
  }

  function numericOr(value, fallback) {
    const number = Number(value);
    return Number.isFinite(number) ? number : fallback;
  }

  function normalizeEntry(entry) {
    const system = normalizeSystem(entry.system || entry.coordinateSystem || entry.renderer);
    const alignment = normalizeAlignment(entry.alignment);
    const xValue = entry.xCoordinate === undefined ? entry.x : entry.xCoordinate;
    const yValue = entry.yCoordinate === undefined ? entry.y : entry.yCoordinate;
    const defaultY = system === "hsm" ? 540 : 500;
    const rawX = numericOr(xValue, 0);
    const rawY = numericOr(yValue, defaultY);

    return {
      id: String(entry.id || "entry"),
      owner: String(entry.owner || "preview"),
      system,
      text: String(entry.text || ""),
      x: rawX,
      y: rawY,
      alignment,
      verticalAlign: normalizeVerticalAlign(entry.verticalAlign || entry.yCoordinateAlign),
      textSize: clamp(entry.textSize === undefined ? DEFAULT_TEXT_SIZE : entry.textSize, 1, 120),
      lineHeight: Math.max(0, numericOr(entry.lineHeight, 0)),
      color: entry.color || null,
      priority: Number(entry.priority || 0),
      preserveLowercase: Boolean(entry.preserveLowercase)
    };
  }

  function defaultX(alignment) {
    if (alignment === "left") {
      return MIN_X;
    }

    if (alignment === "right") {
      return MAX_X;
    }

    return 0;
  }

  function aspectRatio(width, height) {
    return Number(width) / Number(height);
  }

  function edgeOffsetForAspect(aspect) {
    return -Math.min(((aspect * EDGE_OFFSET_BASE) - DISPLAY_AREA_WIDTH) / 2, DISPLAY_AREA_WIDTH);
  }

  function viewportWidthForAspect(aspect) {
    const edgeOffset = edgeOffsetForAspect(aspect);
    return DISPLAY_AREA_WIDTH - (edgeOffset * 2);
  }

  function hsmHalfWidthForAspect(aspect) {
    return HSM_BASE_HEIGHT * Number(aspect || 16 / 9);
  }

  // ==========================================================================
  // HSM COORDINATE CALIBRATION  (THE ONE PLACE TO EDIT AFTER AN IN-GAME SCREENSHOT)
  // --------------------------------------------------------------------------
  // HSM screen coordinates are NOT guaranteed to map 1:1 onto output pixels, so
  // a layout that passes THIS harness can still be wrong in-game (it is only an
  // approximation of TextMeshPro/HSM rendering). Each axis is a LINEAR FIT through
  // two MEASURED reference points, expressed at a reference resolution of
  // HSM_CAL_REF_WIDTH x HSM_CAL_REF_HEIGHT (1920x1080). Percent = pixel / refDim
  // * 100; the harness then scales percent to any viewport (SCP:SL scales its UI,
  // so 4K visually matches 1080p).
  //
  // TO RECALIBRATE FROM A SCREENSHOT (one-line-per-axis change, no other edits):
  //   1. In-game (admin/RA):  reinforcements hsmcal on   -> screenshot at 1920x1080.
  //   2. Y axis: pick two labeled rulers (e.g. "Y = 100" and "Y = 900") and read
  //      the pixel ROW (0=top .. 1080=bottom on the 1080p shot) where each line
  //      actually renders. Put them in HSM_Y_CAL.a / .b as { hsm, px }.
  //   3. X axis: pick two labeled carets (e.g. "X = -600" and "X = 600") and read
  //      the pixel COLUMN (0=left .. 1920=right) under each caret. Put them in
  //      HSM_X_CAL.a / .b as { hsm, px }.
  //   That is the whole change: edit the four { hsm, px } pairs below.
  //
  // KNOWN FACTS (already encoded here; do not regress):
  //   * HSM left-align X=0 is NOT the screen edge (~px360 at 1080p). Reaching the
  //     left edge needs NEGATIVE X. The left/right alignment math lives separately
  //     in hsmAlignedXToPercent (HSM's 1200-wide alignment text area); only
  //     CENTER-aligned hints use HSM_X_CAL below -- and CENTER is the path that
  //     gameplay features and the in-game `hsmcal` grid use.
  //   * HintVerticalAnchor.Bottom renders OFF-SCREEN in-game; do not bottom-anchor
  //     (validateEntries warns on it).
  //   * HSM Y grows top -> bottom (Y=0 near the top, Y=1080 near the bottom). HSM
  //     positions each line via a TMP <voffset> derived from (700 - Y), so the true
  //     pixels-per-HSM-unit is exactly what the screenshot measures (see
  //     HintServiceMeow CoordinateTools / HintParser).
  //
  // DEFAULTS BELOW reproduce the prior (uncalibrated) approximation EXACTLY:
  //   Y: 0->0px, 1080->1080px      X: -1920->0px, 1920->1920px   (16:9)
  // so behavior is unchanged until a real screenshot is read in.
  const HSM_CAL_REF_WIDTH = 1920;
  const HSM_CAL_REF_HEIGHT = 1080;
  const HSM_Y_CAL = {
    a: { hsm: 0, px: 0 },
    b: { hsm: 1080, px: 1080 }
  };
  // MEASURED 2026-06-29 from the in-game `hsmcal` grid (1999x1248 shot normalized to 1920):
  // X=-600 caret -> px 622, X=+600 caret -> px 1290 (centre X=0 -> px ~960; ~0.556 px / HSM-unit).
  // NOTE: this is the CENTER-aligned scale. Right-align is NOT the mirror of left-align: right-aligned
  // text CLAMPS at ~px1620 (it ignores larger +X; verified X=+200 and X=+600 land identically), while
  // left-align + NEGATIVE X reaches ~px76. So edge math stays in hsmAlignedXToPercent, not here.
  const HSM_X_CAL = {
    a: { hsm: -600, px: 622 },
    b: { hsm: 600, px: 1290 }
  };

  // Linear fit pixel = a.px + (value - a.hsm) * (b.px - a.px) / (b.hsm - a.hsm).
  function calibratedPixel(cal, value) {
    const span = cal.b.hsm - cal.a.hsm;
    if (!Number.isFinite(span) || span === 0) {
      return cal.a.px;
    }

    return cal.a.px + (((Number(value) || 0) - cal.a.hsm) * (cal.b.px - cal.a.px)) / span;
  }

  function hsmYToPixel(y) {
    return calibratedPixel(HSM_Y_CAL, y);
  }

  function hsmXToPixel(x) {
    return calibratedPixel(HSM_X_CAL, x);
  }

  function xToPercent(x) {
    return ((clamp(x, MIN_X, MAX_X) + 500) / 1000) * 100;
  }

  function yToPercent(y) {
    return (clamp(y, MIN_Y, MAX_Y) / MAX_Y) * 100;
  }

  // CENTER-aligned HSM X. `aspect` is kept for signature back-compat; the calibration is anchored at the
  // 16:9 reference resolution, and non-16:9 viewports still position by percent (matching SCP:SL UI scaling).
  function hsmXToPercent(x, aspect) {
    return (hsmXToPixel(x) / HSM_CAL_REF_WIDTH) * 100;
  }

  function hsmYToPercent(y) {
    return (hsmYToPixel(y) / HSM_CAL_REF_HEIGHT) * 100;
  }

  function hsmAlignedXToPercent(entry, viewportWidth, aspect) {
    // LEFT/RIGHT alignment positions text inside HSM's 1200-wide alignment area, NOT at the screen edge:
    // with left alignment, X=0 lands the text-area left edge (~px360 at 1080p), so reaching the true screen
    // edge needs a NEGATIVE X. (Center alignment instead flows through HSM_X_CAL via hsmXToPercent.)
    if (entry.alignment === "left") {
      const leftTextAreaEdge = (viewportWidth - DISPLAY_AREA_WIDTH) / 2;
      return ((leftTextAreaEdge + (Number(entry.x) || 0)) / viewportWidth) * 100;
    }

    if (entry.alignment === "right") {
      const rightTextAreaEdge = (viewportWidth + DISPLAY_AREA_WIDTH) / 2;
      return ((rightTextAreaEdge + (Number(entry.x) || 0)) / viewportWidth) * 100;
    }

    return hsmXToPercent(entry.x, aspect);
  }

  function xToPercentForEntry(entry, aspect, viewportWidth) {
    return entry.system === "hsm"
      ? hsmAlignedXToPercent(entry, viewportWidth, aspect)
      : xToPercent(entry.x);
  }

  function yToPercentForEntry(entry) {
    return entry.system === "hsm"
      ? hsmYToPercent(entry.y)
      : yToPercent(entry.y);
  }

  function fontSizeToPixels(textSize, viewportHeight, system) {
    return (clamp(textSize, 1, 120) / 1080) * viewportHeight * 1.22;
  }

  function transformForEntry(entry) {
    const horizontal = entry.alignment === "left"
      ? "0"
      : entry.alignment === "right"
        ? "-100%"
        : "-50%";
    const vertical = entry.verticalAlign === "top"
      ? "0"
      : entry.verticalAlign === "bottom"
        ? "-100%"
        : "-50%";

    return `translate(${horizontal}, ${vertical})`;
  }

  function escapeHtml(value) {
    return String(value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function parseUnityRichText(input, inheritedSize) {
    const stack = [];
    const unsupported = [];
    const text = String(input || "").replace(/\\n/g, "\n");
    let html = "";
    let cursor = 0;
    let match;
    const tagRegex = /<([^<>]+)>/g;

    while ((match = tagRegex.exec(text)) !== null) {
      html += escapeHtml(text.slice(cursor, match.index)).replace(/\n/g, "<br>");
      cursor = tagRegex.lastIndex;

      const raw = match[1].trim();
      const lower = raw.toLowerCase();
      if (lower === "br" || lower === "br/") {
        html += "<br>";
        continue;
      }

      if (lower.startsWith("/")) {
        const closingTag = lower.slice(1).split("=")[0];
        if (stack.length > 0 && stack[stack.length - 1] === closingTag) {
          html += "</span>";
          stack.pop();
        } else {
          unsupported.push(raw);
          html += escapeHtml(match[0]);
        }
        continue;
      }

      if (lower === "b") {
        html += "<span style=\"font-weight:700\">";
      } else if (lower === "i") {
        html += "<span style=\"font-style:italic\">";
      } else if (lower === "lowercase") {
        html += "<span style=\"text-transform:none\">";
      } else if (lower.startsWith("color=")) {
        html += `<span style="color:${escapeHtml(raw.slice(6))}">`;
      } else if (lower.startsWith("size=")) {
        const value = raw.slice(5).trim();
        const size = parseFloat(value);
        const multiplier = value.endsWith("%")
          ? size / 100
          : size / inheritedSize;
        html += `<span style="font-size:${Number.isFinite(multiplier) ? multiplier : 1}em">`;
      } else if (lower.startsWith("alpha=#")) {
        html += `<span style="opacity:${parseInt(raw.slice(7), 16) / 255}">`;
      } else if (lower.startsWith("align=")) {
        const alignment = normalizeAlignment(raw.slice(6));
        html += `<span style="display:block;text-align:${alignment}">`;
      } else if (lower.startsWith("line-height=")) {
        const value = raw.slice(12).trim();
        html += `<span style="line-height:${escapeHtml(value || "1")}">`;
      } else if (lower.startsWith("indent=")) {
        const value = raw.slice(7).trim();
        html += `<span style="display:inline-block;margin-left:${escapeHtml(value || "0")}">`;
      } else if (lower.startsWith("mspace=")) {
        html += "<span style=\"font-family:Consolas,'Courier New',monospace\">";
      } else if (lower.startsWith("space=")) {
        const space = parseFloat(raw.slice(6));
        html += `<span style="display:inline-block;width:${Number.isFinite(space) ? space / 20 : 0}em"></span>`;
        continue;
      } else if (lower.startsWith("scale=")) {
        const scale = parseFloat(raw.slice(6));
        html += `<span style="display:inline-block;transform:scale(${Number.isFinite(scale) ? scale : 1})">`;
      } else if (lower.startsWith("cspace=")) {
        const value = raw.slice(7).trim();
        const amount = parseFloat(value);
        const unit = value.toLowerCase().endsWith("em") ? "em" : "px";
        html += `<span style="letter-spacing:${Number.isFinite(amount) ? amount : 0}${unit}">`;
      } else if (lower.startsWith("voffset=")) {
        const value = raw.slice(8).trim();
        const amount = parseFloat(value);
        const unit = value.toLowerCase().endsWith("em") ? "em" : "px";
        // Positive voffset raises text, so translate by the negative amount.
        html += `<span style="display:inline-block;transform:translateY(${Number.isFinite(amount) ? -amount : 0}${unit})">`;
      } else {
        unsupported.push(raw);
        html += escapeHtml(match[0]);
        continue;
      }

      stack.push(lower.split("=")[0]);
    }

    html += escapeHtml(text.slice(cursor)).replace(/\n/g, "<br>");
    while (stack.length > 0) {
      html += "</span>";
      stack.pop();
    }

    return { html, unsupported };
  }

  function buildCalibrationEntries(config) {
    const cfg = Object.assign({
      columns: 480,
      rows: 42,
      borderTextSize: 18,
      cornerTextSize: 20,
      topY: 965,
      bottomY: 35,
      topLabelY: 930,
      bottomLabelY: 70,
      showScales: true,
      scaleStep: 100,
      scaleMinY: 200,
      scaleMaxY: 800,
      showCoordinateProbes: true
    }, config || {});

    const entries = [];
    const red = "#FF3B30";
    const add = (id, text, y, alignment, textSize, x) => {
      entries.push({ id, owner: "hsm.legacy-calibration", system: "hsm", text, y, x, alignment, textSize, color: red, priority: 1000 });
    };

    const horizontal = "#".repeat(Math.max(1, cfg.columns));
    const vertical = Array.from({ length: Math.max(1, cfg.rows) }, () => "#").join("\n");
    const sideY = cfg.topY - (cfg.borderTextSize * 2);

    add("border.top", horizontal, cfg.topY, "left", cfg.borderTextSize, -500);
    add("border.bottom", horizontal, cfg.bottomY, "left", cfg.borderTextSize, -500);
    add("border.left", vertical, sideY, "left", cfg.borderTextSize, -500);
    add("border.right", vertical, sideY, "right", cfg.borderTextSize, 500);
    add("corner.tl", "### TL ###", cfg.topLabelY, "left", cfg.cornerTextSize, -500);
    add("corner.tr", "### TR ###", cfg.topLabelY, "right", cfg.cornerTextSize, 500);
    add("corner.bl", "### BL ###", cfg.bottomLabelY, "left", cfg.cornerTextSize, -500);
    add("corner.br", "### BR ###", cfg.bottomLabelY, "right", cfg.cornerTextSize, 500);
    add("center", "+ CENTER x0 y500", 500, "center", cfg.cornerTextSize, 0);

    if (cfg.showScales && cfg.scaleStep > 0) {
      const minY = Math.max(cfg.scaleStep, cfg.scaleMinY);
      const maxY = Math.min(950, cfg.scaleMaxY);
      for (let y = minY; y <= maxY; y += cfg.scaleStep) {
        add(`scale.left.${y}`, `| y${y}`, y, "left", cfg.cornerTextSize, -500);
        add(`scale.right.${y}`, `y${y} |`, y, "right", cfg.cornerTextSize, 500);
      }
    }

    if (cfg.showCoordinateProbes) {
      add("probe.left.low", "TXT x-500 y120", 120, "left", cfg.cornerTextSize, -500);
      add("probe.center.low", "TXT x0 y120", 120, "center", cfg.cornerTextSize, 0);
      add("probe.right.low", "TXT x500 y120", 120, "right", cfg.cornerTextSize, 500);
      add("probe.left.high", "TXT x-500 y860", 860, "left", cfg.cornerTextSize, -500);
      add("probe.center.high", "TXT x0 y860", 860, "center", cfg.cornerTextSize, 0);
      add("probe.right.high", "TXT x500 y860", 860, "right", cfg.cornerTextSize, 500);
      add("probe.mid.left", "x-250 y650", 650, "center", cfg.cornerTextSize, -250);
      add("probe.mid.right", "x250 y650", 650, "center", cfg.cornerTextSize, 250);
      add("probe.mid.top", "x0 y750", 750, "center", cfg.cornerTextSize, 0);
      add("probe.mid.bottom", "x0 y250", 250, "center", cfg.cornerTextSize, 0);
    }

    return entries;
  }

  function buildHsmCalibrationEntries(config) {
    const cfg = Object.assign({
      horizontalMarkerCount: 9,
      verticalMarkerCount: 5,
      edgeInsetX: 96,
      edgeInsetY: 34,
      showEdgeLabels: true,
      leftX: -1780,
      rightX: 1780,
      topY: 30,
      bottomY: 1060,
      textSize: 18,
      lineHeight: 0,
      showCoordinates: false,
      primaryColor: "#80FFEA",
      secondaryColor: "#FFCB45"
    }, config || {});

    const entries = [];
    const add = (id, text, x, y, textSize) => {
      entries.push({
        id,
        owner: "hsm.calibration",
        system: "hsm",
        text,
        x,
        y,
        alignment: "center",
        verticalAlign: "middle",
        textSize,
        lineHeight: cfg.lineHeight,
        priority: 1000
      });
    };

    const left = Math.min(cfg.leftX, cfg.rightX);
    const right = Math.max(cfg.leftX, cfg.rightX);
    const top = Math.min(cfg.topY, cfg.bottomY);
    const bottom = Math.max(cfg.topY, cfg.bottomY);
    const width = Math.max(1, right - left);
    const height = Math.max(1, bottom - top);
    const horizontalCount = Math.max(2, Math.min(40, Math.round(cfg.horizontalMarkerCount)));
    const verticalCount = Math.max(2, Math.min(30, Math.round(cfg.verticalMarkerCount)));
    const insetX = Math.min(width * 0.45, Math.max(0, cfg.edgeInsetX));
    const insetY = Math.min(height * 0.35, Math.max(0, cfg.edgeInsetY));
    const textLeft = left + insetX;
    const textRight = right - insetX;
    const textTop = top + insetY;
    const textBottom = bottom - insetY;
    const textWidth = Math.max(1, textRight - textLeft);
    const textHeight = Math.max(1, textBottom - textTop);
    const markerSize = Math.max(6, cfg.textSize);
    const labelSize = Math.max(6, cfg.textSize);

    const markerText = (edge, index, x, y, color) => {
      if (!cfg.showCoordinates) {
        return `<color=${color}>${edge}${String(index).padStart(2, "0")}</color>`;
      }

      return `<color=${color}>${edge}${String(index).padStart(2, "0")}</color>\\n<size=${Math.max(6, cfg.textSize - 5)}><color=#FFFFFF>${Math.round(x)},${Math.round(y)}</color></size>`;
    };

    for (let i = 0; i < horizontalCount; i++) {
      const t = horizontalCount === 1 ? 0.5 : i / (horizontalCount - 1);
      const x = textLeft + (textWidth * t);
      add(`hsmcal.rect.top.${String(i).padStart(2, "0")}`, markerText("T", i, x, textTop, cfg.primaryColor), x, textTop, markerSize);
      add(`hsmcal.rect.bottom.${String(i).padStart(2, "0")}`, markerText("B", i, x, textBottom, cfg.primaryColor), x, textBottom, markerSize);
    }

    for (let i = 0; i < verticalCount; i++) {
      const t = (i + 1) / (verticalCount + 1);
      const y = textTop + (textHeight * t);
      add(`hsmcal.rect.left.${String(i).padStart(2, "0")}`, markerText("L", i, textLeft, y, cfg.secondaryColor), textLeft, y, markerSize);
      add(`hsmcal.rect.right.${String(i).padStart(2, "0")}`, markerText("R", i, textRight, y, cfg.secondaryColor), textRight, y, markerSize);
    }

    if (cfg.showEdgeLabels) {
      const labelY = textTop + Math.min(82, textHeight * 0.18);
      const bottomLabelY = textBottom - Math.min(82, textHeight * 0.18);
      const sideLabelOffset = Math.min(170, textWidth * 0.16);
      const labelText = (label, color) => `<b><color=${color}>${label}</color></b>`;
      add("hsmcal.rect.label.top", labelText("TOP EDGE", cfg.primaryColor), (textLeft + textRight) / 2, labelY, labelSize);
      add("hsmcal.rect.label.bottom", labelText("BOTTOM EDGE", cfg.primaryColor), (textLeft + textRight) / 2, bottomLabelY, labelSize);
      add("hsmcal.rect.label.left", labelText("LEFT EDGE", cfg.secondaryColor), textLeft + sideLabelOffset, labelY, labelSize);
      add("hsmcal.rect.label.right", labelText("RIGHT EDGE", cfg.secondaryColor), textRight - sideLabelOffset, labelY, labelSize);
    }

    return entries;
  }

  function sortEntries(entries) {
    return entries
      .map((entry, sequence) => Object.assign({ sequence }, normalizeEntry(entry)))
      .sort((a, b) => (b.priority - a.priority) || (b.y - a.y) || (a.sequence - b.sequence));
  }

  function buildPreviewModel(entries, viewport) {
    const width = clamp(viewport && viewport.width, 1, 100000);
    const height = clamp(viewport && viewport.height, 1, 100000);
    const aspect = aspectRatio(width, height);
    const hsmHalfWidth = hsmHalfWidthForAspect(aspect);

    const renderedEntries = sortEntries(entries || []).map((entry) => {
      const parsed = parseUnityRichText(entry.text, entry.textSize);
      return {
        id: entry.id,
        owner: entry.owner,
        system: entry.system,
        text: entry.text,
        x: entry.x,
        y: entry.y,
        alignment: entry.alignment,
        verticalAlign: entry.verticalAlign,
        textSize: entry.textSize,
        lineHeight: entry.lineHeight,
        color: entry.color,
        priority: entry.priority,
        leftPercent: xToPercentForEntry(entry, aspect, width),
        topPercent: yToPercentForEntry(entry),
        fontSizePx: fontSizeToPixels(entry.textSize, height, entry.system),
        transform: transformForEntry(entry),
        html: parsed.html,
        unsupportedTags: parsed.unsupported
      };
    });

    return {
      viewport: { width, height, aspectRatio: aspect },
      metrics: {
        hsmXDomain: [-hsmHalfWidth, hsmHalfWidth],
        hsmYDomain: [HSM_MIN_Y, HSM_MAX_Y],
        systems: Array.from(new Set(renderedEntries.map((entry) => entry.system)))
      },
      entries: renderedEntries,
      validation: validateEntries(entries || [])
    };
  }

  function backgroundCollisionZonesForViewport(background, viewport) {
    const zones = background && Array.isArray(background.collisionZones)
      ? background.collisionZones
      : [];
    const sourceWidth = Math.max(1, Number(background && background.sourceWidth) || 1920);
    const sourceHeight = Math.max(1, Number(background && background.sourceHeight) || 1080);
    const width = Math.max(1, Number(viewport && viewport.width) || 1920);
    const height = Math.max(1, Number(viewport && viewport.height) || 1080);

    return zones.map((zone) => {
      const left = (Number(zone.x) || 0) / sourceWidth * width;
      const top = (Number(zone.y) || 0) / sourceHeight * height;
      const zoneWidth = Math.max(0, (Number(zone.width) || 0) / sourceWidth * width);
      const zoneHeight = Math.max(0, (Number(zone.height) || 0) / sourceHeight * height);

      return {
        id: String(zone.id || "native.background-zone"),
        left,
        top,
        width: zoneWidth,
        height: zoneHeight,
        leftPercent: (left / width) * 100,
        topPercent: (top / height) * 100,
        widthPercent: (zoneWidth / width) * 100,
        heightPercent: (zoneHeight / height) * 100
      };
    });
  }

  function renderGameMock(container) {
    container.innerHTML = [
      "<div class=\"nativeUi fps\" data-collision-zone=\"native.fps\">0 ms</div>",
      "<div class=\"nativeUi mute\" data-collision-zone=\"native.mute\">Mute pre-game lobby □</div>"
    ].join("");
  }

  // CJK / full-width glyphs advance ~1.0 em in the in-game font.
  function isWideGlyph(codePoint) {
    return (
      (codePoint >= 0x1100 && codePoint <= 0x115F) || // Hangul Jamo
      (codePoint >= 0x2E80 && codePoint <= 0x303E) || // CJK radicals / Kangxi / punctuation
      (codePoint >= 0x3041 && codePoint <= 0x33FF) || // Kana .. CJK symbols
      (codePoint >= 0x3400 && codePoint <= 0x4DBF) || // CJK Ext A
      (codePoint >= 0x4E00 && codePoint <= 0x9FFF) || // CJK Unified
      (codePoint >= 0xA000 && codePoint <= 0xA4CF) || // Yi
      (codePoint >= 0xAC00 && codePoint <= 0xD7A3) || // Hangul syllables
      (codePoint >= 0xF900 && codePoint <= 0xFAFF) || // CJK compat
      (codePoint >= 0xFF00 && codePoint <= 0xFF60) || // Fullwidth forms
      (codePoint >= 0xFFE0 && codePoint <= 0xFFE6)
    );
  }

  // Box-drawing / block / geometric-shape glyphs (used by HUD bars: # ###  blocks
  // and shape-tray markers). They render roughly full-width.
  function isBlockGlyph(codePoint) {
    return (
      (codePoint >= 0x2500 && codePoint <= 0x259F) || // box drawing + block elements
      (codePoint >= 0x25A0 && codePoint <= 0x25FF) || // geometric shapes (squares/circles/diamonds/triangles)
      (codePoint >= 0x2B00 && codePoint <= 0x2BFF)    // misc symbols & arrowheads
    );
  }

  // Per-glyph advance as a fraction of the font pixel size. Deliberately rough --
  // the HTML preview is not a TMP renderer (see README) -- but good enough to catch
  // when a center-aligned hint leaves the px76..1620 wrap-safe band.
  function glyphAdvancePx(ch, fontPx) {
    if (/\s/.test(ch)) {
      return fontPx * 0.28;
    }

    const codePoint = ch.codePointAt(0);
    if (isWideGlyph(codePoint) || isBlockGlyph(codePoint)) {
      return fontPx * 1.0;
    }

    return fontPx * 0.52; // average Latin / punctuation advance
  }

  // Estimate the rendered pixel box of each text LINE at the 1920x1080 reference.
  // Honors <size=..> (absolute and %) and <mspace=..> (em / px fixed advance) since
  // those dominate HUD width; every other tag is treated as zero-width. Returns one
  // { widthPx, visible } per line so the caller can test the widest line and decide
  // whether that line even has a wrap opportunity.
  function measureTextLines(text, baseTextSize) {
    const normalized = String(text || "").replace(/\\n/g, "\n");
    const rawLines = normalized.split(/\n|<br\s*\/?>/i);
    const base = Number(baseTextSize) || DEFAULT_TEXT_SIZE;
    const lines = [];

    for (const rawLine of rawLines) {
      const sizeStack = [base];
      let mspaceEm = 0; // >0 => fixed advance of (mspaceEm * current fontPx)
      let mspacePx = 0; // >0 => fixed advance of mspacePx pixels
      let widthPx = 0;
      let visible = "";
      const currentSize = () => sizeStack[sizeStack.length - 1];
      const consume = (segment) => {
        for (const ch of segment) {
          if (ch === "\r") {
            continue;
          }

          const fontPx = currentSize() * FONT_PX_PER_SIZE_UNIT;
          let advance;
          if (mspaceEm > 0) {
            advance = mspaceEm * fontPx;
          } else if (mspacePx > 0) {
            advance = mspacePx;
          } else {
            advance = glyphAdvancePx(ch, fontPx);
          }

          widthPx += advance;
          visible += ch;
        }
      };

      const tagRegex = /<([^<>]+)>/g;
      let cursor = 0;
      let match;
      while ((match = tagRegex.exec(rawLine)) !== null) {
        consume(rawLine.slice(cursor, match.index));
        cursor = tagRegex.lastIndex;
        const raw = match[1].trim();
        const lower = raw.toLowerCase();

        if (lower.startsWith("/")) {
          const closing = lower.slice(1).split("=")[0];
          if (closing === "size" && sizeStack.length > 1) {
            sizeStack.pop();
          } else if (closing === "mspace") {
            mspaceEm = 0;
            mspacePx = 0;
          }
          continue;
        }

        if (lower.startsWith("size=")) {
          const value = raw.slice(5).trim();
          const number = parseFloat(value);
          if (Number.isFinite(number)) {
            sizeStack.push(value.endsWith("%") ? (currentSize() * number) / 100 : number);
          } else {
            sizeStack.push(currentSize());
          }
        } else if (lower.startsWith("mspace=")) {
          const value = raw.slice(7).trim();
          const number = parseFloat(value);
          if (Number.isFinite(number)) {
            if (/px$/i.test(value)) {
              mspacePx = number;
              mspaceEm = 0;
            } else {
              mspaceEm = number; // "em" or unitless => em (relative to font size)
              mspacePx = 0;
            }
          }
        }
        // All other tags (color/b/i/line-height/etc.) do not change advance width.
      }

      consume(rawLine.slice(cursor));
      lines.push({ widthPx, visible });
    }

    return lines;
  }

  // Widest rendered line width in px (1920x1080 reference). Exposed for callers/tests.
  function estimateTextWidthPx(text, baseTextSize) {
    return measureTextLines(text, baseTextSize).reduce((max, line) => Math.max(max, line.widthPx), 0);
  }

  // A line WRAPS only if TMP has somewhere to break it: whitespace, or two+ adjacent
  // wide/CJK glyphs (TMP can break between CJK). A lone caret / single token has no
  // wrap point and renders past px1620 intact (header gotcha #4), so it is exempt.
  function lineHasWrapOpportunity(visible) {
    if (/\s/.test(visible)) {
      return true;
    }

    let wide = 0;
    for (const ch of String(visible)) {
      if (isWideGlyph(ch.codePointAt(0))) {
        wide += 1;
        if (wide >= 2) {
          return true;
        }
      }
    }

    return false;
  }

  function validateEntries(entries) {
    const issues = [];
    for (const entry of entries || []) {
      const normalized = normalizeEntry(entry);
      if (!normalized.id) {
        issues.push({ severity: "error", message: "Entry is missing id." });
      }

      if (!normalized.text) {
        issues.push({ severity: "warn", message: `${normalized.id} has empty text.` });
      }

      if (normalized.y < HSM_MIN_Y || normalized.y > HSM_MAX_Y) {
        issues.push({ severity: "warn", message: `${normalized.id} HSM y is outside 0..1080.` });
      }

      // HintVerticalAnchor.Bottom renders OFF-SCREEN in-game (the harness pixel-map did not catch this on
      // its own). Guard it so a layout cannot pass review while relying on a bottom anchor.
      if (normalized.system === "hsm" && normalized.verticalAlign === "bottom") {
        issues.push({
          severity: "warn",
          message: `${normalized.id} uses HSM Bottom vertical anchor, which renders off-screen in-game; anchor Top or Middle instead.`
        });
      }

      // HSM Left/Right alignment CLAMPS the text edge at the hint-area boundary
      // (~px1620 right) and ignores XCoordinate once pushed past it -- it cannot
      // reach the true screen edge (header gotcha #6). Flag using it to reach an
      // edge and point at the only thing that works: center alignment + a large X.
      if (
        normalized.system === "hsm" &&
        normalized.alignment !== "center" &&
        Math.abs(normalized.x) > HSM_ALIGNMENT_CANVAS_HALF_WIDTH
      ) {
        issues.push({
          severity: "warn",
          message: `${normalized.id} uses HSM ${normalized.alignment} alignment at far-edge x=${normalized.x}; HSM ${normalized.alignment} alignment CLAMPS at the hint-area edge (~px${HSM_TEXT_SAFE_RIGHT_PX}) and cannot reach the screen edge -- use center alignment and tune X instead (edge-to-edge needs center + X~=+/-1745).`
        });
      }

      // Wrap-safe band check (the in-game trap, header gotcha #4). Only CENTER
      // alignment positions by the measured center-X scale; Left/Right alignment is
      // the separate clamp path warned about above, so scope this to center to stay
      // faithful and avoid double-flagging HUD lanes. Warn when a WRAPPABLE line's
      // estimated box leaves px76..1620 -- in-game it word-wraps and the overflow
      // fragment scatters across the row.
      if (normalized.system === "hsm" && normalized.alignment === "center") {
        const centerPx = hsmXToPixel(normalized.x);
        for (const line of measureTextLines(normalized.text, normalized.textSize)) {
          if (!lineHasWrapOpportunity(line.visible)) {
            continue; // a single-token / caret-only marker renders past the edge intact
          }

          const halfWidth = line.widthPx / 2;
          const left = centerPx - halfWidth;
          const right = centerPx + halfWidth;
          if (right > HSM_TEXT_SAFE_RIGHT_PX || left < HSM_TEXT_SAFE_LEFT_PX) {
            const side = right > HSM_TEXT_SAFE_RIGHT_PX ? "right" : "left";
            issues.push({
              severity: "warn",
              message: `${normalized.id} (center x=${normalized.x}) text box ~px${Math.round(left)}..${Math.round(right)} overflows the HSM wrap-safe band (px${HSM_TEXT_SAFE_LEFT_PX}..${HSM_TEXT_SAFE_RIGHT_PX}) on the ${side}; it will WORD-WRAP/garble in-game -- keep it inside the band, or use a single-token (caret-only) marker.`
            });
            break; // one warning per entry; the widest wrappable line is enough
          }
        }
      }

      const parsed = parseUnityRichText(normalized.text, normalized.textSize);
      for (const tag of parsed.unsupported) {
        issues.push({ severity: "warn", message: `${normalized.id} uses unsupported tag <${tag}>.` });
      }
    }

    if (issues.length === 0) {
      issues.push({ severity: "ok", message: "Static checks passed." });
    }

    return issues;
  }

  return {
    DISPLAY_AREA_WIDTH,
    EDGE_OFFSET_BASE,
    HSM_ALIGNMENT_CANVAS_HALF_WIDTH,
    HSM_TEXT_SAFE_LEFT_PX,
    HSM_TEXT_SAFE_RIGHT_PX,
    aspectRatio,
    backgroundCollisionZonesForViewport,
    buildHsmCalibrationEntries,
    buildPreviewModel,
    buildCalibrationEntries,
    clamp,
    edgeOffsetForAspect,
    estimateTextWidthPx,
    fontSizeToPixels,
    hsmHalfWidthForAspect,
    hsmAlignedXToPercent,
    hsmXToPercent,
    hsmYToPercent,
    hsmXToPixel,
    hsmYToPixel,
    lineHasWrapOpportunity,
    measureTextLines,
    normalizeEntry,
    parseUnityRichText,
    renderGameMock,
    sortEntries,
    validateEntries,
    viewportWidthForAspect,
    xToPercent,
    yToPercent
  };
});
