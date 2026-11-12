import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, realpath, mkdir, symlink, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { validateMacConfig } from '../server/live-server.mjs';

test('Mac config admits explicit canonical fixture inputs, not external or linked inputs', async () => {
  const root = await realpath(await mkdtemp(path.join(os.tmpdir(), 'studio-mac-scope-')));
  try {
    const config = { allowedRoot: root, cliWatchConfig: path.join(root, 'watch.json'), snapshotPath: path.join(root, 'output', 'snapshot.json') };
    assert.equal(await validateMacConfig(config, path.join(root, 'studio.json')), config);
    await assert.rejects(validateMacConfig({ ...config, allowedRoot: undefined }, path.join(root, 'studio.json')));
    await assert.rejects(validateMacConfig({ ...config, cliWatchConfig: '/outside/config.json' }, path.join(root, 'studio.json')));
    await assert.rejects(validateMacConfig({ ...config, codexCliHome: root }, path.join(root, 'studio.json')));
    await mkdir(path.join(root, 'real'));
    await symlink(path.join(root, 'real'), path.join(root, 'link'));
    await assert.rejects(validateMacConfig({ ...config, snapshotPath: path.join(root, 'link', 'snapshot.json') }, path.join(root, 'studio.json')));
  } finally { await rm(root, { recursive: true, force: true }); }
});
