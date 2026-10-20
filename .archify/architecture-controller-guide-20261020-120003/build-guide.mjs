import fs from 'node:fs';
import path from 'node:path';

const root = '.archify/architecture-controller-guide-20261020-120003';
const revision = 'd26680680bcbb94c2e3de36317bb61ca6af5b585';
const repository = {url:'https://github.com/repoagent/controller.git', revision};
const src = (path,line,end_line,label) => ({path,line,end_line,...label?{label}:{}});
const code = (file,line,end) => src(`src/RepoAgentController/${file}.cs`,line,end);
const protocol = (line,end) => src('docs/broker-protocol.md',line,end);
const operations = (line,end) => src('docs/research/2026-10-20-azure-auth-service-operations.md',line,end);
const slices = (line,end) => src('docs/research/2026-10-20-auth-delivery-slices.md',line,end);
const azure = (line,end) => src('docs/research/2026-10-20-azure-controller-deployment.md',line,end);
const card = (dot,title,...items) => ({dot,title,items});
const n = (id,type,label,sublabel,pos,size,sources,tag) => ({id,type,label,sublabel,pos,size,sources,...tag?{tag}:{}});
const edge = (id,from,to,label,variant='default',extra={})=>({id,from,to,label,variant,...extra});
const participant = (id,type,label,sublabel,sources)=>({id,type,label,sublabel,sources});
const msg = (id,from,to,y,label,variant='default',note)=>({id,from,to,y,label,variant,...note?{note}:{}});
const diagrams=[];
function write(slug,type,title,body) {
  const dir=`${root}/${slug}`; fs.mkdirSync(dir,{recursive:true});
  const candidate={schema_version:1,diagram_type:type,meta:{title,output:`${dir}/${slug}.html`,locale:'en',quality_profile:'showcase',repository},...body};
  fs.writeFileSync(`${dir}/candidate.json`,JSON.stringify(candidate,null,2)+'\n');
  diagrams.push({slug,type,title,dir,html:`${slug}/${slug}.html`});
}

write('overview','architecture','RepoAgent: the system at a glance',{
 components:[
  n('participant','external','Participant','A person in a Discussion',[40,40],[150,76],[src('GLOSSARY.md',1,3)],'EXISTS'),
  n('github','cloud','GitHub','Discussions + App bot identity',[350,40],[180,76],[code('GitHub',55,76),code('GitHub',131,140),src('docs/github-app-auth.md',8,12)],'EXISTS'),
  n('controller','backend','Controller','Observes and forwards changes',[700,40],[180,76],[code('BridgeCommand',83,115),code('Bridge',110,139)],'ON MAIN'),
  n('broker','security','Auth service','API-only credential broker',[1040,40],[180,76],[src('GLOSSARY.md',7,11),src('docs/azure-auth-deployment.md',3,9)],'PLANNED PROVIDER'),
  n('agent','backend','Native Codex','One session per Discussion',[700,280],[180,76],[code('Codex',17,27),code('Codex',38,61)],'ON MAIN'),
  n('approval','cloud','GitHub approval','User-named env + variable',[1040,280],[180,76],undefined,'APPROVED PLAN #31')
 ],
 connections:[
  edge('participate','participant','github','post / react','emphasis'),
  edge('observe','github','controller','observe versions','emphasis'),
  edge('deliver','controller','agent','native input','emphasis'),
  edge('renew','controller','broker','token request','security'),
  edge('approvalcheck','broker','approval','check marker','security'),
  edge('reply','agent','github','reply via App gh','default',{fromSide:'bottom',toSide:'bottom',via:[[790,430],[440,430]]})
 ],
 cards:[
  card('cyan','Start with the user journey','A participant writes in GitHub; the controller sends observed changes to native Codex.','Codex uses the response skill and authenticated gh wrapper to reply directly to GitHub.'),
  card('emerald','What main already contains','Polling, signed Discussion webhooks, native sessions, checkpoints and renewable App credentials.','Direct-App and broker consumer modes exist. The packaged launcher still supplies a PEM.'),
  card('amber','What is proposed','A UI-free Azure auth service supplies repository-bound tokens after reading approval directly from GitHub.','Repository-held approval replaces personal-admin continuity and broker-hosted browser approval. Provider and new installer flow remain unimplemented on this main snapshot.'),
  card('violet','Installer-owned setup and recovery','Install scripts create/reuse the GitHub Environment and write the approval variable under host GitHub authority; users choose both names.','Revoke removes the selected approval without the old device secret. Uninstall revokes then removes only the selected managed runtime, preserving data by default. Plan evidence: issue #31/#39; code links are the pinned main baseline.')
 ]
});

