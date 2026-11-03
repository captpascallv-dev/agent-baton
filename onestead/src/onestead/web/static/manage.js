"use strict";
// 共用业务页；管理操作只给本机主人，普通写入使用当前会话身份。

const LEVEL_NAMES = translatedLabels({ public: "manage.170", internal: "manage.169", restricted: "manage.168" });
const levelHelp = () => uiText("manage.167");
const MG = { docs: "all", projects: "active", decisionsQ: "", decisionsProject: "", showArchived: false };
onWorkspaceReset(() => Object.assign(MG, { docs: "all", projects: "active", decisionsQ: "", decisionsProject: "", showArchived: false }));

function canEdit(rel) {
  return capability("edit_sources") && (/^(handbook|assets|decisions|people)\/[^/]+\.md$/.test(rel) || /^projects\/[^/]+\/project\.md$/.test(rel));
}

function editButton(rel) {
  return canEdit(rel) ? h("button", { class: "btn", type: "button", onclick: () => editDoc(rel) }, uiText("manage.159")) : null;
}

function levelPill(level) {
  return h("span", { class: "pill" + (level === "restricted" ? " accent" : ""), text: LEVEL_NAMES[level] || level });
}

// 敏感级别 + 能看的人 + 使用提示，编辑和新建共用
function levelPicker(state, members) {
  const box = h("div", { class: "level-box" });
  const draw = () => put(box,
    h("div", { class: "seg", role: "group", "aria-label": uiText("manage.090") }, ["public", "internal", "restricted"].map(v =>
      h("button", { type: "button", class: state.sensitivity === v ? "on" : null, onclick: () => { state.sensitivity = v; draw(); } }, LEVEL_NAMES[v]))),
    h("p", { class: "note faint", text: levelHelp() }),
    state.sensitivity === "restricted" ? h("div", { class: "who-picks" }, h("span", { class: "note", text: uiText("manage.166") }),
      members.map(m => h("label", { class: "toggle" }, h("input", { type: "checkbox", checked: state.visible_to.includes(m.key) || null,
        onchange: e => { state.visible_to = e.target.checked ? [...state.visible_to, m.key] : state.visible_to.filter(k => k !== m.key); } }), m.name))) : null,
    h("label", {}, uiText("manage.165"),
      h("input", { class: "input", value: state.access_note || "", maxlength: 200, oninput: e => { state.access_note = e.target.value; } })));
  draw();
  return box;
}

function fieldInput(f, values) {
  if (f.type === "multi") {
    const picked = new Set(values[f.key] || []);
    values[f.key] = [...picked];
    return h("div", { class: "who-picks" }, f.options.length ? f.options.map(o => h("label", { class: "toggle" },
      h("input", { type: "checkbox", checked: picked.has(o) || null, onchange: e => {
        if (e.target.checked) picked.add(o); else picked.delete(o);
        values[f.key] = [...picked];
      } }), f.labels?.[o] || o)) : h("span", { class: "note", text: uiText("manage.164") }));
  }
  if (f.type === "select") {
    const sel = h("select", { class: "input", disabled: f.locked || null }, f.options.map(o =>
      h("option", { value: o, selected: o === (values[f.key] ?? "") || null, text: ["state", "kind", "access_mode"].includes(f.key) || o === "" ? uiLabel(f.labels?.[o] || o) || uiText("manage.163") : f.labels?.[o] || o })));
    sel.addEventListener("change", () => { values[f.key] = sel.value; });
    return sel;
  }
  if (f.type === "lines") {
    const area = h("textarea", { class: "input area", rows: 4, "aria-label": uiLabel(f.label),
      placeholder: uiText("here.local_dirs_hint") });
    area.value = values[f.key] ?? "";
    area.addEventListener("input", () => { values[f.key] = area.value; });
    return area;
  }
  if (f.type === "bool") {
    return h("label", { class: "toggle" }, h("input", { type: "checkbox", checked: values[f.key] || null, disabled: f.locked || null,
      onchange: e => { values[f.key] = e.target.checked; } }), uiText("manage.162"));
  }
  const input = h("input", { class: "input", type: f.type === "date" ? "date" : "text", value: values[f.key] ?? "" });
  input.addEventListener("input", () => { values[f.key] = input.value; });
  return input;
}

// 一行表单。多选的一行不能包在 <label> 里，不然点字段名会误勾第一个选项。
function fieldRow(f, values) {
  return h(f.type === "multi" ? "div" : "label", { class: f.type === "multi" ? "field" : null, "data-show-if": f.show_if, "data-show-value": f.show_value },
    uiLabel(f.label) + (f.locked ? uiText("manage.161") : ""), fieldInput(f, values));
}

// 按布尔开关或指定选项值显示表单行。
function syncShowIf(form, values) {
  const sync = () => form.querySelectorAll("[data-show-if]").forEach(r => { r.hidden = r.dataset.showValue === undefined ? !values[r.dataset.showIf] : values[r.dataset.showIf] !== r.dataset.showValue; });
  form.addEventListener("change", sync);
  sync();
  return form;
}

