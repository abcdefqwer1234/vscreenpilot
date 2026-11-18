/**
 * 文档链接检查：只查相对路径和锚点，HTTP(S)/mailto 交给外部工具。
 * 用法：node scripts/check-docs-links.mjs
 */
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const skipDirs = new Set(['.git', 'node_modules', 'bin', 'obj', 'artifacts', 'dist', '__pycache__']);
const failures = [];
const files = [];

function walk(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (skipDirs.has(entry.name) || entry.name.startsWith('.')) {
        continue;
      }
      walk(path.join(directory, entry.name));
    } else if (entry.name.toLowerCase().endsWith('.md')) {
      files.push(path.join(directory, entry.name));
    }
  }
}

const anchorCache = new Map();

function slugify(heading) {
  return heading
    .trim()
    .toLowerCase()
    .replace(/<[^>]+>/g, '')
    .replace(/[`*_~]/g, '')
    .replace(/[^\p{L}\p{N}\s-]/gu, '')
    .replace(/\s+/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-|-$/g, '');
}

function anchorsOf(file) {
  if (anchorCache.has(file)) {
    return anchorCache.get(file);
  }

  const anchors = new Set();
  const text = readFileSync(file, 'utf8');

  for (const line of text.split(/\r?\n/)) {
    const match = /^#{1,6}\s+(.+)$/.exec(line);
    if (match) {
      anchors.add(slugify(match[1]));
    }
  }

  anchorCache.set(file, anchors);
  return anchors;
}

walk(root);

for (const file of files) {
  const text = readFileSync(file, 'utf8');
  const pattern = /!?\[[^\]]*\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g;

  for (const match of text.matchAll(pattern)) {
    const raw = match[1];
    if (/^(?:https?:|mailto:|tel:|data:|#)/i.test(raw)) {
      continue;
    }

    const [rawPath, anchor] = raw.split('#');
    let target;

    try {
      target = path.resolve(path.dirname(file), decodeURIComponent(rawPath));
    } catch {
      failures.push(`${path.relative(root, file)} -> 非法 URL 编码: ${raw}`);
      continue;
    }

    if (!existsSync(target)) {
      failures.push(`${path.relative(root, file)} -> 缺少文件: ${rawPath}`);
      continue;
    }

    if (!anchor || statSync(target).isDirectory()) {
      continue;
    }

    if (!target.toLowerCase().endsWith('.md')) {
      continue;
    }

    const decoded = decodeURIComponent(anchor).toLowerCase();
    if (!anchorsOf(target).has(decoded)) {
      failures.push(`${path.relative(root, file)} -> 缺少锚点: ${raw}`);
    }
  }
}

if (failures.length > 0) {
  console.error(`[fail] 发现 ${failures.length} 个断链：`);
  for (const failure of failures) {
    console.error(`  - ${failure}`);
  }
  process.exit(1);
}

console.log(`[ok] ${files.length} 个 Markdown 文件，链接全部有效`);
