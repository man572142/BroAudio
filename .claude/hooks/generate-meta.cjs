#!/usr/bin/env node
// PostToolUse(Write|Bash): give a new script/asmdef created outside Unity its .meta, so the asset and its
// .meta are committed together (avoids GUID churn).
// Write names its file; Bash doesn't, so a Bash call scans Assets/ for any script missing its .meta.
// Never touches an existing .meta. Writes LF, UTF-8, no BOM.
//
// A file that is missing its .meta is not always new: `mv Old/Foo.cs New/Foo.cs` leaves Old/Foo.cs.meta
// behind, and `git mv` of the .cs alone or a delete-and-recreate does the same. Minting a fresh GUID there
// silently breaks every reference to the script - a MonoBehaviour such as SoundManager on a prefab, an
// asmdef referenced by GUID. So before minting, look for the .meta this file had under its old path:
//   1. an orphaned .meta on disk (its asset is gone) with the same file name - moved into place, keeping
//      the importer settings with the GUID;
//   2. a .meta git reports deleted from the work tree or the index with the same file name - its committed
//      content is restored at the new path.
// Exactly one candidate is reused. More than one is ambiguous: nothing is generated and the hook says so,
// because picking wrong is worse than letting Unity mint one on the next refresh. A GUID already used by
// another .meta on disk is never reused.
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { execFileSync } = require('child_process');
let data;
try { data = JSON.parse(fs.readFileSync(0, 'utf8')); } catch { process.exit(0); }

const templates = {
  '.cs': 'MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n',
  '.asmdef': 'AssemblyDefinitionImporter:\n  externalObjects: {}\n',
};

// The Unity project a path belongs to: the nearest ancestor holding both Assets/ and ProjectSettings/. Found
// from the path rather than taken from CLAUDE_PROJECT_DIR, so a git worktree under .claude/worktrees/ is its
// own project and not mistaken for the checkout that contains it.
function projectRootOf(start) {
  if (!start) return null;
  for (let dir = path.resolve(start); ; dir = path.dirname(dir)) {
    if (fs.existsSync(path.join(dir, 'Assets')) && fs.existsSync(path.join(dir, 'ProjectSettings'))) return dir;
    if (path.dirname(dir) === dir) return null;
  }
}

const writtenFile = data && data.tool_input && data.tool_input.file_path;
const projectDir = projectRootOf(writtenFile ? path.dirname(writtenFile) : data.cwd)
  || projectRootOf(process.env.CLAUDE_PROJECT_DIR) || process.env.CLAUDE_PROJECT_DIR || process.cwd();
const assetsDir = path.join(projectDir, 'Assets');