function bodyEditor(state, label) {
  const area = h("textarea", { class: "input area body-area", rows: 18, "aria-label": label });
  area.value = state.body;
  area.addEventListener("input", () => { state.body = area.value; });
  const preview = h("div", { class: "prose preview", hidden: true });
  const toggle = h("button", { class: "btn small", type: "button", onclick: async () => {
    if (preview.hidden) {
      try { const r = await api("/api/render", { text: state.body }); put(preview, trusted(r.html)); } catch (e) { if (e.message !== "401") toast(e.message); return; }
      preview.hidden = false; area.hidden = true; toggle.textContent = uiText("manage.160");
    } else { preview.hidden = true; area.hidden = false; toggle.textContent = uiText("app.048"); }
  } }, uiText("app.048"));
  return h("div", { class: "body-edit" }, h("div", { class: "body-edit-head" }, h("span", { text: label }), toggle), area, preview);
}

async function editDoc(rel) {
  if (!canEdit(rel)) return toast(uiText("app.149"));
  drawerLoading(uiText("manage.159"));
  let s;
  try { s = await api(`/api/source?rel=${encodeURIComponent(rel)}`); } catch (e) { return drawerError(uiText("manage.159"), e); }
  const values = Object.fromEntries(s.fields.map(f => [f.key, f.value]));
  const original = JSON.parse(JSON.stringify(values));
  const level = { sensitivity: s.sensitivity, visible_to: [...s.visible_to], access_note: s.access_note };
  const body = { body: s.body };
  const err = h("div", { class: "form-error", hidden: true });
  const save = h("button", { class: "btn primary", type: "button", onclick: async () => {
    save.disabled = true; err.hidden = true;
    const changed = Object.fromEntries(Object.entries(values).filter(([k, v]) => JSON.stringify(v) !== JSON.stringify(original[k])));
    if (s.kind === "person" && values.kind === "human") changed.owner = "";
    const changes = { ...changed, sensitivity: level.sensitivity, visible_to: level.visible_to, access_note: level.access_note };
    try {
      const r = await api("/api/source/save", { rel, version: s.version, changes, body: body.body });
      toast(uiLabel(r.message)); closeLayer();
      if (r.warnings?.length) setTimeout(() => toast(r.warnings[0]), 1800);
      route();
    } catch (e) { if (e.message !== "401") { err.textContent = e.message; err.hidden = false; } save.disabled = false; }
  } }, uiText("manage.158"));
  drawer(uiText("manage.157", [uiLabel(s.kind_name)]),
    h("h2", { text: uiText("manage.156", [values.title]) }),
    s.problem ? h("div", { class: "box accent", text: uiText("manage.155", [s.problem]) }) : null,
    syncShowIf(h("div", { class: "form" },
      s.fields.map(f => fieldRow(f, values)),
      h("div", { class: "block-title flush", text: uiText("manage.090") }), levelPicker(level, s.members),
      bodyEditor(body, s.kind === "decision" ? uiText("manage.154") : uiText("manage.129"))), values),
    err,
    h("div", { class: "form-btns" }, h("button", { class: "btn", type: "button", onclick: closeLayer }, uiText("app.121")), save),
    h("p", { class: "note faint", text: uiText("manage.153") }),
    h("details", { class: "tech" }, h("summary", { text: uiText("app.101") }), h("span", { class: "mono", text: uiText("manage.152", [s.rel, s.version]) })));
}

function newDocForm(kind) {
  if (!capability("manage_company")) return toast(uiText("app.149"));
  const names = { asset: uiText("manage.115"), handbook: uiText("manage.151"), person: uiText("app.144") };
  const values = { id: "", title: "", role: "", kind: "human", can_write: true, connect: "", remote_enabled: false, access_mode: "assigned", owner: "" };
  const level = { sensitivity: "internal", visible_to: [], access_note: "" };
  const body = { body: "" };
  const err = h("div", { class: "form-error", hidden: true });
  const fields = [
    { key: "id", label: kind === "person" ? uiText("manage.150") : uiText("manage.149"), type: "text" },
    { key: "title", label: kind === "person" ? uiText("manage.148") : uiText("manage.104"), type: "text" },
    ...(kind === "person" ? [{ key: "role", label: uiText("app.114"), type: "text" }, { key: "kind", label: uiText("manage.147"), type: "select", options: ["agent", "human"], labels: { human: uiText("manage.062"), agent: "agent" } },
      { key: "can_write", label: uiText("manage.146"), type: "bool" }, { key: "connect", label: uiText("manage.145"), type: "text" },
      { key: "remote_enabled", label: uiText("manage.144"), type: "bool" },
      { key: "access_mode", label: uiText("manage.143"), type: "select", options: ["assigned", "full"], labels: { assigned: uiText("manage.142"), full: uiText("manage.141") } },
      { key: "owner", label: uiText("manage.140"), type: "select", show_if: "kind", show_value: "agent", options: [""], labels: { "": uiText("manage.139") } }] : []),
  ];
  const save = h("button", { class: "btn primary", type: "button", onclick: async () => {
    save.disabled = true; err.hidden = true;
    try {
      const r = await api("/api/source/new", { kind, fields: { ...values, ...(values.kind === "human" ? { owner: "" } : {}), ...level }, body: body.body });
      toast(uiText("manage.138", [r.commit])); closeLayer(); route();
    } catch (e) { if (e.message !== "401") { err.textContent = e.message; err.hidden = false; } save.disabled = false; }
  } }, uiText("app.136"));
  Promise.all([api("/api/decisions"), kind === "person" ? api("/api/members") : Promise.resolve(null)]).then(([d, members]) => {
    if (members) {
      const owners = members.items.filter(m => m.kind === "human" && !m.problem && !m.archived);
      const f = fields.find(f => f.key === "owner");
      f.options.push(...owners.map(m => m.key));
      Object.assign(f.labels, Object.fromEntries(owners.map(m => [m.key, m.title])));
    }
    drawer(uiText("manage.137", [names[kind]]), h("h2", { text: uiText("manage.137", [names[kind]]) }),
      kind === "person" ? h("p", { class: "note", text: uiText("manage.136") }) : null,
      syncShowIf(h("div", { class: "form" }, fields.map(f => fieldRow(f, values)),
        h("div", { class: "block-title flush", text: uiText("manage.090") }), levelPicker(level, d.members),
        bodyEditor(body, uiText("manage.129"))), values),
      err, h("div", { class: "form-btns" }, h("button", { class: "btn", type: "button", onclick: closeLayer }, uiText("app.121")), save));
  }).catch(e => { if (e.message !== "401") toast(e.message); });
}

