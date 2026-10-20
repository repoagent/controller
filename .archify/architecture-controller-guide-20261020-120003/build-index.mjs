import fs from 'node:fs';
const root='.archify/architecture-controller-guide-20261020-120003';
const rev='d26680680bcbb94c2e3de36317bb61ca6af5b585';
const base=`https://github.com/repoagent/controller/blob/${rev}/`;
const escape=s=>s.replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
const issue=n=>`<a href="https://github.com/repoagent/controller/issues/${n}" target="_blank" rel="noopener">#${n}</a>`;
const source=(p,label=p,line)=>`<a href="${base+p}${line?'#L'+line:''}" target="_blank" rel="noopener">${label}</a>`;
const badge=(label,type='current')=>`<span class="badge ${type}">${label}</span>`;
function diagram(slug,title){
 const h=fs.readFileSync(`${root}/${slug}/${slug}.html`,'utf8');
 const vb=h.match(/<svg viewBox="([^"]+)"/)[1].split(' ').map(Number);
 return `<figure><figcaption><span>${title}</span><a href="${slug}/${slug}.html" target="_blank">Open interactive diagram ↗</a></figcaption><iframe title="${title}" src="${slug}/${slug}.html?embed=1&amp;theme=dark" style="aspect-ratio:${vb[2]}/${vb[3]};" loading="lazy"></iframe></figure>`;
}
const term=(name,definition,ref)=>`<article class="term"><h3>${name}</h3><p>${definition}</p><small>${ref}</small></article>`;
const terms=(items)=>`<div class="terms">${items.join('')}</div>`;
const arrow=(...labels)=>`<div class="steps">${labels.map((x,i)=>`${i?'<span aria-hidden="true">→</span>':''}<div>${x}</div>`).join('')}</div>`;
const pages={
 overview:`
 <div class="eyebrow">Level 0 · whole system</div><h1>GitHub conversation → persistent coding-agent session</h1>
 <p class="lead">The controller connects the two. A separate auth service is planned to grant each installed controller bounded GitHub access.</p>
 ${diagram('overview','The main user journey and the proposed auth-service branch')}
 <h2>Before the controller is authorized</h2>
 ${arrow('Start installer / pairing','Host GitHub authority','Install / reuse App','Create chosen env + variable','Save device credential')}
 <p class="micro">GitHub must grant the App access to the target repository before controller approval. Reuse an existing compatible installation; otherwise install the App or extend its selected repositories and wait for any required organization approval. <a href="https://docs.github.com/en/apps/using-github-apps/installing-a-github-app-from-a-third-party" target="_blank" rel="noopener">GitHub installation flow</a></p>
 <div class="verticals">
  <button data-page="conversations"><span class="number">01</span><strong>Conversations & sessions</strong><span>What causes work? Who replies? What survives a restart?</span>${badge('Core runtime on main')}</button>
  <button data-page="authorization"><span class="number">02</span><strong>Authentication & authorization</strong><span>Who approves a controller? Which credential goes where?</span>${badge('Consumer exists; provider planned','planned')}</button>
  <button data-page="delivery"><span class="number">03</span><strong>Installation & deployment</strong><span>What gets built? Who hosts it? How does an update work?</span>${badge('Local setup exists; new flows planned','planned')}</button>
 </div>
 <div class="callout"><strong>Two different enrollments.</strong> Discussion enrollment starts monitoring a conversation. Controller enrollment authorizes a device to renew repository-bound credentials. They are separate operations.</div>
 <h2>Who owns what?</h2>
 ${terms([
  term('Repository participants','Write comments and reactions. Receive App-bot replies in the original Discussion.',source('src/RepoAgentController/Bridge.cs','Observed sources and reply targets',67)),
  term('Repository setup authority','Uses the host GitHub identity to create or remove repository-held approval. No particular person must remain an administrator for renewal.',issue(31)),
  term('Controller operator','Runs a local or Azure controller, its native agent and private persistent runtime data.',issue(39)),
  term('RepoAgent maintainer','Hosts and funds the separate Azure auth service; controls shared App signing authority.',issue(35))
 ])}
 <div class="sourcebar">Code baseline: ${source('','main d266806')} · Approved UI-free policy: ${issue(31)} · <a href="../../docs/auth-repository-approval.md">Updated local design contract</a> · <a href="issues-snapshot.json">Saved current issue snapshot</a></div>
 `,
 conversations:`
 <div class="eyebrow">Level 1 · conversation vertical</div><h1>Observe → deliver → acknowledge → respond</h1>
 <p class="lead">One configured repository per controller; one native Codex session per enrolled Discussion.</p>
 ${diagram('conversation','Existing conversation delivery on main')}
 <div class="split"><div class="box"><h2>Two ways to notice activity</h2>${arrow('Poll GitHub','Read current sources','Compare versions')}<p>Or a signed webhook schedules a reread. Webhook mode also performs periodic full reconciliation.</p><small>${source('src/RepoAgentController/DiscussionWebhooks.cs','Webhook observation and reconciliation',66)}</small></div><div class="box warning"><h2>If acceptance is uncertain</h2>${arrow('Persist pending','Send input','Acknowledgement lost')}<p>Retain the pending marker and stop that Discussion for manual recovery. Do not automatically resend uncertain input.</p><small>${source('src/RepoAgentController/Bridge.cs','Pending marker and delivery order',116)}</small></div></div>
 <h2>Terms in this vertical</h2>
 ${terms([
  term('Discussion enrollment','A mention of the configured bot/mention user, or an explicit Discussion URL, opts a conversation into monitoring.',source('src/RepoAgentController/Bridge.cs','Enrollment gate',55)),
  term('Source','A Discussion body, comment or reply. Its content/state and participant reactions are compared with the last accepted version.',source('src/RepoAgentController/Bridge.cs','Source comparison',67)),
  term('Envelope','The structured input containing observed changes, context and reply targets sent to the native session.',source('src/RepoAgentController/Bridge.cs','Envelope construction',110)),
  term('Native session','A Codex thread reused across the entire Discussion, including its different comment threads. Native Codex retains the conversation history.',source('src/RepoAgentController/Codex.cs','Start, resume and accept',38)),
  term('Checkpoint','Local JSON mapping Discussion → session ID and accepted source versions. An adjacent exclusive file lock limits local writers.',source('src/RepoAgentController/StateStore.cs','StateStore',12)),
  term('EYES receipt','The bridge’s signal that native input was accepted. It does not mean the agent finished the requested work.',source('src/RepoAgentController/Bridge.cs','Checkpoint before receipt',125))
 ])}
 <div class="callout"><strong>Repository context is future work.</strong> Main uses an existing workspace directory. ${issue(42)} proposes cloning the actual default branch, trusting the exact repository root, and refreshing only when native turns and local changes make it safe. Frozen v1 credentials currently exclude Contents read.</div>
 `,
 authorization:`
 <div class="eyebrow">Level 1 · authorization vertical</div><h1>Repository-held approval; a UI-free auth service</h1>
 <p class="lead">The installer manages approval in GitHub. The broker independently checks it and issues bounded tokens; it has no login or admin pages. The provider and new installer actions remain planned.</p>
 <nav class="subnav" aria-label="Authorization detail"><button class="selected" data-detail="architecture">Architecture</button><button data-detail="enrollment">Setup & approval</button><button data-detail="renewal">Token renewal</button><button data-detail="revocation">Revoke / uninstall</button><button data-detail="storage">State & terminology</button></nav>
 <section data-auth="architecture">
 ${diagram('authorization','Proposed service boundary and its existing controller consumer')}
 <div class="split"><div class="box"><h2>Current direct-App path</h2>${arrow('Own-App PEM','DirectAppTokenSource','Shared cache / wrapper')}<p>Explicit alternative for an operator’s own App. The packaged launcher still selects this path.</p><small>${source('container/runtime.py','Actual packaged arguments',44)} · ${source('src/RepoAgentController/DirectAppTokenSource.cs','Direct provider',29)}</small></div><div class="box"><h2>Proposed shared-App path</h2>${arrow('Device credential','Auth service','Bound GitHub token')}<p>Consumers cannot choose a new repository or wider permissions on renewal. Content and replies go directly to GitHub.</p><small>${source('docs/broker-protocol.md','v1 contract',35)} · ${issue(30)}</small></div></div>
 <div class="callout"><strong>Approved policy:</strong> ${issue(31)} replaces the original broker browser/personal-approver model. Approval belongs to the repository. The install scripts create/reuse a GitHub Environment and write its nonsecret enrollment variable through the operator’s host authority. The user chooses both names. Controller tokens cannot modify approval; host credentials never enter the broker or controller.</div>
 <div class="split"><div class="box"><h2>GitHub Environment name</h2><p>User-selected, for example <code>repoagent</code>. Preserve existing rules, secrets and unrelated variables when reusing it. This is separate from an Azure Container Apps environment.</p></div><div class="box"><h2>Approval-variable name</h2><p>User-selected, for example <code>APPROVED_CONTROLLER</code>. The value binds the controller ID and fresh enrollment fingerprint to the exact repository/App/profile. It contains no device secret.</p></div></div>
 <p class="micro">GitHub Environment settings require adequate setup authority and supported repository-plan features. Broker reads use separately scoped GitHub permissions; controller v1 runtime profiles stay unchanged. <a href="https://docs.github.com/en/rest/actions/variables#get-an-environment-variable" target="_blank">GitHub variable API</a> · <a href="https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments" target="_blank">Environment availability</a></p>
 </section>
 <section data-auth="enrollment" hidden>
 ${diagram('enrollment','Installer-created GitHub Environment and approval variable')}
 <div class="callout"><strong>The scripts perform the setup.</strong> Both names have command-line inputs, interactive prompts and saved defaults for reruns. The host GitHub identity creates the environment/variable, not the controller. A copied public marker or pending secret alone cannot activate enrollment; the broker independently reads the exact GitHub approval and requires private possession proof.</div>
 ${arrow('Choose both names','Start bound pending grant','Preview writes','Create / reuse env','Write approval variable','Save + acknowledge')}
 <p class="micro">Expired completion without acknowledgement must revoke the unclaimed device; response-loss retries must not create duplicate enrollments. ${issue(31)}</p>
 </section>
 <section data-auth="renewal" hidden>
 ${diagram('renewal','Future provider checks with the existing v1 consumer')}
 <h2>First token and every renewal use the same request</h2>
 ${arrow('Start or ≤5 min left','Device secret → broker','Read live repo approval','App JWT → GitHub','Validate + replace cache','gh uses current token')}
 <p class="micro">GitHub issues a new installation token with a one-hour lifetime. The existing controller checks every minute and acquires again once five minutes or less remain; no GitHub refresh token is used. The device credential is the broker credential used again. <a href="https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-an-installation-access-token-for-a-github-app" target="_blank" rel="noopener">GitHub issuance contract</a> · ${source('src/RepoAgentController/GitHubAppCredentials.cs','Actual cache/renewal code',79)}</p>
 <div class="callout"><strong>Revocation blocks new tokens.</strong> It does not erase bearer tokens already copied by running children. ${issue(34)} separately requires confirmed revocation or expiry, including ambiguous issuance, before another controller takes ownership.</div>
 ${arrow('Authorization denied','Cache cleared','New wrapper launches fail')}
 <p class="micro">Main can recover on a later successful renewal. Clearing a cache cannot recall credentials already held by an existing process. ${source('docs/broker-protocol.md','Cache lifecycle',59)}</p>
 </section>
 <section data-auth="revocation" hidden>
 ${diagram('revocation','Recover and revoke from a trusted host, without the old secret')}
 <div class="split"><div class="box"><h2>revoke</h2>${arrow('Select repo / names','Verify matching marker','Remove approval','Report closure / pending')}<p>Works with current host GitHub authority and explicit or saved coordinates. The lost machine and its secret are not required. If available, self-revocation adds durable closure. Remove only the matching approval variable.</p></div><div class="box"><h2>uninstall</h2>${arrow('Revoke approval','Stop selected runtime','Remove managed runtime')}<p>Preserve persistent volumes, workspace/history, the shared GitHub Environment, other variables/secrets and the App installation by default. Local removal is not proof that outstanding remote tokens are invalid.</p></div></div>
 <div class="callout"><strong>Fresh enrollment after revocation.</strong> Once missing/changed approval closes the observed grant, restoring an old marker cannot reactivate it. A replacement needs a new controller ID, private credential and fresh approval challenge. Failed GitHub lookup denies issuance; it is not fabricated deletion evidence.</div>
 <p class="micro">Marker removal, broker closure and outstanding-token settlement are separate outcomes. GitHub reads and Table transactions are not atomic; observation latency, deletion/recreation and in-flight issuance still need explicit proof. ${issue(34)} ${issue(51)} ${issue(53)}</p>
 </section>
 <section data-auth="storage" hidden>
 <h2>Why the broker needs durable state</h2>
 ${arrow('Remember approved binding','Reject revoked devices','Coordinate issuance','Recover safely')}
 <div class="tablewrap"><table><thead><tr><th>State</th><th>What it answers</th><th>Owner / status</th></tr></thead><tbody>
 <tr><td>Repository-held approval</td><td>Is the fresh enrollment binding still present in the chosen GitHub Environment variable?</td><td>Repository · installer manages</td></tr>
 <tr><td>Enrollment grant</td><td>Which device, repository/App/installation/profile, marker coordinates and generation were approved?</td><td>Broker ledger · planned</td></tr>
 <tr><td>Revocation and activity</td><td>May this device renew now? Has its inactivity window expired?</td><td>Broker · planned</td></tr>
 <tr><td>Pending pairing / completion recovery</td><td>Can approval or delivery resume safely after response loss, without duplicate devices?</td><td>Broker · planned</td></tr>
 <tr><td>Issuance and outstanding-token evidence</td><td>Could an old controller still possess a usable token, including an ambiguous mint result?</td><td>Broker / ownership protocol · planned</td></tr>
 <tr><td>Host GitHub setup identity</td><td>May the operator create/remove the selected GitHub approval?</td><td>Installer host only; never delegated to broker/controller</td></tr>
 </tbody></table></div>
 <div class="callout"><strong>Azure Table Storage is selected.</strong> The broker retains the grant/revocation and token-accounting ledger even though GitHub holds approval. A repository partition, common conditional gate, per-entity ETags and external restore epoch protect transitions. Exact transactions, marker freshness/recreation, independent cleanup and real managed-identity/recovery evidence remain gates. ${issue(30)} ${issue(50)} ${issue(51)} ${issue(54)} ${issue(55)}</div>
 <h2>Four things people call an “installation”</h2>
 ${terms([
  term('GitHub App','The application identity and its bot account. Signing authority can mint installation tokens for its installations.',source('docs/adr/0001-keep-the-shared-app-key-out-of-controller-installations.md','Identity/security decision',3)),
  term('App installation','GitHub’s grant of that App to selected repositories and permissions. Identified by installationId.',source('docs/broker-protocol.md','Bound identifiers',16)),
  term('Controller device / enrollment','A logical controller identity authorized by a repository-held approval. Its private credential authenticates it; deviceId is not a hardware fingerprint.',issue(31)),
  term('Container revision','A deployed version of the runtime image/configuration. Replacing a revision must preserve runtime data and enforce ownership handover.',issue(38))
 ])}
 <h2>Credentials have different jobs</h2>
 <div class="tablewrap"><table><thead><tr><th>Credential</th><th>Lives where?</th><th>Authority</th></tr></thead><tbody>
 <tr><td>Shared App signing key</td><td>Maintainer signing service; protected key store proposed</td><td>Sign App JWTs; never distributed with shared-App controllers</td></tr>
 <tr><td>Device secret</td><td>Private controller enrollment file; verifier in broker state</td><td>Bound renewal and self-revocation; not a GitHub credential</td></tr>
 <tr><td>Approval marker</td><td>Selected GitHub Environment variable; nonsecret</td><td>Approves the exact enrollment when independently verified; reading it does not authenticate a controller</td></tr>
 <tr><td>Installation token</td><td>Controller’s private renewable cache and invoked processes</td><td>Approved GitHub repository/profile; replies appear as App bot</td></tr>
 <tr><td>Codex sign-in</td><td>Controller’s private native-agent credential storage</td><td>Use the coding-agent runtime; separate from GitHub approval</td></tr>
 <tr><td>Azure deployment identity</td><td>GitHub Actions → OIDC → Azure</td><td>Deploy resources in the configured group; separate from runtime data/signing rights</td></tr>
 </tbody></table></div>
 <div class="retention"><div><b>15 min</b><span>Pending approval</span></div><div><b>24 h</b><span>Abandoned-data cleanup bound</span></div><div><b>30 days</b><span>Audit retention</span></div><div><b>90 days</b><span>Inactivity → fresh approval</span></div></div>
 <p class="micro">Configurable defaults approved in ${issue(35)}. Keys, backups and outstanding-token evidence have separate safety/retention rules.</p>
 </section>
 `,
 delivery:`
 <div class="eyebrow">Level 1 · distribution and deployment vertical</div><h1>Build software once; supply credentials at runtime</h1>
 <p class="lead">The user’s controller deployment and the maintainer’s auth-service deployment are separate tracks.</p>
 ${diagram('delivery','Controller distribution versus auth-service CI/CD')}
 <div class="split"><div class="box"><h2>Current main</h2>${badge('Implemented')}<p>Fixed local container and host launchers. Native controller + Codex + gh. Persistent private runtime volumes. Explicit direct PEM arguments.</p><small>${source('container/Containerfile','Current image',1)} · ${source('container/runtime.py','Current runtime arguments',44)}</small></div><div class="box"><h2>Auth-service bootstrap</h2>${badge('Access check only','planned')}<p>Resource group and deployment identity configured. Manual workflow proves OIDC + an outputs-only Bicep deployment. Service resources and endpoint remain absent.</p><small>${source('docs/azure-auth-deployment.md','What is provisioned',5)} · ${issue(43)} ${issue(47)}</small></div></div>
 <h2>Proposed user installation journey</h2>
 ${arrow('Choose base / tools','Build credential-free image','Install / reuse GitHub App','Create chosen env + variable','Save enrollment','Run locally / plan Azure')}
 <p class="micro">Both install scripts perform actual environment/variable creation with user-selected names, not manual copy/paste. Noninteractive execution requires complete inputs and explicit apply. Revoke works without the old secret; uninstall revokes and preserves shared settings/data by default. ${issue(39)} ${issue(53)}</p>
 <h2>Proposed Azure update: deliberate downtime</h2>
 ${arrow('Preflight rollback','Quiesce old owner','Stop old replicas','Fence old tokens','Start replacement','Verify readiness')}
 <p class="micro">Stop a failed replacement before rollback. A replica count of one is not a repository-wide ownership fence. ${issue(34)} ${issue(38)}</p>
 <h2>Two different kinds of persistence</h2>
 <div class="split"><div class="box"><h3>Broker authorization state</h3><p>Device grants, revocation, pending approval and token accounting. Must survive service scale-to-zero and restart.</p><small>${issue(30)} ${issue(35)}</small></div><div class="box"><h3>Controller runtime filesystem</h3><p>Configuration, checkpoint, Codex credentials/history, workspace and tools. Azure storage must prove real locking, file replacement and runtime compatibility.</p><small>${issue(37)} · ${source('docs/research/2026-10-20-azure-controller-deployment.md','Storage contract',72)}</small></div></div>
 <h2>Issue map</h2>
 <div class="tablewrap"><table><thead><tr><th>Work</th><th>Issues</th><th>Boundary</th></tr></thead><tbody>
 <tr><td>Image manifest / base presets</td><td>${issue(32)} ${issue(46)}</td><td>Compatible pinned base, native controller, supported agent profile, optional tools; no runtime secrets</td></tr>
 <tr><td>Registry publication</td><td>${issue(33)}</td><td>Local, Docker Hub, GHCR or ACR; immutable digest; distinct publish/pull identities</td></tr>
 <tr><td>User Azure lifecycle</td><td>${issue(36)} ${issue(37)} ${issue(38)}</td><td>Read-only plan → real storage proof → controlled create/update</td></tr>
 <tr><td>Auth-service delivery</td><td>${issue(30)} ${issue(31)} ${issue(35)} ${issue(43)} ${issue(47)}</td><td>Provider + approval + durable dependencies → dedicated image → guarded Bicep and readiness</td></tr>
 <tr><td>Guided installer / recovery</td><td>${issue(39)} ${issue(52)} ${issue(53)}</td><td>Named GitHub environment/variable setup, private enrollment recovery, revoke without lost secret, safe uninstall</td></tr>
 <tr><td>Repository context</td><td>${issue(42)}</td><td>Trusted managed checkout and safe push refresh remain future runtime work</td></tr>
 </tbody></table></div>
 `
};

