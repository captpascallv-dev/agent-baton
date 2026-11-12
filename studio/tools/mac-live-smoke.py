"""Real Mac live server + task-owned fake Dispatch + optional own-window preview.
Run under guards/capped_run.py. No model, account, provider or plugin access.
"""
import argparse,json,os,re,socket,subprocess,sys,tempfile,time,urllib.request
from pathlib import Path
REPO=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(REPO/'watch/tests'))
from mac_fixture import prepare
from mac_smoke import wait_file

def main(a):
    root=Path(tempfile.mkdtemp(prefix='agent-baton-studio-mac-',dir='/private/tmp')).resolve()
    fixture=root/'input';output=root/'output';output.mkdir(); evidence=Path(a.output_dir).resolve();evidence.mkdir(parents=True,exist_ok=True)
    server=preview=None
    try:
        prepare(fixture,a.pwsh,a.node)
        with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
        config={'port':port,'cliWatchRuntimeDirectory':str(Path(a.bridge).parent),
           'bridgeExecutable':str(Path(a.bridge).resolve()),'cliWatchConfig':str(fixture/'watch.json'),
           'allowedRoot':str(root),'snapshotPath':str(output/'snapshot.json'),'refreshSeconds':1,'staleAfterSeconds':60}
        configpath=root/'studio.json';configpath.write_text(json.dumps(config))
        log=evidence/'studio-live.log'
        with log.open('w') as stream:
            server=subprocess.Popen(['sh',str(REPO/'studio/start-studio.sh'),'--config',str(configpath)],stdout=stream,stderr=stream)
        def read_phase(expected):
            deadline=time.monotonic()+25
            while time.monotonic()<deadline:
                try:
                    with urllib.request.urlopen(f'http://127.0.0.1:{port}/api/snapshot',timeout=2) as r:data=json.load(r)
                    assert data['mode']=='live'
                    phases=[p['phase'] for p in data['projects']]
                    if any((p.startswith('running_') if expected=='running' else p=='delivered') for p in phases):return data
                except (OSError,ValueError,KeyError):pass
                if server.poll() is not None:raise RuntimeError('Live server exited')
                time.sleep(.3)
            raise RuntimeError('Live phase not observed: '+expected)
        for stage in ('running','delivered','exited'):
            if stage=='delivered':(fixture/'release-delivery').touch();wait_file(fixture/'outbox/READY.json')
            if stage=='exited':(fixture/'release-exit').touch();wait_file(fixture/'claude/jobs/mac-demo/terminal.json')
            if stage=='delivered':time.sleep(1.5)
            data=read_phase('delivered' if stage=='exited' else 'running')
            if stage=='exited':
                time.sleep(1.5);data=read_phase('delivered');assert all(not p['isCurrent'] for p in data['projects'])
            if stage=='delivered': assert any('READY.json' in json.dumps(w) for p in data['projects'] for w in p['works'])
            encoded=json.dumps(data);assert 'PRIVATE_SYNTHETIC_BODY_NOT_FOR_UI' not in encoded
            (evidence/(stage+'.json')).write_text(encoded)
            print(stage,[p['phase'] for p in data['projects']],flush=True)
            if stage=='running' and a.preview:
                previewlog=evidence/'preview.log'
                with previewlog.open('w') as stream:preview=subprocess.Popen([a.preview,f'http://127.0.0.1:{port}'],stdout=stream,stderr=stream)
                deadline=time.monotonic()+20;window=None
                while time.monotonic()<deadline:
                    match=re.search(r'STUDIO_WINDOW_ID=(\d+)',previewlog.read_text())
                    if match:window=match.group(1);break
                    time.sleep(.2)
                if not window:raise RuntimeError('Own preview did not load')
                subprocess.run(['/usr/sbin/screencapture','-x','-l',window,str(evidence/'studio-mac-live.png')],check=True,timeout=10)
                preview.terminate();preview.wait(timeout=5);preview=None
        print('STUDIO_MAC_LIVE_PASS live HTTP, actual fixture collector and three independent stages')
    finally:
        if preview and preview.poll() is None:preview.terminate();preview.wait(timeout=5)
        if server and server.poll() is None:server.terminate();server.wait(timeout=8)
        if fixture.exists():
            (fixture/'release-delivery').touch();(fixture/'release-exit').touch()
            if (fixture/'started.json').exists():wait_file(fixture/'claude/jobs/mac-demo/terminal.json',30)
        subprocess.run(['/usr/bin/trash','-s',str(root)],check=True,timeout=15)
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--bridge',required=True);p.add_argument('--pwsh',required=True)
    p.add_argument('--node',default='node');p.add_argument('--preview');p.add_argument('--output-dir',required=True)
    main(p.parse_args())
