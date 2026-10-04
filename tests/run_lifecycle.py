"""Isolated HTTP lifecycle integration checks; uses a fake Ollama and temporary SQLite/workspace."""
import json, os, pathlib, subprocess, tempfile, threading, time, urllib.request, urllib.error, sys
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
PROJECT = pathlib.Path(__file__).resolve().parents[1]
class Ollama(BaseHTTPRequestHandler):
    def log_message(self,*args): pass
    def do_GET(self):
        # The installed models, which scheduled tasks check before they start.
        payload={'models':[{'name':'fake'}]} if self.path=='/api/tags' else {}
        self.send_response(200 if payload else 404); self.end_headers(); self.wfile.write(json.dumps(payload).encode())
    def do_POST(self):
        if self.headers.get('Transfer-Encoding') == 'chunked':
            raw=b''
            while True:
                size=int(self.rfile.readline().split(b';')[0],16)
                if size==0:
                    self.rfile.readline(); break
                raw+=self.rfile.read(size); self.rfile.read(2)
        else: raw=self.rfile.read(int(self.headers['Content-Length']))
        data=json.loads(raw)
        if self.path=='/api/show': payload={'capabilities':['tools']}
        else:
            messages=data['messages']; text=next(m['content'] for m in reversed(messages) if m['role']=='user')
            turns=sum(1 for m in messages if m['role']=='tool')
            if 'tools' in data and text.startswith('PAGES ') and turns<3:
                # Reads a file (untrusted content), then two pages on one site that nobody linked to.
                call=[{'name':'read_file','arguments':{'path':text.split()[-1]}},
                      {'name':'read_page','arguments':{'url':'https://www.trusted-site.invalid/a'}},
                      {'name':'read_page','arguments':{'url':'https://news.trusted-site.invalid/b'}}][turns]
                payload={'message':{'content':'','tool_calls':[{'function':call}]},'done':True,'prompt_eval_count':200}
            elif 'tools' in data and not any(m['role']=='tool' for m in messages) and 'NO_TOOL' not in text:
                if text.startswith('RUN '): call={'name':'run_command','arguments':{'command':text[4:]}}
                elif text.startswith('EDITBAD '): call={'name':'edit_file','arguments':{'path':text.split()[-1],'old_text':'missing text','new_text':'x'}}
                elif text.startswith('EDIT '): call={'name':'edit_file','arguments':{'path':text.split()[-1],'old_text':'approved','new_text':'edited'}}
                else: call={'name':'create_file','arguments':{'path':text.split()[-1],'content':'approved content'}}
                payload={'message':{'content':'','tool_calls':[{'function':call}]},'done':True,'prompt_eval_count':200}
            else: payload={'message':{'content':'Finished.'},'done':True,'prompt_eval_count':250}
        self.send_response(200); self.end_headers(); self.wfile.write(json.dumps(payload).encode()+b'\n')
server=ThreadingHTTPServer(('127.0.0.1',0),Ollama)
threading.Thread(target=server.serve_forever,daemon=True).start()
base='http://127.0.0.1:15081/api'
def request(path,method='GET',data=None,headers=None):
    req=urllib.request.Request(base+path,method=method,data=json.dumps(data).encode() if data is not None else None,headers={'Content-Type':'application/json',**(headers or {})})
    try:
        with urllib.request.urlopen(req,timeout=15) as r:
            raw=r.read(); return r.status,json.loads(raw) if raw and 'json' in r.headers.get('Content-Type','') else raw.decode()
    except urllib.error.HTTPError as e: return e.code,None

