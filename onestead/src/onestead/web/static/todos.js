"use strict";
// 待办页和首页的“今天的待办”。用 app.js 里的 h / put / api / drawer 等小工具。

const TD = { data: null, view: "month", cursor: null, picked: null, showDone: false };
onWorkspaceReset(() => Object.assign(TD, { data: null, view: "month", cursor: null, picked: null, showDone: false }));
const todoGroups = () => [["overdue", uiText("todos.084")], ["today", uiText("app.068")], ["week", uiText("todos.083")], ["later", uiText("todos.082")], ["nodate", uiText("todos.081")]];
const weekdays = () => [uiText("todos.080"), uiText("todos.079"), uiText("todos.078"), uiText("todos.077"), uiText("todos.076"), uiText("todos.075"), uiText("todos.074")];
const REPEAT_NAMES = translatedLabels({ daily: "todos.073", weekdays: "todos.072", weekly: "todos.071", monthly: "todos.070", yearly: "todos.069" });
const ASK_NAMES = translatedLabels({ approve: "ask.approve", act: "ask.act", fyi: "ask.fyi" });
const ASK_FIELD_NAMES = translatedLabels({ context: "ask.context", why: "ask.why", if_skipped: "ask.if_skipped", action: "ask.action" });

const isoDay = d => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
const fromIso = s => { const [y, m, d] = s.split("-").map(Number); return new Date(y, m - 1, d); };
const addDays = (d, n) => { const x = new Date(d); x.setDate(x.getDate() + n); return x; };
const monday = d => addDays(d, -((d.getDay() + 6) % 7));

async function loadTodos() {
  TD.data = await api("/api/todos");
  if (!TD.cursor) TD.cursor = fromIso(TD.data.today);
  return TD.data;
}

async function saveTodo(id, changes, okText) {
  try {
    await api(id ? `/api/todos/${id}` : "/api/todos/add", changes);
    if (okText) toast(okText);
    return true;
  } catch (e) {
    if (e.message !== "401") toast(e.message);
    return false;
  }
}

function whenText(t, today) {
  if (!t.due) return null;
  const label = t.due === today ? uiText("app.068") : t.due === isoDay(addDays(fromIso(today), 1)) ? uiText("todos.068") : md(t.due);
  return label + (t.time ? ` ${t.time}` : "");
}

function checkButton(t, after) {
  if (!allowed(t, "edit", "write_own_todos")) return null;
  const done = t.status === "done";
  return h("button", {
    class: "check" + (done ? " on" : ""), type: "button", "aria-label": done ? uiText("todos.067") : uiText("todos.066"),
    onclick: async e => {
      e.stopPropagation();
      const btn = e.currentTarget, row = btn.closest(".todo-row");
      if (btn.disabled) return;
      // 写入要提交 git、再重取整页，真实数据要几秒；先把勾打上，失败再退回，免得看起来没点上。
      const show = on => { btn.classList.toggle("on", on); put(btn, on ? icon(ICON.check) : null); row?.classList.toggle("closed", on); };
      btn.disabled = true;
      show(!done);
      const ok = done ? uiText("todos.065") : t.repeat ? uiText("todos.049") : uiText("todos.048");
      if (await saveTodo(t.id, { status: done ? "open" : "done" }, ok)) after();
      else { btn.disabled = false; show(done); }
    },
  }, done ? icon(ICON.check) : null);
}

function todoRow(t, today, after) {
  const when = whenText(t, today);
  return h("div", { class: "todo-row" + (t.status !== "open" ? " closed" : ""), role: "button", tabindex: "0",
                    onclick: () => { if (allowed(t, "edit", "write_own_todos")) editTodo(t, after); } },
    checkButton(t, after),
    h("div", { class: "grow" },
      h("div", { class: "todo-title", text: t.title }),
      (when || t.project || (t.created_by && t.created_by !== DATA_VALUE.self) || t.notes) ? h("div", { class: "todo-sub" },
        when ? h("span", { class: t.bucket === "overdue" ? "red-text" : null, text: when }) : null,
        t.repeat ? h("span", { class: "pill", text: uiText("todos.064", [REPEAT_NAMES[t.repeat]]) }) : null,
        t.project ? h("span", { class: "pill", text: t.project.name }) : null,
        !t.ask && t.created_by && t.created_by !== DATA_VALUE.self ? h("span", { text: uiText("todos.058", [t.created_by]) }) : null,
        t.notes ? h("span", { class: "faint", text: uiText("todos.063") }) : null) : null,
      t.ask ? askInfo(t, after) : null));
}

