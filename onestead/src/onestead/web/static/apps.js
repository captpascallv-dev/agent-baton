"use strict";
// 应用广场、我的 Agent，以及首页的“常用应用”。用 app.js 里的小工具。

const AP = { apps: null, probe: {}, kind: "", project: "", q: "", showUnlisted: false };
onWorkspaceReset(() => Object.assign(AP, { apps: null, probe: {}, kind: "", project: "", q: "", showUnlisted: false }));
const STATE_TEXT = translatedLabels({ online: "apps.104", offline: "apps.103", running: "apps.102", stopped: "apps.101", unknown: "apps.100" });
const OPENABLE = /\.(exe|lnk|html?|pdf|url)$/i;

function copyBox(text) {
  const value = h("span", { text });
  return h("div", { class: "copy" }, value, h("button", { class: "btn", type: "button", onclick: async () => {
    try { await navigator.clipboard.writeText(value.textContent); toast(uiText("app.045")); } catch { toast(uiText("app.044")); }
  } }, icon(ICON.copy), uiText("app.043")));
}

function promptModal(title, text, prompt, extra) {
  openLayer(h("div", { class: "modal", role: "dialog", "aria-label": title },
    h("div", { class: "modal-head" }, h("div", {}, h("h3", { text: title }), h("p", { text })),
      h("button", { class: "btn ghost x", type: "button", "aria-label": uiText("app.041"), onclick: closeLayer }, icon(ICON.x))),
    h("div", { class: "steps" }, copyBox(prompt), extra || null)), "modal");
}

function publishPrompt(a) {
  const domain = S.me.site_domain;
  let slug = a?.site?.slug || a?.id || uiText("apps.099");
  if (a && (!/^[a-z0-9][a-z0-9-]{1,28}[a-z0-9]$/.test(slug) || /^(www|mail|api|admin|auth|login|base|app|apps|static|cdn|mcp|sites)$/.test(slug))) slug = `web-${a.id}`.slice(0, 30).replace(/-$/, "");
  return uiText("apps.095", [appName(), a?.id || uiText("apps.096"), a?.site?.slug ? uiText("apps.098") : uiText("apps.097", [slug]), slug, domain]);
}

function publishButton(a) {
  if (!capability("manage_company") || !S.me.site_domain || (a && a.kind !== DATA_VALUE.web)) return null;
  return h("button", { class: "btn", type: "button", onclick: () => promptModal(uiText("apps.092"),
    uiText("apps.094"), publishPrompt(a)) },
    icon(ICON.copy), a ? uiText("apps.093") : uiText("apps.092"));
}

