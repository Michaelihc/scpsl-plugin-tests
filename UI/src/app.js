(function boot() {
  const core = window.HintDisplayPreviewCore;
  const fixtures = window.HintDisplayPreviewFixtures;
  const backgrounds = window.HintDisplayPreviewBackgrounds || [{ id: "none", name: "None", path: null }];
  const fixtureSelect = document.getElementById("fixtureSelect");
  const backgroundSelect = document.getElementById("backgroundSelect");
  const viewportSelect = document.getElementById("viewportSelect");
  const zoomInput = document.getElementById("zoomInput");
  const gameUiToggle = document.getElementById("gameUiToggle");
  const stage = document.getElementById("stage");
  const nativeBackground = document.getElementById("nativeBackground");
  const backgroundCollisionLayer = document.getElementById("backgroundCollisionLayer");
  const gameMock = document.getElementById("gameMock");
  const hintLayer = document.getElementById("hintLayer");
  const validationList = document.getElementById("validationList");
  const frameList = document.getElementById("frameList");
  const viewportLabel = document.getElementById("viewportLabel");
  const fixtureSummary = document.getElementById("fixtureSummary");
  const metricsList = document.getElementById("metricsList");
  const statusPill = document.getElementById("statusPill");
  const copyButton = document.getElementById("copyButton");
  const resetButton = document.getElementById("resetButton");
  const customToggle = document.getElementById("customToggle");
  const customText = document.getElementById("customText");
  const xInput = document.getElementById("xInput");
  const yInput = document.getElementById("yInput");
  const sizeInput = document.getElementById("sizeInput");
  const alignSelect = document.getElementById("alignSelect");

  for (const [index, fixture] of fixtures.entries()) {
    const option = document.createElement("option");
    option.value = String(index);
    option.textContent = fixture.name;
    fixtureSelect.appendChild(option);
  }

  for (const background of backgrounds) {
    const option = document.createElement("option");
    option.value = background.id;
    option.textContent = background.name;
    backgroundSelect.appendChild(option);
  }

  function parseViewport(value) {
    const [width, height] = value.split("x").map(Number);
    return { width, height };
  }

  function fixtureSystem(fixture) {
    const first = fixture && fixture.entries && fixture.entries[0];
    return first ? core.normalizeEntry(first).system : "hsm";
  }

  function customEntry(fixture) {
    const system = fixtureSystem(fixture);
    return {
      id: "adhoc.custom",
      owner: "preview",
      system,
      text: customText.value,
      x: Number(xInput.value),
      y: Number(yInput.value),
      alignment: alignSelect.value,
      verticalAlign: system === "hsm" ? "middle" : "middle",
      textSize: Number(sizeInput.value),
      priority: 2000
    };
  }

  function currentEntries() {
    const fixture = fixtures[Number(fixtureSelect.value)] || fixtures[0];
    return customToggle.checked ? [...fixture.entries, customEntry(fixture)] : [...fixture.entries];
  }

  function currentBackground() {
    return backgrounds.find((background) => background.id === backgroundSelect.value) || backgrounds[0];
  }

  function render() {
    const fixture = fixtures[Number(fixtureSelect.value)] || fixtures[0];
    const background = currentBackground();
    const viewport = parseViewport(viewportSelect.value);
    const zoom = Number(zoomInput.value) / 100;
    const entries = currentEntries();
    const model = core.buildPreviewModel(entries, viewport);
    const aspect = model.viewport.aspectRatio;

    stage.style.setProperty("--preview-width", String(viewport.width));
    stage.style.setProperty("--preview-height", String(viewport.height));
    stage.style.setProperty("--zoom", String(zoom));

    viewportLabel.textContent = `${viewport.width} x ${viewport.height} (${aspect.toFixed(3)}:1)`;
    fixtureSummary.textContent = fixture.summary || "";
    statusPill.textContent = `${currentEntries().length} entries`;

    if (background && background.path) {
      nativeBackground.src = background.path;
      nativeBackground.style.display = "block";
    } else {
      nativeBackground.removeAttribute("src");
      nativeBackground.style.display = "none";
    }

    gameMock.style.display = gameUiToggle.checked ? "block" : "none";
    core.renderGameMock(gameMock);
    renderBackgroundCollisionZones(background, viewport);
    hintLayer.innerHTML = "";

    for (const entry of model.entries) {
      hintLayer.appendChild(renderEntry(entry));
    }

    renderMetrics(model);
    renderValidation(model.validation);
    renderFrameList(entries);
    requestAnimationFrame(checkLayout);
  }

  function renderEntry(entry) {
    const element = document.createElement("div");
    element.className = "hintEntry";
    element.dataset.id = entry.id;
    element.dataset.align = entry.alignment;
    element.dataset.system = entry.system;
    element.dataset.vertical = entry.verticalAlign;
    element.style.left = `${entry.leftPercent}%`;
    element.style.top = `${entry.topPercent}%`;
    element.style.fontSize = `${entry.fontSizePx}px`;
    element.style.color = entry.color || "inherit";
    element.style.transform = entry.transform;
    element.innerHTML = entry.html;
    return element;
  }

  function renderBackgroundCollisionZones(background, viewport) {
    backgroundCollisionLayer.innerHTML = "";
    for (const zone of core.backgroundCollisionZonesForViewport(background, viewport)) {
      const element = document.createElement("div");
      element.className = "backgroundCollisionZone";
      element.dataset.collisionZone = zone.id;
      element.style.left = `${zone.leftPercent}%`;
      element.style.top = `${zone.topPercent}%`;
      element.style.width = `${zone.widthPercent}%`;
      element.style.height = `${zone.heightPercent}%`;
      backgroundCollisionLayer.appendChild(element);
    }
  }

  function renderMetrics(model) {
    const rows = [
      ["system", model.metrics.systems.join(", ") || "none"],
      ["aspect", model.viewport.aspectRatio.toFixed(4)],
      ["hsm x/y", `${model.metrics.hsmXDomain[0].toFixed(0)}..${model.metrics.hsmXDomain[1].toFixed(0)} / 0..1080`],
      ["safe inset", "96 x 34"],
      ["calibration", "9 x 5 tags"]
    ];

    metricsList.innerHTML = rows
      .map(([key, value]) => `<dt>${key}</dt><dd>${value}</dd>`)
      .join("");
  }

  function renderValidation(validation) {
    validationList.innerHTML = "";
    for (const issue of validation) {
      addValidation(issue.severity, issue.message);
    }
  }

  function addValidation(severity, message) {
    const item = document.createElement("li");
    item.className = severity === "ok" ? "" : severity;
    item.textContent = message;
    validationList.appendChild(item);
  }

  function renderFrameList(entries) {
    frameList.textContent = core.sortEntries(entries)
      .map((entry) => `${entry.system}:${entry.owner}:${entry.id}  x=${entry.x} y=${entry.y} align=${entry.alignment} v=${entry.verticalAlign} size=${entry.textSize} priority=${entry.priority}\n${entry.text}`)
      .join("\n\n");
  }

  function checkLayout() {
    const bounds = stage.getBoundingClientRect();
    const hintBoxes = Array.from(hintLayer.querySelectorAll(".hintEntry")).map((entry) => {
      const rect = entry.getBoundingClientRect();
      const overflows = rect.left < bounds.left - 1 ||
        rect.right > bounds.right + 1 ||
        rect.top < bounds.top - 1 ||
        rect.bottom > bounds.bottom + 1;

      if (overflows) {
        if (!entry.dataset.id.startsWith("border.")) {
          addValidation("warn", `${entry.dataset.id} visually overflows the viewport.`);
        }
      }

      return { id: entry.dataset.id, type: "hint", rect };
    });
    const nativeBoxes = gameUiToggle.checked
      ? Array.from(gameMock.querySelectorAll("[data-collision-zone]")).map((element) => ({
        id: element.dataset.collisionZone,
        type: "native",
        rect: element.getBoundingClientRect()
      }))
      : [];
    const backgroundBoxes = Array.from(backgroundCollisionLayer.querySelectorAll("[data-collision-zone]")).map((element) => ({
      id: element.dataset.collisionZone,
      type: "native",
      rect: element.getBoundingClientRect()
    }));
    const boxes = [...hintBoxes, ...nativeBoxes, ...backgroundBoxes];

    for (let i = 0; i < boxes.length; i++) {
      for (let j = i + 1; j < boxes.length; j++) {
        const first = boxes[i];
        const second = boxes[j];
        if (first.type === "native" && second.type === "native") {
          continue;
        }

        const overlap = overlapSize(first.rect, second.rect);
        if (overlap.width > 2 && overlap.height > 2) {
          addValidation("error", `${first.id} overlaps ${second.id} (${Math.round(overlap.width)}x${Math.round(overlap.height)}px).`);
        }
      }
    }
  }

  function overlapSize(first, second) {
    const width = Math.min(first.right, second.right) - Math.max(first.left, second.left);
    const height = Math.min(first.bottom, second.bottom) - Math.max(first.top, second.top);
    return {
      width: Math.max(0, width),
      height: Math.max(0, height)
    };
  }

  function copyFixture() {
    navigator.clipboard.writeText(JSON.stringify(currentEntries(), null, 2))
      .then(() => {
        statusPill.textContent = "Copied";
        setTimeout(render, 700);
      })
      .catch(() => addValidation("warn", "Clipboard copy failed."));
  }

  function resetCustom() {
    customText.value = "<color=#FF3B30>Custom x0 y500</color>";
    xInput.value = "0";
    yInput.value = "500";
    sizeInput.value = "28";
    alignSelect.value = "center";
    render();
  }

  fixtureSelect.addEventListener("change", render);
  backgroundSelect.addEventListener("change", render);
  viewportSelect.addEventListener("change", render);
  zoomInput.addEventListener("input", render);
  gameUiToggle.addEventListener("change", render);
  copyButton.addEventListener("click", copyFixture);
  resetButton.addEventListener("click", resetCustom);

  for (const input of [customToggle, customText, xInput, yInput, sizeInput, alignSelect]) {
    input.addEventListener("input", render);
    input.addEventListener("change", render);
  }

  render();
})();
