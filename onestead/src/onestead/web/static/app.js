"use strict";
// 页面里的文字一律用 textContent 放进去；只有后台过滤过的 Markdown（*_html 字段）才用 innerHTML。

const TZ = "Asia/Taipei";
const SVGNS = "http://www.w3.org/2000/svg";
const S = { home: null, preview: false, rows: null, me: null,
            filter: { q: "", who: "", project: "", type: "", range: "30" } };
const SESSION = { stopped: false, epoch: 0, view: 0, requests: new Set(), routing: false };
const ownerName = () => S.me?.owner_name || uiText("app.001");
const appName = () => S.me?.workspace_name || document.getElementById("brand-name")?.textContent || "Onestead";
const capability = name => S.me?.capabilities?.[name] === true;
const allowed = (object, action, fallback) => object.allowed_actions?.[action] ?? capability(fallback);
// 各页面自己登记换号时要清掉的状态；这里不写死别的文件里的变量。
const workspaceResets = [];
function onWorkspaceReset(fn) { workspaceResets.push(fn); }

function clearWorkspace() {
  SESSION.epoch++;
  for (const controller of SESSION.requests) controller.abort();
  SESSION.requests.clear();
  S.home = S.rows = null; S.preview = false; S.scope = "";
  S.filter = { q: "", who: "", project: "", type: "", range: "30" };
  for (const fn of workspaceResets) fn();
  document.querySelector(".toast")?.remove();
  closeLayer(); put(document.getElementById("page"));
}

function expireSession(remoteHint, expired) {
  const remote = S.me ? capability("sign_out") : !!remoteHint;
  SESSION.stopped = true; clearInterval(beat.timer); clearWorkspace(); S.me = null;
  document.querySelector(".nav").hidden = true;
  document.getElementById("open-search").hidden = true;
  document.getElementById("logout").hidden = true;
  document.getElementById("me-name").textContent = uiText("app.162");
  document.getElementById("me-initial").textContent = "";
  document.getElementById("me-role").textContent = "";
  // 主动退出仍回空白登录页；被收回、过期、停用才带固定参数，登录页只认这个参数。
  if (remote) location.assign(expired ? "/login?expired=1" : "/login");
  else gone(uiText("app.161"), uiText("app.160", [appName()]));
}

// ---------- 小工具 ----------

function h(tag, attrs, ...kids) {
  const el = document.createElement(tag);
  setAttrs(el, attrs);
  for (const kid of kids.flat(Infinity)) {
    if (kid == null || kid === false) continue;
    el.append(kid instanceof Node ? kid : document.createTextNode(String(kid)));
  }
  return el;
}

function s(tag, attrs, ...kids) {  // SVG 元素
  const el = document.createElementNS(SVGNS, tag);
  setAttrs(el, attrs);
  for (const kid of kids.flat(Infinity)) {
    if (kid == null || kid === false) continue;
    el.append(kid instanceof Node ? kid : document.createTextNode(String(kid)));
  }
  return el;
}

function setAttrs(el, attrs) {
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k === "class") el.setAttribute("class", v);
    else if (k === "text") el.textContent = v;
    else if (k.startsWith("on")) el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v === true ? "" : v);
  }
}

function put(el, ...kids) {  // replaceChildren 不会展开数组，这里先摊平
  el.replaceChildren(...kids.flat(Infinity).filter(k => k != null && k !== false));
}

function trusted(html, cls) {  // 只给后台用 nh3 过滤过的 HTML
  const el = h("div", { class: cls || "prose" });
  el.innerHTML = html || "";
  return el;
}

// 页面的安全策略不许写 style="" 属性，宽度这类动态样式只能经 CSSOM 设
function widthed(el, ratio) { el.style.width = `${(ratio * 100).toFixed(1)}%`; return el; }

function icon(d, extra) {
  return s("svg", { class: "i", viewBox: "0 0 16 16" }, s("path", { d }), extra);
}
const ICON = {
  x: "M4 4l8 8M12 4l-8 8",
  chev: "M6 3.5 10.5 8 6 12.5",
  check: "m3.6 8.3 2.8 2.8 6-6.4",
  search: "M10.4 10.4l3.1 3.1M11.6 7A4.6 4.6 0 1 1 2.4 7a4.6 4.6 0 0 1 9.2 0z",
  copy: "M5.5 5.5V3.2c0-.7.5-1.2 1.2-1.2h6.1c.7 0 1.2.5 1.2 1.2v6.1c0 .7-.5 1.2-1.2 1.2h-2.3M3.2 5.5h6.1c.7 0 1.2.5 1.2 1.2v6.1c0 .7-.5 1.2-1.2 1.2H3.2c-.7 0-1.2-.5-1.2-1.2V6.7c0-.7.5-1.2 1.2-1.2z",
  edit: "M10.5 2.8 13.2 5.5 6 12.7H3.3V10z",
};

// ---------- 时间 ----------

function parts(iso) {
  const p = {};
  for (const x of new Intl.DateTimeFormat(uiLocale(), {
    timeZone: TZ, year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit",
    weekday: "long", hourCycle: "h23",
  }).formatToParts(new Date(iso))) p[x.type] = x.value;
  return p;
}
const dayKey = iso => { const p = parts(iso); return `${p.year}-${p.month}-${p.day}`; };
const hm = iso => { const p = parts(iso); return `${p.hour}:${p.minute}`; };
function dayLabel(iso) {
  const key = dayKey(iso), now = new Date();
  if (key === dayKey(now.toISOString())) return uiText("app.068");
  if (key === dayKey(new Date(now - 864e5).toISOString())) return uiText("app.156");
  const p = parts(iso);
  return uiText("app.159", [+p.month, +p.day, p.weekday]);
}
const fullTime = iso => `${dayLabel(iso)} ${hm(iso)}`;
const md = d => uiText("app.158", [+d.slice(5, 7), +d.slice(8, 10)]);
function ago(iso) {
  const days = Math.floor((Date.now() - new Date(iso)) / 864e5);
  if (dayKey(iso) === dayKey(new Date().toISOString())) return uiText("app.157", [hm(iso)]);
  if (days <= 1) return uiText("app.156");
  if (days < 30) return uiText("app.155", [days]);
  return md(dayKey(iso));
}
function greeting(iso) {
  const hr = +parts(iso).hour;
  if (hr < 5) return uiText("app.154");
  if (hr < 11) return uiText("app.153");
  if (hr < 13) return uiText("app.152");
  if (hr < 18) return uiText("app.151");
  return uiText("app.150");
}

// ---------- 和后台说话 ----------

