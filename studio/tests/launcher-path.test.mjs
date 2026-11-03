import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

test('nested studio launcher keeps its default live configuration outside the checkout',
  { skip: process.platform !== 'win32' }, () => {
    const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'studio-entry-'));
    const canonical = fs.realpathSync(temp);
    const checkout = path.join(temp, 'repo'), studio = path.join(checkout, 'studio');
    fs.mkdirSync(studio, { recursive: true });
    const launcher = path.join(studio, 'Start-Studio.ps1');
    fs.copyFileSync(fileURLToPath(new URL('../Start-Studio.ps1', import.meta.url)), launcher);
    try {
      const result = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-File', launcher, '-NoOpen'],
        { cwd: checkout, encoding: 'utf8', windowsHide: true, timeout: 15000 });
      assert.equal(result.status, 1);
      const expected = path.join(temp, 'ai-moe-studio-state', 'config.json');
      assert.ok(!expected.startsWith(checkout + path.sep));
      assert.ok(fs.readFileSync(path.join(studio, 'work', 'startup-error.log'), 'utf8').includes(expected));
    } finally {
      assert.equal(fs.realpathSync(temp), canonical);
      assert.equal(path.dirname(canonical), fs.realpathSync(os.tmpdir()));
      fs.rmSync(canonical, { recursive: true, force: true });
    }
  });