write('conversation','sequence','1. From a Discussion change to a native session',{
 participants:[
  participant('github','cloud','GitHub','Discussions API',[code('GitHub',78,113),code('GitHub',131,140)]),
  participant('bridge','backend','Controller','Bridge observes versions',[code('Bridge',37,61),code('Bridge',110,139)]),
  participant('state','database','Checkpoint','JSON + exclusive file lock',[code('StateStore',12,24),code('StateStore',38,42)]),
  participant('codex','backend','Codex','Native app-server session',[code('Codex',17,27),code('Codex',38,61)]),
  participant('wrapper','security','App gh','Fresh token per command',[code('AppGitHubCommand',9,22),code('AppGitHubCommand',33,45)])
 ],
 messages:[
  msg('read','bridge','github',180,'Fetch Discussion + sources','emphasis','Polling, or webhook-triggered reread.'),
  msg('versions','github','bridge',250,'Bodies, state, reactions','return','New Discussion needs a mention or explicit URL.'),
  msg('pending','bridge','state',320,'Persist pending input','default'),
  msg('input','bridge','codex',375,'Start / resume; send envelope','emphasis','One native session for the whole Discussion.'),
  msg('accepted','codex','bridge',445,'Native input accepted','return','Acceptance is not completion of agent work.'),
  msg('commit','bridge','state',515,'Save versions; clear pending','default'),
  msg('eyes','bridge','github',570,'Add or reuse EYES receipt','default'),
  msg('replytool','codex','wrapper',630,'Response skill invokes gh','emphasis','Agent work and publication run independently.'),
  msg('publish','wrapper','github',705,'Publish as the App bot','security')
 ],
 cards:[
  card('emerald','Already on main','The bridge owns EYES receipts. The native agent owns participant-facing replies.','Native conversation history stays with Codex; the JSON file records IDs and observed versions.'),
  card('rose','Uncertain delivery stops this Discussion','A missing acknowledgement leaves pending in the checkpoint. Manual recovery is required.','Other Discussions can continue. Receipt failure after acceptance does not resend accepted input.'),
  card('amber','Workspace is a separate future change','Main passes an existing workspace directory to Codex. It does not clone or refresh the repository.','Issue #42 proposes authenticated checkout, exact-root trust and safe default-branch refresh.')
 ]
});