function toast(msg) {
  document.querySelector(".toast")?.remove();
  const t = h("div", { class: "toast", role: "status", text: msg });
  document.body.append(t);
  setTimeout(() => t.remove(), 2600);
}

function gone(title, text) {
  if (document.querySelector(".gone")) return;
  document.body.append(h("div", { class: "gone" }, h("div", { class: "card" }, h("b", { text: title }), h("span", { text }))));
  clearInterval(beat.timer);
}

async function api(path, body) {
  if (SESSION.stopped) throw new Error("401");
  const epoch = SESSION.epoch, view = SESSION.view, controller = new AbortController();
  SESSION.requests.add(controller);
  const opt = body === undefined ? {} : {
    method: "POST", body: JSON.stringify(body),
    headers: { "Content-Type": "application/json", "X-Requested-By": "company-base" },
  };
  opt.signal = controller.signal; opt.cache = "no-store";
  if (body !== undefined && capability("require_csrf")) opt.headers["X-CSRF-Token"] = S.me.csrf_token;
  let r;
  try {
    r = await fetch(path, opt);
    if (r.status === 401) { expireSession(r.headers.get("X-Workspace-Context") === "remote", true); throw new Error("401"); }
    const data = await r.json().catch(() => ({}));
    if (SESSION.stopped || epoch !== SESSION.epoch || (body === undefined && path !== "/api/me" && view !== SESSION.view)) throw new Error("401");
    data._status = r.status;
    if (r.status === 403) {
      SESSION.requests.delete(controller);
      if (path !== "/api/me") await loadMe();
      const msg = data.error || uiText("app.149");
      const follow = body === undefined && path !== "/api/me" && !SESSION.routing && !SESSION.stopped;
      if (!SESSION.stopped && !SESSION.routing) toast(msg);
      if (follow) {
        SESSION.routing = true;
        route().finally(() => { SESSION.routing = false; });
      }
      throw new Error(msg);
    }
    if (r.status === 404) throw new Error(uiText("app.004"));
    if (!r.ok && r.status !== 409) throw new Error(data.error || uiText("app.148", [r.status]));
    return data;
  } catch (e) {
    if (controller.signal.aborted || SESSION.stopped) throw new Error("401");
    if (!r) {
      if (capability("sign_out")) toast(uiText("app.147"));
      else gone(uiText("app.146"), uiText("app.145", [appName()]));
    }
    throw e;
  } finally {
    SESSION.requests.delete(controller);
  }
}

async function beat() {
  if (!S.me || SESSION.stopped || !await loadMe()) return;
  await api("/api/heartbeat", {}).catch(() => {});
}

// ---------- 共用的小块 ----------

const whoName = who => who.key === S.me?.key && who.name === DATA_VALUE.self ? uiText("label.you") : !who.key ? uiLabel(who.name) : who.name;
function avatar(who, cls) {
  const name = whoName(who) || who.display || "?";
  return h("span", { class: "avatar" + (who.key === S.me?.key ? " self" : "") + (cls ? " " + cls : ""),
                     text: name.slice(0, 1), "aria-hidden": "true" });
}
const whoChip = who => h("span", { class: "who" }, avatar(who), whoName(who));

// 动态的安全摘要行（改到别人的个人内容、或路径无法确认的提交）只有 safe_actor、visible_objects，
// 没有原始说明和差异；补成列表、详情能显示的样子，不能撤回。
function commitRow(c) {
  if (c.who) return c;
  const objects = c.visible_objects || [], ids = [...new Set(objects.map(o => o.project).filter(Boolean))];
  const project = ids.length === 1 ? { id: ids[0], name: objects.find(o => o.project_name)?.project_name || objects.find(o => o.kind === "project")?.title || ids[0] } : null;
  return { ...c, who: c.safe_actor || { key: null, name: uiText("app.144") }, excerpt: null, subject: "", project,
           files: (c.visible_objects || []).map(o => ({ label: o.title })), diff: [], can_revert: false };
}

function statePill(state, project) {
  const cls = "pill" + (state === DATA_VALUE.active ? " live" : ""), text = uiLabel(state) || uiText("app.143");
  // 只有本机主人能点（和“管理”页的改文档同一个权限）；其他人照旧只显示。
  if (!project?.id || !allowed(project, "edit", "edit_sources")) return h("span", { class: cls }, text);
  return h("button", { class: cls + " state-pick", type: "button", title: uiText("app.163"), "aria-label": uiText("app.163"),
                       onclick: e => { e.stopPropagation(); return pickState(project.id); } }, text);
}

// 首页、项目列表上点状态：读 /api/source 的 state 选项，带 version 存回 /api/source/save。
async function pickState(pid) {
  const rel = `projects/${pid}/project.md`, url = `/api/source?rel=${encodeURIComponent(rel)}`;
  let s;
  try { s = await api(url); } catch (e) { if (e.message !== "401") toast(e.message); return; }
  const field = (s.fields || []).find(f => f.key === "state");
  if (!field) return toast(uiText("app.149"));
  const choose = async value => {
    if (value === field.value) return closeLayer();
    try {
      const r = await api("/api/source/save", { rel, version: s.version, changes: { state: value } });
      if (r.error || r.ok === false) throw new Error(r.error || uiText("app.164"));
    } catch (e) {
      if (e.message === "401") return;
      let now = null;
      try { now = await api(url); } catch { /* 读不到就按原错误提示 */ }
      toast(now && now.version !== s.version ? uiText("app.164") : e.message);
      return;
    }
    closeLayer(); toast(uiText("app.165", [uiLabel(value)]));
    S.rows = null; route();
  };
  openLayer(h("div", { class: "modal", role: "dialog", "aria-label": uiText("app.163") },
    h("div", { class: "modal-head" }, h("div", {}, h("h3", { text: uiText("app.163") })),
      h("button", { class: "btn ghost x", type: "button", "aria-label": uiText("app.041"), onclick: closeLayer }, icon(ICON.x))),
    h("div", { class: "form-btns left state-options" }, (field.options || []).filter(Boolean).map(o =>
      h("button", { class: "btn" + (o === field.value ? " primary" : ""), type: "button", "data-state": o, onclick: () => choose(o) }, uiLabel(o))))), "modal");
}

function secHead(title, n, more) {
  return h("div", { class: "sec-head" }, h("h2", { text: uiLabel(title) }),
    n != null ? h("span", { class: "n", text: n }) : null, more || null);
}

function empty(title, text) {
  return h("div", { class: "empty" }, h("span", { class: "ok" }, icon(ICON.check)),
    h("b", { text: title }), h("span", { text }));
}