def upload(name,data,base_url=None,headers=None):
    boundary='leona-check-boundary'
    body=(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{name}"\r\nContent-Type: application/octet-stream\r\n\r\n').encode()+data+f'\r\n--{boundary}--\r\n'.encode()
    req=urllib.request.Request((base_url or base)+'/uploads',data=body,method='POST',headers={'Content-Type':f'multipart/form-data; boundary={boundary}',**(headers or {})})
    try:
        with urllib.request.urlopen(req,timeout=15) as r: return r.status,json.loads(r.read())
    except urllib.error.HTTPError as e: return e.code,None

def check(value,label):
    assert value,label
    print('PASS',label,flush=True)
def eventually(fn):
    for _ in range(200):
        value=fn()
        if value: return value
        time.sleep(.05)
    raise AssertionError('Timed out')
def run(text,commands=False):
    status,c=request('/conversations','POST'); assert status==200
    status,r=request('/runs','POST',{'conversationId':c['id'],'input':{'text':text,'model':'fake','think':False,'files':True,'commands':commands}})
    assert status==202,(status,r)
    return r
def state(r): return request('/runs/'+r['id'])[1]['status']
def wait(r,status): return eventually(lambda:state(r)==status)
def pending(r):
    wait(r,'awaiting_approval')
    # Read just the first event connection up to approval, then disconnect.
    with urllib.request.urlopen(base+'/runs/'+r['id']+'/events',timeout=10) as response:
        event_id=None
        for line in response:
            if line.startswith(b'id: '): event_id=line[4:].strip().decode()
            if line.startswith(b'data: '):
                data=json.loads(line[6:])
                if data['type']=='approval_required': return data,event_id
    raise AssertionError('No approval event')
with tempfile.TemporaryDirectory(prefix='leona-lifecycle-') as temp:
    env={**os.environ,'ASPNETCORE_URLS':'http://127.0.0.1:15081','Urls':'http://127.0.0.1:15081','Ollama__BaseUrl':f'http://127.0.0.1:{server.server_port}','ConnectionStrings__ChatDb':f'Data Source={temp}/runs.db','Tools__WorkspacePath':f'{temp}/workspace','Tools__UploadsPath':f'{temp}/uploads','Personal__DataPath':f'{temp}/personal','DataProtection__KeysPath':f'{temp}/keys'}
    log=open(f'{temp}/backend.log','w+')
    def start():
        process=subprocess.Popen(['dotnet',str(PROJECT/'backend/bin/Debug/net10.0/Harness.dll')],cwd=PROJECT/'backend',env=env,stdout=log,stderr=log)
        def ready():
            if process.poll() is not None:
                log.seek(0); raise RuntimeError(log.read())
            try: return request('/runs/active')[0]==200
            except OSError: return False
        eventually(ready); return process
    proc=start()
    try:
        check(request('/runs','POST',{}, {'Origin':'https://untrusted.example'})[0]==403,'cross-origin writes rejected')
        r=run('Create allowed.txt'); approval,cursor=pending(r)
        check(not pathlib.Path(temp,'workspace/allowed.txt').exists(),'file absent before approval')
        check(request('/conversations/'+str(r['conversationId']),'DELETE')[0]==409,'deletion blocked while approval pending')
        check(request('/runs','POST',{'conversationId':r['conversationId'],'input':{'text':'again','model':'fake','think':False}})[0]==409,'same-conversation concurrent run rejected')
        other=run('NO_TOOL'); wait(other,'completed')
        titles={c['id']:c['title'] for c in request('/conversations')[1]}
        check(titles[other['conversationId']]=='Finished','first exchange is named by the model')
        run_log=request('/runs/'+other['id']+'/log')[1]
        kinds=[e['type'] for e in run_log['events']]
        check('model_request' in kinds and 'usage' in kinds and 'title' in kinds and run_log['run']['status']=='completed','run log exposes model requests, usage and title')
        check(request('/conversations/'+str(other['conversationId'])+'/runs')[1][0]['id']==other['id'],'conversation lists its runs')
        history=request('/conversations/'+str(other['conversationId'])+'/messages')[1]
        user_id=next(m['id'] for m in history if m['role']=='user'); reply_id=next(m['id'] for m in history if m['role']=='assistant')
        bad=request('/runs','POST',{'conversationId':other['conversationId'],'rewindFromMessageId':reply_id,'input':{'text':'NO_TOOL','model':'fake','think':False}})
        check(bad[0]==400,'rewinding from an assistant message is rejected')
        edited=request('/runs','POST',{'conversationId':other['conversationId'],'rewindFromMessageId':user_id,'input':{'text':'NO_TOOL edited','model':'fake','think':False}})[1]
        wait(edited,'completed')
        history=request('/conversations/'+str(other['conversationId'])+'/messages')[1]
        check(len(history)==2 and history[0]['content']=='NO_TOOL edited' and history[1]['complete'],'editing a message replaces that turn')
        check(request('/settings')[1]['contextWindow']==16384,'settings default when none are saved')
        check(request('/settings','PUT',{**request('/settings')[1],'contextWindow':100})[0]==400,'invalid settings rejected')
        status,saved=request('/settings','PUT',{**request('/settings')[1],'contextWindow':8192,'customInstructions':'Be brief.'})
        check(status==200 and request('/settings')[1]['contextWindow']==8192,'settings saved')
        cid=other['conversationId']
        listed=[c for c in request('/conversations')[1] if not c['pinned']]
        check(all(c['updatedAt'].endswith('Z') for c in listed) and [c['updatedAt'] for c in listed]==sorted([c['updatedAt'] for c in listed],reverse=True),'conversations come newest first, with UTC times')
        check(request('/conversations/'+str(cid),'PATCH',{'pinned':True})[1]['pinned'],'conversation pinned')
        check(request('/conversations')[1][0]['id']==cid,'pinned conversation listed first')
        request('/conversations/'+str(cid),'PATCH',{'archived':True})
        check(all(c['id']!=cid for c in request('/conversations')[1]) and any(c['id']==cid for c in request('/conversations?archived=true')[1]),'archived conversation moves to the archive')
        again=request('/runs','POST',{'conversationId':cid,'input':{'text':'NO_TOOL again','model':'fake','think':False}})[1]; wait(again,'completed')
        check(any(c['id']==cid for c in request('/conversations')[1]),'new message restores an archived conversation')
        status,doc=upload('packing list.txt',b'Tent\nStove\n')
        check(status==200 and doc['kind']=='document' and doc['name']=='packing list.txt','text files can be uploaded')
        check(upload('page.html',b'<script>alert(1)</script>')[0]==400,'unsupported uploads are refused')
        status,c=request('/conversations','POST')
        bad=request('/runs','POST',{'conversationId':c['id'],'input':{'text':'NO_TOOL','model':'fake','think':False,'attachments':[{'id':'00000000-0000-0000-0000-000000000001'}]}})
        check(bad[0]==400,'unknown attachments are refused')
        attached=request('/runs','POST',{'conversationId':c['id'],'input':{'text':'NO_TOOL with a file','model':'fake','think':False,'attachments':[{'id':doc['id']}]}})[1]
        check(attached['input']['attachments'][0]['name']=='packing list.txt','attachment details come from the upload store'); wait(attached,'completed')
        user_message=[m for m in request('/conversations/'+str(c['id'])+'/messages')[1] if m['role']=='user'][0]
        check(user_message['attachments'][0]['id']==doc['id'] and request('/uploads/'+doc['id'])[0]==200,'attachments are kept with the message and can be opened')
        status,acct=request('/accounts','POST',{'kind':'mail','label':'Adam','settings':{'address':'adam@example.com'},'secret':'pw'})
        listed=request('/accounts')[1]
        check(status==200 and acct['hasSecret'] and 'pw' not in json.dumps(listed),'accounts are saved without exposing secrets')
        check(request('/accounts','POST',{'kind':'mail','label':'Sambo','settings':{'address':'not-an-address'},'secret':'pw'})[0]==400,'invalid accounts are refused')
        setup=request('/spotify/setup')[1]
        check(setup['redirectUris'][0]=='http://127.0.0.1:15081/spotify/callback','Spotify redirects to the loopback address')
        check(request('/spotify/connect','POST',{'label':'Spotify','clientId':'nope'})[0]==400 and request('/accounts','POST',{'kind':'spotify','label':'Spotify','settings':{'clientId':'0'*32},'secret':None})[0]==400,'Spotify needs a real Client ID and its own login')
        status,login=request('/spotify/connect','POST',{'label':'Spotify','clientId':'a'*32})
        check(status==200 and login['url'].startswith('https://accounts.spotify.com/authorize?') and 'code_challenge_method=S256' in login['url'] and 'user-top-read' in login['url'].replace('%20',' '),'connecting Spotify starts a PKCE login')
        with urllib.request.urlopen('http://127.0.0.1:15081/spotify/callback?state=forged&code=x',timeout=15) as callback: landed=callback.geturl()
        check(landed.endswith('/?spotify=expired'),'a forged Spotify callback is refused')
        check(request('/tasks/concert-radar','POST')[1]['name']=='Konsertradar','the concert radar preset can be added')
        status,task=request('/tasks','POST',{'name':'Evening check','prompt':'NO_TOOL evening summary','time':'21:00','days':31,'model':'fake','web':False,'files':False,'accounts':False})
        check(status==200 and task['schedule']=='Weekdays at 21:00' and task['nextRun'],'tasks get a schedule and next run')
        check(request('/tasks/morning-brief','POST')[1]['name']=='Morning brief','the morning brief preset can be added')
        check(request('/tasks/'+str(task['id'])+'/run','POST')[0]==202,'a task can run now')
        eventually(lambda: any(n['title']=='Evening check' for n in request('/notifications')[1]['items']))
        check(request('/notifications')[1]['unread']>=1,'a finished task notifies with its answer')
        # A task whose run waits for approval shows it in the list, so "Run now" visibly does something.
        status,needs=request('/tasks','POST',{'name':'Needs approval','prompt':'Create runnow.md','time':'21:00','days':31,'model':'fake','web':False,'files':True,'accounts':False})
        def task_view(t): return next(x for x in request('/tasks')[1] if x['id']==t['id'])
        check(request('/tasks/'+str(needs['id'])+'/run','POST')[0]==202 and eventually(lambda: task_view(needs)['runStatus']=='awaiting_approval') and task_view(task)['runStatus'] is None,'the task list shows which tasks have a run going')
        waiting_run=request('/conversations/'+str(task_view(needs)['conversationId'])+'/runs')[1][0]
        request('/runs/'+waiting_run['id']+'/cancel','POST'); wait(waiting_run,'cancelled')
        check(request('/watches','POST',{'url':'ftp://example.com','find':'Price'})[0]==400 and request('/watches','POST',{'url':'https://example.com','find':'Price','below':100,'intervalMinutes':60})[0]==200,'watches are validated and saved')
        check(len(request('/push/key')[1]['publicKey'])==87,'a push key is available for subscriptions')
        check(state(r)=='awaiting_approval','another conversation completes while first awaits approval')
        path='/runs/'+r['id']+'/approvals/'+approval['approvalId']
        check(request(path,'POST',{'approve':True})[0]==204,'approve once accepted')
        check(request(path,'POST',{'approve':True})[0]==409,'duplicate approval rejected')
        wait(r,'completed')
        check(pathlib.Path(temp,'workspace/allowed.txt').read_text()=='approved content','approved exact content executed')
        run_log=request('/runs/'+r['id']+'/log')[1]
        started=[e for e in run_log['events'] if e['type']=='tool_started']
        finished=[e for e in run_log['events'] if e['type']=='tool_finished']
        check(started and started[0]['id']==approval['approvalId'] and finished[0]['status']=='completed' and run_log['actions'][0]['status']=='completed','approval ID matches the tool step and the ledger')
        edit=run('EDIT allowed.txt'); a,_=pending(edit)
        check('- approved content' in a['preview'] and '+ edited content' in a['preview'],'edit approval carries a diff preview')
        check(pathlib.Path(temp,'workspace/allowed.txt').read_text()=='approved content','file unchanged before the edit is approved')
        request('/runs/'+edit['id']+'/approvals/'+a['approvalId'],'POST',{'approve':True}); wait(edit,'completed')
        check(pathlib.Path(temp,'workspace/allowed.txt').read_text()=='edited content','approved edit applied')
        bad=run('EDITBAD allowed.txt'); wait(bad,'completed')
        bad_log=request('/runs/'+bad['id']+'/log')[1]
        check(not any(e['type']=='approval_required' for e in bad_log['events']) and any(e['type']=='tool_finished' and e['status']=='failed' for e in bad_log['events']) and bad_log['actions'][0]['status']=='failed','impossible edits fail without asking for approval')
        cmd=run('RUN echo lifecycle-ok',commands=True); a,_=pending(cmd)
        check('$ echo lifecycle-ok' in a['preview'],'command approval shows the exact command')
        request('/runs/'+cmd['id']+'/approvals/'+a['approvalId'],'POST',{'approve':True}); wait(cmd,'completed')
        cmd_done=[e for e in request('/runs/'+cmd['id']+'/log')[1]['events'] if e['type']=='tool_finished'][0]
        check(cmd_done['status']=='completed' and 'lifecycle-ok' in cmd_done['detail']['result'],'approved command runs and returns its output')
        off=run('RUN echo should-not-run'); wait(off,'completed')
        check(not any(e['type']=='approval_required' for e in request('/runs/'+off['id']+'/log')[1]['events']),'commands are not offered unless enabled')
        status,stream=request('/runs/'+r['id']+'/events',headers={'Last-Event-ID':cursor})
        ids=[int(line[4:]) for line in stream.splitlines() if line.startswith('id: ')]
        check(status==200 and ids and all(i>int(cursor) for i in ids) and 'run_finished' in stream,'SSE replay resumes after cursor and includes completion')
        check(request('/conversations/'+str(r['conversationId'])+'/messages')[1][-1]['complete'],'completion persisted after disconnected viewer')
        check(request('/conversations/'+str(r['conversationId'])+'/messages')[1][-1]['model']=='fake','each reply records the model that wrote it')
        def unread(cid): return next(c for c in request('/conversations')[1] if c['id']==cid)['unread']
        check(unread(r['conversationId']) is True and request('/conversations/'+str(r['conversationId'])+'/read','POST')[0]==204 and unread(r['conversationId']) is False,'a new reply is unread until the chat is opened')
        check(request('/mail/manage','POST',{'ids':['2/1/INBOX'],'action':'explode'})[0]==400 and request('/mail/manage','POST',{'ids':[],'action':'delete'})[0]==400 and request('/mail/manage','POST',{'ids':['9/1/INBOX'],'action':'delete'})[0]==404,'the mail list only acts on connected mailboxes with known actions')
        current=request('/settings')[1]
        check(request('/settings','PUT',{**current,'defaultModel':'fake'})[0]==200 and request('/settings')[1]['defaultModel']=='fake','the default model is saved in Settings')
        request('/settings','PUT',current)
        rejected=run('Create denied.txt'); a,_=pending(rejected)
        check(request('/runs/'+rejected['id']+'/approvals/'+a['approvalId'],'POST',{'approve':False})[0]==204,'decline accepted')
        wait(rejected,'completed'); check(not pathlib.Path(temp,'workspace/denied.txt').exists(),'rejection never writes file; model finishes')
        check(any(e['type']=='tool_finished' and e['status']=='rejected' for e in request('/runs/'+rejected['id']+'/log')[1]['events']),'declined step reported as rejected')
        status,c=request('/conversations','POST')
        status,pages=request('/runs','POST',{'conversationId':c['id'],'input':{'text':'PAGES allowed.txt','model':'fake','think':False,'files':True,'web':True}})
        a,_=pending(pages)
        check(a['toolName']=='read_page' and a['arguments']['url']=='https://www.trusted-site.invalid/a','an unlinked page after reading files asks first')
        check(request('/runs/'+pages['id']+'/approvals/'+a['approvalId'],'POST',{'approve':True,'always':True})[0]==204,'always allow is accepted')
        wait(pages,'completed'); events=request('/runs/'+pages['id']+'/log')[1]['events']
        check(sum(e['type']=='approval_required' for e in events)==1 and sum(e['type']=='tool_finished' for e in events)==3,'later pages on a trusted site open without asking')
        sites=request('/trusted-sites')[1]
        check([s['host'] for s in sites]==['trusted-site.invalid'],'always allow saves the site without www')
        check(request('/trusted-sites/'+str(sites[0]['id']),'DELETE')[0]==204 and request('/trusted-sites')[1]==[],'trusted sites can be removed')
        check(request('/trusted-sites','POST',{'address':'not a site'})[0]==400,'invalid sites are refused')
        cancelled=run('Create cancelled.txt'); a,_=pending(cancelled)
        check(request('/runs/'+cancelled['id']+'/cancel','POST')[0]==202,'explicit cancellation accepted')
        wait(cancelled,'cancelled')
        check(request('/runs/'+cancelled['id']+'/approvals/'+a['approvalId'],'POST',{'approve':True})[0]==409 and not pathlib.Path(temp,'workspace/cancelled.txt').exists(),'cancellation invalidates approval without writing')
        crashed=run('Create crash.txt'); a,_=pending(crashed)
        proc.kill(); proc.wait(); proc=start(); wait(crashed,'interrupted')
        check(not pathlib.Path(temp,'workspace/crash.txt').exists(),'restart marks interrupted without replaying write')
        check(request('/runs/'+crashed['id']+'/approvals/'+a['approvalId'],'POST',{'approve':True})[0]==409,'restart invalidates pending approvals')
        check(request('/conversations/'+str(crashed['conversationId']),'DELETE')[0]==204 and request('/runs/'+crashed['id'])[0]==404,'terminal conversation deletion cascades to run data')
        # Phone access: a second backend listening on the LAN, reached through this machine's own LAN address
        # so requests are not loopback and must be paired.
        lan=next((a for a in subprocess.run(['hostname','-I'],capture_output=True,text=True).stdout.split() if a.count('.')==3 and not a.startswith(('127.','172.17.','172.18.'))),None)
        if lan is None:
            print('SKIP phone access checks (no LAN address)',flush=True)
        else:
            remote_env={**env,'Remote__Enabled':'true','Remote__Port':'15082','ASPNETCORE_URLS':'','Urls':''}
            remote_log=open(f'{temp}/remote.log','w+')
            remote_proc=subprocess.Popen(['dotnet',str(PROJECT/'backend/bin/Debug/net10.0/Harness.dll')],cwd=PROJECT/'backend',env=remote_env,stdout=remote_log,stderr=remote_log)
            def at(base,path,method='GET',data=None,headers=None):
                req=urllib.request.Request(base+path,method=method,data=json.dumps(data).encode() if data is not None else None,headers={'Content-Type':'application/json',**(headers or {})})
                try:
                    with urllib.request.urlopen(req,timeout=10) as r:
                        raw=r.read(); return r.status,(json.loads(raw) if raw and 'json' in r.headers.get('Content-Type','') else None),r.headers
                except urllib.error.HTTPError as e: return e.code,None,e.headers
            phone=f'http://{lan}:15082'; desk='http://127.0.0.1:15082'
            try:
                def remote_ready():
                    if remote_proc.poll() is not None:
                        remote_log.seek(0); raise RuntimeError(remote_log.read())
                    try: return at(desk,'/api/session')[0]==200
                    except OSError: return False
                eventually(remote_ready)
                check(at(phone,'/api/conversations')[0]==401,'unpaired devices cannot read conversations')
                check(upload('x.txt',b'x',phone+'/api')[0]==401,'unpaired devices cannot upload files')
                check(at(phone,'/api/session')[1]['paired'] is False and at(desk,'/api/session')[1]['local'],'session status tells devices apart from the computer')
                check(at(phone,'/api/remote/pairings','POST')[0] in (401,403),'unpaired devices cannot create pairing codes')
                check(at(phone,'/api/session',headers={'Host':'evil.example:15082'})[0]==403,'unknown host names are refused')
                pairing=at(desk,'/api/remote/pairings','POST')[1]
                check(len(pairing['code'])==6 and any(lan in link for link in pairing['links']),'the computer creates a code and a LAN link')
                check(at(phone,'/api/pair','POST',{'code':'000000' if pairing['code']!='000000' else '111111'})[0]==400,'wrong codes are refused')
                status,_,headers=at(phone,'/api/pair','POST',{'code':pairing['code']},{'Origin':phone})
                cookie=(headers.get('Set-Cookie') or '').split(';')[0]
                check(status==204 and cookie.startswith('leona_session=') and 'httponly' in headers.get('Set-Cookie','').lower(),'a correct code pairs the device with an HttpOnly cookie')
                check(at(phone,'/api/conversations',headers={'Cookie':cookie})[0]==200,'paired devices can use the API')
                check(at(phone,'/api/accounts','POST',{'kind':'home','label':'Home','settings':{'url':'http://ha.local:8123'},'secret':'token'},{'Cookie':cookie,'Origin':phone})[0]==400,'passwords are refused over plain Wi-Fi')
                check(at(phone,'/api/remote/pairings','POST',headers={'Cookie':cookie})[0]==403 and at(phone,'/api/remote',headers={'Cookie':cookie})[0]==403,'paired devices cannot manage pairing')
                check(at(desk,'/api/status')[1]['running'] and at(phone,'/api/status',headers={'Cookie':cookie})[0]==403,'only the computer sees the service status')
                check(at(phone,'/api/pair','POST',{'code':pairing['code']})[0]==400,'codes work only once')
                check(at(phone,'/api/conversations','POST',headers={'Cookie':cookie,'Origin':'http://evil.example'})[0]==403,'cross-origin writes from paired devices are refused')
                status,key,_=at(phone,'/api/siri/keys','POST',{'name':'Siri'},{'Cookie':cookie,'Origin':phone})
                check(status==200 and len(key['key'])>30,'a paired phone can make a Siri key')
                def ask(token):
                    req=urllib.request.Request(phone+'/api/ask',data='Hej Leona, NO_TOOL hur mår du?'.encode(),method='POST',headers={'Authorization':'Bearer '+token,'Content-Type':'text/plain'})
                    try:
                        with urllib.request.urlopen(req,timeout=60) as r: return r.status,r.read().decode()
                    except urllib.error.HTTPError as e: return e.code,''
                check(ask(key['key'])==(200,'Finished.'),'a Siri key asks a question and gets plain text back')
                check(at(phone,'/api/conversations',headers={'Authorization':'Bearer '+key['key']})[0]==401,'a Siri key cannot read chats')
                check(at(phone,'/api/siri/keys/'+str(key['id']),'DELETE',headers={'Cookie':cookie,'Origin':phone})[0]==204 and ask(key['key'])[0]==401,'a removed Siri key stops working')
                pairing=at(desk,'/api/remote/pairings','POST')[1]
                for _ in range(10): at(phone,'/api/pair','POST',{'code':'x'})
                check(at(phone,'/api/pair','POST',{'code':pairing['code']})[0]==400,'repeated wrong guesses cancel the pairing')
                device=at(desk,'/api/remote')[1]['devices'][0]
                check(at(desk,'/api/remote/devices/'+str(device['id']),'DELETE')[0]==204 and at(phone,'/api/conversations',headers={'Cookie':cookie})[0]==401,'removing a device revokes its session')
                # Profiles: a phone paired for the partner only sees the partner's data.
                sambo=at(desk,'/api/profiles','POST',{'name':'Sambo'})[1]
                check(sambo and not sambo['owner'] and at(desk,'/api/profiles','POST',{'name':'sambo'})[0]==400,'profiles are created with unique names')
                owner_chat=at(desk,'/api/conversations','POST')[1]
                pairing=at(desk,'/api/remote/pairings','POST',{'profileId':sambo['id']})[1]
                status,_,headers=at(phone,'/api/pair','POST',{'code':pairing['code']},{'Origin':phone})
                partner={'Cookie':(headers.get('Set-Cookie') or '').split(';')[0],'Origin':phone}
                session=at(phone,'/api/session',headers=partner)[1]
                check(status==204 and session['profile']['name']=='Sambo' and not session['profile']['owner'],'a phone paired for a profile signs in as that profile')
                check(all(c['id']!=owner_chat['id'] for c in at(phone,'/api/conversations',headers=partner)[1]) and at(phone,f"/api/conversations/{owner_chat['id']}",'PATCH',{'pinned':True},partner)[0]==404 and at(phone,f"/api/conversations/{owner_chat['id']}",'DELETE',headers=partner)[0]==404,"the partner cannot see or change the owner's chats")
                partner_chat=at(phone,'/api/conversations','POST',headers=partner)[1]
                check(all(c['id']!=partner_chat['id'] for c in at(desk,'/api/conversations')[1]),"the owner's list leaves out the partner's chats")
                status,partner_run,_=at(phone,'/api/runs','POST',{'conversationId':partner_chat['id'],'input':{'text':'NO_TOOL hi','model':'fake','think':False,'files':True,'commands':True}},partner)
                check(status==202 and not partner_run['input']['files'] and not partner_run['input']['commands'] and at(phone,'/api/folders',headers=partner)[0]==403,'file and terminal tools stay with the owner')
                check(at(desk,'/api/runs/'+partner_run['id'])[0]==404 and at(desk,'/api/runs/'+partner_run['id']+'/cancel','POST')[0]==404,'runs are private to their profile')
                status,_,headers=at(desk,f"/api/profiles/{sambo['id']}/use",'POST')
                switched={'Cookie':(headers.get('Set-Cookie') or '').split(';')[0]}
                check(status==204 and any(c['id']==partner_chat['id'] for c in at(desk,'/api/conversations',headers=switched)[1]),'the computer can switch to another profile')
                eventually(lambda: at(phone,'/api/runs/'+partner_run['id'],headers=partner)[1]['status']=='completed')
                check(at(desk,f"/api/profiles/{sambo['id']}",'DELETE')[0]==204 and at(phone,'/api/conversations',headers=partner)[0]==401 and at(desk,'/api/conversations',headers=switched)[0]==200,'removing a profile signs its devices out')
            except Exception:
                remote_log.flush(); remote_log.seek(0); print(remote_log.read()[-4000:],file=sys.stderr); raise
            finally:
                remote_proc.terminate(); remote_proc.wait(timeout=15); remote_log.close()
    except Exception:
        log.flush(); log.seek(0); print(log.read()[-6000:],file=sys.stderr); raise
    finally:
        proc.terminate(); proc.wait(timeout=15); log.close(); server.shutdown()