// ---------- 项目 ----------

function projectLevel(state) {
  const box = h("div", { class: "level-box" });
  const draw = () => put(box,
    h("div", { class: "seg", role: "group", "aria-label": uiText("manage.090") }, ["public", "internal", "restricted"].map(v =>
      h("button", { type: "button", class: state.sensitivity === v ? "on" : null, onclick: () => {
        state.sensitivity = v;
        if (v === "restricted") state.visible_to = [];
        draw();
      } }, LEVEL_NAMES[v]))),
    h("p", { class: "note faint", text: state.sensitivity === "restricted"
      ? uiText("manage.135") : levelHelp() }));
  draw();
  return box;
}

function newProjectForm() {
  if (!capability("manage_company")) return toast(uiText("app.149"));
  const values = { id: "", title: "", goal: "", state: DATA_VALUE.idea };
  const level = { sensitivity: "internal", visible_to: [] };
  const body = { body: DATA_VALUE.projectBody };
  const err = h("div", { class: "form-error", hidden: true });
  const save = h("button", { class: "btn primary", type: "button", onclick: async () => {
    save.disabled = true; err.hidden = true;
    const id = values.id.trim();
    try {
      const r = await api("/api/projects", { id, title: values.title, goal: values.goal, state: values.state,
        body: body.body, sensitivity: level.sensitivity });
      toast(uiText("manage.134"));
      S.rows = S.home = null;
      await route();
      await showProject(r.id || id);
    } catch (e) {
      if (e.message !== "401") { err.textContent = e.message; err.hidden = false; }
      save.disabled = false;
    }
  } }, uiText("app.136"));
  drawer(uiText("manage.123"), h("h2", { text: uiText("manage.123") }),
    h("p", { class: "note", text: uiText("manage.133") }),
    h("div", { class: "form" },
      fieldRow({ key: "id", label: uiText("manage.132"), type: "text" }, values),
      fieldRow({ key: "title", label: uiText("manage.131"), type: "text" }, values),
      fieldRow({ key: "state", label: uiText("app.090"), type: "select", options: [DATA_VALUE.active, DATA_VALUE.maintaining, DATA_VALUE.idea, DATA_VALUE.paused] }, values),
      fieldRow({ key: "goal", label: uiText("manage.130"), type: "text" }, values),
      h("div", { class: "block-title flush", text: uiText("manage.090") }), projectLevel(level),
      bodyEditor(body, uiText("manage.129"))),
    err, h("div", { class: "form-btns" }, h("button", { class: "btn", type: "button", onclick: closeLayer }, uiText("app.121")), save));
}

async function renderProjects() {
  const page = document.getElementById("page");
  let data;
  try { data = await api("/api/projects"); } catch (e) { if (e.message !== "401") put(page, h("p", { class: "muted", text: e.message })); return; }
  const wrap = h("div");
  const seg = h("div", { class: "seg", role: "group", "aria-label": uiText("manage.128") });
  const draw = () => {
    put(seg, [["active", uiText("manage.127")], ["closed", uiText("manage.126")], ["all", uiText("app.017")]].map(([v, t]) =>
      h("button", { type: "button", class: MG.projects === v ? "on" : null, onclick: () => { MG.projects = v; draw(); } }, t)));
    const closed = r => [DATA_VALUE.history, DATA_VALUE.finished].includes(r.state);
    const rows = data.rows.filter(r => MG.projects === "all" || (MG.projects === "closed" ? closed(r) : !closed(r)));
    put(wrap, MODULES.projects({ name: "", data: { rows } })[1]);
  };
  draw();
  put(page,
    h("div", { class: "page-head" }, h("div", {}, h("h1", { text: uiText("app.008") }),
      h("p", { class: "lead", text: capability("edit_sources") ? uiText("manage.125") : uiText("manage.124") })),
      capability("manage_company") ? h("button", { class: "btn", type: "button", onclick: () => newProjectForm() }, uiText("manage.123")) : null),
    h("div", { class: "card filters" }, seg, capability("manage_company") ? h("span", { class: "note faint", text: uiText("manage.122") }) : null),
    data.rows.length ? h("div", { class: "spaced" }, wrap) : empty(uiText("manage.121"), uiText("manage.120", [ownerName()])));
}