const html=`<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>RepoAgent · Architecture guide</title><style>
:root{color-scheme:dark;--bg:#0b111b;--panel:#121c2b;--line:#28374c;--text:#edf3fc;--muted:#a6b6cb;--cyan:#80d9ee;--amber:#f3cb83;--green:#8fe2bb}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:16px/1.55 system-ui,-apple-system,Segoe UI,sans-serif}a{color:var(--cyan);text-decoration:none}a:hover{text-decoration:underline}button{font:inherit;cursor:pointer;color:inherit}header{background:#0b111bf2;border-bottom:1px solid var(--line);position:sticky;top:0;z-index:3;backdrop-filter:blur(12px)}.top{max-width:1320px;margin:auto;padding:14px 28px;display:flex;align-items:center;gap:28px;flex-wrap:wrap}.brand{font-weight:750;letter-spacing:.02em}.brand span{color:var(--muted);font-weight:400;margin-left:8px}.nav{display:flex;gap:6px;flex-wrap:wrap}.nav button,.subnav button{border:1px solid transparent;background:transparent;padding:9px 13px;border-radius:9px;color:var(--muted)}.nav button.selected,.subnav button.selected{background:#193349;border-color:#31546b;color:var(--text)}.snapshot{margin-left:auto;font-size:12px;color:var(--muted)}main{max-width:1260px;margin:0 auto;padding:30px 28px 65px}.eyebrow{font-size:12px;text-transform:uppercase;letter-spacing:.14em;color:var(--cyan);font-weight:650}h1{font-size:clamp(25px,3vw,39px);line-height:1.18;letter-spacing:-.025em;margin:12px 0}h2{font-size:22px;line-height:1.25;margin:30px 0 14px}h3{font-size:17px;margin:0 0 6px}p{margin:8px 0 12px}.lead{max-width:980px;font-size:18px;color:var(--muted);margin:14px 0 24px}.badge{font-size:11px;line-height:1.35;font-weight:700;letter-spacing:.025em;display:inline-block;padding:5px 9px;border-radius:99px;background:#163c32;color:var(--green);width:max-content;max-width:100%}.badge.planned{background:#3c3020;color:var(--amber)}figure{margin:20px 0 26px;border:1px solid var(--line);border-radius:14px;overflow:clip;background:#101926}figcaption{display:flex;align-items:center;justify-content:space-between;gap:20px;padding:12px 18px;border-bottom:1px solid var(--line);font-size:13px;color:var(--muted)}figcaption a{white-space:nowrap;font-size:12px}iframe{display:block;width:100%;border:0;min-height:260px}.verticals{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:16px}.verticals button{display:flex;flex-direction:column;gap:10px;text-align:left;background:var(--panel);border:1px solid var(--line);border-radius:13px;padding:22px}.verticals button:hover{border-color:var(--cyan);background:#172638}.verticals strong{font-size:20px}.verticals button>span:not(.number):not(.badge){font-size:14px;color:var(--muted)}.number{color:var(--cyan);font-size:13px;font-weight:750;letter-spacing:.12em}.verticals .badge{margin-top:auto}.callout{padding:18px 22px;border-left:3px solid var(--cyan);background:#122335;border-radius:0 10px 10px 0;margin:24px 0;color:var(--muted)}.callout strong{color:var(--text)}.terms{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:15px}.term,.box{padding:20px;border:1px solid var(--line);border-radius:12px;background:var(--panel)}.term p,.box p{color:var(--muted);font-size:14px}.term small,.box small{font-size:12px}.split{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:18px;margin:22px 0}.box h2{margin-top:0;font-size:20px}.warning{border-color:#6e4d2e}.steps{display:flex;align-items:center;gap:10px;flex-wrap:wrap;margin:16px 0}.steps div{flex:1;min-width:100px;padding:12px;border:1px solid #35536d;border-radius:9px;background:#162a3c;text-align:center;font-size:13px;line-height:1.35}.steps>span{color:var(--cyan)}.micro,.sourcebar{font-size:13px;color:var(--muted)}.sourcebar{margin-top:32px;border-top:1px solid var(--line);padding-top:18px}.subnav{display:flex;flex-wrap:wrap;gap:6px;border-bottom:1px solid var(--line);padding:0 0 14px;margin:24px 0}.subnav button{border-color:var(--line);background:var(--panel);font-size:14px}.tablewrap{overflow-x:auto;border:1px solid var(--line);border-radius:12px;margin:18px 0}table{width:100%;border-collapse:collapse;font-size:14px;text-align:left}th{font-size:12px;text-transform:uppercase;letter-spacing:.06em;background:#19283a;color:var(--muted)}td,th{padding:15px 18px;border-bottom:1px solid var(--line);vertical-align:top}td{color:var(--muted)}td:first-child{font-weight:600;color:var(--text);width:26%}tr:last-child td{border-bottom:0}.retention{display:grid;grid-template-columns:repeat(4,1fr);gap:14px;margin:24px 0 10px}.retention div{padding:20px;border:1px solid var(--line);border-radius:10px;text-align:center;background:var(--panel)}.retention b{display:block;color:var(--amber);font-size:26px}.retention span{font-size:12px;color:var(--muted)}footer{max-width:1260px;margin:auto;padding:0 28px 35px;color:var(--muted);font-size:12px}footer p{border-top:1px solid var(--line);padding-top:20px}[hidden]{display:none!important}button:focus-visible,a:focus-visible{outline:2px solid var(--cyan);outline-offset:4px}@media(max-width:850px){.terms,.verticals{grid-template-columns:1fr 1fr}.top{gap:12px}.snapshot{display:none}.split{grid-template-columns:1fr}.retention{grid-template-columns:1fr 1fr}}@media(max-width:560px){main{padding:24px 16px}.top{padding:12px 16px}.brand span{display:none}.terms,.verticals{grid-template-columns:1fr}.nav button{padding:7px 9px;font-size:12px}figcaption{align-items:flex-start;flex-direction:column;gap:6px}iframe{min-height:190px}}
</style></head><body><header><div class="top"><div class="brand">RepoAgent<span>Architecture guide</span></div><nav class="nav" aria-label="Architecture levels"><button data-page="overview" class="selected">0 · Overview</button><button data-page="conversations">1 · Conversations</button><button data-page="authorization">2 · Authorization</button><button data-page="delivery">3 · Delivery</button></nav><div class="snapshot">main d266806 · 20 Oct 2026</div></div></header><main>${Object.entries(pages).map(([key,body])=>`<section data-panel="${key}" ${key==='overview'?'':'hidden'}>${body}</section>`).join('')}</main><footer><p>Read as an architecture explanation, not proof of hosted readiness. Green/“on main” identifies source-backed implementation; amber/“planned” identifies approved issue requirements. Code evidence is pinned to main ${rev}; the updated local design docs and saved issue snapshot describe the approved UI-free policy. All seven diagrams retain their standalone interactive viewers and source references. <a href="diagrams.json">Diagram index</a></p></footer><script>
function activate(page,detail){if(!document.querySelector('[data-panel="'+page+'"]'))page='overview';document.querySelectorAll('[data-panel]').forEach(el=>el.hidden=el.dataset.panel!==page);document.querySelectorAll('.nav [data-page]').forEach(el=>{const on=el.dataset.page===page;el.classList.toggle('selected',on);el.setAttribute('aria-current',on?'page':'false')});if(page==='authorization'){detail=detail||'architecture';if(!document.querySelector('[data-auth="'+detail+'"]'))detail='architecture';document.querySelectorAll('[data-auth]').forEach(el=>el.hidden=el.dataset.auth!==detail);document.querySelectorAll('[data-detail]').forEach(el=>el.classList.toggle('selected',el.dataset.detail===detail))}window.scrollTo(0,0)}
document.addEventListener('click',e=>{const p=e.target.closest('[data-page]');const d=e.target.closest('[data-detail]');if(p)location.hash=p.dataset.page;if(d)location.hash='authorization/'+d.dataset.detail});function route(){const[p,d]=location.hash.slice(1).split('/');activate(p||'overview',d)}window.addEventListener('hashchange',route);route();
</script></body></html>`;
fs.writeFileSync(`${root}/index.html`,html);
console.log(`${root}/index.html`);
