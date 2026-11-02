"""用 Node 执行真实前端函数，验证路由、身份按钮和缓存清理；视觉检查由 Lead 完成。"""

from .support import OWNER, OWNER_NAME

import json
import subprocess
from pathlib import Path

import pytest
from starlette.testclient import TestClient

from onestead.web.app import build_app

from .support import login

STATIC = Path(__file__).parents[1] / "src/onestead/web/static"

# 这里只模拟 DOM 的节点和事件，不模拟业务函数。所有页面函数均从交付的 JS 加载。
HARNESS = r'''
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
class N {
  constructor(tag = '') { this.tag = tag; this.children = []; this.attrs = {}; this.dataset = {}; this.events = {}; this.style = {}; this.hidden = false; this._text = ''; this._value = ''; }
  append(...kids) { for (let k of kids) { if (!(k instanceof N)) k = new T(String(k)); k.parent = this; this.children.push(k); } }
  replaceChildren(...kids) { this.children = []; this._text = ''; this.append(...kids); }
  get textContent() { return this._text + this.children.map(x => x.textContent).join(''); }
  set textContent(v) { this._text = String(v); this.children = []; }
  set innerHTML(v) { this.html = v; this.textContent = v; }
  get innerHTML() { return this.html || ''; }
  setAttribute(k, v) { this.attrs[k] = String(v); if (k === 'hidden') this.hidden = true; if (k === 'value') this._value = String(v); if (k.startsWith('data-')) this.dataset[k.slice(5)] = String(v); }
  getAttribute(k) { return this.attrs[k] ?? null; }
  get value() { return this.tag === 'select' ? this._value || this.children.find(x => x.attrs.selected !== undefined)?.value || this.children[0]?.value || '' : this._value; }
  set value(v) { this._value = v; }
  get childElementCount() { return this.children.length; }
  get classList() { const el = this; return { toggle(k, on) { const c = new Set((el.attrs.class || '').split(' ')); if (on) c.add(k); else c.delete(k); el.attrs.class = [...c].join(' '); }, add(k) { this.toggle(k, true); }, remove(k) { this.toggle(k, false); } }; }
  addEventListener(k, fn) { (this.events[k] ||= []).push(fn); }
  async fire(k, more = {}) { for (const fn of this.events[k] || []) await fn({ target: this, currentTarget: this, preventDefault() {}, stopPropagation() {}, ...more }); }
  dispatchEvent(e) { this.fire(e.type); }
  matches(sel) { const tag = sel.match(/^[a-z]+/)?.[0]; if (tag && tag !== this.tag) return false; for (const m of sel.matchAll(/\.([\w-]+)/g)) if (!(this.attrs.class || '').split(' ').includes(m[1])) return false; for (const m of sel.matchAll(/\[([\w-]+)(?:=["']?([^\]"']+)["']?)?\]/g)) if (!(m[1] in this.attrs) || (m[2] && this.attrs[m[1]] !== m[2])) return false; return true; }
  querySelectorAll(sel) { const parts = sel.split(' '), out = []; const walk = n => { for (const c of n.children) { if (c.matches(parts.at(-1))) { let p = c.parent, i = parts.length - 2; while (i >= 0 && p) { if (p.matches(parts[i])) i--; p = p.parent; } if (i < 0) out.push(c); } walk(c); } }; walk(this); return out; }
  querySelector(sel) { return this.querySelectorAll(sel)[0] || null; }
  remove() { if (this.parent) this.parent.children = this.parent.children.filter(x => x !== this); }
  focus() {} scrollIntoView() {}
}
class T extends N { constructor(v) { super(); this._text = v; } matches() { return false; } }
const document = new N('document'); document.body = new N('body'); document.append(document.body);
document.createElement = tag => new N(tag); document.createElementNS = (_, tag) => new N(tag); document.createTextNode = text => new T(text);
const ids = {}; for (const id of ['page', 'main', 'layer', 'open-search', 'logout', 'me-name', 'me-initial', 'me-role', 'nav-pending']) { ids[id] = new N('div'); document.body.append(ids[id]); }
document.getElementById = id => ids[id];
const nav = new N('nav'); nav.setAttribute('class', 'nav'); document.body.append(nav);
for (const route of ['home', 'activity', 'todos', 'projects', 'apps', 'agents', 'skills', 'docs', 'decisions', 'members', 'approvals']) { const a = new N('a'); a.setAttribute('data-route', route); nav.append(a); }
const window = new N('window'), location = { hash: '#/', assign(url) { this.assigned = url; } };
const timers = new Map(); let timerId = 0;
const context = vm.createContext({ Node: N, document, window, location, Intl, Date, URLSearchParams, AbortController, Event, ResizeObserver: class { observe() {} },
  setTimeout(fn) { timers.set(++timerId, fn); return timerId; }, clearTimeout(id) { timers.delete(id); }, setInterval(fn) { timers.set(++timerId, fn); return timerId; }, clearInterval(id) { timers.delete(id); },
  navigator: { languages: ['zh-CN'], clipboard: { async writeText(value) { context.copied = value; } } },
  localStorage: { data: {}, getItem(key) { return this.data[key] || null; }, setItem(key, value) { this.data[key] = value; } },
  calls: [], payloads: PAYLOADS, assert,
});
function response(data, status = 200) { return { ok: status < 400, status, headers: { get: () => 'remote' }, async json() { return structuredClone(data); } }; }
context.response = response;
context.fetch = async (path, opt) => { context.calls.push([path, opt]); if (!(path in context.payloads)) throw Error('没有接口夹具：' + path); return response(context.payloads[path]); };
for (const name of ['i18n-zh.js', 'i18n-en.js', 'i18n.js', 'values.js', 'app.js', 'todos.js', 'apps.js', 'skills.js', 'manage.js']) vm.runInContext(fs.readFileSync(STATIC + '/' + name, 'utf8'), context, { filename: name });
context.findButton = (node, text) => node.querySelectorAll('button').find(x => x.textContent === text);
(async () => { await vm.runInContext('(async () => {' + CASE + '})()', context); })().catch(e => { console.error(e); process.exitCode = 1; });
'''

