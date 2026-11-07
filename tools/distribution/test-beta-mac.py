from pathlib import Path
import os, json, subprocess, tempfile, time, shutil
root=Path(__file__).resolve().parents[2]
base=root.parent
results=[]
def run(name,args,env=None,expected=0):
    result=subprocess.run(args,cwd=root,env=env,capture_output=True,text=True,timeout=180)
    (base/(name+'.log')).write_text(result.stdout+result.stderr)
    passed=result.returncode==expected if expected==0 else result.returncode!=0
    results.append({'name':name,'exitCode':result.returncode,'passed':passed})
    print(name,passed,flush=True)
    return result
for tool,arg in [('ScreenLayoutProbe',None),('ClipboardProbe',None),('FileTransferProbe',None),('PairedTlsProbe','settings')]:
    command=['dotnet','run','--project','tools/'+tool,'-c','Release']
    if arg: command+=['--',arg]
    run('mac-'+tool,command)
testhome=Path(tempfile.mkdtemp(prefix='LinkBridge-Beta-install-'))
environment={**os.environ,'HOME':str(testhome)}
install=root/'artifacts/public-release/Install.command'
run('mac-first-install',['sh',str(install)],environment)
run('mac-repeat-install',['sh',str(install)],environment)
app=testhome/'Applications/LinkBridge.app'
run('mac-installed-signature',['codesign','--verify','--deep','--strict',str(app)])
run('mac-version',[str(app/'Contents/MacOS/LinkBridge.App'),'--version'],environment)
isolated=testhome/'isolated-settings'
isolated.mkdir()
(isolated/'settings.json').write_text(json.dumps({'displayName':'Beta installation test','autoDiscover':False,'enableInputServer':False,'shareClipboardAutomatically':False,'shareSystemAudioAutomatically':False,'shareLocalCamera':False,'shareLocalMicrophone':False,'windowRoamingEnabled':False}))
with (base/'mac-gui-start.log').open('w') as log:
    proc=subprocess.Popen([str(app/'Contents/MacOS/LinkBridge.App')],env={**environment,'LINKBRIDGE_WORKSPACE_HOME':str(isolated)},stdout=log,stderr=log)
    time.sleep(7)
    alive=proc.poll() is None
    if alive:
        proc.terminate()
        try: proc.wait(20)
        except subprocess.TimeoutExpired: proc.kill();proc.wait()
    results.append({'name':'mac-isolated-gui-start-and-graceful-exit','passed':alive and proc.returncode==0,'exitCode':proc.returncode,'limitation':'No fresh Gatekeeper or TCC authorization tested.'})
run('mac-fixture-different-signature',['codesign','--force','--sign','-','--identifier','com.linkbridge.test.different',str(app)])
run('mac-different-signature-refused',['sh',str(install)],environment,expected=1)
(base/'mac-tests.json').write_text(json.dumps(results,indent=2))
raise SystemExit(0 if all(r['passed'] for r in results) else 1)