function sitePanel(a) {
  if (!capability("manage_company") || !S.me.site_domain || a.kind !== DATA_VALUE.web) return null;
  const site = a.site;
  if (!site) return h("section", { class: "site-panel" }, h("h3", { text: uiText("apps.083") }), publishButton(a),
    h("p", { class: "note", text: uiText("apps.091") }));
  const error = h("p", { class: "form-error", hidden: true, role: "alert" });
  const act = async data => {
    error.hidden = true;
    try {
      const result = await api(`/api/apps/${a.id}/site`, data);
      toast(uiLabel(result.message));
      if (result.url) {
        drawer(uiText("apps.072"), h("h2", { text: a.title }), h("p", { text: uiLabel(result.message) }),
          h("div", { "data-secret": "true" },
            result.qr_svg ? h("div", { class: "phone-qr" },
              h("img", { src: "data:image/svg+xml;charset=utf-8," + encodeURIComponent(result.qr_svg), alt: uiText("apps.phone-qr") }),
              h("p", { class: "note", text: uiText("apps.phone-scan") })) : null,
            copyBox(result.url)));
      } else {
        await loadApps(); showApp(AP.apps.find(row => row.id === a.id));
      }
    } catch (e) { if (e.message !== "401") { error.textContent = e.message; error.hidden = false; } }
  };
  const visibility = h("select", { class: "input", "aria-label": uiText("apps.081") },
    [DATA_VALUE.selected, DATA_VALUE.teammates, DATA_VALUE.public].map(value => h("option", { value, selected: value === site.visibility, text: uiLabel(value) })));
  const checks = (a.site_people || []).map(person => h("input", { type: "checkbox", value: person.key,
    checked: (site.members || []).includes(person.key) }));
  const members = h("div", { class: "site-members" }, checks.length ? checks.map((check, i) =>
    h("label", { class: "toggle" }, check, a.site_people[i].name)) : h("p", { class: "note", text: uiText("apps.090") }));
  const confirmation = h("input", { type: "checkbox", "aria-label": uiText("apps.089") });
  const publicNote = h("label", { class: "toggle site-public" }, confirmation, uiText("apps.088"));
  const update = () => { members.hidden = visibility.value !== DATA_VALUE.selected; publicNote.hidden = visibility.value !== DATA_VALUE.public; confirmation.checked = false; };
  visibility.addEventListener("change", update); update();
  const version = h("select", { class: "input", "aria-label": uiText("apps.087") }, (site.versions || []).map(v =>
    h("option", { value: v.id, selected: v.id === site.current,
      text: `${v.created.replace("T", " ").replace("+08:00", "")} · ${v.by}${v.id === site.current ? uiText("apps.086") : v.id === site.pending ? uiText("apps.085") : ""}` })));
  const slug = h("input", { class: "input", value: site.slug, "aria-label": uiText("apps.084"), minlength: 3, maxlength: 30 });
  return h("section", { class: "site-panel" }, h("h3", { text: uiText("apps.083") }),
    h("dl", { class: "kv" }, h("dt", { text: uiText("apps.082") }), h("dd", { class: "mono-wrap" }, h("a", { href: site.url, target: "_blank", rel: "noopener noreferrer", text: site.url })),
      h("dt", { text: uiText("app.090") }), h("dd", { text: uiLabel(site.status) }), h("dt", { text: uiText("apps.081") }), h("dd", { text: uiLabel(site.visibility) }),
      h("dt", { text: uiText("apps.080") }), h("dd", { class: "mono-wrap", text: site.current || uiText("apps.079") })),
    publishButton(a),
    h("div", { class: "form" }, h("label", {}, uiText("apps.078"), visibility), members, publicNote,
      h("button", { class: "btn primary", type: "button", onclick: () => act({ action: site.status === DATA_VALUE.published ? "visibility" : "publish",
        visibility: visibility.value, members: checks.filter(c => c.checked).map(c => c.value), confirm_public: confirmation.checked }) },
        site.status === DATA_VALUE.published ? uiText("apps.077") : site.status === DATA_VALUE.disabled ? uiText("apps.076") : uiText("apps.075")),
      h("label", {}, uiText("apps.074"), version),
      site.status === DATA_VALUE.published ? h("div", { class: "site-actions" },
        h("button", { class: "btn", type: "button", onclick: () => act({ action: "rollback", version: version.value }) }, uiText("apps.073")),
        h("button", { class: "btn", type: "button", onclick: () => act({ action: "phone" }) }, uiText("apps.072")),
        h("button", { class: "btn danger", type: "button", onclick: () => act({ action: "disable" }) }, uiText("apps.071"))) : null,
      h("label", {}, uiText("apps.070"), slug),
      h("button", { class: "btn", type: "button", onclick: () => act({ action: "rename", slug: slug.value }) }, uiText("apps.069"))), error);
}

function appTile(a, size) {
  const glyph = a.icon || (a.title || "?").slice(0, 1);
  return h("span", { class: "app-tile" + (size ? ` ${size}` : ""), "aria-hidden": "true", text: glyph });
}

function stateDot(a) {
  if (!capability("probe_local")) return a.entry_note ? h("span", { class: "app-state faint", text: uiLabel(a.entry_note) }) : null;
  const p = AP.probe[a.id];
  if (!p || p.state === "unknown") return null;  // 还没探过，或没登记网址和进程：先不写状态
  const good = p.state === "online" || p.state === "running";
  return h("span", { class: "app-state" + (good ? " good" : "") },
    h("span", { class: "dot" + (good ? " on" : "") }), STATE_TEXT[p.state] || p.state);
}

function probeLine(a) {
  if (!capability("probe_local")) return null;
  const p = AP.probe[a.id];
  if (!p) return null;
  if (p.how === DATA_VALUE.currentPage) return uiText("apps.068");
  if (p.how) return uiText("apps.067", [uiLabel(p.how)]) + (a.probe_process ? `（${a.probe_process}）` : "");
  return uiText("apps.066");
}

async function openProjectApp(id) {
  try {
    let found = (AP.apps || []).find(a => a.id === id);
    if (!found) {
      await loadApps();
      found = (AP.apps || []).find(a => a.id === id);
    }
    if (!found) { toast(uiText("app.100")); return; }
    if (capability("probe_local") && !AP.probe[found.id]) await loadProbe();
    showApp(found);
  } catch (e) {
    if (e.message !== "401") toast(e.message);
  }
}

