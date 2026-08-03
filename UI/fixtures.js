(function attachFixtures(root, factory) {
  const fixtures = factory(root.HintDisplayPreviewCore);
  if (typeof module === "object" && module.exports) {
    module.exports = fixtures;
  }
  root.HintDisplayPreviewFixtures = fixtures;
})(typeof globalThis !== "undefined" ? globalThis : this, function createFixtures(core) {
  const buildHsmCalibrationEntries = core
    ? core.buildHsmCalibrationEntries
    : () => [];
  const toHsmY = (y) => Math.max(0, Math.min(1080, Number(y) || 0));
  const qfont = (text) => text;

  // ---- WarmupScpSelector Aim Range HUD --------------------------------------------------------
  // Faithful JS mirrors of the C# builders (src/WarmupScpSelector/Text/{AimRangeText,WarmupText}.cs) so these
  // fixtures render byte-identical HSM markup to what the plugin pushes. The whole in-range HUD renders on one
  // configurable left-lane HSM X (AimRangeActivityConfig.HudX, default -1077) so its boxes land in the narrow
  // ~px216..496 corridor between the native inventory list (x0..214) and the inventory wheel (x498..1404) at
  // 1920x1080 — clear of both while TAB is held. Compact zones: force-shown flash (Y592), a persistent three-line
  // hero (Y700: tiny weapon/target/bot eyebrow, short signature state rail, compact hits/accuracy strip), a short
  // footer (Y805), and the collapsed status strip (Y900) that replaces the full SCP draft panel (Y840) while a
  // player is inside the range. Color obeys the plan HUD law (teal = you/live, white = values, dim/line = not-yet
  // track). Bands + lane X are configurable in the plugin; these defaults are what the tests/screenshots verify
  // are collision-free against inventory-highlighted at 1920x1080.
  const WS = (function buildWarmupScpHud() {
    const Accent = "#4FCBFF", Gold = "#FFD24D", Urgent = "#FF5555", Ready = "#5BFF80",
      White = "#E7ECF3", Muted = "#8B95A6", Dim = "#5B6270", DividerColor = "#232A37";
    const RAIL_CELLS = 5;
    const col = (hex, t) => "<color=" + hex + ">" + t + "</color>";
    const sep = () => col(Dim, " · ");

    function weaponLabel(id) {
      if (!id) return "";
      id = String(id).replace(/[<>]/g, "").trim();
      if (/^bot-/i.test(id)) id = id.slice(4);
      const map = {
        com15: "COM-15", com18: "COM-18", com45: "COM-45", fsp9: "FSP-9",
        crossvec: "Crossvec", ak: "AK", e11sr: "E11-SR", logicer: "Logicer",
        revolver: "Revolver", shotgun: "Shotgun"
      };
      return map[id.toLowerCase()] || id.toUpperCase();
    }

    function targetText(t, cn) {
      return t === "static" ? (cn ? "静态靶" : "Static")
        : t === "moving" ? (cn ? "移动靶" : "Moving")
          : (cn ? "无靶" : "No target");
    }

    function botText(p, cn) {
      switch (p) {
        case "passive": return [Muted, cn ? "机器人待机" : "Bots idle"];
        case "retaliating": return [Urgent, cn ? "机器人还击" : "Bots firing"];
        case "respawning": return [Muted, cn ? "机器人重生" : "Bot respawning"];
        default: return [Muted, ""];
      }
    }

    function eyebrow(s, cn) {
      let out = "";
      if (s.hasWeapon) {
        const label = weaponLabel(s.preset) || (cn ? "武器" : "Gun");
        out += "<b>" + col(White, label) + "</b>";
      } else {
        out += col(Dim, cn ? "未持枪" : "No gun");
      }
      out += sep() + col(Muted, targetText(s.target, cn));
      const bot = botText(s.bots, cn);
      if (bot[1].length) out += sep() + col(bot[0], bot[1]);
      return out;
    }

    // The signature state rail (the Ghost Rail read): a teal accuracy-filled span + teal "you" marker, then the
    // dim/Line "not-yet" remainder. Total glyphs = RAIL_CELLS + 1 (one marker). It is line 2 of the hero.
    function buildRail(s) {
      const hits = s.targetHits + s.botHits;
      let filled = s.shots > 0 ? Math.round(hits / s.shots * RAIL_CELLS) : 0;
      filled = Math.max(0, Math.min(RAIL_CELLS, filled));
      return (filled > 0 ? col(Accent, "━".repeat(filled)) : "") + col(Accent, "◆")
        + (filled < RAIL_CELLS ? col(DividerColor, "━".repeat(RAIL_CELLS - filled)) : "");
    }

    // Line 3 of the hero: value-first session hits + accuracy (or the TAKE A GUN call when unarmed).
    function statLine(s, cn) {
      if (!s.hasWeapon) return "<b>" + col(Accent, cn ? "去取一把枪" : "TAKE A GUN") + "</b>";
      const hits = s.targetHits + s.botHits;
      let out = "<b>" + col(White, String(hits)) + "</b>" + col(Muted, cn ? " 命中" : " HITS");
      if (s.shots > 0) {
        const acc = Math.max(0, Math.min(100, Math.round(hits / s.shots * 100)));
        const av = "<b>" + col(White, acc + "%") + "</b>";
        out += col(Dim, " · ") + av + col(Muted, cn ? " 命中率" : " ACC");
      }
      return out;
    }

    // Three sibling size spans (never nested) sized for the narrow left lane: eyebrow, rail, stat.
    function hero(s, cn) {
      return "<align=center><size=58%>" + eyebrow(s, cn) + "</size>\n"
        + "<size=105%>" + buildRail(s) + "</size>\n"
        + "<size=66%>" + statLine(s, cn) + "</size></align>";
    }

    function footer(s, cn) {
      let t;
      if (!s.hasWeapon) t = cn ? "去武器架取一把枪" : "Grab a gun off the rack";
      else if (s.bots === "retaliating" || (s.incoming > 0 && s.bots !== "none")) t = cn ? "切断视线躲子弹" : "Break line of sight";
      else if (s.target !== "none") t = cn ? "击中亮起的靶子" : "Hit the lit target";
      else t = cn ? "射击机器人激怒它" : "Shoot a bot to engage it";
      return "<align=center><size=80%>" + col(Muted, t) + "</size></align>";
    }

    function flash(kind, cn) {
      let c, t;
      switch (kind) {
        case "targethit": c = Ready; t = cn ? "命中！" : "HIT!"; break;
        case "botprovoked": c = Accent; t = cn ? "机器人被激怒" : "BOT ENGAGED"; break;
        case "incoming": c = Urgent; t = cn ? "你被击中！" : "INCOMING!"; break;
        case "botdown": c = Ready; t = cn ? "机器人倒下" : "BOT DOWN"; break;
        case "botback": c = Accent; t = cn ? "机器人重生" : "BOT BACK"; break;
        case "reset": c = Accent; t = cn ? "靶场重置" : "RANGE RESET"; break;
        case "handoff": c = Ready; t = cn ? "回合开始" : "ROUND START"; break;
        default: return "";
      }
      return "<align=center><size=120%><b>" + col(c, t) + "</b></size></align>";
    }

    // Terse countdown for the collapsed strip (no leading glyph / verb prefix) so it fits the narrow lane.
    function compactCountdown(timer, cn) {
      if (timer === -2) return col(Muted, cn ? "等待" : "Wait");
      if (timer <= 0) return "<b>" + col(Ready, cn ? "开始" : "Start") + "</b>";
      const c = timer <= 5 ? Urgent : Gold;
      const sec = "<b>" + col(c, String(timer)) + "</b>";
      return sec + col(Muted, cn ? "秒" : "s");
    }

    // Wrapped in one outer size span (never nested) so the strip fits the narrow lane at the widest role name.
    function collapsed(timer, roleName, count, cn) {
      let out = "<align=center><size=88%>" + col(Muted, cn ? "已选" : "PICK") + " ";
      if (roleName) {
        out += "<b>" + col(Accent, roleName) + "</b>";
        if (count > 0) out += col(Dim, " ·" + count);
      } else {
        out += "<i>" + col(Dim, cn ? "尚未选择" : "none yet") + "</i>";
      }
      out += col(Dim, " | ") + compactCountdown(timer, cn) + "</size></align>";
      return out;
    }

    function fullDraft(timer, players, max, cn) {
      const grad = ["#6BFF6B", "#66F684", "#62EE9C", "#5DE5B5", "#58DCCE", "#54D4E6", "#4FCBFF"];
      const codes = ["049", "079", "096", "106", "173", "939", "3114"];
      const selIdx = 2, selName = "SCP-096", selCount = 3;
      const title = col(Accent, "SCP") + col(Gold, cn ? " 选择" : " SELECTION");
      let cd;
      if (timer === -2) cd = col(Muted, cn ? "● 等待玩家中" : "● Waiting for players");
      else if (timer <= 0) cd = "<b>" + col(Ready, cn ? "▶ 即将开始" : "▶ Starting") + "</b>";
      else {
        const c = timer <= 5 ? Urgent : Gold;
        const s = "<b>" + col(c, String(timer)) + "</b>";
        cd = cn ? col(Muted, "倒计时 ") + s + col(Muted, "秒") : col(Muted, "Starts in ") + s + col(Muted, "s");
      }
      const pl = col(Muted, cn ? "玩家 " : "Players ") + "<b>" + col(White, String(players)) + "</b>" + col(Dim, " / " + max);
      const divider = col(DividerColor, "———————————————");
      let sel = col(Muted, cn ? "已选择" : "SELECTED") + "　" + "<b>" + col(Accent, selName) + "</b>";
      sel += col(Dim, "　·　") + "<b>" + col(White, String(selCount)) + "</b>" + col(Muted, cn ? " 人" : (selCount === 1 ? " pick" : " picks"));
      let chips = "<size=92%>";
      for (let i = 0; i < codes.length; i++) {
        if (i > 0) chips += "  ";
        if (i === selIdx) chips += "<b>" + col(grad[i], "[") + col(White, codes[i]) + col(grad[i], "]") + "</b>";
        else chips += col(Dim, codes[i]);
        if (i === selIdx && selCount > 0) chips += "<size=70%>" + col(Muted, "·" + selCount) + "</size>";
      }
      chips += "</size>";
      const instr = "<size=80%>" + col(Muted, cn ? "抓取硬币以选择或更改" : "Grab a coin to pick or change") + "</size>";
      return "<align=center><size=165%><b>" + title + "</b></size>\n<size=70%> </size>\n"
        + cd + "   " + col(Dim, "|") + "   " + pl + "\n" + divider + "\n" + sel + "\n" + chips
        + "\n<size=70%> </size>\n" + instr + "</align>";
    }

    return { hero, footer, flash, collapsed, fullDraft };
  })();

  const AIM_FLASH_Y = 592, AIM_HERO_Y = 700, AIM_FOOTER_Y = 805, AIM_STATUS_Y = 900, DRAFT_Y = 840;
  // The in-range HUD lane's HSM center-X (AimRangeActivityConfig.HudX). -1077 lands every in-range box in the
  // narrow ~px216..496 corridor between the native inventory list and wheel at 1920x1080. The full draft panel
  // shown OUTSIDE the range keeps the centered default (x 0).
  const AIM_HUD_X = -1077;

  function wsEntry(id, y, text, priority, x) {
    return { id, owner: "WarmupScpSelector", system: "hsm", text, x: x || 0, y, alignment: "center", verticalAlign: "middle", textSize: 20, priority: priority || 10 };
  }

  function aimEntries(lang, view, collapsedTimer, flashKind, roleName, pickCount) {
    const cn = lang === "cn";
    const entries = [
      wsEntry("warmupscp.aim.hero", AIM_HERO_Y, WS.hero(view, cn), 40, AIM_HUD_X),
      wsEntry("warmupscp.aim.footer", AIM_FOOTER_Y, WS.footer(view, cn), 30, AIM_HUD_X),
      wsEntry("warmupscp.status", AIM_STATUS_Y, WS.collapsed(collapsedTimer, roleName, pickCount, cn), 20, AIM_HUD_X)
    ];
    if (flashKind) entries.unshift(wsEntry("warmupscp.aim.flash", AIM_FLASH_Y, WS.flash(flashKind, cn), 50, AIM_HUD_X));
    return entries;
  }

  const AIM_STATES = [
    { key: "no-weapon", label: "no weapon", view: { hasWeapon: false, preset: "", target: "none", bots: "passive", shots: 0, targetHits: 0, botHits: 0, incoming: 0 }, timer: 12, flash: null },
    { key: "static-target", label: "static target + HIT", view: { hasWeapon: true, preset: "fsp9", target: "static", bots: "passive", shots: 24, targetHits: 6, botHits: 0, incoming: 0 }, timer: 12, flash: "targethit" },
    { key: "moving-target", label: "moving target", view: { hasWeapon: true, preset: "crossvec", target: "moving", bots: "passive", shots: 31, targetHits: 9, botHits: 0, incoming: 0 }, timer: 12, flash: null },
    { key: "bot-passive", label: "bot passive", view: { hasWeapon: true, preset: "ak", target: "none", bots: "passive", shots: 12, targetHits: 3, botHits: 1, incoming: 0 }, timer: 12, flash: null },
    { key: "bot-retaliating", label: "bot retaliating", view: { hasWeapon: true, preset: "ak", target: "none", bots: "retaliating", shots: 20, targetHits: 3, botHits: 5, incoming: 1 }, timer: 12, flash: "botprovoked" },
    { key: "bot-respawning", label: "bot respawning + BOT DOWN", view: { hasWeapon: true, preset: "ak", target: "none", bots: "respawning", shots: 26, targetHits: 4, botHits: 8, incoming: 1 }, timer: 12, flash: "botdown" },
    { key: "incoming-hit", label: "incoming hit", view: { hasWeapon: true, preset: "ak", target: "none", bots: "retaliating", shots: 22, targetHits: 3, botHits: 6, incoming: 3 }, timer: 12, flash: "incoming" },
    { key: "reset", label: "range reset", view: { hasWeapon: true, preset: "ak", target: "none", bots: "passive", shots: 22, targetHits: 3, botHits: 6, incoming: 3 }, timer: 12, flash: "reset" },
    { key: "final-countdown", label: "final countdown", view: { hasWeapon: true, preset: "fsp9", target: "static", bots: "passive", shots: 40, targetHits: 12, botHits: 2, incoming: 0 }, timer: 3, flash: null },
    { key: "handoff", label: "round handoff", view: { hasWeapon: true, preset: "fsp9", target: "none", bots: "passive", shots: 40, targetHits: 12, botHits: 2, incoming: 0 }, timer: 0, flash: "handoff" }
  ];

  function buildWarmupScpFixtures() {
    const fixtures = [];
    for (const lang of ["en", "cn"]) {
      const tag = lang.toUpperCase();
      fixtures.push({
        name: `WarmupScp Draft ${tag} (outside)`,
        summary: `Full SCP draft panel at Y${DRAFT_Y} — shown outside any activity (${tag}).`,
        entries: [wsEntry("warmupscp.status", DRAFT_Y, WS.fullDraft(12, 7, 50, lang === "cn"), 10)]
      });
      for (const state of AIM_STATES) {
        fixtures.push({
          name: `WarmupScp Aim ${tag} · ${state.label}`,
          summary: `Range HUD on the left lane x${AIM_HUD_X} (flash Y${AIM_FLASH_Y} / hero Y${AIM_HERO_Y} / footer Y${AIM_FOOTER_Y}) + collapsed status Y${AIM_STATUS_Y} — ${state.label} (${tag}).`,
          entries: aimEntries(lang, state.view, state.timer, state.flash, "SCP-096", 3)
        });
      }
    }
    return fixtures;
  }

  const baseFixtures = [
    {
      name: "Starter QoL HSM",
      summary: "Calibrated defaults for CustomizableUIMeow, KillInfoPlugin, and SB_WelcomeMessage.",
      entries: [
        {
          id: "qol.welcome",
          owner: "SB_WelcomeMessage",
          system: "hsm",
          text: qfont("<size=30><color=#CBD5E1>欢迎来到</color><size=38><b><color=#FDE68A>莺</color><color=#CFE2A0>歌</color><color=#9BE5BB>傲</color><color=#38BDF8>然</color></b></size><color=#64748B>，</color><color=#93C5FD><b>Dummy</b></color></size>\\n<size=25><color=#CBD5E1>祝你玩得愉快</color></size>"),
          x: 0,
          y: toHsmY(180),
          alignment: "center",
          verticalAlign: "middle",
          textSize: 34,
          lineHeight: 8,
          priority: 50
        },
        {
          id: "qol.teammates",
          owner: "CustomizableUIMeow",
          system: "hsm",
          text: qfont("<size=18><color=#6EE7B7>队友</color> <color=#A7F3D0>4</color>\\n<mspace=0.62em><color=#94A3B8>1.</color> <color=#FDE68A>SCI</color> <color=#FDE68A>100HP</color> </mspace><color=#A7F3D0>[Lv2]</color> River\\n<mspace=0.62em><color=#94A3B8>2.</color> <color=#93C5FD>FG </color> <color=#FDE68A>100HP</color> </mspace>Nova\\n<mspace=0.62em><color=#94A3B8>3.</color> <color=#93C5FD>NTF</color> <color=#FDE68A> 87HP</color> </mspace><color=#C4B5FD>SCSC</color>\\n<mspace=0.62em><color=#94A3B8>4.</color> <color=#22C55E>CI </color> <color=#FDE68A> 73HP</color> </mspace><color=#6EE7B7>Echo</color></size>"),
          x: 360,
          y: toHsmY(110),
          alignment: "right",
          verticalAlign: "middle",
          textSize: 32,
          lineHeight: 0,
          priority: 40
        },
        {
          id: "qol.notice",
          owner: "CustomizableUIMeow",
          system: "hsm",
          text: qfont("<b><color=#38BDF8>提示</color></b> <color=#E2E8F0>设置已保存</color>"),
          x: 0,
          y: toHsmY(650),
          alignment: "center",
          verticalAlign: "middle",
          textSize: 34,
          priority: 30
        },
        {
          id: "qol.kill",
          owner: "KillInfoPlugin",
          system: "hsm",
          text: qfont("<b><color=#FDE68A>击杀</color></b> <color=#FCA5A5>SCP-939</color> <color=#CBD5E1>+20 XP</color>"),
          x: 0,
          y: toHsmY(778),
          alignment: "center",
          verticalAlign: "middle",
          textSize: 34,
          priority: 20
        },
        {
          id: "qol.hud",
          owner: "CustomizableUIMeow",
          system: "hsm",
          text: qfont("<size=18><color=#FDE68A>等级</color> <color=#FDE68A>4</color> <color=#64748B>·</color> <color=#38BDF8>经验</color> <color=#38BDF8>[■■■■■■□□□□]</color> 62/100 <color=#64748B>·</color> <color=#FDE68A>MVP</color> <color=#FDE68A>5</color> <color=#64748B>·</color> <color=#FB7185>K:</color><color=#FCA5A5>48</color> <color=#FB7185>D:</color><color=#FCA5A5>7</color> <color=#64748B>·</color> <color=#CBD5E1>总时</color> <color=#CBD5E1>1h 31m</color> <color=#64748B>·</color> <color=#CBD5E1>今日</color> <color=#CBD5E1>38m</color></size>"),
          x: 0,
          y: toHsmY(1048),
          alignment: "center",
          verticalAlign: "middle",
          textSize: 32,
          lineHeight: 0,
          priority: 10
        }
      ]
    },
    {
      name: "HSM Calibration Rectangle",
      summary: "Current HsmCalibration rectangle: 9x5 tags, 96x34 safe inset, coordinates hidden.",
      entries: buildHsmCalibrationEntries()
    },
    {
      name: "API Coordinate Samples",
      summary: "Small direct-use HSM sample for supported plugins.",
      entries: [
        { id: "left-status", owner: "hsm.sample", system: "hsm", text: "LEFT x-1684 y220", x: -1684, y: 220, alignment: "center", textSize: 26, color: "#66F2C0", priority: 20 },
        { id: "right-status", owner: "hsm.sample", system: "hsm", text: "RIGHT x1684 y220", x: 1684, y: 220, alignment: "center", textSize: 26, color: "#66F2C0", priority: 20 },
        { id: "center", owner: "hsm.sample", system: "hsm", text: "<color=#F2D46B>Center x0 y540</color>", x: 0, y: 540, alignment: "center", textSize: 32, priority: 30 },
        { id: "legacy", owner: "legacy", text: "<color=#FF8080>Legacy captured notice</color>\\nThis would be routed through the compatibility layer.", x: 0, y: 720, alignment: "center", textSize: 24, priority: 1 }
      ]
    },
    {
      name: "Localized HUD Stress",
      summary: "Chinese text, mixed colors, and current bottom HUD-style blocks.",
      entries: [
        { id: "team", owner: "hsm.localized", system: "hsm", text: "<size=18><color=#66F2C0>队友</color> 0</size>", x: 360, y: 110, alignment: "right", textSize: 32, color: "#66F2C0", priority: 40 },
        { id: "stats", owner: "hsm.localized", system: "hsm", text: "<size=18>☆ 等级 1 <color=#64748B>·</color> <color=#38BDF8>◇ 经验 [--------]</color> 0/100 <color=#64748B>·</color> 🏆 MVP 0 <color=#64748B>·</color> <color=#FF6B81>☠</color> K:<color=#FF9AAF>0</color> D:<color=#FF9AAF>0</color> <color=#64748B>·</color> 总时 0M <color=#64748B>·</color> 今日 0M</size>", x: 0, y: 1048, alignment: "center", textSize: 32, priority: 30 },
        { id: "notice", owner: "hsm.localized", system: "hsm", text: "<color=#E6CF72>提示</color> 你已进入电梯。", x: 0, y: 650, alignment: "center", textSize: 28, priority: 20 }
      ]
    },
    {
      name: "SCP-999 HUD Friendly",
      summary: "SCP999 port: friendly status (y135) + delayed intro (y330) + heal feed (y450) co-rendered with the live CUIMeow layout.",
      entries: [
        { id: "scp999.status", owner: "SCP999", system: "hsm", text: "<size=26><b><color=#FFAA00>★ [SCP-999] 甜心 ★</color></b></size>\\n<size=17><color=#87CEEB>治愈之触 · 用任意武器为他人回血　|　SCP语音　|　前往广播室觉醒 [数据删除]</color></size>", x: 0, y: 135, alignment: "center", verticalAlign: "middle", textSize: 20, priority: 60 },
        { id: "scp999.intro", owner: "SCP999", system: "hsm", text: "<size=30><color=#FFAA00><b>你是 [SCP-999]「甜心」</b></color></size>\\n<size=18><color=#87CEEB>你可以使用任何武器为任何人回血</color></size>\\n<size=18><color=#87CEEB>你拥有SCP组队语音频道</color></size>\\n<size=18><color=#87CEEB>特殊[数据删除] 你因不满基金会的做法</color></size>\\n<size=18><color=#FF69B4>你可以选择隐藏，前往广播室按下广播吧</color></size>\\n<size=18><color=#FF4444>期待你的选择.....</color></size>", x: 0, y: 330, alignment: "center", verticalAlign: "middle", textSize: 20, priority: 50 },
        { id: "scp999.feed", owner: "SCP999", system: "hsm", text: "<size=26><color=#00FF7F><b>♥ +45 HP</b></color></size>\\n<size=20><color=#FFFFFF>Dummy-Alpha</color> <color=#AAAAAA>[ClassD]</color></size>\\n<size=18><color=#00BFFF>血量: 78/100</color></size>", x: 0, y: 465, alignment: "center", verticalAlign: "middle", textSize: 20, priority: 40 },
        { id: "qol.teammates", owner: "CustomizableUIMeow", system: "hsm", text: "<size=18><color=#6EE7B7>队友</color> <color=#A7F3D0>2</color>\\n<mspace=0.62em><color=#94A3B8>1.</color> <color=#FDE68A>SCI</color> <color=#FDE68A>100HP</color> </mspace>Nova\\n<mspace=0.62em><color=#94A3B8>2.</color> <color=#93C5FD>NTF</color> <color=#FDE68A> 87HP</color> </mspace><color=#C4B5FD>SCSC</color></size>", x: 360, y: 110, alignment: "right", verticalAlign: "middle", textSize: 32, priority: 30 },
        { id: "qol.notice", owner: "CustomizableUIMeow", system: "hsm", text: "<b><color=#38BDF8>提示</color></b> <color=#E2E8F0>设置已保存</color>", x: 0, y: 650, alignment: "center", verticalAlign: "middle", textSize: 34, priority: 20 },
        { id: "qol.kill", owner: "KillInfoPlugin", system: "hsm", text: "<b><color=#FDE68A>击杀</color></b> <color=#FCA5A5>SCP-939</color> <color=#CBD5E1>+20 XP</color>", x: 0, y: 778, alignment: "center", verticalAlign: "middle", textSize: 34, priority: 20 },
        { id: "qol.hud", owner: "CustomizableUIMeow", system: "hsm", text: "<size=18><color=#FDE68A>等级</color> <color=#FDE68A>4</color> <color=#64748B>·</color> <color=#38BDF8>经验</color> <color=#38BDF8>[■■■■■■□□□□]</color> 62/100 <color=#64748B>·</color> <color=#FDE68A>MVP</color> <color=#FDE68A>5</color> <color=#64748B>·</color> <color=#FB7185>K:</color><color=#FCA5A5>48</color> <color=#FB7185>D:</color><color=#FCA5A5>7</color> <color=#64748B>·</color> <color=#CBD5E1>总时</color> <color=#CBD5E1>1h 31m</color> <color=#64748B>·</color> <color=#CBD5E1>今日</color> <color=#CBD5E1>38m</color></size>", x: 0, y: 1048, alignment: "center", verticalAlign: "middle", textSize: 32, priority: 10 }
      ]
    },
    {
      name: "SCP-999 HUD Betrayed",
      summary: "SCP999 port: betrayed status (y135) + facility notice (y215) + awakened feed (y450) co-rendered with the live CUIMeow layout.",
      entries: [
        { id: "scp999.status", owner: "SCP999", system: "hsm", text: "<size=26><b><color=#FF0000>★ [SCP-999] 甜心·叛变体 ★</color></b></size>\\n<size=17><color=#FF4444>摧毁一切　|　击杀所有人　|　撤离设施</color></size>", x: 0, y: 135, alignment: "center", verticalAlign: "middle", textSize: 20, priority: 60 },
        { id: "scp999.notice", owner: "SCP999", system: "hsm", text: "<size=30><color=#FF0000><b>⚠ 异常事件 ⚠</b></color></size>\\n<size=22><color=#FFAA00>[SCP-999] 已接入设施广播系统</color></size>\\n<size=18><color=#FF4444>「我受够了...你们所有人都要付出代价」</color></size>", x: 0, y: 230, alignment: "center", verticalAlign: "middle", textSize: 24, priority: 55 },
        { id: "scp999.feed", owner: "SCP999", system: "hsm", text: "<size=28><color=#FF0000><b>★ 觉醒完成 ★</b></color></size>\\n<size=20><color=#FFD700>A7 虚拟现实扭曲枪已装备 | 无限弹药</color></size>\\n<size=18><color=#FF4444>任务：击杀所有人 → 撤离设施</color></size>", x: 0, y: 465, alignment: "center", verticalAlign: "middle", textSize: 20, priority: 40 },
        { id: "qol.teammates", owner: "CustomizableUIMeow", system: "hsm", text: "<size=18><color=#6EE7B7>队友</color> <color=#A7F3D0>2</color>\\n<mspace=0.62em><color=#94A3B8>1.</color> <color=#FDE68A>SCI</color> <color=#FDE68A>100HP</color> </mspace>Nova\\n<mspace=0.62em><color=#94A3B8>2.</color> <color=#93C5FD>NTF</color> <color=#FDE68A> 87HP</color> </mspace><color=#C4B5FD>SCSC</color></size>", x: 360, y: 110, alignment: "right", verticalAlign: "middle", textSize: 32, priority: 30 },
        { id: "qol.notice", owner: "CustomizableUIMeow", system: "hsm", text: "<b><color=#38BDF8>提示</color></b> <color=#E2E8F0>设置已保存</color>", x: 0, y: 650, alignment: "center", verticalAlign: "middle", textSize: 34, priority: 20 },
        { id: "qol.kill", owner: "KillInfoPlugin", system: "hsm", text: "<b><color=#FDE68A>击杀</color></b> <color=#FCA5A5>SCP-939</color> <color=#CBD5E1>+20 XP</color>", x: 0, y: 778, alignment: "center", verticalAlign: "middle", textSize: 34, priority: 20 },
        { id: "qol.hud", owner: "CustomizableUIMeow", system: "hsm", text: "<size=18><color=#FDE68A>等级</color> <color=#FDE68A>4</color> <color=#64748B>·</color> <color=#38BDF8>经验</color> <color=#38BDF8>[■■■■■■□□□□]</color> 62/100 <color=#64748B>·</color> <color=#FDE68A>MVP</color> <color=#FDE68A>5</color> <color=#64748B>·</color> <color=#FB7185>K:</color><color=#FCA5A5>48</color> <color=#FB7185>D:</color><color=#FCA5A5>7</color> <color=#64748B>·</color> <color=#CBD5E1>总时</color> <color=#CBD5E1>1h 31m</color> <color=#64748B>·</color> <color=#CBD5E1>今日</color> <color=#CBD5E1>38m</color></size>", x: 0, y: 1048, alignment: "center", verticalAlign: "middle", textSize: 32, priority: 10 }
      ]
    }
  ];

  return baseFixtures.concat(buildWarmupScpFixtures());
});
