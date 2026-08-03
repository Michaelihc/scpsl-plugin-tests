# Hint Display UI Harness

## HSM RENDERING — READ BEFORE POSITIONING (in-game-verified gotchas)

These were all confirmed **in-game on 2026-06-29** with `reinforcements hsmcal on` at 1920×1080
(screenshots were 1999×1248 / 16:10, normalized onto the 1920×1080 virtual canvas SCP:SL **always**
composes its UI on — 4K/16:10 are scaled to it). Every pixel number below is in that 1920×1080 space.
They are encoded in `src/preview-core.js` (top banner + `HSM_X_CAL`/`HSM_Y_CAL`/`HSM_TEXT_SAFE_*` +
`validateEntries`). Read them before moving any hint — they cost a long, painful calibration saga.

1. **Y axis is 1:1.** HSM `Y=0` → top edge (px0), `Y=1080` → bottom edge (px1080). `HSM_Y_CAL`
   `{0→0, 1080→1080}` is correct — leave it.
2. **Center-X is linear & symmetric, ~0.556 px per HSM unit.** Measured `X=0`→px960 (exact screen
   centre), `X=−600`→px622, `X=+600`→px1290. So a **center-aligned** hint lands at
   `pixel ≈ 960 + 0.556·X` (this is exactly `HSM_X_CAL`).
3. **Edge-to-edge is reached ONLY via center alignment + a large X** (NOT left/right alignment).
   `X ≈ ±1745` hits the literal screen edges (px0 / px1920); `±1700` ≈ px25 / px1895 (just inside);
   `±1780` is off-screen and its label wraps back toward centre.
4. **THE BIG TRAP (cost the most time): the HSM text area is ASYMMETRIC and text WRAPS past its right
   edge.** The wrap-safe horizontal band is roughly **px76 (left) … px1620 (right)** — NOT centred,
   and the right edge sits **~300px INSIDE** the screen's right edge. Multi-character / multi-word text
   whose rendered box extends **past ~px1620 on the right gets TMP word-wrapped**, and the overflow
   fragment scatters elsewhere on the row (a right-edge "X=1700" label once dumped its "1700" on top of
   the centre "X=0" marker). The left side is more forgiving (text renders out to ~px76). A **single
   token** with no wrap point (e.g. a lone caret `▼`) renders past px1620 **without wrapping** — which
   is why edge **carets work but edge labels don't**. Encoded as `HSM_TEXT_SAFE_LEFT_PX = 76` /
   `HSM_TEXT_SAFE_RIGHT_PX = 1620`; `validateEntries` warns on center-aligned text whose estimated box
   leaves the band.