async function loadApps() {
  AP.apps = (await api("/api/apps")).apps;
  return AP.apps;
}

async function loadProbe(redraw) {
  if (!capability("probe_local")) return;
  try { AP.probe = await api("/api/apps/probe"); } catch { return; }
  redraw?.();
}

async function openApp(a, what, index) {
  try { await api(`/api/apps/${a.id}/open`, { what, index }); toast(uiText("apps.065")); }
  catch (e) { if (e.message !== "401") toast(e.message); }
}

function openButtons(a) {
  const o = a.open || {}, out = [];
  if (a.site_url) out.push(h("a", { class: "btn primary", href: a.site_url, target: "_blank", rel: "noopener noreferrer" }, uiText("apps.041")));
  if (o.url && allowed(a, "open_url", "open_local")) out.push(h("a", { class: "btn primary", href: o.url, target: "_blank", rel: "noopener noreferrer" }, uiText("apps.064")));
  if (!allowed(a, "open_local", "open_local")) return out;
  if (o.shortcut) out.push(h("button", { class: "btn" + (o.url ? "" : " primary"), type: "button", onclick: () => openApp(a, "shortcut") }, uiText("apps.041")));
  if (o.path && !o.shortcut) {
    const main = o.url ? "btn" : "btn primary";
    if (OPENABLE.test(o.path)) out.push(h("button", { class: main, type: "button", onclick: () => openApp(a, "path") }, o.url ? uiText("apps.063") : uiText("apps.041")));
    else if (!/\.[a-z0-9]{1,5}$/i.test(o.path)) out.push(h("button", { class: main, type: "button", onclick: () => openApp(a, "path") }, uiText("apps.062")));
    else out.push(h("button", { class: "btn", type: "button", onclick: () => openApp(a, "folder") }, uiText("apps.061")));
  }
  if (a.code_path) out.push(h("button", { class: "btn", type: "button", onclick: () => openApp(a, "code") }, uiText("apps.060")));
  return out;
}

function showApp(a) {
  const o = a.open || {};
  const prompt = uiText("apps.059", [appName(), a.title, a.id]);
  const toggle = h("button", { class: "link-danger", type: "button", onclick: async () => {
    try {
      await api(`/api/apps/${a.id}/listed`, { listed: !a.listed });
      toast(a.listed ? uiText("apps.058") : uiText("apps.057"));
      closeLayer(); AP.apps = null; route();
    } catch (e) { if (e.message !== "401") toast(e.message); }
  } }, a.listed ? uiText("apps.056") : uiText("apps.055"));
  drawer(uiText("app.111"),
    h("div", { class: "app-head" }, appTile(a, "lg"), h("div", {}, h("h2", { text: a.title }),
      h("div", { class: "meta-line" }, h("span", { class: "pill", text: uiLabel(a.kind) }), stateDot(a),
        a.listed ? null : h("span", { class: "pill red", text: uiText("app.110") })))),
    h("p", { class: "app-line", text: a.one_line }),
    a.status_note ? h("div", { class: "box accent", text: a.status_note }) : null,
    h("div", { class: "form-btns left" }, openButtons(a)),
    sitePanel(a),
    capability("open_local") && o.command ? [h("div", { class: "block-title", text: uiText("apps.054") }), copyBox(o.command)] : null,
    h("div", { class: "block-title", text: uiText("apps.053") }),
    h("dl", { class: "kv" }, [
      [uiText("app.008"), a.project ? a.project.name : uiText("apps.052")], [uiText("apps.051"), a.maintainer || uiText("apps.016")],
      [uiText("apps.050"), o.url], [uiText("apps.049"), o.shortcut], [uiText("apps.048"), o.path], [uiText("apps.047"), a.code_path],
      [uiText("apps.046"), probeLine(a)],
      [uiText("apps.045"), [a.registered_by, a.updated ? md(a.updated) : null].filter(Boolean).join(" · ")],
    ].filter(([, v]) => v).map(([k, v]) => [h("dt", { text: k }), h("dd", { class: /[\\/]/.test(v) ? "mono-wrap" : null, text: v })])),
    a.links.length ? [h("div", { class: "block-title", text: uiText("apps.044") }),
      h("div", { class: "links" }, a.links.map((l, i) => {
        const web = /^https?:/.test(l.target);
        return h("div", { class: "link-row" },
          h("div", { class: "grow" }, web ? h("a", { href: l.target, target: "_blank", rel: "noopener noreferrer", text: l.label }) : h("b", { text: l.label }),
            web ? null : h("div", { class: "mono-wrap faint", text: l.target }),
            l.note ? h("div", { class: "note", text: l.note }) : null),
          web || !capability("open_local") ? null : h("button", { class: "btn small", type: "button", onclick: () => openApp(a, "link", i) }, uiText("apps.041")));
      }))] : null,
    a.notes_html ? [h("div", { class: "block-title", text: uiText("app.130") }), trusted(a.notes_html)] : null,
    capability("manage_company") ? [h("div", { class: "block-title", text: uiText("apps.043") }),
      h("p", { class: "note", text: uiText("apps.042") }), copyBox(prompt)] : null,
    allowed(a, "listed", "manage_company") ? h("div", { class: "danger-zone" }, toggle) : null,
    h("details", { class: "tech" }, h("summary", { text: uiText("app.101") }), h("span", { class: "mono", text: a.rel })));
}

