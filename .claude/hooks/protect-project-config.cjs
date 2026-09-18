#!/usr/bin/env node
// PreToolUse(Edit|Write|MultiEdit): guard shared project config.
// Changes here affect every team member's project; require explicit human sign-off.
const fs = require('fs');
let data;
try { data = JSON.parse(fs.readFileSync(0, 'utf8')); } catch { process.exit(0); }
const fp = data && data.tool_input && data.tool_input.file_path;
if (!fp) process.exit(0);
const norm = fp.replace(/\\/g, '/');

if (/(^|\/)ProjectSettings\//.test(norm) || /(^|\/)Packages\/manifest\.json$/.test(norm)) {
  process.stderr.write(`Blocked: '${fp}' is shared project config. Ask the user before modifying it.\n`);
  process.exit(2);
}
process.exit(0);