function git(args) {
  try {
    return execFileSync('git', args, { cwd: projectDir, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
  } catch {
    return null;
  }
}

function guidOf(metaText) {
  const match = /^guid:\s*([0-9a-f]{32})\s*$/m.exec(metaText || '');
  return match ? match[1] : null;
}

// Unity imports nothing under dot-prefixed or ~-suffixed folders, so those never get a .meta.
function walk(dir, visit) {
  let entries;
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
  for (const entry of entries) {
    if (entry.name.startsWith('.') || entry.name.endsWith('~')) continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full, visit);
    else visit(full);
  }
}

// Built once per hook run: every GUID in use, orphaned .meta files, and .meta files git says were deleted.
let index = null;
function buildIndex() {
  const used = new Set();
  const orphans = []; // { name, metaPath, guid }
  walk(assetsDir, (full) => {
    if (!full.endsWith('.meta')) return;
    const guid = guidOf(fs.readFileSync(full, 'utf8'));
    if (guid) used.add(guid);
    const asset = full.slice(0, -'.meta'.length);
    if (!fs.existsSync(asset)) orphans.push({ name: path.basename(asset), metaPath: full, guid });
  });

  const deleted = []; // { name, repoPath, content, guid }
  const listed = new Set();
  for (const args of [['ls-files', '--deleted', '-z', '--', 'Assets'],
                      ['diff', '--cached', '--name-only', '--diff-filter=D', '-z', '--', 'Assets']]) {
    for (const repoPath of (git(args) || '').split('\0')) {
      if (!repoPath.endsWith('.meta') || listed.has(repoPath)) continue;
      listed.add(repoPath);
      const content = git(['show', `HEAD:${repoPath}`]);
      const guid = guidOf(content);
      if (!guid) continue;
      deleted.push({ name: path.basename(repoPath.slice(0, -'.meta'.length)), repoPath, content, guid });
    }
  }
  index = { used, orphans, deleted, claimed: new Set() };
}

function candidatesFor(fp) {
  if (!index) buildIndex();
  const name = path.basename(fp);
  const free = (c) => c.guid && !index.claimed.has(c.guid);
  const orphans = index.orphans.filter((c) => c.name === name && free(c));
  // A deleted .meta whose GUID is still in use elsewhere was moved properly; it is not a candidate.
  const deleted = index.deleted.filter((c) => c.name === name && free(c) && !index.used.has(c.guid)
    && !orphans.some((o) => o.guid === c.guid));
  return orphans.map((c) => ({ kind: 'orphan', ...c })).concat(deleted.map((c) => ({ kind: 'deleted', ...c })));
}

// Only what Unity imports: under this project's Assets/, outside dot-prefixed and ~-suffixed folders. A script
// written to a scratch directory or a hidden folder gets no .meta.
function isImportedAsset(fp) {
  const relative = path.relative(assetsDir, path.resolve(projectDir, fp));
  if (!relative || relative.startsWith('..') || path.isAbsolute(relative)) return false;
  return relative.split(path.sep).slice(0, -1).every((part) => !part.startsWith('.') && !part.endsWith('~'));
}

function generateMeta(fp) {
  const importer = templates[path.extname(fp).toLowerCase()];
  const meta = fp + '.meta';
  if (!importer || !isImportedAsset(fp) || !fs.existsSync(fp) || fs.existsSync(meta)) return;

  const candidates = candidatesFor(fp);
  if (candidates.length > 1) {
    process.stderr.write(
      `Not generating ${meta}: ${path.basename(fp)} looks moved, but ${candidates.length} old .meta files could be ` +
      `its own (${candidates.map((c) => c.metaPath || c.repoPath).join(', ')}). Restore the right one next to it ` +
      `so its GUID - and every reference to it - survives the move.\n`);
    return;
  }
  if (candidates.length === 1) {
    const found = candidates[0];
    index.claimed.add(found.guid);
    if (found.kind === 'orphan') {
      fs.renameSync(found.metaPath, meta);
      process.stderr.write(`Moved ${found.metaPath} to ${meta}, keeping guid ${found.guid}: ` +
        `${path.basename(fp)} was moved without its .meta. Verify in Unity on next refresh.\n`);
    } else {
      fs.writeFileSync(meta, found.content, { encoding: 'utf8' });
      process.stderr.write(`Restored ${meta} from the deleted ${found.repoPath} (guid ${found.guid}): ` +
        `${path.basename(fp)} was moved without its .meta. Verify in Unity on next refresh.\n`);
    }
    return;
  }

  const guid = crypto.randomUUID().replace(/-/g, '');
  fs.writeFileSync(meta, `fileFormatVersion: 2\nguid: ${guid}\n${importer}  userData:\n  assetBundleName:\n  assetBundleVariant:\n`, { encoding: 'utf8' });
  process.stderr.write(`Generated ${meta} (guid ${guid}). Verify in Unity on next refresh.\n`);
}

if (writtenFile) {
  generateMeta(writtenFile);
} else if (data && data.tool_name === 'Bash') {
  if (fs.existsSync(assetsDir)) walk(assetsDir, generateMeta);
}
process.exit(0);