// ---------- 资料（手册 + 资产） ----------

async function renderDocs() {
  const page = document.getElementById("page");
  let data;
  try { data = await api("/api/docs"); } catch (e) { if (e.message !== "401") put(page, h("p", { class: "muted", text: e.message })); return; }
  const body = h("tbody");
  const seg = h("div", { class: "seg", role: "group", "aria-label": uiText("app.021") });
  const draw = () => {
    put(seg, [["all", uiText("app.017")], ["handbook", uiText("manage.119")], ["asset", uiText("manage.115")]].map(([v, t]) =>
      h("button", { type: "button", class: MG.docs === v ? "on" : null, onclick: () => { MG.docs = v; draw(); } }, t)));
    const items = data.items.filter(x => MG.docs === "all" || x.kind_key === MG.docs);
    put(body, items.length ? items.map(x => h("tr", { class: "click", onclick: () => showDoc(x.rel) },
      h("td", {}, h("div", { class: "cell-name", text: x.title }), h("div", { class: "cell-sub mono", text: x.rel })),
      h("td", {}, h("span", { class: "pill", text: uiLabel(x.kind) })),
      h("td", {}, levelPill(x.sensitivity), x.problem ? h("span", { class: "pill red", text: uiText("manage.057") }) : null),
      h("td", { class: "hide-sm" }, x.access_note ? h("span", { class: "cell-sub", text: x.access_note }) : h("span", { class: "faint", text: "—" })),
      h("td", { class: "hide-sm nowrap" }, x.updated ? md(x.updated) : h("span", { class: "faint", text: "—" }))))
      : h("tr", {}, h("td", { colspan: 5 }, empty(uiText("manage.118"), capability("manage_company") ? uiText("manage.117") : uiText("manage.116", [ownerName()])))));
  };
  draw();
  put(page,
    h("div", { class: "page-head" }, h("div", {}, h("h1", { text: uiText("manage.115") }),
      h("p", { class: "lead", text: capability("edit_sources") ? uiText("manage.114") : uiText("manage.113") })),
      capability("manage_company") ? h("span", { class: "head-btns" }, h("button", { class: "btn", type: "button", onclick: () => newDocForm("asset") }, uiText("manage.112")),
        h("button", { class: "btn", type: "button", onclick: () => newDocForm("handbook") }, uiText("manage.111"))) : null),
    h("div", { class: "card filters" }, seg),
    h("div", { class: "card table-wrap spaced" }, h("table", { class: "t" },
      h("thead", {}, h("tr", {}, [uiText("manage.104"), uiText("app.021"), uiText("manage.090"), uiText("manage.110"), uiText("manage.109")].map((t, i) =>
        h("th", { class: i >= 3 ? "hide-sm" : null, text: t })))), body)));
}

// ---------- 决定 ----------

function newDecisionForm(meta, project = "") {
  const projects = meta.projects.filter(p => allowed(p, "record_decision", "write_company"));
  const company = capability("manage_company");
  if (!company && !projects.length) return toast(uiText("app.149"));
  const v = { title: "", decision: "", reason: "", project: project || (company ? "" : projects[0].id) };
  const level = { sensitivity: "internal", visible_to: [], access_note: "" };
  const err = h("div", { class: "form-error", hidden: true });
  const area = (key, label) => {
    const a = h("textarea", { class: "input area", rows: 5 });
    a.addEventListener("input", () => { v[key] = a.value; });
    return h("label", {}, label, a);
  };
  const title = h("input", { class: "input", oninput: e => { v.title = e.target.value; } });
  const proj = h("select", { class: "input", onchange: e => { v.project = e.target.value; } },
    company ? h("option", { value: "", text: uiText("manage.108") }) : null, projects.map(p => h("option", { value: p.id, selected: p.id === v.project, text: p.name })));
  const save = h("button", { class: "btn primary", type: "button", onclick: async () => {
    save.disabled = true; err.hidden = true;
    try {
      const selected = projects.find(p => p.id === v.project);
      await api("/api/decision/new", company ? { ...v, ...level } : { ...v, sensitivity: selected?.sensitivity === "restricted" ? "restricted" : "internal" });
      toast(uiText("manage.107")); closeLayer(); route();
    } catch (e) { if (e.message !== "401") { err.textContent = e.message; err.hidden = false; } save.disabled = false; }
  } }, uiText("manage.017"));
  drawer(uiText("manage.094"), h("h2", { text: uiText("manage.094") }),
    h("p", { class: "note", text: company ? uiText("manage.106") : uiText("manage.105") }),
    h("div", { class: "form" }, h("label", {}, uiText("manage.104"), title), area("decision", uiText("manage.103")), area("reason", uiText("manage.102")),
      h("label", {}, uiText("manage.101"), proj), company ? [h("div", { class: "block-title flush", text: uiText("manage.090") }), levelPicker(level, meta.members)] : null),
    err, h("div", { class: "form-btns" }, h("button", { class: "btn", type: "button", onclick: closeLayer }, uiText("app.121")), save));
}