CASES = {
    "登记置顶与知晓按钮双语": r'''
payloads = LOCAL; await loadMe();
const today = payloads['/api/todos'].today;
const plain = {id:'plain', title:'Ordinary task', status:'open', due:today, time:null, bucket:'today', project:null, notes:'', created:today, created_by:'Agent', repeat:null, allowed_actions:{edit:true}};
const request = { ...plain, id:'request', title:'Please sign in again', ask:'act', notice:'unnotified', context:'Session expired while preparing an order. <img onerror=x>', why:'Only you can scan the code.', if_skipped:'The order waits.', action:'Open the supplier portal and sign in.', notified_by:null, notified_at:null };
const old = { ...request, id:'older', title:'Already relayed', notice:'notified', notified_by:'Agent', notified_at:'2026-10-10 23:30', created:'2026-10-09' };
const closed = { ...request, id:'closed', title:'Resolved request', status:'done', notice:'closed' };
const long = { ...request, id:'long', title:'Long explanation', created:'2099-01-01', context:'Long context. '.repeat(60) };
payloads['/api/todos'].items = [plain, old, long, closed, request];
const normalText = todoRow(plain, today, () => {}).textContent;
location.hash = '#/todos'; await renderTodos();
const page = document.getElementById('page');
assert.equal(page.querySelector('.todo-group').textContent, '要你处理3');
assert.equal(page.querySelectorAll('.todo-row .todo-title').map(n => n.textContent).join('|'), 'Please sign in again|Long explanation|Already relayed|Ordinary task');
for (const text of ['要你动手', '登记：Agent', '未通知', '已通知：Agent，2026-10-10 23:30', '怎么来的', '为什么要你', '不做会怎样', '要你做']) assert.ok(page.textContent.includes(text), text);
const details = page.querySelectorAll('.ask-details');
assert.equal(details[0].getAttribute('open'), '');
assert.equal(details[1].getAttribute('open'), null);
assert.equal(page.querySelector('img'), null);
assert.equal(page.querySelectorAll('.todo-row').at(-1).textContent, normalText);
assert.ok(!page.textContent.includes('Resolved request'));
await selectLanguage('en');
for (const text of ['For your attention', 'Your action', 'Registered: Agent', 'Unnotified', 'How this arose', 'Why you are needed', 'If skipped', 'What you should do', 'Got it']) assert.ok(page.textContent.includes(text), text);
const previousFetch = fetch;
fetch = async (path, opt) => {
  if (path === '/api/todos/request' && opt?.method === 'POST') {
    calls.push([path,opt]);
    Object.assign(request, {notice:'notified', notified_by:DATA_VALUE.self, notified_at:'2026-10-11 00:01'});
    return response({ok:true});
  }
  return previousFetch(path,opt);
};
await findButton(page, 'Got it').fire('click');
const write = calls.find(([path]) => path === '/api/todos/request');
assert.deepEqual(JSON.parse(write[1].body), {notified:true});
await renderTodos();
assert.ok(page.textContent.includes('Notified: You, 2026-10-11 00:01'));
assert.equal(request.status, 'open');
await selectLanguage('zh');
assert.ok(page.textContent.includes('已通知：你，2026-10-11 00:01'));
''',
    "撤下技能的显示开关与计数": r'''
payloads = LOCAL; await loadMe();
const listed = { id: 'example', name: 'Available skill', description: '', installs: {}, alert: null, listed: true, in_shared: false };
const withdrawn = { ...listed, id: 'withdrawn', name: 'Withdrawn skill', listed: false };
const base = payloads['/api/skills'];
payloads['/api/skills'] = { ...base, skills: [listed] };
payloads['/api/skills?include_unlisted=1'] = { ...base, skills: [listed, withdrawn] };
await renderSkills();
const page = document.getElementById('page');
assert.ok(page.textContent.includes('Available skill'));
assert.ok(!page.textContent.includes('Withdrawn skill'));
const toggle = page.querySelector('label.toggle input');
assert.ok(toggle);
toggle.checked = true; await toggle.fire('change');
assert.ok(page.textContent.includes('Withdrawn skill'));
assert.ok(page.textContent.includes(uiText('api.unlisted')));
assert.equal(page.querySelectorAll('.stat b')[0].textContent, '1');
assert.equal(page.querySelectorAll('.stat b')[1].textContent, '0');
assert.equal(page.querySelectorAll('.stat b')[2].textContent, '0');
const again = page.querySelector('label.toggle input');
again.checked = false; await again.fire('change');
assert.ok(!page.textContent.includes('Withdrawn skill'));
''',
    "首页点状态直接改": r'''
await loadMe(); await renderProjects();
assert.equal(document.getElementById('page').querySelector('button.state-pick'), null);
payloads = LOCAL; await loadMe(); location.hash = '#/projects'; await renderProjects();
const pill = document.getElementById('page').querySelector('button.state-pick');
assert.ok(pill);
const rel = 'projects%2Falpha%2Fproject.md';
payloads['/api/source?rel=' + rel] = { rel: 'projects/alpha/project.md', version: 'v1', fields: [{ key: 'state', value: '进行中', options: ['进行中', '暂停', '已结束'] }] };
payloads['/api/source/save'] = { ok: true, message: '已保存' };
await pill.fire('click');
assert.ok(layer().querySelector('.state-options'));
const before = calls.filter(x => x[0] === '/api/source/save').length;
await layer().querySelector('button[data-state=暂停]').fire('click');
const saves = calls.filter(x => x[0] === '/api/source/save');
assert.equal(saves.length, before + 1);
assert.deepEqual(JSON.parse(saves.at(-1)[1].body), { rel: 'projects/alpha/project.md', version: 'v1', changes: { state: '暂停' } });
assert.equal(layer().querySelector('.state-options'), null);
payloads['/api/source/save'] = { ok: false, error: '被别人改过' };
await document.getElementById('page').querySelector('button.state-pick').fire('click');
payloads['/api/source?rel=' + rel] = { ...payloads['/api/source?rel=' + rel], version: 'v2' };
await layer().querySelector('button[data-state=已结束]').fire('click');
assert.ok(document.body.textContent.includes(uiText('app.164')));
''',
    "双语优先级与即时切换": r'''
assert.equal(resolveLanguage('zh', null, ['fr-FR']), 'zh');
assert.equal(resolveLanguage('zh', null, ['en-US']), 'en');
assert.equal(resolveLanguage('en', null, ['zh-TW']), 'zh');
assert.equal(resolveLanguage('en', 'en', ['zh-CN']), 'en');
assert.equal(resolveLanguage('zh', 'zh', ['en-GB']), 'zh');
assert.equal(resolveLanguage('zh', 'invalid', ['en-US']), 'en');
await loadMe(); location.hash = '#/todos'; await route();
assert.ok(document.getElementById('page').textContent.includes('我的待办'));
await selectLanguage('en');
assert.equal(localStorage.getItem(LANGUAGE_KEY), 'en');
assert.ok(document.getElementById('page').textContent.includes('My to-dos'));
assert.equal(REPEAT_NAMES.daily, 'daily');
assert.equal(STATE_TEXT.online, 'Online');
assert.equal(LEVEL_NAMES.restricted, 'Restricted');
assert.ok(!calls.some(([,opt]) => opt?.method === 'POST'));
await renderProjects(); assert.ok(document.getElementById('page').textContent.includes('In progress'));
await showProject('alpha'); assert.ok(layer().textContent.includes('阿尔法'));
assert.ok(findButton(layer(), 'Add progress'));
await selectLanguage('zh');
assert.equal(STATE_TEXT.online, '在线'); assert.equal(REPEAT_NAMES.daily, '每天');
assert.ok(document.getElementById('page').textContent.includes('待办'));
assert.equal(uiText('app.003', ['<img onerror=x>$&']), '这一页只有 <img onerror=x>$& 能用。');
''',
    "英文显示与中文协议写入": r'''
payloads = LOCAL; await loadMe(); await selectLanguage('en');
const value = { id: 'demo-web', title: '用户的中文名字', kind: '网页', listed: true, links: [], open: {}, site_people: [], site: {slug: 'demo', url: 'https://demo.example.com', status: '待发布', visibility: '指定的人', members: [], versions: []} };
S.me.site_domain = 'example.com';
const panel = sitePanel(value);
assert.ok(panel.textContent.includes('Web publishing'));
assert.ok(panel.textContent.includes('Pending'));
assert.ok(panel.textContent.includes('Selected people'));
assert.equal(panel.querySelectorAll('select')[0].children[0].value, '指定的人');
assert.equal(panel.querySelectorAll('select')[0].children[1].value, '所有远程同事');
const previousFetch = fetch; let written;
fetch = async (path, opt) => { if (opt?.method === 'POST' && path.endsWith('/site')) { written = JSON.parse(opt.body); return response({url:'https://demo.example.com',message:'已保存，可在动态里撤回。'}); } return previousFetch(path, opt); };
await findButton(panel, 'Publish').fire('click');
assert.equal(written.visibility, '指定的人'); assert.equal(written.action, 'publish');
assert.equal(value.kind, '网页'); assert.equal(value.site.status, '待发布');
assert.equal(uiSentence('在「用户的中文名字」记了一条进展'), 'Added progress to “用户的中文名字”');
assert.equal(uiSentence('更新了《用户的中文名字》的进展'), 'Updated progress in “用户的中文名字”');
assert.equal(uiSentence('非系统格式的用户说明'), '非系统格式的用户说明');
assert.equal(uiSentence('项目'), '项目');
assert.equal(uiSentence('网页：发布「用户的中文名字」'), 'Published web app “用户的中文名字”');
assert.equal(uiText('app.091', [1]), '1 item');
assert.equal(uiText('app.091', [2]), '2 items');
assert.equal(whoName({key:S.me.key,name:'你'}), 'You');
newProjectForm();
const states = layer().querySelector('select');
assert.equal(states.children[0].textContent, 'In progress');
assert.equal(states.children[0].value, '进行中');
assert.equal(states.value, '构想中');
assert.equal(layer().querySelector('textarea').value, '## 目标\n\n## 现状\n\n## 下一步\n\n## 分工\n\n## 入口\n');
''',
    "启动与所有远程页面": r'''
assert.equal(calls.length, 0);
await document.fire('DOMContentLoaded');
assert.equal(calls[0][0], '/api/me');
assert.ok(document.getElementById('page').textContent.includes(S.me.name.split(/[（(]/)[0]));
assert.equal(document.querySelectorAll('.nav a[data-route]').find(a => a.dataset.route === 'approvals').hidden, true);
assert.equal(document.getElementById('logout').hidden, false);
assert.equal(calls.find(x => x[0] === '/api/heartbeat')[1].headers['X-CSRF-Token'], S.me.csrf_token);
for (const page of ['activity', 'todos', 'projects', 'apps', 'agents', 'skills', 'docs', 'decisions', 'members']) { location.hash = '#/' + page; await route(); assert.ok(document.getElementById('page').textContent); }
assert.ok(!calls.some(x => x[0] === '/api/apps/probe'));
location.hash = '#/skills'; await route(); assert.ok(document.getElementById('page').textContent.includes('暂无授权'));
location.hash = '#/members'; await route(); assert.ok(!document.getElementById('page').textContent.includes('加一个成员'));
const before = calls.length; location.hash = '#/approvals'; await route(); assert.equal(calls.length, before); assert.ok(document.getElementById('page').textContent.includes('没有权限'));
''',
    "深链接与授权按钮": r'''
await loadMe();
assert.equal(parseRoute('#/projects/alpha').id, 'alpha');
assert.equal(parseRoute('#/docs/assets%2Fdeal.md').id, 'assets/deal.md');
assert.equal(parseRoute('#/search?q=%E9%A1%B9%E7%9B%AE%26').q, '项目&');
location.hash = '#/projects/alpha'; await route(); assert.ok(layer().textContent.includes('阿尔法'));
assert.ok(findButton(layer(), '记一条进展')); assert.ok(findButton(layer(), '记决定')); assert.ok(!findButton(layer(), '编辑'));
await findButton(layer(), '记一条进展').fire('click'); layer().querySelector('textarea').value = '这次完成了接线'; await findButton(layer(), '记下').fire('click');
assert.equal(calls.find(x => x[0] === '/api/project/alpha/status')[1].body, JSON.stringify({ text: '这次完成了接线' }));
newDecisionForm(payloads['/api/decisions']); assert.ok(!layer().textContent.includes('敏感级别')); await findButton(layer(), '记下').fire('click');
const body = JSON.parse(calls.find(x => x[0] === '/api/decision/new')[1].body); assert.equal(body.project, 'alpha'); assert.ok(!('visible_to' in body)); assert.notEqual(body.sensitivity, 'public');
location.hash = '#/docs/deal'; await route(); assert.ok(layer().textContent.includes('合作条款'));
''',
    "只读同事仍有个人工作台": r'''
await loadMe(); S.me.capabilities.write_company = false;
payloads['/api/project/alpha'].allowed_actions = { append_status: false, record_decision: false, edit: false };
payloads['/api/decisions'].projects = [];
await showProject('alpha'); assert.ok(!findButton(layer(), '记一条进展')); assert.ok(!findButton(layer(), '记决定'));
await renderDecisions(); assert.ok(!findButton(document.getElementById('page'), '记一个决定'));
await renderTodos(); assert.ok(document.getElementById('page').textContent.includes('我的待办'));
assert.equal(capability('write_own_todos'), true); assert.equal(capability('manage_own_agent_tokens'), true);
''',
    "动态摘要没有差异与撤回": fr'''
await loadMe(); await renderActivity(); assert.ok(document.getElementById('page').textContent.includes(payloads['/api/activity'].scope));
await showCommit('safe'); assert.ok(layer().textContent.includes('当前可见内容')); assert.ok(layer().textContent.includes('{OWNER_NAME}'));
assert.ok(!layer().textContent.includes('技术详情')); assert.ok(!layer().textContent.includes('撤回'));
''',
    "共享Skill详情与下载": r'''
await loadMe(); SK.data = { mode: 'shared', skills: [{ id: 'shared-tool', name: '共享工具', version: '1.2', one_line: '处理示例' }] };
location.hash = '#/skills/shared-tool'; payloads['/api/skills'] = SK.data; await route();
assert.ok(layer().textContent.includes('SKILL.md')); assert.ok(layer().textContent.includes('scripts/run.py'));
assert.equal(layer().querySelector('a').attrs.href, '/api/skills/shared-tool/download');
assert.ok(!layer().textContent.includes('各家')); assert.ok(!layer().textContent.includes('公开给'));
''',
    "Agent口令一次显示": r'''
await loadMe(); const agent = { key: 'own-agent', title: '本人的 Agent', kind: 'agent', tokens: [], allowed_actions: { issue_token: true, revoke_token: true } };
tokenDrawer(agent); await findButton(layer(), '发一个口令').fire('click');
assert.ok(layer().textContent.includes('cbx_test_once_secret')); assert.ok(layer().textContent.includes('你的 agent 的接入说明')); assert.ok(layer().textContent.includes('发证人 张三'));
assert.ok(!layer().textContent.includes('人用')); assert.equal(layer().querySelector('[data-secret]').innerHTML, '');
const value = layer().querySelector('[data-secret] .copy span'); closeLayer(); assert.equal(value.textContent, ''); assert.equal(layer().textContent, '');
assert.ok(!JSON.stringify([S, AP, TD, SK, MG]).includes('cbx_test_once_secret'));
''',
    "401停止和清掉迟到响应": r'''
await loadMe(); S.home = { old: true }; AP.apps = ['旧缓存']; TD.data = { old: true }; SK.data = { old: true }; drawer('旧抽屉', h('p', { text: '旧内容' }));
let resolve; fetch = async path => path === '/api/apps' ? new Promise(r => { resolve = r; }) : response({ error: '失效' }, 401);
const pending = api('/api/apps').then(() => false, e => e.message);
await assert.rejects(api('/api/home'), /401/); resolve(response({ apps: ['旧缓存'] })); assert.equal(await pending, '401');
assert.equal(SESSION.stopped, true); assert.equal(S.me, null); assert.equal(AP.apps, null); assert.equal(TD.data, null); assert.equal(SK.data, null);
assert.equal(layer().textContent, ''); assert.equal(location.assigned, '/login?expired=1'); const before = calls.length; beat(); assert.equal(calls.length, before);
''',
    "403显示服务端说明，GET重走当前页": r'''
await loadMe(); const me = S.me; let refreshed = 0;
fetch = async path => {
  calls.push([path]);
  if (path === '/api/me') { refreshed++; return response(me); }
  if (path === '/forbidden') return response({ error: '内容权限已变化，请重新打开页面。' }, 403);
  if (path === '/no-error') return response({}, 403);
  if (path === '/missing') return response({ error: '不应展示的对象名字' }, 404);
  if (path in payloads) return response(payloads[path]);
  return response({});
};
await assert.rejects(api('/forbidden'), /内容权限已变化，请重新打开页面。/);
assert.equal(refreshed, 1);
assert.ok(calls.some(x => x[0] === '/api/home'));
const homes = calls.filter(x => x[0] === '/api/home').length;
await assert.rejects(api('/forbidden', {}), /内容权限已变化，请重新打开页面。/);
assert.equal(calls.filter(x => x[0] === '/api/home').length, homes);
await assert.rejects(api('/no-error'), /这个操作你现在不能用/);
await assert.rejects(api('/missing'), /^Error: 找不到$/);
assert.ok(!document.body.textContent.includes('不应展示的对象名字'));
''',
    "首页搜索框点击": r'''
await loadMe(); location.hash = '#/'; await route();
const btn = document.getElementById('page').querySelector('button.hero-search');
assert.ok(btn);
await btn.fire('click');
const input = layer().querySelector('input');
assert.ok(input); assert.equal(input.value, '');
assert.ok(layer().textContent.includes('输入关键词'));
''',
    "搜索结果按类型打开": r'''
await loadMe();
payloads['/api/apps'] = { apps: [{ id: 'alpha-web', rel: 'apps/alpha-web.md', title: '阿尔法网页', one_line: '在线入口', kind: '网页', listed: true, open: { url: 'https://example.test/alpha' }, links: [], allowed_actions: { open_url: true } }] };
payloads['/api/todos'] = { today: '2026-10-12', projects: [], items: [{ id: 'buy', rel: 'todos/buy.md', title: '买牛奶', status: 'open', notes: '', allowed_actions: { edit: true } }] };
AP.apps = null; TD.data = null; SK.data = null;
await openHit({ kind: '应用', title: '阿尔法网页', rel: 'apps/alpha-web.md' });
assert.ok(layer().textContent.includes('阿尔法网页')); assert.ok(layer().textContent.includes('打开网址')); assert.ok(!layer().textContent.includes('正文'));
assert.ok(!calls.some(x => x[0] === '/api/apps/probe'));
await openHit({ kind: '待办', title: '买牛奶', rel: 'todos/buy.md' });
assert.ok(findButton(layer(), '保存')); assert.ok(layer().textContent.includes('买牛奶')); assert.ok(!layer().textContent.includes('正文'));
await openHit({ kind: 'Skill', title: '共享工具', rel: 'skills/shared-tool.md' });
assert.ok(layer().textContent.includes('SKILL.md')); assert.ok(layer().textContent.includes('下载 Skill 包'));
await openHit({ kind: '项目', project: 'alpha', rel: 'projects/alpha/project.md', title: '阿尔法' });
assert.ok(findButton(layer(), '记一条进展'));
await openHit({ kind: '资料', rel: 'assets/deal.md', title: '合作条款' });
assert.ok(layer().textContent.includes('正文'));
''',
    "没有agent不显示整理入口": r'''
await loadMe();
await renderTodos();
assert.ok(findButton(document.getElementById('page'), '让伙伴帮你整理'));
payloads['/api/agents'] = { agents: [] };
payloads['/api/todos'] = { today: '2026-10-12', projects: [], items: [] };
TD.data = null;
await renderTodos();
assert.equal(findButton(document.getElementById('page'), '让伙伴帮你整理') || null, null);
assert.ok(!document.getElementById('page').textContent.includes('让伙伴帮你整理'));
''',
    "换号调用各页登记的清理": r'''
await loadMe();
let cleared = 0;
onWorkspaceReset(() => { cleared++; });
AP.apps = ['上一人缓存']; TD.data = { old: true }; SK.data = { old: true }; MG.showArchived = true;
drawer('旧抽屉', h('p', { text: '旧内容' }));
payloads['/api/me'].key = 'third-user'; payloads['/api/me'].csrf_token = 'third-session';
await loadMe();
assert.equal(cleared, 1);
assert.equal(AP.apps, null); assert.equal(TD.data, null); assert.equal(SK.data, null); assert.equal(MG.showArchived, false);
assert.equal(layer().textContent, ''); assert.equal(S.me.key, 'third-user');
''',
    "换号不保留上个人的数据": r'''
await loadMe(); AP.apps = ['上一人缓存']; drawer('旧抽屉', h('p', { text: '旧内容' }));
payloads['/api/me'].key = 'second-user'; payloads['/api/me'].csrf_token = 'second-session'; await loadMe();
assert.equal(S.me.key, 'second-user'); assert.equal(AP.apps, null); assert.equal(layer().textContent, '');
''',
    f"本机{OWNER_NAME}管理操作保留": r'''
payloads = LOCAL; await loadMe(); assert.equal(document.getElementById('logout').hidden, true);
assert.equal(document.querySelectorAll('.nav a[data-route]').find(a => a.dataset.route === 'approvals').hidden, false);
await showProject('alpha'); assert.ok(findButton(layer(), '编辑'));
await renderDocs(); assert.ok(findButton(document.getElementById('page'), '新建资料')); assert.ok(findButton(document.getElementById('page'), '新建手册页'));
await renderMembers(); assert.ok(findButton(document.getElementById('page'), '加一个成员'));
await renderSkills(); assert.ok(findButton(document.getElementById('page'), '重新扫一遍'));
await renderApps(); assert.ok(findButton(document.getElementById('page'), '让伙伴登记一个应用')); assert.ok(calls.some(x => x[0] === '/api/apps/probe'));
AP.probe = {};
const aid = (AP.apps || []).find(a => a.open && a.open.url)?.id || (AP.apps || [])[0]?.id;
assert.ok(aid);
const probed = calls.length;
await openProjectApp(aid);
assert.ok(calls.slice(probed).some(x => x[0] === '/api/apps/probe'));
assert.ok(!layer().textContent.includes('探测中'));
await renderApprovals(); assert.ok(!document.getElementById('page').textContent.includes('没有权限'));
''',
    "能力决定表单、点击和退出": r'''
await loadMe();
assert.equal(S.me.remote, true);
assert.equal(capability('manage_company'), false);
assert.equal(capability('sign_out'), true);
assert.equal(capability('require_csrf'), true);
assert.equal(document.getElementById('logout').hidden, false);
const agent = payloads['/api/agents'].agents[0];
assert.equal(agent.allowed_actions.open_token_drawer, true);
newDecisionForm(payloads['/api/decisions']);
assert.ok(!layer().textContent.includes('敏感级别'));
assert.ok(!layer().textContent.includes('不关联项目'));
closeLayer();
S.me.capabilities.manage_company = true;
newDecisionForm(payloads['/api/decisions']);
assert.ok(layer().textContent.includes('敏感级别'));
assert.ok(layer().textContent.includes('不关联项目'));
await findButton(layer(), '记下').fire('click');
const managed = JSON.parse(calls.filter(x => x[0] === '/api/decision/new').at(-1)[1].body);
assert.ok('visible_to' in managed);
assert.equal(managed.project, '');
closeLayer();
S.me.capabilities.manage_company = false;
await renderAgents();
await document.getElementById('page').querySelector('tr.click').fire('click');
assert.ok(findButton(layer(), '发一个口令'));
closeLayer();
agent.allowed_actions.open_token_drawer = false;
agent.rel = 'people/ext-zhang-claude.md';
payloads['/api/doc?rel=' + encodeURIComponent(agent.rel)] = payloads['/api/doc?rel=assets%2Fdeal.md'];
await renderAgents();
await document.getElementById('page').querySelector('tr.click').fire('click');
assert.equal(findButton(layer(), '发一个口令') || null, null);
assert.ok(layer().textContent.includes('正文'));
closeLayer();
S.me.capabilities.require_csrf = false;
await api('/api/heartbeat', {});
assert.equal(calls.at(-1)[1].headers['X-CSRF-Token'], undefined);
S.me.capabilities.require_csrf = true;
await api('/api/heartbeat', {});
assert.equal(calls.at(-1)[1].headers['X-CSRF-Token'], S.me.csrf_token);
payloads = LOCAL;
payloads['/api/heartbeat'] = { ok: true };
payloads['/api/decision/new'] = { ok: true };
await loadMe();
assert.equal(S.me.remote, false);
assert.equal(capability('manage_company'), true);
assert.equal(capability('sign_out'), false);
assert.equal(capability('require_csrf'), false);
assert.equal(document.getElementById('logout').hidden, true);
const localAgent = payloads['/api/agents'].agents[0];
assert.equal(localAgent.allowed_actions.open_token_drawer, false);
newDecisionForm(payloads['/api/decisions']);
assert.ok(layer().textContent.includes('敏感级别'));
assert.ok(layer().textContent.includes('不关联项目'));
const beforeLocal = calls.filter(x => x[0] === '/api/decision/new').length;
await findButton(layer(), '记下').fire('click');
const localPosts = calls.filter(x => x[0] === '/api/decision/new');
assert.equal(localPosts.length, beforeLocal + 1);
const localBody = JSON.parse(localPosts.at(-1)[1].body);
assert.ok('visible_to' in localBody);
closeLayer();
payloads['/api/doc?rel=' + encodeURIComponent(localAgent.rel)] = payloads['/api/doc?rel=assets%2Fdeal.md'];
await renderAgents();
await document.getElementById('page').querySelector('tr.click').fire('click');
assert.equal(findButton(layer(), '发一个口令') || null, null);
assert.ok(layer().textContent.includes('正文'));
closeLayer();
await api('/api/heartbeat', {});
assert.equal(calls.at(-1)[1].headers['X-CSRF-Token'], undefined);
payloads['/api/me'].remote = false;
payloads['/api/me'].capabilities.sign_out = true;
await loadMe();
assert.equal(S.me.remote, false);
assert.equal(document.getElementById('logout').hidden, false);
expireSession(false, true);
assert.equal(location.assigned, '/login?expired=1');
''',
}


