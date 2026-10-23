import { execFile } from 'node:child_process';
import { mkdir, lstat, open, rename, unlink } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

export async function createPublisher(directory, producerId) {
  if (typeof directory !== 'string' || !path.isAbsolute(directory)) throw new Error('Absolute outputDirectory required');
  const target = path.resolve(directory);
  if (process.platform === 'win32') {
    await promisify(execFile)('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
      fileURLToPath(new URL('./private-directory.ps1', import.meta.url)), '-Directory', target],
    { windowsHide: true, timeout: 5000, maxBuffer: 8192 });
  } else {
    await mkdir(target, { mode: 0o700 }).catch(error => { if (error.code !== 'EEXIST') throw error; });
    const info = await lstat(target);
    if (!info.isDirectory() || info.isSymbolicLink() || info.uid !== process.getuid() || (info.mode & 0o077)) throw new Error('Private output directory required');
  }
  const filename = path.join(target, `dsh-status-${process.pid}-${producerId}.json`);
  let sequence = 0;
  return { filename, async publish(frame) {
    const temporary = filename + `.tmp-${++sequence}`;
    let handle;
    try {
      handle = await open(temporary, 'wx', 0o600);
      await handle.writeFile(JSON.stringify(frame), 'utf8');
      await handle.close(); handle = null;
      await rename(temporary, filename);
    } finally {
      await handle?.close().catch(() => {});
      await unlink(temporary).catch(error => { if (error.code !== 'ENOENT') throw error; });
    }
  } };
}