async function renderDecisions() {
  const page = document.getElementById("page");
  let data;
  try { data = await api("/api/decisions"); } catch (e) { if (e.message !== "401") put(page, h("p", { class: "muted", text: e.message })); return; }
  const body = h("tbody");
  const count = h("div", { class: "count-line" });
  const draw = () => {
    const q = MG.decisionsQ.trim().toLowerCase();
    const items = data.items.filter(x => (!MG.decisionsProject || x.project?.id === MG.decisionsProject)
      && (!q || [x.title, x.decided_by, x.project?.name].join(" ").toLowerCase().includes(q)));
    count.textContent = uiText("manage.100", [items.length]);
    put(body, items.length ? items.map(x => h("tr", { class: "click", onclick: () => showDoc(x.rel) },
      h("td", { class: "nowrap" }, x.date ? md(x.date) : "—"),
      h("td", {}, h("div", { class: "cell-name", text: x.title }), x.access_note ? h("div", { class: "cell-sub", text: x.access_note }) : null),
      h("td", { class: "hide-sm" }, x.project ? h("span", { class: "pill", text: x.project.name }) : h("span", { class: "faint", text: "—" })),
      h("td", { class: "hide-sm" }, x.decided_by || "—", x.recorded_by ? h("div", { class: "cell-sub", text: uiText("manage.099", [x.recorded_by]) }) : null),
      h("td", {}, levelPill(x.sensitivity))))
      : h("tr", {}, h("td", { colspan: 5 }, empty(uiText("manage.098"), uiText("app.013")))));
  };
  const q = h("input", { class: "input grow", placeholder: uiText("manage.097"), value: MG.decisionsQ, "aria-label": uiText("manage.096") });
  q.addEventListener("input", () => { MG.decisionsQ = q.value; draw(); });
  const proj = h("select", { class: "input", "aria-label": uiText("app.008") }, h("option", { value: "", text: uiText("app.022") }),
    [...new Map([...data.projects, ...data.items.map(x => x.project).filter(Boolean)].map(p => [p.id, p])).values()]
      .map(p => h("option", { value: p.id, selected: p.id === MG.decisionsProject || null, text: p.name })));
  proj.addEventListener("change", () => { MG.decisionsProject = proj.value; draw(); });
  draw();
  put(page,
    h("div", { class: "page-head" }, h("div", {}, h("h1", { text: uiText("manage.092") }),
      h("p", { class: "lead", text: uiText("manage.095") })),
      capability("manage_company") || data.projects.some(p => allowed(p, "record_decision", "write_company")) ? h("button", { class: "btn primary", type: "button", onclick: () => newDecisionForm(data) }, uiText("manage.094")) : null),
    h("div", { class: "card filters" }, q, proj), count,
    h("div", { class: "card table-wrap" }, h("table", { class: "t" },
      h("thead", {}, h("tr", {}, [uiText("manage.093"), uiText("manage.092"), uiText("app.008"), uiText("manage.091"), uiText("manage.090")].map((t, i) =>
        h("th", { class: i === 2 || i === 3 ? "hide-sm" : null, text: t })))), body)));
}

// ---------- 成员 ----------

// 首页和成员页共用。对上 = 让这个飞书账号以那个同事的身份进网页
function feishuPendingCard(data, title) {
  if (!capability("manage_company") || !capability("feishu")) return null;
  const rows = data.feishu_pending || [];
  if (!rows.length) return null;
  const colleagues = data.items.filter(x => x.remote_enabled && x.kind === "human" && x.key && !x.problem && !x.archived);
  return h("div", { class: "card pad feishu-pending" },
    h("h2", { text: title || uiText("manage.089") }),
    colleagues.length ? null : h("p", { class: "note", text: uiText("manage.088") }),
    rows.map(p => {
      const sel = h("select", { class: "input", "aria-label": uiText("manage.087") },
        h("option", { value: "", text: uiText("manage.086") }),
        colleagues.map(m => h("option", { value: m.key, text: m.title })));
      const bind = h("button", { class: "btn small primary", type: "button", onclick: async () => {
        if (!sel.value) { toast(uiText("manage.085")); return; }
        bind.disabled = true;
        try {
          await api("/api/feishu/bind", { open_id: p.open_id, key: sel.value });
          toast(uiText("manage.084"));
          route();
        } catch (e) { if (e.message !== "401") toast(e.message); bind.disabled = false; }
      } }, uiText("manage.083"));
      const ignore = h("button", { class: "btn small", type: "button", onclick: async () => {
        ignore.disabled = true;
        try {
          await api("/api/feishu/dismiss", { open_id: p.open_id });
          toast(uiText("manage.082"));
          route();
        } catch (e) { if (e.message !== "401") toast(e.message); ignore.disabled = false; }
      } }, uiText("manage.081"));
      const when = String(p.last_seen || "").slice(0, 16).replace("T", " ");
      return h("div", { class: "token-row" },
        h("div", { class: "grow" }, h("b", { text: p.name || uiText("manage.080") }),
          h("div", { class: "cell-sub", text: when ? uiText("manage.079", [when]) : uiText("manage.078") })),
        sel, bind, ignore);
    }));
}

