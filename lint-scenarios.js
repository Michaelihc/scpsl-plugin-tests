#!/usr/bin/env node
// Scenario reflection lint — enforces the "no runtime reflection against the plugin under test"
// rule from .tests\AGENTS.md (Writing Tests). Scans behavior scenario classes only; shared harness
// plumbing (Playtest fulfillers, Behavioral\Harness) is governed by the centralized-adapter
// exception and is deliberately out of scope.
//
// Usage:  node lint-scenarios.js
// Exit 0 = clean (legacy-allowlisted files are reported but do not fail the run).
// Exit 1 = violation: fix the scenario, or — read-only geometry probes only — add a GEOMETRY
//          allowlist entry with a reason in scenario-reflection-allowlist.txt.
//
// Tiers:
//  HARD  reflective invocation/mutation (GetMethod/InvokeMember/SetValue). Never legal in a
//        scenario — the geometry carve-out is read-only by definition. Only LEGACY-MIGRATE
//        entries suppress these, and only until the file is migrated.
//  WARN  any other reflection marker (BindingFlags, GetField/GetProperty, string type lookup,
//        assembly scans). Legal only for GEOMETRY-allowlisted read-only spatial-anchor probes.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const allowlistPath = path.join(root, 'scenario-reflection-allowlist.txt');
const scenarioDirs = [
  path.join(root, 'Behavioral', 'Scenarios'),
  path.join(root, 'Playtest', 'Scenarios'),
];

const HARD_PATTERNS = [
  { re: /\bGetMethod\s*\(/, label: 'GetMethod( — reflective method lookup/invocation' },
  { re: /\bInvokeMember\s*\(/, label: 'InvokeMember( — reflective invocation' },
  { re: /\.SetValue\s*\(/, label: '.SetValue( — reflective mutation' },
  { re: /\bActivator\.CreateInstance\b/, label: 'Activator.CreateInstance — reflective construction' },
];

const WARN_PATTERNS = [
  { re: /\busing\s+System\.Reflection\s*;/, label: 'using System.Reflection' },
  { re: /\bBindingFlags\b/, label: 'BindingFlags' },
  { re: /\bGetField\s*\(/, label: 'GetField(' },
  { re: /\bGetProperty\s*\(/, label: 'GetProperty(' },
  { re: /\bGetType\s*\(\s*"/, label: 'GetType("…") — string type lookup' },
];
// NOT flagged: pure assembly-presence guards (AppDomain scan + assembly.GetName().Name equality,
// e.g. CivilianProtectionScenario's "CivilianProtection.dll is not loaded" check). They touch no
// members; the harmful variant (scan → GetType("…") → member walk) is caught by the patterns above.

function loadAllowlist() {
  const entries = new Map();
  if (!fs.existsSync(allowlistPath)) return entries;
  for (const raw of fs.readFileSync(allowlistPath, 'utf8').split(/\r?\n/)) {
    const line = raw.trim();
    if (line.length === 0 || line.startsWith('#')) continue;
    const parts = line.split('|').map((part) => part.trim());
    if (parts.length < 3) {
      console.error(`[ScenarioLint] ERROR allowlist entry needs "path | TAG | reason": ${line}`);
      process.exitCode = 1;
      continue;
    }
    const [file, tag, reason] = parts;
    if (tag !== 'GEOMETRY' && tag !== 'LEGACY-MIGRATE') {
      console.error(`[ScenarioLint] ERROR allowlist tag must be GEOMETRY or LEGACY-MIGRATE: ${line}`);
      process.exitCode = 1;
      continue;
    }
    entries.set(file.replace(/\\/g, '/'), { tag, reason, used: false });
  }
  return entries;
}

function scenarioFiles() {
  const files = [];
  for (const dir of scenarioDirs) {
    if (!fs.existsSync(dir)) continue;
    for (const name of fs.readdirSync(dir)) {
      if (name.endsWith('.cs')) files.push(path.join(dir, name));
    }
  }
  return files;
}

function stripComments(line) {
  // Line-based heuristic: drop // comments; block comments in these files are doc headers.
  const index = line.indexOf('//');
  return index >= 0 ? line.slice(0, index) : line;
}

function scan(file) {
  const hits = { hard: [], warn: [] };
  const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
  let inBlockComment = false;
  lines.forEach((rawLine, i) => {
    let line = rawLine;
    if (inBlockComment) {
      const end = line.indexOf('*/');
      if (end < 0) return;
      line = line.slice(end + 2);
      inBlockComment = false;
    }
    const start = line.indexOf('/*');
    if (start >= 0 && line.indexOf('*/', start + 2) < 0) {
      line = line.slice(0, start);
      inBlockComment = true;
    }
    line = stripComments(line);
    for (const pattern of HARD_PATTERNS) {
      if (pattern.re.test(line)) hits.hard.push({ line: i + 1, label: pattern.label });
    }
    for (const pattern of WARN_PATTERNS) {
      if (pattern.re.test(line)) hits.warn.push({ line: i + 1, label: pattern.label });
    }
  });
  return hits;
}

const allowlist = loadAllowlist();
let errors = 0;
let legacyFiles = 0;
let geometryFiles = 0;
let cleanFiles = 0;

for (const file of scenarioFiles()) {
  const rel = path.relative(root, file).replace(/\\/g, '/');
  const hits = scan(file);
  const entry = allowlist.get(rel);
  if (entry) entry.used = true;

  if (hits.hard.length === 0 && hits.warn.length === 0) {
    cleanFiles += 1;
    if (entry) console.log(`[ScenarioLint] NOTE ${rel} is allowlisted (${entry.tag}) but has no reflection — remove the entry.`);
    continue;
  }

  if (entry?.tag === 'LEGACY-MIGRATE') {
    legacyFiles += 1;
    console.log(`[ScenarioLint] LEGACY ${rel} (${hits.hard.length} hard, ${hits.warn.length} warn) — migration backlog: ${entry.reason}`);
    continue;
  }

  for (const hit of hits.hard) {
    errors += 1;
    console.error(`[ScenarioLint] ERROR ${rel}:${hit.line} ${hit.label} — reflective invoke/mutate is never legal in a scenario (AGENTS.md "Writing Tests"). Drive the behavior through a real RA command or LabAPI event instead.`);
  }

  if (entry?.tag === 'GEOMETRY') {
    geometryFiles += 1;
    if (hits.hard.length === 0) {
      console.log(`[ScenarioLint] OK ${rel} — GEOMETRY allowlisted read-only probes (${hits.warn.length} marker(s)): ${entry.reason}`);
    }
    continue;
  }

  for (const hit of hits.warn) {
    errors += 1;
    console.error(`[ScenarioLint] ERROR ${rel}:${hit.line} ${hit.label} — reflection in a scenario. If this is a read-only spatial anchor feeding world-state probes, add "${rel} | GEOMETRY | <reason>" to scenario-reflection-allowlist.txt; otherwise use an RA command/LabAPI surface.`);
  }
}

for (const [file, entry] of allowlist) {
  if (!entry.used) {
    console.log(`[ScenarioLint] NOTE allowlist entry for missing file: ${file} — remove it.`);
  }
}

console.log(`[ScenarioLint] summary errors=${errors} legacy=${legacyFiles} geometry=${geometryFiles} clean=${cleanFiles}`);
if (errors > 0) process.exitCode = 1;