function appCard(a) {
  const card = h("button", { class: (a.site_url ? "" : "card ") + "app-card" + (a.listed ? "" : " unlisted"), type: "button", onclick: () => showApp(a) },
    h("div", { class: "app-card-top" }, appTile(a), stateDot(a)),
    h("div", { class: "app-name", text: a.title }),
    h("div", { class: "app-desc", text: a.one_line }),
    h("div", { class: "app-meta" }, h("span", { class: "pill", text: uiLabel(a.kind) }),
      a.project ? h("span", { class: "pill", text: a.project.name }) : null));
  return a.site_url ? h("div", { class: "card site-app-card" }, card,
    h("a", { class: "btn", href: a.site_url, target: "_blank", rel: "noopener noreferrer", text: uiText("apps.041") })) : card;
}

async function renderApps() {
  const page = document.getElementById("page");
  try { await loadApps(); } catch (e) {
    if (e.message !== "401") put(page, h("p", { class: "muted", text: e.message }));
    return;
  }
  const all = AP.apps;
  const kinds = [...new Set(all.map(a => a.kind))];
  const projects = [...new Map(all.filter(a => a.project).map(a => [a.project.id, a.project.name])).entries()];
  const grid = h("div", { class: "app-grid" });
  const count = h("div", { class: "count-line" });
  const draw = () => {
    const q = AP.q.trim().toLowerCase();
    const shown = all.filter(a => (AP.showUnlisted || a.listed) && (!AP.kind || a.kind === AP.kind)
      && (!AP.project || a.project?.id === AP.project)
      && (!q || [a.title, a.one_line, a.maintainer, a.project?.name, a.id].join(" ").toLowerCase().includes(q)));
    count.textContent = uiText("apps.040", [shown.length]) + (all.some(a => !a.listed) && !AP.showUnlisted ? uiText("apps.039", [all.filter(a => !a.listed).length]) : "");
    put(grid, shown.length ? shown.map(appCard) : h("div", { class: "card wide" }, empty(uiText("apps.038"), uiText("app.013"))));
  };
  const seg = h("div", { class: "seg", role: "group", "aria-label": uiText("app.021") });
  const drawSeg = () => put(seg, [["", uiText("app.017")], ...kinds.map(k => [k, uiLabel(k)])].map(([v, t]) =>
    h("button", { type: "button", class: AP.kind === v ? "on" : null, onclick: () => { AP.kind = v; drawSeg(); draw(); } }, t)));
  drawSeg();
  const q = h("input", { class: "input grow", placeholder: uiText("apps.037"), value: AP.q, "aria-label": uiText("apps.036") });
  q.addEventListener("input", () => { AP.q = q.value; draw(); });
  const proj = h("select", { class: "input", "aria-label": uiText("app.008") }, h("option", { value: "", text: uiText("app.022") }),
    projects.map(([id, name]) => h("option", { value: id, selected: id === AP.project, text: name })));
  proj.addEventListener("change", () => { AP.project = proj.value; draw(); });
  const unlisted = h("label", { class: "toggle" }, h("input", { type: "checkbox", checked: AP.showUnlisted || null,
    onchange: e => { AP.showUnlisted = e.target.checked; draw(); } }), uiText("apps.035"));
  put(page,
    h("div", { class: "page-head" },
      h("div", {}, h("h1", { text: uiText("apps.034") }), h("p", { class: "lead", text: S.me.remote ? uiText("apps.033") : uiText("apps.032") })),
      publishButton(),
      capability("manage_company") ? h("button", { class: "btn", type: "button", onclick: () => promptModal(uiText("apps.029"),
        uiText("apps.031"),
        uiText("apps.030", [appName()])) },
        icon(ICON.copy), uiText("apps.029")) : null),
    h("div", { class: "card filters" }, q, seg, proj, capability("manage_company") ? unlisted : null),
    count, grid);
  draw();
  loadProbe(draw);
}