function reminders(x) {
  const out = [];
  if (x.problem) out.push(h("div", { class: "box", text: uiText("app.142", [x.problem]) }));
  if (x.sensitivity === "restricted") out.push(h("div", { class: "box accent", text: uiText("app.141") }));
  if (x.access_note) out.push(h("div", { class: "box accent", text: uiText("app.140", [x.access_note]) }));
  return out.length ? h("div", { style: null, class: "stack" }, out) : null;
}

// ---------- 浮层：抽屉、弹窗 ----------

const layer = () => document.getElementById("layer");
let layerVersion = 0;
function closeLayer() {
  layerVersion++;
  for (const value of layer().querySelectorAll("[data-secret] .copy span")) value.textContent = "";
  layer().textContent = "";
}
function openLayer(node, kind) {
  layerVersion++;
  put(layer(), h("div", { class: "scrim", onclick: closeLayer }),
    kind === "modal" ? h("div", { class: "modal-wrap", onclick: e => { if (e.target === e.currentTarget) closeLayer(); } }, node) : node);
  node.querySelector("[autofocus]")?.focus();
}

function drawer(kindText, ...body) {
  const open = layer().querySelector(".drawer");
  if (open) {  // 已经开着抽屉（比如从“读取中”换成内容、从改动跳到项目），只换里面，不重播滑入
    layerVersion++;
    open.querySelector(".kind").textContent = kindText;
    open.setAttribute("aria-label", kindText);
    put(open.querySelector(".drawer-body"), body);
    open.scrollTop = 0;
    return open;
  }
  const el = h("aside", { class: "drawer", role: "dialog", "aria-label": kindText },
    h("div", { class: "drawer-head" }, h("span", { class: "kind", text: kindText }),
      h("button", { class: "btn ghost x", type: "button", "aria-label": uiText("app.041"), onclick: closeLayer }, icon(ICON.x))),
    h("div", { class: "drawer-body" }, body));
  openLayer(el);
  return el;
}
function drawerLoading(kindText) { drawer(kindText, h("p", { class: "muted", text: uiText("app.139") })); }
function drawerError(kindText, e) { if (e.message !== "401") drawer(kindText, h("p", { class: "muted", text: e.message })); }

async function showCommit(sha) {
  drawerLoading(uiText("app.138"));
  const ticket = layerVersion;
  let c;
  try { c = commitRow(await api(`/api/commit/${sha}`)); } catch (e) { return drawerError(uiText("app.138"), e); }
  if (ticket !== layerVersion) return;
  if (c.visible_objects) {
    drawer(uiText("app.134", [uiLabel(c.type)]), h("h2", { text: uiSentence(c.what) }),
      h("div", { class: "meta-line" }, whoChip(c.who), fullTime(c.when)),
      h("div", { class: "block-title", text: uiText("app.137") }),
      c.visible_objects.map(o => h("button", { class: "btn", type: "button", onclick: () => o.project ? showProject(o.project) : showDoc(o.rel) }, o.title)));
    return;
  }
  const zone = h("div", { class: "danger-zone" });
  const files = c.diff.map(f => h("div", { class: "diff" },
    h("div", { class: "diff-head" }, uiSentence(f.label),
      f.status === "added" ? h("span", { class: "pill", text: uiText("app.136") }) : null,
      f.status === "deleted" ? h("span", { class: "pill red", text: uiText("app.135") }) : null),
    h("div", { class: "diff-body" }, f.lines.map(l => h("div", { class: "ln " + l.t, text: l.s })))));
  drawer(uiText("app.134", [uiLabel(c.type)]),
    h("h2", { text: uiSentence(c.what) }),
    h("div", { class: "meta-line" }, whoChip(c.who), h("span", { class: "faint", text: "·" }), fullTime(c.when),
      c.project ? [h("span", { class: "faint", text: "·" }),
        h("a", { href: "#", onclick: e => { e.preventDefault(); showProject(c.project.id); }, text: c.project.name })] : null),
    c.reverted_by ? h("div", { class: "box", style: null, text: uiText("app.133", [c.reverted_by]) }) : null,
    c.reverts ? h("div", { class: "box", text: uiText("app.132", [uiSentence(c.reverts_what) || c.reverts]) }) : null,
    c.added_html ? [h("div", { class: "block-title", text: uiText("app.131") }), h("div", { class: "box" }, trusted(c.added_html))]
      : c.body ? [h("div", { class: "block-title", text: uiText("app.130") }), h("p", { class: "note", text: c.body })] : null,
    h("div", { class: "block-title", text: uiText("app.129", [c.files.length]) }),
    h("div", { class: "note" }, c.files.map(f => h("div", { text: uiSentence(f.label) }))),
    h("details", { class: "tech" }, h("summary", { text: uiText("app.128") }),
      h("span", { class: "mono", text: uiText("app.127", [c.sha]) }), h("span", { class: "mono", text: c.subject }), files),
    zone);
  if (c.is_root) put(zone, h("p", { class: "note faint", text: uiText("app.126") }));
  else if (!c.reverted_by && c.can_revert !== false && allowed(c, "revert", "revert_commits")) put(zone, h("button", { class: "link-danger", type: "button", onclick: () => confirmRevert(c, zone) }, uiText("app.125")));
  else zone.remove();
}

function confirmRevert(c, zone) {
  const reason = h("input", { class: "input", placeholder: uiText("app.124"), maxlength: "200" });
  const msg = h("div");
  const go = h("button", { class: "btn danger", type: "button" }, uiText("app.123"));
  const box = h("div", { class: "confirm" },
    h("p", { text: uiText("app.122") }),
    reason, msg,
    h("div", { class: "btns" }, h("button", { class: "btn", type: "button", onclick: () => box.remove() }, uiText("app.121")), go));
  go.addEventListener("click", async () => {
    go.disabled = true;
    try {
      const r = await api("/api/revert", { sha: c.sha, reason: reason.value });
      if (r.ok) {
        toast(uiText("app.012"));
        S.rows = null; S.home = null;
        closeLayer(); route(); return;
      }
      put(msg, h("p", { text: uiText("app.120") }),
        h("ul", { class: "blockers" }, r.blockers.map(b => h("li", { onclick: () => showCommit(b.sha) },
          `${b.who} · ${uiSentence(b.what)} (${fullTime(b.when)})`))));
    } catch (e) {
      if (e.message !== "401") put(msg, h("p", { class: "muted", text: e.message }));
    }
    go.disabled = false;
  });
  zone.querySelector(".confirm")?.remove();
  zone.append(box);
  reason.focus();
}

