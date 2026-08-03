(function attachNativeBackgrounds(root, factory) {
  const backgrounds = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = backgrounds;
  }
  root.HintDisplayPreviewBackgrounds = backgrounds;
})(typeof globalThis !== "undefined" ? globalThis : this, function createNativeBackgrounds() {
  const spectatorZones = [
    { id: "native.spectator-team-bars", x: 455, y: 3, width: 1006, height: 82 },
    { id: "native.spectator-count", x: 1763, y: 8, width: 146, height: 106 },
    { id: "native.spectator-menu", x: 1605, y: 874, width: 310, height: 206 }
  ];

  // Always-on bottom-left stat bars (HP/AHP/HS) and the top-right zone label, plus the transient
  // CASSIE subtitle band. Derived from the *-highlighted red overlays. Used to validate that GOC
  // infiltrator hints (left checklist, centred intro, low-centre held-item card) never sit on the
  // persistent native HUD. The CASSIE band is single-line height; multi-line CASSIE can grow taller.
  const statBarsZone = { id: "native.stat-bars", x: 6, y: 998, width: 208, height: 78 };
  const zoneLabelZone = { id: "native.zone-label", x: 1786, y: 12, width: 128, height: 30 };
  const cassieZone = { id: "native.cassie-subtitle", x: 6, y: 980, width: 1380, height: 44 };
  const announceZones = [statBarsZone, zoneLabelZone, cassieZone];
  // Inventory wheel (centre) and the equipped-items list (left), shown only while TAB is held.
  const inventoryZones = [
    { id: "native.inventory-wheel", x: 498, y: 114, width: 906, height: 852 },
    { id: "native.inventory-list", x: 0, y: 368, width: 214, height: 348 },
    statBarsZone,
    zoneLabelZone
  ];
  // Native role-spawn flash (transient ~3s at spawn): the "YOU ARE / <role>" title, the role
  // description, the F1 prompt, plus the spawn keybind icons and the wider spawn stat block. Measured
  // from spawn-flash-highlighted. Lets the harness reveal that a centred spawn intro coincides with
  // the native role flash for those first seconds.
  const spawnFlashZones = [
    { id: "native.role-you-are", x: 810, y: 266, width: 299, height: 109 },
    { id: "native.role-title", x: 103, y: 393, width: 1718, height: 141 },
    { id: "native.role-desc", x: 578, y: 573, width: 758, height: 90 },
    { id: "native.role-f1", x: 455, y: 779, width: 983, height: 88 },
    { id: "native.spawn-stat-block", x: 0, y: 970, width: 436, height: 109 },
    { id: "native.spawn-keybinds", x: 1522, y: 962, width: 383, height: 117 },
    { id: "native.zone-label", x: 1770, y: 46, width: 145, height: 126 }
  ];

  return [
    { id: "none", name: "None", path: null },
    { id: "waiting-for-players", name: "Waiting For Players", path: "assets/native-screenshots/waiting-for-players.png" },
    { id: "waiting-for-players-highlighted", name: "Waiting For Players Highlighted", path: "assets/native-screenshots/waiting-for-players-highlighted.png" },
    { id: "announcements-and-stat-bar", name: "Announcements And Stat Bar", path: "assets/native-screenshots/announcements-and-stat-bar.png", collisionZones: announceZones },
    { id: "announcements-and-stat-bar-highlighted", name: "Announcements And Stat Bar Highlighted", path: "assets/native-screenshots/announcements-and-stat-bar-highlighted.png", collisionZones: announceZones },
    { id: "inventory", name: "Inventory", path: "assets/native-screenshots/inventory.png", collisionZones: inventoryZones },
    { id: "inventory-highlighted", name: "Inventory Highlighted", path: "assets/native-screenshots/inventory-highlighted.png", collisionZones: inventoryZones },
    { id: "scp-106-minimap", name: "SCP-106 Minimap", path: "assets/native-screenshots/scp-106-minimap.png" },
    { id: "scp-106-minimap-highlighted", name: "SCP-106 Minimap Highlighted", path: "assets/native-screenshots/scp-106-minimap-highlighted.png" },
    { id: "scp-abilities", name: "SCP Abilities", path: "assets/native-screenshots/scp-abilities.png" },
    { id: "scp-abilities-highlighted", name: "SCP Abilities Highlighted", path: "assets/native-screenshots/scp-abilities-highlighted.png" },
    { id: "spawn-flash", name: "Spawn Flash", path: "assets/native-screenshots/spawn-flash.png", collisionZones: spawnFlashZones },
    { id: "spawn-flash-highlighted", name: "Spawn Flash Highlighted", path: "assets/native-screenshots/spawn-flash-highlighted.png", collisionZones: spawnFlashZones },
    { id: "spectator", name: "Spectator", path: "assets/native-screenshots/spectator.png", collisionZones: spectatorZones },
    { id: "spectator-highlighted", name: "Spectator Highlighted", path: "assets/native-screenshots/spectator-highlighted.png", collisionZones: spectatorZones }
  ];
});