5. **`<nobr>` does NOT reliably stop the wrap** (tested in-game — HSM's parser / TMP still wrapped). Do
   not rely on it. The reliable fix is to keep wrappable text inside ~px76…1620, **or** make outer/edge
   markers caret-only (a single token with no wrap point).
6. **HSM Left/Right alignment CLAMPS** the text edge at the hint-area boundary (~px1620 right) and
   **ignores `XCoordinate`** once pushed past it — it **cannot** reach the true right screen edge. (A
   HSM-fork attempt to render alignment via per-line `<pos>` was reverted: it caused exactly the wrap in
   #4. The hint-area width is client-side and unchangeable from HSM.) For edge HUDs use **center + the
   right X** (#3), never left/right alignment.
7. **`HintVerticalAnchor.Bottom` renders OFF-SCREEN in-game** — never bottom-anchor; use Middle or Top
   only (`validateEntries` warns on Bottom).
8. **The reinforcements `HsmHintDisplayProvider` forces Center + DefaultX** unless you call its
   `ShowPrompt(...alignment, anchor)` overload. Its real value is **exposing X**: use center alignment
   + a large X for edge positioning.

**Validation the harness now enforces (deliverable B):** for every HSM entry, `validateEntries`
estimates the rendered text box (center-X pixel scale × estimated text width, size/`<size>`/`<mspace>`
aware) and **warns when a center-aligned, wrappable line's box leaves px76…1620** —
`"… text box ~pxL..R overflows the HSM wrap-safe band (px76..1620) on the {side}; it will
WORD-WRAP/garble in-game — keep it inside the band, or use a single-token (caret-only) marker."`
Single-token (caret-only) markers are exempt (#4). Left/Right alignment at a far edge gets a distinct
warning that it **clamps** at the hint-area edge and to use **center + X** instead. The Bottom-anchor
warning (#7) is unchanged.

---

Open `index.html` in a browser to preview HintServiceMeow output without launching SCP:SL.

Install local Node dependencies before running the harness:

```powershell
cd .\.tests\UI
npm install
npx playwright install
```

`node_modules` and generated `output` captures are local artifacts and should not be committed.

The same renderer is usable from Node for automated checks:

```powershell
node render-model.js --fixture "HSM Calibration Rectangle" --viewport 1920x1080
```

That command emits JSON with renderer metrics, normalized entries, CSS-like positions, parsed HTML, unsupported tags, and validation messages. Tests can also import `src/preview-core.js` directly and call `buildPreviewModel(entries, { width, height })`.

To render entries/tags directly to an image:

```powershell
node render-image.js --input tags.json --output output/playwright/tags.png --viewport 1920x1080
```

Native SCP:SL screenshots can be used as render backgrounds:

```powershell
node render-image.js --fixture "Localized HUD Stress" --background waiting-for-players --output output/playwright/localized-on-native.png --viewport 1920x1080
```

Bundled backgrounds live in `assets/native-screenshots` and are registered by `backgrounds.js`. Use `none`, a registered id such as `inventory-highlighted`, or a direct image path. Registered backgrounds may also define invisible native collision zones; `spectator-highlighted`, for example, reserves the top reinforcement bars, spectator count, and spectator controls so hint overlap fails the harness.

For SCP:SL hint layout work, prefer these real native screenshot backgrounds over drawing a mock lobby or approximate native UI. Start with the highlighted scenario that matches the user report, such as `waiting-for-players-highlighted`, `announcements-and-stat-bar-highlighted`, `inventory-highlighted`, `scp-106-minimap-highlighted`, `scp-abilities-highlighted`, `spawn-flash-highlighted`, or `spectator-highlighted`. Use the highlighted red zones as the visual source of truth for native collision risk, add explicit `collisionZones` in `backgrounds.js` when a native UI area must be protected, then confirm with live/manual verification when the change is deployed.

`tags.json` can be either an array or an object with an `entries` array:

```json
[
  {
    "id": "center",
      "owner": "example",
      "system": "hsm",
      "text": "<color=#FF3B30>Center</color>",
      "x": 0,
      "y": 540,
      "alignment": "center",
      "verticalAlign": "middle",
      "textSize": 28,
      "priority": 10
  }
]
```

For pipeline use, pass stdin with `--input -`:

```powershell
Get-Content tags.json | node render-image.js --input - --output output/playwright/tags.png
```

The harness models the HintServiceMeow coordinate system used by the HSM calibration plugin:

- `system: "hsm"` uses `X = 0` at center.
- HSM centered `X` follows the observed 1080p-style display domain. On 1920x1080, the visible domain is approximately `-1920..1920`; 4K should visually match 1080p because SCP:SL scales the UI.
- `Y = 0` is top.
- `Y = 1080` is bottom.
- The current HSM fixture mirrors the live calibration defaults: `9x5` edge tags, `leftX = -1780`, `rightX = 1780`, `edgeInsetX = 96`, `edgeInsetY = 34`, `textSize = 18`, and coordinates hidden.
- HSM `alignment: "left"` and `"right"` are composition alignment modes, not simple text-origin anchors. They position text inside HSM's centered 1200-wide TMP text area. For compact top-left labels, use `alignment: "left"` with an offset inside that area, such as `x = -290` on 1920x1080. For a compact right-side HUD lane, `alignment: "right"` with a small positive offset (e.g. `x = 360`) hugs the hint-area's right boundary. **In-game caveat (gotcha #6 above): that boundary is ~px1620, NOT the screen edge (px1920) — left/right alignment CLAMPS there and cannot reach the true screen edge.** For an actual screen-edge HUD use **center alignment + a large X** (gotcha #3), not `right`.
- Static validation warns when an HSM entry combines far-edge `x` values with `left` or `right` alignment (clamp warning), and when a **center-aligned** entry's estimated text box leaves the px76…1620 wrap-safe band (wrap warning, gotcha #4).

The browser view intentionally does not draw the SCP:SL waiting-for-players lobby by default. It keeps the visual surface close to the hint layer so screenshots and automation are not polluted by base game UI. The optional chrome toggle only adds a tiny FPS/mute reference.

The HTML preview is not a Unity TextMeshPro renderer, so exact glyph metrics will still need a final in-game check. The coordinate model, fixtures, and validation pass are meant to catch layout and overlap issues before deploying a calibration plugin.

Run the static harness checks with:

```powershell
node smoke-test.js
```

## Recalibrating the HSM coordinate transform from a real screenshot

The HSM coordinate-to-pixel mapping in this harness is an APPROXIMATION; a layout can pass here and
still be wrong in-game (the Serpent's Hand HUD case; and `HintVerticalAnchor.Bottom` rendering
off-screen). Both axes are now a single LINEAR FIT through two measured reference points, in one place:
`src/preview-core.js` -> the `HSM COORDINATE CALIBRATION` block (`HSM_Y_CAL` and `HSM_X_CAL`).

To lock the numbers to reality:

1. In-game, as an admin/RA: `reinforcements hsmcal on` (from `reinforcements-system`). It draws labeled
   HSM Y rulers (`Y = 0 .. 1080`) and a row of X carets (`X = -600 .. 600`) through the real
   `IHintDisplayProvider`/HSM path. Screenshot at 1920x1080, then `reinforcements hsmcal off`.
2. Y axis: pick two rulers (e.g. `Y = 100`, `Y = 900`), read the pixel ROW each line actually lands on
   (0=top .. 1080=bottom), and put those `{ hsm, px }` pairs in `HSM_Y_CAL.a` / `.b`.
3. X axis: pick two carets (e.g. `X = -600`, `X = 600`), read the pixel COLUMN under each caret
   (0=left .. 1920=right), and put those `{ hsm, px }` pairs in `HSM_X_CAL.a` / `.b`.

That four-pair edit is the whole recalibration; everything else (percent conversion, alignment,
font scaling) derives from it. The shipped defaults reproduce the prior approximation exactly
(`Y: 0->0, 1080->1080`; `X: -1920->0, 1920->1920` at 16:9), so behavior is unchanged until measured.

Only CENTER-aligned hints use `HSM_X_CAL` (that is the path gameplay features and the `hsmcal` grid
use); `left`/`right` alignment keeps HSM's separate 1200-wide alignment-area math, where `X = 0` is the
text-area edge (~px360 at 1080p), not the screen edge.