async function showProject(pid) {
  drawerLoading(uiText("app.008"));
  const ticket = layerVersion;
  let p;
  try { p = await api(`/api/project/${encodeURIComponent(pid)}`); } catch (e) { return drawerError(uiText("app.008"), e); }
  if (ticket !== layerVersion) return;
  drawer(uiText("app.008"),
    h("div", { class: "drawer-title" }, h("h2", { text: p.title || p.name }), allowed(p, "edit", "edit_sources") ? editButton(`projects/${pid}/project.md`) : null),
    h("div", { class: "form-btns left" },
      allowed(p, "append_status", "write_company") ? h("button", { class: "btn primary", type: "button", onclick: () => projectStatusForm(p) }, uiText("app.119")) : null,
      allowed(p, "record_decision", "write_company") ? h("button", { class: "btn", type: "button", onclick: async () => {
        try { newDecisionForm(await api("/api/decisions"), p.id); } catch (e) { if (e.message !== "401") toast(e.message); }
      } }, uiText("app.118")) : null),
    h("div", { class: "meta-line" }, statePill(p.state, p),
      p.summary_as_of ? h("span", { text: uiText("app.117", [md(p.summary_as_of)]) }) : h("span", { text: uiText("app.116") }),
      p.stale ? h("span", { class: "pill red", text: uiText("app.083") }) : null),
    reminders(p),
    p.goal ? [h("div", { class: "block-title", text: uiText("app.115") }), h("div", { class: "box", text: p.goal })] : null,
    p.roles.length ? [h("div", { class: "block-title", text: uiText("app.114") }),
      h("dl", { class: "kv" }, p.roles.map(r => [h("dt", { text: r.role }),
        h("dd", {}, r.who, r.key ? h("span", { class: "pill", style: null, text: uiText("app.113") }) : null,
          r.since ? h("span", { class: "faint small", text: uiText("app.112", [md(r.since)]) }) : null)]))] : null,
    (p.apps || []).length ? [h("div", { class: "block-title", text: uiText("app.111") }),
      h("div", { class: "card" }, p.apps.map(a => h("button", { class: "list-row", type: "button", onclick: () => openProjectApp(a.id) },
        appTile(a),
        h("div", { class: "grow" }, h("div", { class: "cell-name", text: a.title }),
          a.one_line ? h("div", { class: "cell-sub", text: a.one_line }) : null),
        h("span", { class: "pill", text: uiLabel(a.kind) || "" }),
        a.listed === false ? h("span", { class: "pill red", text: uiText("app.110") }) : null)))] : null,
    p.authority ? [h("div", { class: "block-title", text: uiText("app.109") }),
      h("p", { class: "note" }, p.authority.label || "", p.authority.path ? h("code", { text: ` ${p.authority.path}` }) : null)] : null,
    h("div", { class: "block-title", text: p.status_total ? uiText("app.108", [p.status_total]) : uiText("app.107") }),
    p.status.length ? h("div", {}, p.status.map(e => h("div", { class: "entry" },
      h("div", { class: "head", text: e.head }), trusted(e.html)))) : h("p", { class: "note faint", text: uiText("app.106") }),
    h("div", { class: "block-title", text: uiText("app.105") }),
    p.newer_status ? h("p", { class: "note faint", text: uiText("app.104", [p.newer_status]) }) : null,
    trusted(p.body_html));
}

async function showDoc(rel) {
  drawerLoading(uiText("app.103"));
  const ticket = layerVersion;
  let d;
  try { d = await api(`/api/doc?rel=${encodeURIComponent(rel)}`); } catch (e) { return drawerError(uiText("app.103"), e); }
  if (ticket !== layerVersion) return;
  drawer(uiLabel(d.kind),
    h("div", { class: "drawer-title" }, h("h2", { text: d.title }), editButton(d.rel)),
    d.facts.length ? h("dl", { class: "kv", style: null }, d.facts.map(f => [h("dt", { text: uiLabel(f.k) }), h("dd", { text: f.k === I18N_ZH["manage.059"] ? uiLabel(f.v) : f.v })])) : null,
    reminders(d),
    h("div", { class: "block-title", text: uiText("app.102") }), trusted(d.html),
    h("details", { class: "tech" }, h("summary", { text: uiText("app.101") }), h("span", { class: "mono", text: d.rel })));
}

function fileId(rel, folder) {
  const name = String(rel || "");
  const prefix = folder + "/";
  if (!name.startsWith(prefix) || !name.endsWith(".md")) return "";
  const id = name.slice(prefix.length, -3);
  return id && !id.includes("/") ? id : "";
}

async function openHit(x) {
  if (x.kind === DATA_VALUE.project && x.project) return showProject(x.project);
  if (x.kind === DATA_VALUE.app) return openAppFromSearch(x);
  if (x.kind === DATA_VALUE.todo) return openTodoFromSearch(x);
  if (x.kind === "Skill") {
    const id = fileId(x.rel, "skills");
    if (id) return showSkillById(id);
  }
  return showDoc(x.rel);
}

async function openAppFromSearch(x) {
  try {
    const apps = AP.apps || await loadApps();
    const found = apps.find(a => a.rel === x.rel) || apps.find(a => a.id && a.id === fileId(x.rel, "apps"));
    if (found) return openProjectApp(found.id);
    const id = fileId(x.rel, "apps");
    if (id) return openProjectApp(id);
    toast(uiText("app.100"));
  } catch (e) {
    if (e.message !== "401") toast(e.message);
  }
}

async function openTodoFromSearch(x) {
  try {
    if (!TD.data) await loadTodos();
    const items = TD.data?.items || [];
    const todo = items.find(item => item.rel === x.rel) || items.find(item => item.id && item.id === fileId(x.rel, "todos"));
    if (todo) return editTodo(todo, () => route());
    toast(uiText("app.099"));
  } catch (e) {
    if (e.message !== "401") toast(e.message);
  }
}

// ---------- 搜索 ----------