function askInfo(t, after) {
  const fields = ["context", "why", "if_skipped", "action"];
  const name = value => value === DATA_VALUE.self ? uiText("label.you") : value;
  const state = t.notice === "closed" ? uiText(t.status === "done" ? "todos.047" : "todos.050")
    : t.notice === "notified" ? uiText("ask.notified", [name(t.notified_by), t.notified_at]) : uiText("ask.unnotified");
  return h("div", { class: "ask-info", onclick: e => e.stopPropagation() },
    h("div", { class: "todo-sub" },
      h("span", { class: "pill live", text: ASK_NAMES[t.ask] }),
      h("span", { text: uiText("ask.registered", [name(t.created_by)]) }),
      h("span", { class: t.notice === "unnotified" ? "ask-pending" : "faint", text: state })),
    h("details", { class: "ask-details", open: fields.reduce((n, key) => n + t[key].length, 0) <= 360 || null },
      h("summary", { text: uiText("ask.details") }),
      fields.map(key => h("section", {}, h("h4", { text: ASK_FIELD_NAMES[key] }), h("p", { text: t[key] })))),
    t.notice === "unnotified" && allowed(t, "edit", "write_own_todos")
      ? h("button", { class: "btn small", type: "button", onclick: async () => {
        if (await saveTodo(t.id, { notified: true })) after();
      } }, uiText("ask.acknowledge")) : null);
}

function repeatSelect(value) {
  return h("select", { class: "input", "aria-label": uiText("todos.056") },
    h("option", { value: "", text: uiText("todos.062") }),
    Object.entries(REPEAT_NAMES).map(([k, v]) => h("option", { value: k, selected: k === value || null, text: v })));
}

function projectSelect(projects, value) {
  return h("select", { class: "input", "aria-label": uiText("todos.061") },
    h("option", { value: "", text: uiText("manage.108") }),
    projects.map(p => h("option", { value: p.id, selected: p.id === value, text: p.name })));
}

async function editTodo(t, after) {
  if (!allowed(t, "edit", "write_own_todos")) return toast(uiText("app.149"));
  if (!TD.data) { try { await loadTodos(); } catch { return; } }
  const { projects } = TD.data;
  const title = h("input", { class: "input", value: t.title, maxlength: "80", "aria-label": uiText("manage.104") });
  const due = h("input", { class: "input", type: "date", value: t.due || "", "aria-label": uiText("manage.093") });
  const time = h("input", { class: "input", type: "time", value: t.time || "", "aria-label": uiText("app.011") });
  const proj = projectSelect(projects, t.project?.id);
  const repeat = repeatSelect(t.repeat || "");
  const notes = h("textarea", { class: "input area", rows: "5", placeholder: uiText("todos.060"), "aria-label": uiText("todos.054") });
  notes.value = t.notes || "";
  const askFields = {};
  const askKind = t.ask ? h("select", { class: "input", "aria-label": uiText("ask.category") },
    ["approve", "act", "fyi"].map(value => h("option", { value, selected: value === t.ask || null, text: ASK_NAMES[value] }))) : null;
  if (t.ask) for (const key of ["context", "why", "if_skipped", "action"]) {
    askFields[key] = h("textarea", { class: "input area", rows: "3", maxlength: key === "context" ? "1000" : "400",
      "aria-label": ASK_FIELD_NAMES[key] });
    askFields[key].value = t[key];
  }
  const save = async (extra, text) => {
    const changes = { title: title.value, due: due.value, time: time.value, project: proj.value, repeat: repeat.value,
                      notes: notes.value, ...extra };
    if (t.ask) Object.assign(changes, { ask: askKind.value }, Object.fromEntries(Object.entries(askFields).map(([k, el]) => [k, el.value])));
    if (await saveTodo(t.id, changes, text)) { closeLayer(); after(); }
  };
  drawer(uiText("todos.008"),
    h("h2", { text: t.title }),
    h("div", { class: "meta-line" },
      h("span", { class: "pill" + (t.status === "open" ? " live" : ""), text: { open: uiText("todos.059"), done: uiText("todos.047"), dropped: uiText("todos.050") }[t.status] }),
      t.created_by ? h("span", { text: uiText("todos.058", [t.created_by]) + (t.created ? ` · ${md(t.created)}` : "") }) : null,
      t.done_at ? h("span", { text: uiText("todos.057", [md(t.done_at)]) }) : null),
    h("div", { class: "form" },
      h("label", {}, h("span", { text: uiText("manage.104") }), title),
      h("div", { class: "form-row" }, h("label", {}, h("span", { text: uiText("manage.093") }), due), h("label", {}, h("span", { text: uiText("app.011") }), time)),
      h("div", { class: "form-row" }, h("label", {}, h("span", { text: uiText("app.008") }), proj),
        t.ask ? h("label", {}, h("span", { text: uiText("ask.category") }), askKind) : h("label", {}, h("span", { text: uiText("todos.056") }), repeat)),
      t.ask ? Object.entries(askFields).map(([key, el]) => h("label", {}, h("span", { text: ASK_FIELD_NAMES[key] }), el)) : null,
      t.repeat ? h("p", { class: "note faint", text: uiText("todos.055") }) : null,
      h("label", {}, h("span", { text: uiText("todos.054") }), notes)),
    h("div", { class: "form-btns" },
      t.repeat ? h("button", { class: "btn", type: "button", onclick: () => save({ repeat: "" }, uiText("todos.053")) }, uiText("todos.052")) : null,
      t.status === "open"
        ? [h("button", { class: "btn", type: "button", onclick: () => save({ status: "dropped" }, t.repeat ? uiText("todos.051") : uiText("todos.050")) }, uiText("todos.050")),
           h("button", { class: "btn", type: "button", onclick: () => save({ status: "done" }, t.repeat ? uiText("todos.049") : uiText("todos.048")) }, icon(ICON.check), uiText("todos.047"))]
        : h("button", { class: "btn", type: "button", onclick: () => save({ status: "open" }, uiText("todos.046")) }, uiText("todos.045")),
      h("button", { class: "btn primary", type: "button", onclick: () => save({}, uiText("todos.044")) }, uiText("manage.158"))),
    h("details", { class: "tech" }, h("summary", { text: uiText("app.101") }), h("span", { class: "mono", text: t.rel })));
}