@pytest.mark.parametrize("case", CASES, ids=list(CASES))
def test_frontend_behavior(workspace, repo, case):  # noqa: F811
    assert login(workspace)[0].status_code == 303
    paths = ["/api/me", "/api/home", "/api/activity", "/api/todos", "/api/projects", "/api/apps", "/api/agents",
             "/api/skills", "/api/docs", "/api/decisions", "/api/members", "/api/project/alpha", "/api/doc?rel=assets%2Fdeal.md"]
    payloads = {p: workspace.get(p).json() for p in paths}
    payloads["/api/heartbeat"] = {"ok": True}
    payloads["/api/project/alpha/status"] = payloads["/api/decision/new"] = {"ok": True}
    payloads["/api/commit/safe"] = {"sha": "safe", "when": "2026-10-12T00:00:00+08:00", "safe_actor": {"key": OWNER, "name": OWNER_NAME},
        "type": "进展", "what": "更新了项目进展", "visible_objects": [{"kind": "project", "project": "alpha", "rel": "projects/alpha/project.md", "title": "阿尔法"}],
        "allowed_actions": {"view_diff": False, "revert": False}}
    payloads["/api/skills/shared-tool"] = {"id": "shared-tool", "name": "共享工具", "version": "1.2", "one_line": "处理示例", "html": "<p>步骤</p>",
        "files": [{"path": "SKILL.md", "size": 10}, {"path": "scripts/run.py", "size": 12}], "download_url": "/api/skills/shared-tool/download"}
    payloads["/api/tokens/issue"] = {"token": "cbx_test_once_secret", "record": {"label": "测试", "issued_by": "ext-zhang", "tail": "secret", "created": "2026-10-12"},
        "urls": {"mcp": "https://workspace.example.test/mcp", "web": "https://workspace.example.test/"}}
    with TestClient(build_app(repo, 48499, "test-shortcut"), base_url="http://127.0.0.1:48499", follow_redirects=False) as local:
        local.get("/open?t=test-shortcut")
        local_payloads = {p: local.get(p).json() for p in paths + ["/api/approvals", "/api/apps/probe"]}
    script = "const STATIC=" + json.dumps(str(STATIC)) + "; const PAYLOADS=" + json.dumps(payloads, ensure_ascii=False) + "; const LOCAL=" + json.dumps(local_payloads, ensure_ascii=False) + "; const CASE=" + json.dumps(CASES[case], ensure_ascii=False) + ";\n" + HARNESS
    script = script.replace("calls: [], payloads: PAYLOADS, assert,", "calls: [], payloads: PAYLOADS, LOCAL, assert,")
    completed = subprocess.run(["node", "-"], input=script, text=True, encoding="utf-8", capture_output=True, timeout=30)
    assert completed.returncode == 0, completed.stdout + completed.stderr


def test_login_expired_notice(workspace):
    notice = f"登录已失效，请重新登录；进不去请找 {OWNER_NAME}。"
    expired = workspace.get("/login?expired=1")
    assert expired.status_code == 200 and notice in expired.text
    noisy = workspace.get("/login?expired=1&token=cbx_secret")
    assert notice in noisy.text and "cbx_secret" not in noisy.text
    assert notice not in workspace.get("/login").text
    assert notice not in workspace.get("/login?expired=2").text