function archiveImpact(x, items) {
  const agents = x.kind === "human"
    ? items.filter(a => a.kind === "agent" && a.owner_key === x.key && !a.archived && !a.problem) : [];
  const keys = new Set([x.key, ...agents.map(a => a.key)]);
  const revoked = items.reduce((n, m) => (keys.has(m.key) ? n + [...(m.tokens || []), ...(m.sessions || [])].filter(isLive).length : n), 0);
  return { agents, revoked };
}

function confirmArchive(x, items) {
  const { agents, revoked } = archiveImpact(x, items);
  const names = agents.map(a => a.title).join("、");
  const msg = h("div");
  const go = h("button", { class: "btn danger", type: "button" }, uiText("apps.071"));
  openLayer(h("div", { class: "modal", role: "dialog", "aria-label": uiText("manage.077") },
    h("div", { class: "modal-head" }, h("div", {}, h("h3", { text: uiText("manage.076", [x.title]) }),
      // agent 没有名下 agent，也没有自己的待办和首页，这几句只对人说。
      h("p", { text: uiText("manage.075", [revoked])
        + (x.kind !== "human" ? "" : names ? uiText("manage.074", [names]) : uiText("manage.073"))
        + uiText("manage.072")
        + (x.kind === "human" ? uiText("manage.071") : "")
        + uiText("manage.070")
        + (x.kind === "human" ? uiText("manage.069") : uiText("punct.period")) }))),
    msg,
    h("div", { class: "form-btns" }, h("button", { class: "btn", type: "button", onclick: closeLayer }, uiText("app.121")), go)), "modal");
  go.addEventListener("click", async () => {
    go.disabled = true;
    try {
      await api(`/api/members/${encodeURIComponent(x.key)}/archive`, {});
      toast(uiText("manage.061")); closeLayer(); route();
    } catch (e) {
      if (e.message !== "401") put(msg, h("p", { class: "muted", text: e.message }));
      go.disabled = false;
    }
  });
}

async function restoreMember(x, btn) {
  btn.disabled = true;
  try {
    await api(`/api/members/${encodeURIComponent(x.key)}/restore`, {});
    toast(uiText("manage.068")); route();
  } catch (e) {
    if (e.message !== "401") toast(e.message);
    btn.disabled = false;
  }
}

async function renderMembers() {
  const page = document.getElementById("page");
  let data;
  try { data = await api("/api/members"); } catch (e) { if (e.message !== "401") put(page, h("p", { class: "muted", text: e.message })); return; }
  if (!capability("manage_company")) {
    put(page, h("h1", { text: uiText("app.144") }), h("p", { class: "lead", text: uiText("manage.067") }),
      h("div", { class: "member-grid" }, data.items.map(x => h("button", { class: "card member", type: "button", onclick: () => drawer(uiText("app.144"), h("h2", { text: x.name }), h("p", { text: x.role || uiText("manage.063") }), h("p", { text: x.kind === "human" ? uiText("manage.062") : "Agent" })) },
        h("div", { class: "who" }, avatar({ key: x.key, name: x.name }, "lg"), h("b", { text: x.name })),
        h("div", { class: "member-role", text: x.role || uiText("manage.063") }), h("span", { class: "pill", text: x.kind === "human" ? uiText("manage.062") : "Agent" })))));
    return;
  }
  const archived = data.items.filter(x => x.archived);
  const shown = data.items.filter(x => !x.archived || MG.showArchived);
  put(page,
    h("div", { class: "page-head" }, h("div", {}, h("h1", { text: uiText("app.144") }),
      h("p", { class: "lead", text: uiText("manage.066") })),
      h("button", { class: "btn", type: "button", onclick: () => newDocForm("person") }, uiText("manage.065"))),
    feishuPendingCard(data),
    h("div", { class: "member-grid" }, shown.map(x => h("div", { class: "card member-wrap" + (x.archived ? " archived" : "") },
      h("button", { class: "card member", type: "button", onclick: () => showDoc(x.rel) },
        h("div", { class: "who" }, avatar({ key: x.key, name: x.name }, "lg"),
          h("div", {}, h("div", { class: "cell-name", text: x.title }), h("div", { class: "cell-sub mono", text: uiText("apps.015", [x.key || uiText("manage.064")]) }))),
        h("div", { class: "member-role", text: x.role || uiText("manage.063") }),
        h("div", { class: "app-meta" }, h("span", { class: "pill", text: x.kind === "human" ? uiText("manage.062") : uiText("label.agent") }),
          x.archived ? h("span", { class: "pill", text: uiText("manage.061") }) : null,
          x.remote_enabled ? h("span", { class: "pill accent", text: uiText("manage.060") }) : null,
          h("span", { class: "pill" + (x.can_write ? " accent" : ""), text: x.can_write ? uiText("manage.059") : uiText("manage.058") }),
          x.problem ? h("span", { class: "pill red", text: uiText("manage.057") }) : null),
        h("div", { class: "cell-sub wrap", text: x.access_mode === "full" ? uiText("app.017") : colleagueScope(x) }),
        x.connect ? h("div", { class: "cell-sub wrap", text: x.connect }) : null),
      h("div", { class: "member-actions" },
        x.archived ? h("button", { class: "btn small", type: "button", onclick: e => restoreMember(x, e.currentTarget) }, uiText("manage.056")) : null,
        capability("feishu") && !x.archived && x.remote_enabled && x.feishu_bound ? h("span", { class: "member-feishu", text: uiText("manage.055") }) : null,
        capability("feishu") && !x.archived && x.remote_enabled && x.feishu_bound ? h("button", { class: "btn small", type: "button", onclick: async e => {
          e.target.disabled = true;
          try { await api("/api/feishu/unbind", { key: x.key }); toast(uiText("manage.054")); route(); }
          catch (err) { if (err.message !== "401") toast(err.message); e.target.disabled = false; }
        } }, uiText("manage.053")) : null,
        !x.archived && x.remote_enabled ? h("button", { class: "btn small member-tokens", type: "button", onclick: () => tokenDrawer({ ...x, remote: data.remote }, data.items) },
          uiText("manage.052", [x.tokens.filter(isLive).length])) : null,
        !x.archived && !(S.me?.is_owner && x.key === S.me.key) && !x.problem ? h("button", { class: "btn small", type: "button", onclick: () => confirmArchive(x, data.items) }, uiText("apps.071")) : null)))),
    h("div", { class: "card filters" }, h("button", { class: "btn" + (MG.showArchived ? " on" : ""), type: "button",
      onclick: () => { MG.showArchived = !MG.showArchived; renderMembers(); } }, uiText("manage.051", [archived.length]))));
}