function openSearch(query = "") {
  if (!S.me || SESSION.stopped) return;
  const input = h("input", { placeholder: uiText("app.098"), autofocus: true, "aria-label": uiText("app.095") });
  const results = h("div", { class: "results" }, h("div", { class: "hint", text: uiText("app.096") }));
  let hits = [], on = 0, timer = null, seq = 0;
  const draw = () => put(results, hits.length ? hits.map((x, i) => h("button", {
    class: "result" + (i === on ? " on" : ""), type: "button", onclick: () => openHit(x),
  }, h("div", { class: "r1" }, h("span", { class: "pill", text: uiLabel(x.kind) }), x.title),
     x.snippet ? h("div", { class: "r2", text: x.snippet }) : null))
    : h("div", { class: "hint", text: input.value.trim() ? uiText("app.097") : uiText("app.096") }));
  input.addEventListener("input", () => {
    clearTimeout(timer);
    timer = setTimeout(async () => {
      const mine = ++seq, q = input.value.trim();
      const r = q ? await api(`/api/search?q=${encodeURIComponent(q)}`).catch(() => ({ hits: [] })) : { hits: [] };
      if (mine !== seq || SESSION.stopped) return;
      hits = r.hits; on = 0; draw();
    }, 140);
  });
  input.addEventListener("keydown", e => {
    if (e.key === "ArrowDown" && hits.length) { on = (on + 1) % hits.length; draw(); e.preventDefault(); }
    if (e.key === "ArrowUp" && hits.length) { on = (on - 1 + hits.length) % hits.length; draw(); e.preventDefault(); }
    if (e.key === "Enter" && hits[on]) openHit(hits[on]);
  });
  openLayer(h("div", { class: "modal", role: "dialog", "aria-label": uiText("app.095") },
    h("div", { class: "search-box" }, icon(ICON.search), input), results), "modal");
  if (query) { input.value = query.slice(0, 100); input.dispatchEvent(new Event("input")); }
}

// ---------- 首页 ----------

const MODULES = {
  attention(m) {
    const items = m.data.items;
    const pill = { [DATA_VALUE.approval]: "pill accent", [DATA_VALUE.outdated]: "pill", [DATA_VALUE.frontmatter]: "pill red" };
    return [secHead(m.name, items.length ? uiText("app.094", [items.length]) : null),
      h("div", { class: "card" }, items.length ? items.map(x => h("div", {
        class: "list-row", role: "button", tabindex: "0",
        onclick: () => x.kind === DATA_VALUE.approval ? (location.hash = "#/approvals") : x.project ? showProject(x.project) : showDoc(x.rel),
      }, h("span", { class: pill[x.kind] + " tag-kind", text: uiLabel(x.kind) }),
        h("div", { class: "grow" }, h("div", { class: "cell-name", text: uiSentence(x.title) }), h("div", { class: "note", text: uiSentence(x.detail) })),
        h("span", { class: "chev" }, icon(ICON.chev))))
        : empty(uiText("app.093"), uiText("app.092")))];
  },

  projects(m) {
    const rows = m.data.rows;
    return [secHead(m.name, uiText("app.091", [rows.length])),
      h("div", { class: "card table-wrap" }, h("table", { class: "t proj-t" },
        h("thead", {}, h("tr", {}, [uiText("app.008"), uiText("app.090"), uiText("app.089"), uiText("app.088"), uiText("app.087"), uiText("app.086")].map((t, i) =>
          h("th", { class: i === 5 ? "num" : (i === 3 || i === 4 ? "hide-sm" : null), text: t })))),
        h("tbody", {}, rows.map(r => h("tr", { class: "click", onclick: () => showProject(r.id) },
          h("td", {}, h("div", { class: "cell-name", text: r.name }), r.goal ? h("div", { class: "cell-sub", text: r.goal }) : null),
          h("td", {}, statePill(r.state, r)),
          h("td", {}, r.lead ? [h("div", { text: r.lead.who }), h("div", { class: "cell-sub", text: r.lead.role })] : h("span", { class: "faint", text: "—" })),
          h("td", { class: "hide-sm nowrap" }, r.last ? [h("div", { text: ago(r.last) }), h("div", { class: "cell-sub", text: uiLabel(r.last_by) })] : h("span", { class: "faint", text: "—" })),
          h("td", { class: "hide-sm nowrap" },
            h("div", {}, r.summary_as_of ? h("span", { text: md(r.summary_as_of) }) : h("span", { class: "faint", text: uiText("app.085") }),
              r.newer_status ? [" ", h("span", { class: "pill", text: uiText("app.084") })] : null),
            r.stale && ![DATA_VALUE.history, DATA_VALUE.finished].includes(r.state) ? h("div", { class: "cell-sub" }, h("span", { class: "pill red", text: uiText("app.083") })) : null),
          h("td", { class: "num" }, r.count30 || h("span", { class: "faint", text: "0" })))))))];
  },

  activity_chart(m) {
    const d = m.data;
    const chart = h("div", { class: "chart" });
    const draw = () => columnChart(chart, d.series);
    new ResizeObserver(draw).observe(chart);
    const max = Math.max(1, ...d.by_who.map(x => x.count));
    return [secHead(m.name, uiText("app.082", [d.days, d.total]) + (S.me.remote ? uiText("app.081") : uiText("app.080"))),
      h("div", { class: "grid2" },
        h("div", { class: "card" }, h("div", { class: "card-title" }, h("h3", { text: uiText("app.079") }),
          h("span", { class: "sub", text: uiText("app.078") })), chart,
          h("details", { class: "data" }, h("summary", { text: uiText("app.077") }),
            h("table", {}, d.series.slice().reverse().map(x => h("tr", {}, h("td", { text: md(x.date) }), h("td", { text: uiText("app.076", [x.count]) })))))),
        h("div", { class: "card" }, h("div", { class: "card-title" }, h("h3", { text: uiText("app.075") })),
          d.by_who.length ? h("div", { class: "hbars" }, d.by_who.map(x => h("div", { class: "hbar" },
            h("span", { class: "name", text: x.name, title: x.name }),
            h("span", { class: "track" }, widthed(h("span", { class: "fill" }), x.count / max)),
            h("span", { class: "num", text: x.count }))))
            : empty(uiText("app.074"), uiText("app.073"))))];
  },

  recent(m) {
    return [secHead(m.name, null, h("a", { class: "more", href: "#/activity", text: uiText("app.072") })),
      h("div", { class: "card" }, m.data.rows.length ? m.data.rows.map(commitRow).map(c => h("div", {
        class: "list-row", role: "button", tabindex: "0", onclick: () => showCommit(c.sha),
      }, avatar(c.who), h("div", { class: "grow" },
        h("div", {}, h("b", { text: whoName(c.who) }), " ", uiSentence(c.what)),
        c.excerpt ? h("div", { class: "cell-sub", text: c.excerpt }) : null),
        h("span", { class: "faint small nowrap", text: ago(c.when) }))) : empty(uiText("app.071"), "")),
    ];
  },
};