function quickAdd(projects, after, presetDay) {
  const title = h("input", { class: "input grow", placeholder: uiText("todos.043"), maxlength: "80", "aria-label": uiText("todos.042") });
  const due = h("input", { class: "input", type: "date", value: presetDay || "", "aria-label": uiText("manage.093") });
  const time = h("input", { class: "input", type: "time", "aria-label": uiText("app.011") });
  const proj = projectSelect(projects, "");
  const repeat = repeatSelect("");
  const go = async () => {
    if (!title.value.trim()) { title.focus(); return; }
    if (repeat.value && !due.value) { toast(uiText("todos.041")); due.focus(); return; }
    const ok = await saveTodo(null, { title: title.value, due: due.value, time: time.value, project: proj.value,
                                      repeat: repeat.value }, uiText("todos.040"));
    if (ok) { title.value = ""; repeat.value = ""; after(); }
  };
  const addBtn = h("button", { class: "btn primary", type: "button", onclick: go }, uiText("todos.039"));
  title.addEventListener("keydown", e => { if (e.key === "Enter" && !e.isComposing) addBtn.click(); });
  const box = h("div", { class: "card filters quick" }, title, due, time, repeat, proj, addBtn);
  box.setDay = d => { due.value = d || ""; title.focus(); };
  return box;
}

function openOrganizeHelp() {
  const prompt = uiText("todos.038", [appName()]);
  openLayer(h("div", { class: "modal", role: "dialog", "aria-label": uiText("todos.006") },
    h("div", { class: "modal-head" }, h("div", {}, h("h3", { text: uiText("todos.037") }),
      h("p", { text: S.me.remote ? uiText("todos.036") : uiText("todos.035") })),
      h("button", { class: "btn ghost x", type: "button", "aria-label": uiText("app.041"), onclick: closeLayer }, icon(ICON.x))),
    h("div", { class: "steps" },
      h("div", { class: "copy" }, h("span", { text: prompt }), h("button", { class: "btn", type: "button", onclick: async () => {
        try { await navigator.clipboard.writeText(prompt); toast(uiText("app.045")); } catch { toast(uiText("app.044")); }
      } }, icon(ICON.copy), uiText("app.043"))),
      h("p", { class: "note faint", text: uiText("todos.034") }))), "modal");
}

// ---------- 日历 ----------