write('authorization','architecture','2. Proposed broker architecture and trust boundaries',{
 components:[
  n('consumer','backend','Controller','Broker consumer is on main',[40,40],[185,82],[code('BrokerTokenSource',22,59),code('BrokerTokenSource',79,105)],'USER OPERATED'),
  n('service','security','UI-free auth service','Live marker + device checks',[430,40],[195,82],[src('GLOSSARY.md',7,11),src('docs/azure-auth-deployment.md',3,9)],'APPROVED PLAN #31'),
  n('github','cloud','GitHub API','Installation + repo grants',[1010,40],[210,82],[slices(24,25),code('DirectAppTokenSource',62,73)],'TOKEN ISSUER'),
  n('cache','database','Local credentials','Enrollment file + token cache',[40,300],[185,82],[protocol(7,29),code('GitHubAppCredentials',79,119)],'EXISTS CONSUMER'),
  n('store','database','Table grant ledger','Revocation + token accounting',[430,300],[195,82],[operations(41,53)],'AZURE TABLE SELECTED'),
  n('signer','security','Protected signing','Shared App key authority',[760,300],[185,82],[operations(55,59)],'KEY VAULT PROPOSED'),
  n('approval','cloud','Repository approval','Selected env / variable / binding',[1010,300],[210,82],undefined,'HOST WRITES; BROKER READS')
 ],
 boundaries:[{kind:'region',label:'Maintainer-operated Azure service: proposed runtime',wraps:['service','store','signer'],pad:22}],
 connections:[
  edge('request','consumer','service','device secret','security'),
  edge('mint','service','github','read + mint','security'),
  edge('localfiles','consumer','cache','read / publish'),
  edge('durableauth','service','store','durable checks','security'),
  edge('sign','service','signer','sign App JWT','security'),
  edge('repositorymarker','github','approval','approval marker','security')
 ],
 cards:[
  card('cyan','GitHub is the approval surface','Host setup authority creates the selected GitHub Environment and nonsecret approval variable after verifying App installation access.','The installer previews the bound controller/repository/profile. No broker login, browser ceremony or admin page; Codex sign-in remains separate.'),
  card('emerald','Current implementation boundary','Controller validation, caching and renewal exist. An operator-supplied enrollment file is an integration input.','The packaged installer still wires direct PEM mode. No hosted broker is supplied by main.'),
  card('amber','Approved policy: repository-owned authority','The exact marker and durable active grant authorize renewal; a particular person leaving does not revoke it. Names are user-selectable.','Azure Table Storage is selected. Marker absence/change closes observed enrollment permanently; fresh enrollment is required. Detailed freshness/recreation/race proof remains a gate. Plan sources: issues #30/#31/#39/#51-54.'),
  card('rose','Security boundary','The auth service runs no coding agent. Shared App signing authority stays outside user controllers.','Same-UID agent processes can read local device/token credentials; the bound scope limits their authority.')
 ]
});

write('enrollment','sequence','2a. Installer-created repository approval; no broker UI',{
 participants:[
  participant('installer','frontend','Install scripts','Chosen names + private result',[protocol(7,29),slices(40,41)]),
  participant('host','security','Operator / host gh','GitHub setup authority'),
  participant('broker','security','UI-free broker','Pending + marker verification',[protocol(35,35)]),
  participant('github','cloud','GitHub','App install + env variable',[src('docs/github-app-auth.md',8,12)]),
  participant('store','database','Durable state','Pending + approved grant',[slices(40,43),operations(27,35)])
 ],
 messages:[
  msg('start','installer','broker',180,'Start bound pending enrollment','emphasis','Repository, profile and chosen marker coordinates.'),
  msg('challenge','broker','installer',255,'Controller ID + approval material','return','Private possession proof stays separate from marker.'),
  msg('preview','installer','host',330,'Preview exact binding and writes','security'),
  msg('appcheck','host','github',380,'Ensure App installation access','security','Open GitHub installation page only if needed.'),
  msg('createenv','host','github',455,'Create / safely reuse chosen env','security','Preserve unrelated rules, secrets and variables.'),
  msg('writeapproval','host','github',530,'Write chosen approval variable','security','Nonsecret fresh enrollment binding; no device secret.'),
  msg('poll','installer','broker',605,'Poll for completion','default'),
  msg('readmarker','broker','github',655,'Read actual bound approval','security','Broker-only lookup; initiating secret is not approval.'),
  msg('approved','github','broker',730,'Matching marker + valid grants','return'),
  msg('persist','broker','store',780,'Commit device + recovery envelope','default'),
  msg('completion','broker','installer',830,'Bound private enrollment','return','Save atomically with owner-only permissions.'),
  msg('ack','installer','broker',905,'Acknowledge durable receipt','default'),
  msg('stillapproved','broker','github',955,'Verify approval still valid','security'),
  msg('activate','broker','store',1005,'Activate under current gate','default')
 ],
 cards:[
  card('amber','Approved design; not implemented on main','Issues #31/#52/#53 replace the earlier broker browser ceremony. The installer performs GitHub env/variable creation; the broker hosts APIs only.','User chooses both names. GitHub authenticates host setup; its credentials never enter the broker or controller. Readable marker metadata identifies exact scope.'),
  card('cyan','No approval by self-assertion','Possessing the pending secret or reading a public controller ID is insufficient: the broker independently reads the exact marker from GitHub.','The device has separate private proof and no environment-management permission. A person leaving does not revoke repository-held approval.'),
  card('rose','Expiry and response loss','Expired unacknowledged completion revokes the unclaimed device. Retry must not create duplicate devices.','Approved defaults: 15-minute approval window, abandoned-data cleanup within 24 hours; cleanup must work while API replicas are zero.')
 ]
});