// 柱状图：单一序列，用强调色；柱宽不超过 24px，顶端 4px 圆角；只给最高和最新两根标数，其余靠悬停看。
function columnChart(box, series) {
  const W = Math.max(240, box.clientWidth - 40), H = 190, L = 28, B = 22, T = 16;
  const n = series.length, band = (W - L) / n, bw = Math.min(24, band * .62);
  const top = Math.max(...series.map(x => x.count));
  const nice = top <= 4 ? 4 : (() => { const p = 10 ** Math.floor(Math.log10(top)); const f = top / p; return (f <= 2 ? 2 : f <= 5 ? 5 : 10) * p; })();
  const y = v => H - B - (v / nice) * (H - B - T);
  const tip = h("div", { class: "tip", hidden: true });
  const svg = s("svg", { viewBox: `0 0 ${W} ${H}`, width: W, height: H, role: "img",
    "aria-label": uiText("app.070", [n, top]) });
  const grid = s("g", { class: "grid" });
  for (const v of [0, nice / 2, nice]) {
    grid.append(s("line", { x1: L, x2: W, y1: y(v), y2: y(v) }),
      s("text", { class: "tick", x: L - 8, y: y(v) + 4, "text-anchor": "end" }, String(v)));
  }
  svg.append(grid);
  const step = Math.ceil(n / 7), maxIdx = series.findIndex(x => x.count === top);
  series.forEach((x, i) => {
    const cx = L + band * i + band / 2, x0 = cx - bw / 2, y0 = y(x.count), base = y(0);
    const hgt = base - y0, r = Math.min(4, hgt);
    const bar = x.count ? s("path", { class: "bar",
      d: `M${x0},${base}V${y0 + r}Q${x0},${y0} ${x0 + r},${y0}H${x0 + bw - r}Q${x0 + bw},${y0} ${x0 + bw},${y0 + r}V${base}Z` }) : null;
    const hit = s("rect", { class: "hit", x: L + band * i, y: T, width: band, height: H - B - T });
    hit.addEventListener("mouseenter", () => {
      bar?.classList.add("hl");
      tip.textContent = uiText("app.069", [md(x.date), x.count]);
      tip.style.left = `${20 + cx}px`; tip.style.top = `${8 + Math.min(y0, base - 2) - 6}px`; tip.hidden = false;
    });
    hit.addEventListener("mouseleave", () => { bar?.classList.remove("hl"); tip.hidden = true; });
    svg.append(hit);
    if (bar) svg.append(bar);
    if (x.count && (i === maxIdx || i === n - 1)) svg.append(s("text", { class: "val", x: cx, y: y0 - 5, "text-anchor": "middle" }, String(x.count)));
    if ((n - 1 - i) % step === 0) svg.append(s("text", { class: "tick", x: cx, y: H - 5, "text-anchor": "middle" },
      i === n - 1 ? uiText("app.068") : `${+x.date.slice(5, 7)}/${+x.date.slice(8, 10)}`));
  });
  put(box, svg, tip);
}

function orbit(rows) {
  const live = rows.filter(r => r.state === DATA_VALUE.active).slice(0, 6);
  if (!live.length) return null;
  const cx = 160, cy = 104;
  const rings = [{ rx: 148, ry: 78 }, { rx: 96, ry: 50 }];
  const g = s("svg", { class: "orbit", viewBox: "-60 0 440 210", width: "100%", height: "210", "aria-hidden": "true" },
    rings.map(r => s("ellipse", { class: "ring", cx, cy, rx: r.rx, ry: r.ry })),
    s("rect", { class: "core", x: cx - 13, y: cy - 13, width: 26, height: 26, rx: 7 }),
    s("path", { class: "core-mark", d: `M${cx - 6} ${cy + 4}h12M${cx - 6} ${cy}h12M${cx - 6} ${cy - 4}h8` }));
  live.forEach((r, i) => {
    const ring = rings[i % 2], a = (-90 + i * (360 / live.length) + (i % 2) * 24) * Math.PI / 180;
    const x = cx + ring.rx * Math.cos(a), yy = cy + ring.ry * Math.sin(a), right = Math.cos(a) >= -0.01;
    g.append(s("circle", { class: "dot", cx: x, cy: yy, r: 4 }),
      s("text", { x: x + (right ? 8 : -8), y: yy + 4, "text-anchor": right ? "start" : "end" },
        clip(r.name, 7)));
  });
  return g;
}

// 轨道上的名字按显示宽度截：汉字算 1，英文字母和数字算半个
function clip(text, max) {
  let w = 0, out = "";
  for (const ch of text) {
    w += /[\x00-\xff]/.test(ch) ? .55 : 1;
    if (w > max) return out + "…";
    out += ch;
  }
  return out;
}

function heroLead(mods, home) {
  const parts = [];
  const fs = (home && home.feishu_pending || []).length;
  if (capability("feishu") && fs) parts.push(uiText("app.067", [fs]));
  const td = mods.find(m => m.kind === "today_todos");
  if (td && td.data.items.length) {
    const late = td.data.items.filter(t => t.bucket === "overdue").length;
    parts.push(uiText("app.066", [td.data.items.length]) + (late ? uiText("app.065", [late]) : uiText("punct.period")));
  }
  const att = mods.find(m => m.kind === "attention");
  if (att) parts.push(att.data.items.length ? uiText("app.064", [att.data.items.length]) : fs ? "" : uiText("app.063"));
  const ch = mods.find(m => m.kind === "activity_chart");
  if (ch) {
    const week = ch.data.by_who.reduce((a, x) => a + x.count, 0);
    parts.push(week ? uiText("app.062", [week]) : uiText("app.061"));
  }
  return parts.join("");
}

function draftBanner(home) {
  const d = home.draft;
  if (!d) return null;
  const by = d.by_name || uiText("app.060");
  // 带上看到的草稿版本；草稿在这之后又被改过，服务端回 409，重新载入让你看新的那份再定。
  const decide = async (path, done) => {
    try {
      const r = await api(path, { version: d.version });
      if (r._status === 409) { toast(r.error || uiText("app.059")); renderHome(); return; }
      toast(done); S.preview = false; renderHome();
    } catch (e) { if (e.message !== "401") toast(e.message); }
  };
  const adopt = () => decide("/api/home/adopt", uiText("app.058"));
  const discard = () => decide("/api/home/discard", S.me.remote ? uiText("app.057") : uiText("app.056"));
  if (d.error) {
    return h("div", { class: "banner" }, h("div", { class: "grow" }, h("b", { text: uiText("app.055", [by]) }), d.error),
      allowed(d, "discard", "adopt_own_home") ? h("button", { class: "btn", type: "button", onclick: discard }, uiText("app.049")) : null);
  }
  if (home.preview) {
    return h("div", { class: "banner" }, h("div", { class: "grow" }, h("b", { text: uiText("app.054") }), d.note || uiText("app.050"), `（${by}）`),
      h("button", { class: "btn", type: "button", onclick: () => { S.preview = false; renderHome(true); } }, uiText("app.053")),
      allowed(d, "adopt", "adopt_own_home") ? h("button", { class: "btn primary", type: "button", onclick: adopt }, uiText("app.052")) : null);
  }
  return h("div", { class: "banner" }, h("div", { class: "grow" }, h("b", { text: uiText("app.051", [by]) }), d.note || uiText("app.050")),
    allowed(d, "discard", "adopt_own_home") ? h("button", { class: "btn ghost", type: "button", onclick: discard }, uiText("app.049")) : null,
    h("button", { class: "btn", type: "button", onclick: () => { S.preview = true; renderHome(true); } }, uiText("app.048")),
    allowed(d, "adopt", "adopt_own_home") ? h("button", { class: "btn primary", type: "button", onclick: adopt }, uiText("app.047")) : null);
}

