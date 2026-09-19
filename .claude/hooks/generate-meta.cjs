#!/usr/bin/env node
// PostToolUse(Write|Bash): generate a .meta with a fresh GUID for new scripts/asmdefs created
// outside Unity, so the asset and its .meta are committed together (avoids GUID churn).
// Write names its file; Bash doesn't, so a Bash call scans Assets/ for any script missing its .meta.
// Generation-only: never touches an existing .meta. Writes LF, UTF-8, no BOM.
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
let data;
try { data = JSON.parse(fs.readFileSync(0, 'utf8')); } catch { process.exit(0); }

const templates = {
  '.cs': 'MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n',
  '.asmdef': 'AssemblyDefinitionImporter:\n  externalObjects: {}\n',
};

function generateMeta(fp) {
  const importer = templates[path.extname(fp).toLowerCase()];
  const meta = fp + '.meta';
  if (!importer || !fs.existsSync(fp) || fs.existsSync(meta)) return;
  const guid = crypto.randomUUID().replace(/-/g, '');
  fs.writeFileSync(meta, `fileFormatVersion: 2\nguid: ${guid}\n${importer}  userData:\n  assetBundleName:\n  assetBundleVariant:\n`, { encoding: 'utf8' });
  process.stderr.write(`Generated ${meta} (guid ${guid}). Verify in Unity on next refresh.\n`);
}

// Unity imports nothing under dot-prefixed or ~-suffixed folders, so those never get a .meta.
function scan(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.name.startsWith('.') || entry.name.endsWith('~')) continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) scan(full);
    else generateMeta(full);
  }
}

const fp = data && data.tool_input && data.tool_input.file_path;
if (fp) {
  generateMeta(fp);
} else if (data && data.tool_name === 'Bash') {
  const assets = path.join(process.env.CLAUDE_PROJECT_DIR || data.cwd || process.cwd(), 'Assets');
  if (fs.existsSync(assets)) scan(assets);
}
process.exit(0);