write('renewal','sequence','2b. Proposed token issuance with the existing consumer',{
 participants:[
  participant('controller','backend','Controller','Existing v1 consumer',[code('BrokerTokenSource',79,105),code('GitHubAppCredentials',79,132),code('GitHub',55,60)]),
  participant('broker','security','UI-free broker','Live marker + device grant',[slices(25,27)]),
  participant('store','database','Durable state','Grant + issuance accounting',[operations(27,35),operations(43,53)]),
  participant('github','cloud','GitHub','Authority + actual token scope',[slices(24,27)])
 ],
 messages:[
  msg('request','controller','broker',180,'POST installation-token','security','Bearer device secret; no client scope selector.'),
  msg('reserve','broker','store',255,'Check grant; reserve issuance','default','Must coordinate with revocation and recovery.'),
  msg('grant','store','broker',330,'Active immutable binding','return'),
  msg('authority','broker','github',380,'Read approval + installation','security','Direct lookup of bound environment and variable.'),
  msg('authorized','github','broker',455,'Marker and repository grant valid','return'),
  msg('mint','broker','github',505,'Mint one repository + profile','security','Broker App JWT; exact repository_ids and permissions.'),
  msg('actualscope','github','broker',580,'Token, actual scope, expiry','return'),
  msg('recheck','broker','github',630,'Recheck approval before release','security','Reviewed freshness; GitHub and Table are not atomic.'),
  msg('record','broker','store',705,'Recheck grant; account for token','default','ETag/common gate; preserve unknown-attempt evidence.'),
  msg('commit','store','broker',780,'Authorized issuance committed','return'),
  msg('lease','broker','controller',830,'Return only if still authorized','security','Consumer validates binding, scope and expiry.'),
  msg('viewer','controller','github',905,'Startup: check actual bot viewer','security','Cache is removed if startup verification fails.'),
  msg('viewerok','github','controller',980,'Expected App bot identity','return')
 ],
 cards:[
  card('emerald','Consumer code already on main','One shared credential lifecycle serves direct-App and broker sources.','Refresh checks run every minute and acquire a new token within five minutes of expiry. Every native gh command rereads the cache.'),
  card('rose','Approval removal and outstanding tokens','A confirmed missing/changed marker closes observed enrollment permanently; failed GitHub lookup denies issuance. Recreating the old marker cannot reactivate the grant.','Fresh enrollment is required. Old GitHub tokens and unknown mint outcomes retain the separate #34 replacement barrier; removal is not an instantaneous token recall.'),
  card('amber','A behavior contract, not a database design','The diagram names required coordination; it does not claim a database transaction includes the GitHub HTTP call.','Store, partitioning, concurrency, cleanup and recovery need real-provider proof before hosted readiness.')
 ]
});

write('revocation','sequence','2c. Revoke without the lost controller; uninstall preserves data',{
 participants:[
  participant('host','frontend','Trusted host','Install script + GitHub identity'),
  participant('github','cloud','GitHub','Chosen approval variable'),
  participant('broker','security','UI-free broker','Observe removal; close grant'),
  participant('store','database','Grant ledger','Keep revocation + token evidence',[operations(43,53)]),
  participant('runtime','backend','Local runtime','Selected managed instance',[src('container/Containerfile',25,38)])
 ],
 messages:[
  msg('target','host','github',180,'Read selected marker and binding','security','Names from saved config or explicit host arguments.'),
  msg('remove','host','github',255,'Revoke: remove matching variable','security','No old controller secret or machine required.'),
  msg('notify','host','broker',330,'Request reconciliation if supported','default','Unavailable broker means closure is still pending.'),
  msg('verify','broker','github',405,'Read approval at authorization check','security'),
  msg('absent','github','broker',455,'Confirmed absent / changed marker','return'),
  msg('close','broker','store',505,'Permanently close observed grant','security','Retain known-token and ambiguous-attempt accounting.'),
  msg('status','broker','host',580,'Closure + outstanding-token status','return'),
  msg('uninstall','host','runtime',630,'Uninstall only: stop / remove runtime','default','Keep volumes, workspace/history and shared settings.')
 ],
 cards:[
  card('cyan','Two explicit installer actions','Revoke removes only the selected approval; additionally self-revoke if the private credential is still available.','Uninstall revokes first, then tears down only the selected managed runtime. User-selected environment/variable names work for both.'),
  card('rose','Different observable outcomes','GitHub marker removal, durable broker closure and old GitHub-token settlement are distinct states. No false success after a partial failure.','A copied old marker cannot reopen a revoked grant. Fresh enrollment needs new identity/credential/challenge; a GitHub read outage denies issuance without fabricating deletion.'),
  card('amber','Protected scope and open evidence','Preserve persistent data, shared environment, unrelated variables/secrets and the GitHub App installation by default.','Actual GitHub freshness, deletion/recreation and in-flight token races still need engineering review and hosted proof. This is the approved issue #31/#39/#53 design, not implemented behavior on the code baseline.')
 ]
});