function colleagueScope(x) {
  const granted = [...x.projects, ...x.assets, ...(x.skills || []).map(s => `Skill：${s}`)];
  if (x.owner) return uiText("manage.050", [x.owner]) + (granted.length ? uiText("manage.049", [granted.join("、")]) : "");
  return uiText("manage.047", [granted.join("、") || uiText("manage.048")]);
}

// 口令记录目前只有发证人的身份键。名字能从当前登录者、口令所属的人，或本页已经载入的成员名单里对上时就用名字。
function personName(key, directory) {
  if (!key) return uiText("manage.046");
  const hit = (directory || []).find(m => m && m.key === key);
  const raw = hit?.name || hit?.title || (S.me?.key === key ? S.me.name : "");
  const name = raw ? String(raw).split(/[（(]/)[0].trim() : "";
  return name || key;
}

// 服务端给的 live 为准：源口令收回或过期后，网页会话自己没标收回也算失效。旧数据没有 live 时按 revoked 判断。
const isLive = t => t.live ?? !t.revoked;
const stamp = v => String(v || "").slice(0, 16).replace("T", " ");

function sessionList(rows) {
  rows = (rows || []).filter(t => capability("feishu") || (t.kind !== "feishu" && t.auth_method !== "feishu"));
  if (!rows.length) return null;
  return [h("div", { class: "block-title flush", text: uiText("manage.040") }),
    h("div", { class: "token-list" }, rows.map(t => h("div", { class: "token-row" + (isLive(t) ? "" : " revoked") },
      h("div", { class: "grow" }, h("b", { text: t.kind === "oauth" ? uiText("manage.045") : t.kind === "feishu" || t.auth_method === "feishu" ? uiText("manage.041") : uiText("manage.044") }),
        h("div", { class: "cell-sub", text: uiText("manage.043", [stamp(t.created)])
          + (t.expires ? uiText("manage.038", [stamp(t.expires)]) : "")
          + (t.revoked ? uiText("manage.037", [stamp(t.revoked)]) : isLive(t) ? "" : uiText("manage.036")) })))))];
}

// 同事和其 Agent 的口令：发一个（只显示这一次）、收回。口令只存在本机，不进 git。
function tokenDrawer(x, directory) {
  let ticket;
  const refreshPage = () => {
    const page = parseRoute(location.hash).page;
    if (page === "agents") renderAgents();
    else if (page === "members") renderMembers();
  };
  const label = h("input", { class: "input grow", placeholder: uiText("manage.042"), maxlength: 60 });
  const shown = h("div");
  const list = h("div", { class: "token-list" });
  const drawList = rows => put(list, rows.length ? rows.map(t => h("div", { class: "token-row" + (isLive(t) ? "" : " revoked") },
    h("div", { class: "grow" }, h("b", { text: t.kind === "feishu" ? (capability("feishu") ? uiText("manage.041") : uiText("manage.040")) : t.label }),
      h("div", { class: "cell-sub", text: uiText("manage.039", [stamp(t.created), t.tail])
        + (t.expires ? uiText("manage.038", [stamp(t.expires)]) : "")
        + (t.revoked ? uiText("manage.037", [stamp(t.revoked)]) : isLive(t) ? "" : uiText("manage.036"))
        + uiText("manage.035", [t.issued_by_name || personName(t.issued_by, directory)]) })),
    !isLive(t) || !allowed(x, "revoke_token", "manage_own_agent_tokens") ? null : h("button", { class: "btn small", type: "button", onclick: async e => {
      e.target.disabled = true;
      try { await api("/api/tokens/revoke", { id: t.id }); if (ticket !== layerVersion) return; toast(uiText("manage.034")); t.revoked = uiText("manage.033"); t.live = false; drawList(rows); refreshPage(); }
      catch (err) { if (err.message !== "401") toast(err.message); e.target.disabled = false; }
    } }, uiText("manage.032")))) : h("p", { class: "muted", text: uiText("manage.031") }));
  const issue = h("button", { class: "btn primary", type: "button", onclick: async () => {
    issue.disabled = true;
    try {
      const r = await api("/api/tokens/issue", { key: x.key, label: label.value });
      if (ticket !== layerVersion) return;
      x.tokens = [...x.tokens, r.record]; drawList(x.tokens); label.value = ""; refreshPage();
      put(shown, h("div", { class: "box accent", "data-secret": true },
        h("p", { text: uiText("manage.030") }),
        h("div", { class: "block-title flush", text: uiText("manage.024") }), copyBox(r.token),
        S.me.remote || x.kind === "agent" ? null : h("p", { class: "note", text: uiText("manage.029", [r.urls.web]) }),
        h("div", { class: "block-title flush", text: (S.me.remote ? uiText("manage.028") : uiText("manage.027")) + uiText("manage.026") }),
        copyBox(uiText("token.http", [r.urls.mcp, r.token]))));
    } catch (e) { if (e.message !== "401") toast(e.message); }
    issue.disabled = false;
  } }, uiText("manage.025"));
  drawList(x.tokens);
  drawer(uiText("manage.024"), h("h2", { text: uiText("manage.023", [x.title]) }),
    h("p", { class: "note", text: S.me.remote ? uiText("manage.022") : uiText("manage.021") }),
    allowed(x, "issue_token", "manage_own_agent_tokens") ? h("div", { class: "inline-form" }, label, issue) : null, shown,
    h("div", { class: "block-title flush", text: uiText("manage.020") }), list,
    sessionList(x.sessions),
    S.me.remote ? null : h("p", { class: "note faint", text: x.remote ? uiText("manage.019", [x.remote.web, x.remote.mcp]) : "" }));
  ticket = layerVersion;
}

function projectStatusForm(p) {
  const text = h("textarea", { class: "input area", rows: 8, "aria-label": uiText("app.107"), autofocus: true });
  const error = h("div", { class: "form-error", hidden: true });
  const save = h("button", { class: "btn primary", type: "button", onclick: async () => {
    save.disabled = true;
    try { await api(`/api/project/${encodeURIComponent(p.id)}/status`, { text: text.value }); toast(uiText("manage.018")); S.rows = S.home = null; await route(); await showProject(p.id); }
    catch (e) { if (e.message !== "401") { error.textContent = e.message; error.hidden = false; } save.disabled = false; }
  } }, uiText("manage.017"));
  drawer(uiText("app.119"), h("h2", { text: p.title || p.name }), h("div", { class: "form" }, h("label", {}, uiText("manage.016"), text)), error,
    h("div", { class: "form-btns" }, h("button", { class: "btn", type: "button", onclick: closeLayer }, uiText("app.121")), save));
}

// ---------- 待批准 ----------

async function renderApprovals() {
  const page = document.getElementById("page");
  if (!capability("approve_handovers")) { put(page, empty(uiText("app.005"), "")); return; }
  let data;
  try { data = await api("/api/approvals"); } catch (e) { if (e.message !== "401") put(page, h("p", { class: "muted", text: e.message })); return; }
  const decide = async (p, approve, note, btns) => {
    btns.forEach(b => { b.disabled = true; });
    try {
      const r = await api("/api/handover/decide", { project: p.project, role: p.role, to: p.to, approve, note });
      toast(approve ? uiText("manage.015") : uiText("manage.014")); loadMe(); route();
      return r;
    } catch (e) { if (e.message !== "401") toast(e.message); btns.forEach(b => { b.disabled = false; }); }
  };
  const card = p => {
    const note = h("input", { class: "input grow", placeholder: uiText("manage.013"), maxlength: 300 });
    const yes = h("button", { class: "btn primary", type: "button" }, uiText("manage.012"));
    const no = h("button", { class: "btn", type: "button" }, uiText("manage.011"));
    yes.addEventListener("click", () => decide(p, true, note.value, [yes, no]));
    no.addEventListener("click", () => decide(p, false, note.value, [yes, no]));
    return h("div", { class: "card pad approval" },
      h("div", { class: "approval-head" }, h("span", { class: "pill accent", text: uiText("manage.010") }),
        h("button", { class: "link", type: "button", onclick: () => showProject(p.project), text: p.project_name })),
      h("div", { class: "approval-title", text: uiText("manage.009", [p.role, p.from, p.to]) }),
      h("div", { class: "note faint", text: uiText("manage.008", [p.head]) }),
      h("div", { class: "form-btns left" }, note, no, yes));
  };
  put(page,
    h("div", { class: "page-head" }, h("div", {}, h("h1", { text: uiText("manage.007") }),
      h("p", { class: "lead", text: uiText("manage.006") }))),
    h("div", { class: "stack spaced" },
      data.handovers.map(card),
      data.draft ? h("div", { class: "card pad approval" },
        h("div", { class: "approval-head" }, h("span", { class: "pill accent", text: uiText("manage.005") })),
        h("div", { class: "approval-title", text: data.draft.note || uiText("app.050") }),
        h("div", { class: "note faint", text: uiText("manage.004", [data.draft.by_name || uiText("apps.023")]) }),
        h("div", { class: "form-btns left" }, h("a", { class: "btn primary", href: "#/" }, uiText("manage.003")))) : null,
      !data.handovers.length && !data.draft ? h("div", { class: "card" }, empty(uiText("manage.002"), uiText("manage.001"))) : null));
}
