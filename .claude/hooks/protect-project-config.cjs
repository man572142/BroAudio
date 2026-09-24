#!/usr/bin/env node
// PreToolUse(Edit|Write|MultiEdit|Bash): guard shared project config.
// Changes here affect every team member's project; require explicit human sign-off.
//
// Edit/Write/MultiEdit name their file, so the check is exact. A Bash command does not, so it is read as
// text and blocked when it both names a protected path and does something that writes: a redirection into
// it, a writing command (mv, rm, cp onto it, tee, touch, sed -i, a git subcommand that rewrites the work
// tree) naming it, an interpreter whose inline program names it alongside a write call, or a repo script
// known to rewrite the manifest. Reading - cat, grep, git diff, sed -n, cp out of it - stays allowed. This
// is a guard against accidents, not a sandbox: a command built to evade it will.
const fs = require('fs');
const path = require('path');
let data;
try { data = JSON.parse(fs.readFileSync(0, 'utf8')); } catch { process.exit(0); }

const PROTECTED = /(^|[\s'"=:(\/\\])(ProjectSettings(\/|\\|(?=[\s'";|&)]|$))|Packages[\/\\]manifest\.json)/;
const PROTECTED_TOKEN = /(^|\/)(ProjectSettings(\/|$)|Packages\/manifest\.json$)/;

function block(what) {
  process.stderr.write(`Blocked: ${what} is shared project config. Ask the user before modifying it.\n`);
  process.exit(2);
}

// ---- Edit / Write / MultiEdit ----
const fp = data && data.tool_input && data.tool_input.file_path;
if (fp) {
  const norm = fp.replace(/\\/g, '/');
  if (/(^|\/)ProjectSettings\//.test(norm) || /(^|\/)Packages\/manifest\.json$/.test(norm)) block(`'${fp}'`);
  process.exit(0);
}

// ---- Bash ----
const command = data && data.tool_name === 'Bash' && data.tool_input && data.tool_input.command;
if (typeof command !== 'string') process.exit(0);

// Rewrites Packages/manifest.json and packages-lock.json of the project it is pointed at (CI runs it on a
// throwaway checkout). Pointed at this project's own Packages/, it is a manifest edit.
const strip = /strip_optional_packages\.py\s+(?:"([^"]+)"|'([^']+)'|(\S+))/.exec(command);
if (strip) {
  const target = strip[1] || strip[2] || strip[3];
  const cwd = data.cwd || process.env.CLAUDE_PROJECT_DIR || process.cwd();
  const resolved = path.resolve(cwd, target);
  if (fs.existsSync(path.join(path.dirname(resolved), 'ProjectSettings')) && path.basename(resolved) === 'Packages') {
    block(`strip_optional_packages.py on '${target}' rewrites Packages/manifest.json, which`);
  }
}

if (!PROTECTED.test(command)) process.exit(0);

const unquote = (word) => word.replace(/^['"]|['"]$/g, '');
const isProtected = (word) => PROTECTED_TOKEN.test(unquote(word).replace(/\\/g, '/').replace(/^\.\//, ''));

// Redirection into a protected path, anywhere in the command: > file, >> file, >| file, &> file, 2> file.
if (/(^|[^<>&0-9])(\d?|&)>{1,2}\|?\s*['"]?[^\s'";|&]*(ProjectSettings[\/\\]|Packages[\/\\]manifest\.json)/.test(command)) {
  block('a redirection into ProjectSettings/ or Packages/manifest.json writes to it, and it');
}

// An interpreter's inline program (-c, -e, or a heredoc) that names the path and writes something.
if (/\b(python[0-9.]*|node|ruby|perl|pwsh|powershell|php)\b/.test(command) &&
    /\b(write|writeFile|writeFileSync|dump|open\s*\([^)]*['"][wax+]|unlink|remove|rename|replace|truncate|rmtree|copy|move|save)\b/i.test(command)) {
  block('an inline script that names ProjectSettings/ or Packages/manifest.json and writes files would edit it, and it');
}

// Command by command: split on ; && || | and newlines, then look at what each one does.
const WRITES_ANY_ARG = new Set(['mv', 'rm', 'rmdir', 'touch', 'truncate', 'tee', 'chmod', 'chown', 'shred', 'unlink']);
const WRITES_LAST_ARG = new Set(['cp', 'install', 'ln', 'rsync', 'scp']);
const GIT_WRITES = new Set(['checkout', 'restore', 'reset', 'mv', 'rm', 'apply', 'stash', 'clean', 'am', 'switch']);
for (const segment of command.split(/\n|;|&&|\|\||\|/)) {
  const words = segment.trim().split(/\s+/).filter(Boolean);
  while (words.length && (/^\w+=/.test(words[0]) || ['sudo', 'env', 'command', 'exec', 'xargs', 'time', 'nohup'].includes(words[0]))) {
    words.shift();
  }
  if (!words.length) continue;
  const tool = path.basename(unquote(words[0]));
  const args = words.slice(1);
  const operands = args.filter((a) => !a.startsWith('-'));
  const named = args.some(isProtected);
  if (!named) continue;

  if (WRITES_ANY_ARG.has(tool)) block(`'${segment.trim()}' writes to a path that`);
  if (WRITES_LAST_ARG.has(tool) && operands.length && isProtected(operands[operands.length - 1])) {
    block(`'${segment.trim()}' writes to a path that`);
  }
  if (tool === 'dd' && args.some((a) => a.startsWith('of=') && isProtected(a.slice(3)))) block(`'${segment.trim()}' writes to a path that`);
  if ((tool === 'sed' || tool === 'perl' || tool === 'yq' || tool === 'gsed') &&
      args.some((a) => /^-[a-zA-Z]*i/.test(a) || a === '--in-place' || a.startsWith('--in-place='))) {
    block(`'${segment.trim()}' edits in place a path that`);
  }
  if (tool === 'git' && operands.length && GIT_WRITES.has(operands[0])) block(`'${segment.trim()}' rewrites a path that`);
}
process.exit(0);