function openHomeHelp() {
  const prompt = uiText("app.046", [appName()]);
  const copy = h("button", { class: "btn", type: "button", onclick: async () => {
    try { await navigator.clipboard.writeText(prompt); toast(uiText("app.045")); } catch { toast(uiText("app.044")); }
  } }, icon(ICON.copy), uiText("app.043"));
  const step = (no, title, text, extra) => h("div", { class: "step" }, h("span", { class: "no", text: no }),
    h("div", {}, h("b", { text: title }), h("p", { text }), extra));
  openLayer(h("div", { class: "modal", role: "dialog", "aria-label": uiText("app.026") },
    h("div", { class: "modal-head" }, h("div", {}, h("h3", { text: uiText("app.026") }),
      h("p", { text: uiText("app.042") })),
      h("button", { class: "btn ghost x", type: "button", "aria-label": uiText("app.041"), onclick: closeLayer }, icon(ICON.x))),
    h("div", { class: "steps" },
      step("1", uiText("app.040"), uiText("app.039"),
        h("div", { class: "copy" }, h("span", { text: prompt }), copy)),
      step("2", uiText("app.038"), uiText("app.037")),
      step("3", uiText("app.036"), S.me.remote ? uiText("app.035") : uiText("app.034")),
      h("p", { class: "note faint", text: S.me.remote ? uiText("app.033") : uiText("app.032") }))), "modal");
}