function calendar(items, today, onPick) {
  const byDay = {};
  for (const t of items) if (t.due && t.status !== "dropped") (byDay[t.due] ||= []).push(t);
  const body = h("div", { class: "cal-body" });
  const label = h("b", { class: "cal-label" });
  const seg = h("div", { class: "seg", role: "group", "aria-label": uiText("todos.033") });
  const draw = () => {
    put(seg, [["today", uiText("app.068")], ["week", uiText("todos.027")], ["month", uiText("todos.032")]].map(([v, t]) =>
      h("button", { type: "button", class: TD.view === v ? "on" : null, onclick: () => { TD.view = v; TD.cursor = fromIso(today); draw(); } }, t)));
    const cur = TD.cursor;
    if (TD.view === "month") {
      label.textContent = uiText("todos.031", [cur.getFullYear(), cur.getMonth() + 1]);
      const first = new Date(cur.getFullYear(), cur.getMonth(), 1);
      const start = monday(first);
      const daysIn = new Date(cur.getFullYear(), cur.getMonth() + 1, 0).getDate();
      const weeks = Math.ceil(((first.getDay() + 6) % 7 + daysIn) / 7);
      const cells = [];
      for (let i = 0; i < weeks * 7; i++) {
        const d = addDays(start, i), key = isoDay(d), list = byDay[key] || [];
        cells.push(h("button", {
          type: "button", class: "cal-cell" + (d.getMonth() !== cur.getMonth() ? " out" : "") + (key === today ? " today" : "")
            + (key === TD.picked ? " picked" : ""),
          onclick: () => { TD.picked = TD.picked === key ? null : key; onPick(TD.picked); draw(); },
          "aria-label": uiText("todos.030", [d.getMonth() + 1, d.getDate(), list.length]),
        }, h("span", { class: "cal-day", text: d.getDate() }),
          list.slice(0, 2).map(t => h("span", { class: "cal-item" + (t.status === "done" ? " done" : ""), text: t.title })),
          list.length > 2 ? h("span", { class: "cal-more", text: uiText("todos.029", [list.length - 2]) }) : null));
      }
      put(body, h("div", { class: "cal-grid" }, weekdays().map(w => h("span", { class: "cal-head", text: w })), cells));
    } else {
      const days = TD.view === "today" ? [cur] : [...Array(7)].map((_, i) => addDays(monday(cur), i));
      const isThis = TD.view === "today" ? isoDay(cur) === today : days.some(d => isoDay(d) === today);
      label.textContent = TD.view === "today" ? `${isThis ? uiText("app.068") : uiText("todos.028")} · ${md(isoDay(cur))}`
        : uiText("todos.025", [isThis ? uiText("todos.027") : uiText("todos.026"), md(isoDay(days[0])), md(isoDay(days[6]))]);
      put(body, h("div", { class: "cal-list" }, days.map(d => {
        const key = isoDay(d), list = (byDay[key] || []).slice().sort((a, b) => (a.time || "").localeCompare(b.time || ""));
        return h("div", { class: "cal-row" + (key === today ? " today" : "") },
          h("div", { class: "cal-date" }, h("b", { text: uiText("todos.024", [weekdays()[(d.getDay() + 6) % 7]]) }), h("span", { text: md(key) })),
          h("div", { class: "grow" }, list.length ? list.map(t => h("div", {
            class: "cal-line" + (t.status === "done" ? " done" : ""), role: "button", tabindex: "0",
            onclick: () => editTodo(t, renderTodos),
          }, h("span", { class: "faint", text: t.time || uiText("todos.023") }), t.title)) : h("span", { class: "faint small", text: uiText("todos.022") })));
      })));
    }
  };
  const nav = (n) => {  // 月视图翻月，本周翻周，今天翻天
    const c = TD.cursor;
    TD.cursor = TD.view === "month" ? new Date(c.getFullYear(), c.getMonth() + n, 1)
      : addDays(c, TD.view === "week" ? 7 * n : n);
    draw();
  };
  const head = h("div", { class: "cal-headbar" },
    h("button", { class: "btn ghost", type: "button", "aria-label": uiText("todos.021"), onclick: () => nav(-1) }, icon("M10 3.5 5.5 8 10 12.5")),
    label,
    h("button", { class: "btn ghost", type: "button", "aria-label": uiText("todos.020"), onclick: () => nav(1) }, icon(ICON.chev)),
    h("button", { class: "btn ghost small", type: "button", onclick: () => { TD.cursor = fromIso(today); draw(); } }, uiText("todos.019")),
    h("span", { class: "grow" }), seg);
  draw();
  return h("div", { class: "card cal" }, head, body);
}

