"""Serial real-Dispatch/fake-executor integration for the Mac watch and Studio.

Wrap in guards/capped_run.py. No provider, account, private session or plugin.
Outputs are task-only snapshots, optional application-only screenshots and a
small result summary. The fixture executor has its own 180-second cap.
"""
import argparse,json,os,re,shutil,subprocess,sys,tempfile,time
from pathlib import Path
from mac_fixture import prepare, REPO


def wait_file(path, seconds=25):
    deadline=time.monotonic()+seconds
    while not path.exists():
        if time.monotonic()>deadline: raise RuntimeError('Fixture file wait timed out: '+path.name)
        time.sleep(.1)


def capture(app,snapshot,appearance,destination):
    log=destination.with_suffix('.capture.log')
    with log.open('w') as stream:
        proc=subprocess.Popen([app,'--snapshot',str(snapshot),'--appearance',appearance],stdout=stream,stderr=stream)
    try:
        deadline=time.monotonic()+15
        window=None
        while time.monotonic()<deadline:
            match=re.search(r'WATCH_WINDOW_ID=(\d+)',log.read_text())
            if match: window=match.group(1);break
            if proc.poll() is not None: break
            time.sleep(.2)
        if not window: raise RuntimeError('Own watch window not ready')
        time.sleep(1)
        subprocess.run(['/usr/sbin/screencapture','-x','-l',window,str(destination)],check=True,timeout=10)
    finally:
        if proc.poll() is None: proc.terminate();proc.wait(timeout=5)


def main(a):
    out=Path(a.output_dir).resolve();out.mkdir(parents=True,exist_ok=True)
    root=Path(tempfile.mkdtemp(prefix='agent-baton-mac-d-parent-',dir='/private/tmp')).resolve()
    fixture=root/'fixture'
    snapshots=[]
    try:
        prepare(fixture,a.pwsh,a.node)
        for stage in ('running','delivered','exited'):
            if stage=='delivered':
                (fixture/'release-delivery').touch();wait_file(fixture/'outbox'/'READY.json')
            if stage=='exited':
                (fixture/'release-exit').touch();wait_file(fixture/'claude/jobs/mac-demo/terminal.json')
            target=out/(stage+'.json')
            result=subprocess.run([a.dotnet,a.cli,'--config',str(fixture/'watch.json'),
                '--allowed-root',str(fixture),'--output',str(target),'--once'],capture_output=True,text=True,timeout=25)
            if result.returncode: raise RuntimeError('Watch CLI failed: '+result.stderr)
            raw=target.read_text();assert 'PRIVATE_SYNTHETIC_BODY_NOT_FOR_UI' not in raw
            data=json.loads(raw);projects=data['snapshot']['projects'];assert projects,'No task projected'
            phases=[p['phase'] for p in projects]
            if stage in ('running','delivered'): assert any(p.startswith('running_') for p in phases),phases
            else: assert 'delivered' in phases,phases
            if stage=='delivered': assert any(a['label']=='READY.json' for p in projects for w in p['workItems'] for a in w['artifacts'])
            if stage=='exited': assert all(not p['isCurrent'] for p in projects),projects
            acceptance=[w['axes']['acceptance'] for p in projects for w in p['workItems']]
            assert all(v not in ('accepted','passed','approved') for v in acceptance),acceptance
            summary={'stage':stage,'phases':phases,'acceptance':acceptance}
            if a.bridge:
                target_studio=root/('studio-'+stage+'.json')
                result=subprocess.run([a.dotnet,a.bridge,'--config',str(fixture/'watch.json'),
                    '--allowed-root',str(root),'--snapshot',str(target_studio),'--once'],capture_output=True,text=True,timeout=25)
                if result.returncode: raise RuntimeError('Studio bridge failed: '+result.stderr)
                shutil.copyfile(target_studio,out/target_studio.name)
                raw=target_studio.read_text();assert 'PRIVATE_SYNTHETIC_BODY_NOT_FOR_UI' not in raw
                studio=json.loads(raw);assert studio['mode']=='live'
                summary['studioPhases']=[p['phase'] for p in studio['projects']]
                assert summary['studioPhases']==phases,summary
            if a.watch_app and stage=='running':
                for appearance in ('light','dark'):capture(a.watch_app,target,appearance,out/('watch-'+appearance+'.png'))
            snapshots.append(summary);print(json.dumps(summary),flush=True)
        assert (fixture/'claude/jobs/mac-demo/exit_code.txt').read_text().strip()=='0'
        (out/'summary.json').write_text(json.dumps(snapshots,indent=2))
        print('MAC_SMOKE_PASS real Dispatch, fake CLI live identity, READY, history; acceptance independent')
    finally:
        if fixture.exists():
            (fixture/'release-delivery').touch();(fixture/'release-exit').touch()
            terminal=fixture/'claude/jobs/mac-demo/terminal.json'
            if (fixture/'started.json').exists():wait_file(terminal,30)
        # Preserve generated test snapshots outside root; recycle only our unique fixture.
        subprocess.run(['/usr/bin/trash','-s',str(root)],check=True,timeout=15)


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--dotnet',required=True);p.add_argument('--cli',required=True)
    p.add_argument('--bridge');p.add_argument('--pwsh',required=True);p.add_argument('--node',default=shutil.which('node'))
    p.add_argument('--watch-app');p.add_argument('--output-dir',required=True)
    main(p.parse_args())