write('delivery','architecture','3. Proposed distribution and the separate auth-service pipeline',{
 components:[
  n('release','backend','Controller release','Binary + skill + checksums',[40,40],[175,78],[azure(13,15),src('docs/azure-auth-deployment.md',71,75)],'PLANNED #43'),
  n('compose','backend','Image composition','Chosen base + Codex + gh',[415,40],[190,78],[azure(13,15),src('container/Containerfile',1,14)],'PLANNED #32 / #46'),
  n('registry','cloud','Image registry','Docker Hub / GHCR / ACR',[840,40],[190,78],[azure(82,92)],'PLANNED #33'),
  n('local','backend','Local Docker','Private persistent volumes',[415,290],[190,78],[src('container/Containerfile',25,38),src('container/runtime.py',44,57)],'EXISTS; NEW FLOW PLANNED'),
  n('azure','cloud','User Azure controller','Plan, storage, safe handover',[840,290],[190,78],[azure(30,55),azure(72,78)],'PLANNED #36-39'),
  n('authsource','backend','Auth service source','UI-free service/auth API',[40,540],[175,78],[src('docs/azure-auth-deployment.md',71,75)],'PROPOSED #30 / #31'),
  n('authci','backend','Auth service CI/CD','Test, build, publish, Bicep',[415,540],[190,78],[src('docs/azure-auth-deployment.md',43,47),src('docs/azure-auth-deployment.md',71,75)],'PLANNED #43 / #47'),
  n('authapp','security','Maintainer Azure','Auth service; scale to zero',[840,540],[190,78],[src('docs/azure-auth-deployment.md',3,9),operations(7,23)],'BOOTSTRAP ONLY TODAY')
 ],
 connections:[
  edge('releaseinput','release','compose','verified artifact'),
  edge('publish','compose','registry','publish digest','emphasis'),
  edge('runlocal','compose','local','local image'),
  edge('pull','registry','azure','pull digest','emphasis'),
  edge('authchange','authsource','authci','changes on main','emphasis'),
  edge('authdeploy','authci','authapp','OIDC + Bicep','security')
 ],
 cards:[
  card('cyan','Two different deployments','User controllers run native agents and need persistent workspace/history. The maintainer auth service runs no agent.','Auth-service scale-to-zero does not mean an active conversation controller should scale to zero.'),
  card('emerald','What has actually been bootstrapped','The main-branch auth workflow verifies OIDC login and an outputs-only Bicep deployment.','The selected resource group and deployment identity exist according to the runbook. No auth Container App/store/endpoint exists at this snapshot.'),
  card('amber','Installer setup and teardown','Issue #39/#53: both install scripts create the user-named GitHub Environment and user-named approval variable through host GitHub authority.','Revoke works without the lost device secret. Uninstall revokes and removes the selected runtime while preserving data and shared GitHub settings. #43/#47 retain the separate auth-service delivery/resource scope.'),
  card('rose','Before user Azure apply','Prove real storage/locking (#37), broker readiness and outstanding-token fencing (#34).','Updates stop old owners before starting replacements. Runtime secrets arrive after image build; publish and runtime pull identities are separate.')
 ]
});

fs.writeFileSync(`${root}/diagrams.json`,JSON.stringify(diagrams,null,2)+'\n');
console.log(JSON.stringify(diagrams));