// ---------- 页面 ----------

async function renderTodos() {
  const page = document.getElementById("page");
  let data;
  try { data = await loadTodos(); } catch (e) {
    if (e.message !== "401") put(page, h("p", { class: "muted", text: e.message }));
    return;
  }
  const { items, today, projects } = data;
  let hasAgent = false;
  try { hasAgent = ((await api("/api/agents")).agents || []).length > 0; }
  catch (e) { if (e.message === "401") return; }
  const open = items.filter(t => t.status === "open");
  const asks = open.filter(t => t.ask).sort((a, b) =>
    Number(a.notice !== "unnotified") - Number(b.notice !== "unnotified") || a.created.localeCompare(b.created) || a.id.localeCompare(b.id));
  const closed = items.filter(t => t.status !== "open").sort((a, b) => (b.done_at || b.created).localeCompare(a.done_at || a.created));
  const add = quickAdd(projects, renderTodos, TD.picked);
  const list = h("div", { class: "card todo-list" });
  const drawList = () => {
    const parts = [];
    if (asks.length) parts.push(h("div", { class: "todo-group" }, uiText("ask.group"), h("span", { class: "n", text: asks.length })),
      asks.map(t => todoRow(t, today, renderTodos)));
    const shown = open.filter(t => !t.ask && (!TD.picked || t.due === TD.picked));
    if (TD.picked) parts.push(h("div", { class: "todo-filter" }, uiText("todos.018", [md(TD.picked)]),
      h("button", { class: "btn ghost small", type: "button", onclick: () => { TD.picked = null; renderTodos(); } }, uiText("todos.017"))));
    for (const [key, name] of todoGroups()) {
      const g = shown.filter(t => t.bucket === key);
      if (!g.length) continue;
      parts.push(h("div", { class: "todo-group" + (key === "overdue" ? " red" : "") }, name, h("span", { class: "n", text: g.length })),
        g.map(t => todoRow(t, today, renderTodos)));
    }
    if (!shown.length && !asks.length) parts.push(empty(TD.picked ? uiText("todos.016") : uiText("todos.015"), TD.picked ? uiText("todos.014") : (hasAgent ? uiText("todos.013") : uiText("todos.012"))));
    if (closed.length && !TD.picked) {
      parts.push(h("button", { class: "todo-more", type: "button", onclick: () => { TD.showDone = !TD.showDone; drawList(); } },
        TD.showDone ? uiText("todos.011") : uiText("todos.010", [closed.length])));
      if (TD.showDone) parts.push(closed.slice(0, 50).map(t => todoRow(t, today, renderTodos)));
    }
    put(list, parts);
  };
  drawList();
  const cal = calendar(items, today, day => { add.setDay(day); drawList(); });
  put(page,
    h("div", { class: "page-head" },
      h("div", {}, h("h1", { text: S.me.remote ? uiText("todos.009") : uiText("todos.008") }),
        h("p", { class: "lead", text: uiText("todos.007", [md(today), open.length]) +
          (open.filter(t => t.bucket === "overdue").length ? uiText("app.065", [open.filter(t => t.bucket === "overdue").length]) : uiText("punct.period")) })),
      hasAgent ? h("button", { class: "btn", type: "button", onclick: openOrganizeHelp }, icon(ICON.copy), uiText("todos.006")) : null),
    capability("write_own_todos") ? add : null,
    h("div", { class: "todo-grid" }, list, cal));
}

// 首页模块：今天的待办（含逾期），能直接勾掉
MODULES.today_todos = (m) => {
  const items = m.data.items;
  const today = dayKey(new Date().toISOString());
  const after = () => renderHome();
  return [secHead(m.name, items.length ? uiText("app.094", [items.length]) : null, h("a", { class: "more", href: "#/todos", text: uiText("todos.005") })),
    h("div", { class: "card" }, items.length
      ? items.map(t => todoRow(t, today, after))
      : empty(uiText("todos.004"), m.data.week ? uiText("todos.003", [m.data.week]) : uiText("todos.002")),
      items.length && m.data.week ? h("div", { class: "todo-foot", text: uiText("todos.001", [m.data.week]) }) : null)];
};