async function renderHome(toTop) {
  const page = document.getElementById("page");
  let home;
  try { home = await api("/api/home" + (S.preview ? "?draft=1" : "")); } catch (e) {
    if (e.message !== "401") put(page, h("p", { class: "muted", text: e.message }));
    return;
  }
  if (S.preview && !home.preview) S.preview = false;
  S.home = home;
  const proj = home.modules.find(m => m.kind === "projects");
  const p = parts(home.now);
  const mods = home.modules.map(m => MODULES[m.kind] ? MODULES[m.kind](m) : null);
  const name = S.me.name.split(/[（(]/)[0];
  put(page,
    h("div", { class: "hero-row" },
      h("div", { class: "left" },
        h("div", { class: "date", text: uiText("app.031", [+p.month, +p.day, p.weekday]) }),
        h("h1", { class: "hero", text: uiText("home.greeting", [greeting(home.now), name]) }),
        h("p", { class: "lead", text: heroLead(home.modules, home) }),
        h("button", { class: "hero-search", type: "button", onclick: () => openSearch() }, icon(ICON.search), h("span", { class: "search-label", text: uiText("app.030") }), h("kbd", { text: "Ctrl K" }))),
      proj ? h("div", { class: "art" }, orbit(proj.data.rows)) : null),
    draftBanner(home),
    capability("manage_company") && capability("feishu") ? feishuPendingCard({ feishu_pending: home.feishu_pending, items: (home.remote_members || []).map(m => ({ ...m, remote_enabled: true, kind: "human" })) },
      uiText("app.029")) : null,
    mods,
    h("div", { class: "foot" }, home.preview ? uiText("app.028") : uiText("app.027"),
      h("button", { class: "btn", type: "button", onclick: openHomeHelp }, icon(ICON.edit), uiText("app.026"))));
  if (toTop) document.getElementById("main").scrollTop = 0;
}

// ---------- 动态 ----------

function rowMatches(c, f) {
  if (f.who && c.who.name !== f.who) return false;
  if (f.project && c.project?.id !== f.project) return false;
  if (f.type && c.type !== f.type) return false;
  if (f.range !== "all" && Date.now() - new Date(c.when) > +f.range * 864e5) return false;
  if (f.q) {
    const text = [c.what, c.excerpt, c.who.name, c.who.full, c.project?.name, c.subject, ...c.files.map(x => x.label)].join(" ").toLowerCase();
    if (!f.q.toLowerCase().split(/\s+/).filter(Boolean).every(t => text.includes(t))) return false;
  }
  return true;
}

async function renderActivity() {
  const page = document.getElementById("page");
  if (!S.rows) {
    try { const data = await api("/api/activity"); S.rows = data.rows.map(commitRow); S.scope = data.scope; } catch (e) {
      if (e.message !== "401") put(page, h("p", { class: "muted", text: e.message }));
      return;
    }
  }
  const rows = S.rows, f = S.filter;
  const uniq = arr => [...new Set(arr.filter(Boolean))];
  const opt = (label, values, cur) => [h("option", { value: "", text: label }),
    values.map(([v, t]) => h("option", { value: v, selected: v === cur, text: t }))];
  const projects = uniq(rows.map(c => c.project?.id)).map(id => [id, rows.find(c => c.project?.id === id).project.name]);
  const q = h("input", { class: "input grow", placeholder: uiText("app.025"), value: f.q, "aria-label": uiText("app.024") });
  const who = h("select", { class: "input", "aria-label": uiText("app.010") }, opt(uiText("app.023"), uniq(rows.map(c => c.who.name)).map(n => [n, whoName(rows.find(c => c.who.name === n).who)]), f.who));
  const proj = h("select", { class: "input", "aria-label": uiText("app.008") }, opt(uiText("app.022"), projects, f.project));
  const type = h("select", { class: "input", "aria-label": uiText("app.021") }, opt(uiText("app.020"), uniq(rows.map(c => c.type)).map(t => [t, uiLabel(t)]), f.type));
  const seg = h("div", { class: "seg", role: "group", "aria-label": uiText("app.011") }, [["7", uiText("app.019")], ["30", uiText("app.018")], ["all", uiText("app.017")]].map(([v, t]) =>
    h("button", { type: "button", class: f.range === v ? "on" : null, onclick: () => { f.range = v; renderActivity(); } }, t)));
  const list = h("div");
  const count = h("div", { class: "count-line" });
  const drawList = () => {
    const shown = rows.filter(c => rowMatches(c, f));
    count.textContent = shown.length === rows.length ? uiText("app.016", [rows.length]) : uiText("app.015", [shown.length, rows.length]);
    if (!shown.length) return put(list, h("div", { class: "card" }, empty(uiText("app.014"), uiText("app.013"))));
    const body = [];
    let last = null;
    for (const c of shown) {
      const k = dayKey(c.when);
      if (k !== last) { body.push(h("tr", { class: "day" }, h("td", { colspan: "5", text: dayLabel(c.when) }))); last = k; }
      body.push(h("tr", { class: "click", onclick: () => showCommit(c.sha) },
        h("td", { class: "nowrap faint", text: hm(c.when) }),
        h("td", { class: "nowrap" }, whoChip(c.who)),
        h("td", {}, h("div", { text: uiSentence(c.what) }), c.excerpt ? h("div", { class: "cell-sub", text: c.excerpt }) : null),
        h("td", { class: "hide-sm" }, c.project ? h("span", { class: "pill", text: c.project.name }) : null),
        h("td", { class: "nowrap" }, c.reverted_by ? h("span", { class: "pill", text: uiText("app.012") }) : null)));
    }
    put(list, h("div", { class: "card table-wrap" }, h("table", { class: "t" },
      h("thead", {}, h("tr", {}, [uiText("app.011"), uiText("app.010"), uiText("app.009"), uiText("app.008"), ""].map((t, i) => h("th", { class: i === 3 ? "hide-sm" : null, text: t })))),
      h("tbody", {}, body))));
  };
  q.addEventListener("input", () => { f.q = q.value; drawList(); });
  for (const [el, key] of [[who, "who"], [proj, "project"], [type, "type"]]) el.addEventListener("change", () => { f[key] = el.value; drawList(); });
  put(page,
    h("h1", { text: uiText("app.007") }),
    h("p", { class: "lead", text: S.me.remote ? uiLabel(S.scope) : uiText("app.006", [uiLabel(S.scope)]) }),
    h("div", { class: "card filters" }, q, who, proj, type, seg),
    count, list);
  drawList();
}

// ---------- 路由与启动 ----------

async function route() {
  if (!S.me || SESSION.stopped) return;
  SESSION.view++;
  const view = SESSION.view;
  closeLayer();
  const target = parseRoute(location.hash);
  const page = target.page === "search" ? "home" : target.page;
  if (!S.me.navigation.includes(page)) {
    for (const a of document.querySelectorAll(".nav a[data-route]")) a.classList.remove("on");
    put(document.getElementById("page"), empty(page === "approvals" ? uiText("app.005") : uiText("app.004"),
      page === "approvals" ? uiText("app.003", [ownerName()]) : ""));
    return;
  }
  for (const a of document.querySelectorAll(".nav a[data-route]")) a.classList.toggle("on", a.dataset.route === page);
  document.querySelector(".nav a.on")?.scrollIntoView({ block: "nearest", inline: "nearest" });  // 手机上导航横着滚
  document.getElementById("main").scrollTop = 0;
  if (page === "activity") await renderActivity();
  else if (page === "todos") await renderTodos();
  else if (page === "apps") await renderApps();
  else if (page === "agents") await renderAgents();
  else if (page === "skills") await renderSkills();
  else if (page === "projects") await renderProjects();
  else if (page === "docs") await renderDocs();
  else if (page === "decisions") await renderDecisions();
  else if (page === "members") await renderMembers();
  else if (page === "approvals") await renderApprovals();
  else await renderHome();
  if (SESSION.stopped || view !== SESSION.view) return;
  if (target.id && page === "projects") await showProject(target.id);
  else if (target.id && page === "docs") await showDoc(target.id.includes("/") ? target.id : `assets/${target.id}.md`);
  else if (target.id && page === "skills") await showSkillById(target.id);
  else if (target.page === "search") openSearch(target.q);
}

function parseRoute(hash) {
  const [path, query = ""] = (hash || "#/").replace(/^#\/?/, "").split("?");
  const [page = "", ...rest] = path.split("/");
  try { return { page: page || "home", id: rest.length ? decodeURIComponent(rest.join("/")) : "", q: new URLSearchParams(query).get("q")?.slice(0, 100) || "" }; }
  catch { return { page: "missing", id: "", q: "" }; }
}

async function loadMe() {
  let me;
  try { me = await api("/api/me"); } catch { return false; }
  const changed = S.me && (S.me.key !== me.key || S.me.csrf_token !== me.csrf_token || JSON.stringify(S.me.capabilities) !== JSON.stringify(me.capabilities));
  if (changed) clearWorkspace();
  S.me = me;
  initLanguage(me.lang);
  const name = S.me.name.split(/[（(]/)[0];
  document.getElementById("me-name").textContent = name;
  document.getElementById("me-initial").textContent = name.slice(0, 1);
  document.getElementById("me-role").textContent = S.me.role || (S.me.remote ? uiText("app.002") : uiText("app.001"));
  for (const a of document.querySelectorAll(".nav a[data-route]")) a.hidden = !S.me.navigation.includes(a.dataset.route);
  document.querySelector(".nav").hidden = false;
  document.getElementById("open-search").hidden = false;
  document.getElementById("logout").hidden = !capability("sign_out");
  const pend = document.getElementById("nav-pending");
  pend.textContent = S.me.pending || ""; pend.hidden = !S.me.pending;
  if (changed) setTimeout(route, 0);
  return true;
}

document.getElementById("open-search").addEventListener("click", () => openSearch());
document.getElementById("logout").addEventListener("click", async e => {
  e.target.disabled = true;
  try { await api("/auth/logout", {}); expireSession(true); }
  catch (error) { if (error.message !== "401") toast(error.message); e.target.disabled = false; }
});
document.addEventListener("keydown", e => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { e.preventDefault(); openSearch(); }
  if (e.key === "Escape" && layer().childElementCount) closeLayer();
  if ((e.key === "Enter" || e.key === " ") && e.target.matches?.(".list-row[role=button]")) { e.preventDefault(); e.target.click(); }
});
window.addEventListener("hashchange", () => { S.rows = null; route(); });
document.addEventListener("visibilitychange", () => { if (!document.hidden) beat(); });
window.addEventListener("pageshow", async e => { if (e.persisted) { clearWorkspace(); if (await loadMe()) route(); } });
document.addEventListener("DOMContentLoaded", async () => {
  if (!await loadMe()) return;
  beat.timer = setInterval(beat, 20000); beat();
  await route();
});
