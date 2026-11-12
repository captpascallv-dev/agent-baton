"""Task-owned Dispatch fixture for Mac integration checks; never calls a model.

The fake executor waits for explicit release markers. This helper only creates
fixture files and invokes the public Dispatcher. Use guards/capped_run.py around
this script; the detached fake executor has a separate guard and time limit.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

REPO = Path(__file__).resolve().parents[2]


def prepare(root: Path, pwsh: str, node: str):
    root.mkdir(parents=True, exist_ok=False)
    (root / '.fixture-owner').write_text('agent-baton-mac-d\n')
    box = root / 'claude' / 'bin'
    box.mkdir(parents=True)
    for name in ('Dispatch.ps1', 'Run-Agent.ps1', 'start_detached.py'):
        shutil.copy2(REPO / 'claude' / 'bin' / name, box / name)
    entry = root / 'node_modules' / '@earendil-works' / 'pi-coding-agent' / 'dist' / 'cli.js'
    entry.parent.mkdir(parents=True)
    entry.write_text('''const fs=require('node:fs'),path=require('node:path');
const arg=k=>process.argv[process.argv.indexOf(k)+1];
const root=arg('--fixture-root'),outbox=arg('--outbox');
fs.writeFileSync(path.join(root,'started.json'),JSON.stringify({pid:process.pid}));
let delivered=false;
const timer=setInterval(()=>{
 if(!delivered && fs.existsSync(path.join(root,'release-delivery'))){
  fs.mkdirSync(outbox,{recursive:true});
  fs.writeFileSync(path.join(outbox,'REPORT.md'),'PRIVATE_SYNTHETIC_BODY_NOT_FOR_UI');
  fs.writeFileSync(path.join(outbox,'READY.json'),JSON.stringify({status:'completed',deliverables:['REPORT.md']}));
  delivered=true;
 }
 if(delivered && fs.existsSync(path.join(root,'release-exit'))){clearInterval(timer);process.exit(0);}
},100);
setTimeout(()=>process.exit(7),170000);
''')
    (root / 'card.md').write_text('PRIVATE_SYNTHETIC_BODY_NOT_FOR_UI\n')
    config = {'executors': {'pi': {'command': sys.executable, 'args': [
        str(REPO/'guards'/'capped_run.py'), '--mem-gb','0.5','--timeout','180',
        '--log','{jobdir}/fake-guard.log','--',node,str(entry),'--fixture-root',str(root),
        '--outbox','{outbox}','--model','demo-model','--thinking','high']}}}
    (root / 'executors.json').write_text(json.dumps(config))
    watch={'claude_mailbox_roots':[str(root/'claude')], 'mailbox_roots':[],
           'registry_paths':[], 'additional_source_paths':[], 'max_files_per_refresh':512,
           'read_only':True,'allow_network':False,'send_messages_automatically':False}
    (root/'watch.json').write_text(json.dumps(watch))
    env=dict(os.environ);env['PATH']=str(Path(pwsh).parent)+os.pathsep+env.get('PATH','')
    result=subprocess.run([pwsh,'-NoProfile','-File',str(box/'Dispatch.ps1'),
        '-Executor','pi','-Workspace',str(root),'-Outbox',str(root/'outbox'),
        '-PromptFile',str(root/'card.md'),'-Task','Mac synthetic execution',
        '-Project','mac-demo','-JobId','mac-demo','-Config',str(root/'executors.json'),
        '-TimeoutMinutes','3'],env=env,capture_output=True,text=True,timeout=30)
    if result.returncode: raise RuntimeError('Fixture Dispatch failed: '+result.stderr)
    until=time.monotonic()+15
    while not (root/'started.json').exists():
        if time.monotonic()>until: raise RuntimeError('Fake executor did not start')
        time.sleep(.1)
    print('MAC_FIXTURE_RUNNING',flush=True)


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--root',type=Path,required=True)
    p.add_argument('--pwsh',required=True);p.add_argument('--node',default=shutil.which('node'))
    a=p.parse_args();prepare(a.root.resolve(),a.pwsh,a.node)