// ---------- 我的 Agent ----------

async function renderAgents() {
  const page = document.getElementById("page");
  let data;
  try { data = await api("/api/agents"); } catch (e) {
    if (e.message !== "401") put(page, h("p", { class: "muted", text: e.message }));
    return;
  }
  const rows = data.agents;
  const prompt = data.connect_prompt || uiText("apps.028", [ownerName()]);
  put(page,
    h("div", { class: "page-head" },
      h("div", {}, h("h1", { text: uiText("apps.027") }),
        h("p", { class: "lead", text: uiText("apps.026") })),
      capability("manage_company") ? h("button", { class: "btn", type: "button", onclick: () => promptModal(uiText("apps.024"),
        uiText("apps.025", [appName()]), prompt) },
        icon(ICON.copy), uiText("apps.024")) : null),
    rows.length ? h("div", { class: "card table-wrap" }, h("table", { class: "t agent-t" },
      h("thead", {}, h("tr", {}, h("th", { text: uiText("apps.023") }), h("th", { class: "hide-sm", text: uiText("apps.022") }),
        h("th", { text: uiText("apps.021") }), h("th", { text: uiText("apps.020") }))),
      h("tbody", {}, rows.map(r => h("tr", { class: "click", onclick: () => allowed(r, "open_token_drawer") ? tokenDrawer(r) : showDoc(r.rel) },
        h("td", {}, h("div", { class: "who" }, avatar({ key: r.key, name: r.name }),
          h("div", {}, h("div", { class: "cell-name", text: r.name }), h("div", { class: "cell-sub", text: r.role || "" }))),
          r.names?.length ? h("div", { class: "agent-names" }, r.names.map(n => h("span", { class: "pill", text: n }))) : null,
          S.me.remote ? h("div", { class: "cell-sub", text: uiText("apps.018", [[...new Set((r.tokens || []).map(t => t.issued_by_name || personName(t.issued_by)))].join("、") || uiText("apps.019")]) }) : null),
        h("td", { class: "hide-sm" }, r.connect ? h("div", { class: "cell-sub wrap", text: r.connect }) : h("span", { class: "faint", text: allowed(r, "open_token_drawer") ? uiText("apps.017") : uiText("apps.016") }),
          h("div", { class: "cell-sub mono" }, uiText("apps.015", [r.key]))),
        h("td", { class: "nowrap" }, r.last_use ? [h("div", { text: ago(r.last_use) }),
          h("div", { class: "cell-sub", text: `${{ cli: uiText("apps.014"), remote: uiText("apps.013"), web: uiText("apps.012"), portal: uiText("apps.011") }[r.last_via] || "MCP"} · ${uiLabel(r.last_tool)}` }),
          h("div", { class: "cell-sub", text: uiText("apps.009", [r.calls_7d]) })]
          : h("span", { class: "faint", text: uiText("apps.010") })),
        h("td", { class: "nowrap" }, r.last_write ? [h("div", { text: ago(r.last_write) }),
          h("div", { class: "cell-sub", text: uiText("apps.009", [r.writes_7d]) })] : h("span", { class: "faint", text: uiText("apps.008") }))))))) : empty(uiText("apps.007", [ownerName()]), uiText("apps.006")),
    h("p", { class: "note faint foot-note", text: S.me.remote ? uiText("apps.005") : uiText("apps.004") }));
}

// 首页模块：常用应用
MODULES.apps = (m) => {
  const items = m.data.items;
  const grid = h("div", { class: "app-mini-grid" });
  const draw = () => put(grid, items.map(a => h("button", { class: "app-mini", type: "button", onclick: () => showApp(a) },
    appTile(a), h("div", { class: "grow" }, h("div", { class: "app-name", text: a.title }), stateDot(a)))));
  draw();
  if (items.length) loadProbe(draw);
  return [secHead(m.name, null, h("a", { class: "more", href: "#/apps", text: uiText("apps.003") })),
    items.length ? h("div", { class: "card pad" }, grid) : h("div", { class: "card" }, empty(uiText("apps.002"), uiText("apps.001")))];
};
