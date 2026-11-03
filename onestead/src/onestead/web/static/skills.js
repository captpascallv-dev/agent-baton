"use strict";
// Skill 广场，以及首页的“Skill 提醒”。用 app.js、apps.js 里的小工具。

const SK = { data: null, q: "", view: "all", open: new Set(), showUnlisted: false };
const SK_STATE = {
  same: ["skills.092", "ok"], behind: ["skills.091", "bad"], differs: ["skills.090", "warn"], broken: ["skills.017", "bad"], snapshot: ["skills.089", "faint"],
};
const FAMILY_MIN = 5;

function sameCopyText(v) {
  const names = Object.fromEntries(SK.data.harnesses.map(x => [x.key, x.name]));
  if (v.in_shared_dir) return uiText("skills.088");
  return v.same_copy_as.length ? uiText("skills.087", [v.same_copy_as.map(k => names[k]).join("、")]) : "";
}

function skState(v) {
  if (!v) return h("span", { class: "sk-cell none", text: "—" });
  if (v.state === "same" && v.shared) return h("span", { class: "sk-cell ok shared", title: sameCopyText(v), text: uiText("skills.086") });
  const [key, cls] = SK_STATE[v.state] || [null, ""];
  return h("span", { class: `sk-cell ${cls}` }, v.state === "same" ? icon(ICON.check) : key ? uiText(key) : v.state);
}

function skWhy(r) {
  const out = [];
  const names = Object.fromEntries(SK.data.harnesses.map(x => [x.key, x.name]));
  for (const [k, v] of Object.entries(r.installs)) {
    if (v.state === "behind") out.push(uiText("skills.085", [names[k]]));
    else if (v.state === "differs") out.push(uiText("skills.084", [names[k]]));
    else if (v.dangling) out.push(uiText("skills.083", [names[k]]));
    else if (v.no_skill_md) out.push(uiText("skills.082", [names[k]]));
    else if (v.damaged_count) out.push(uiText("skills.081", [names[k], v.damaged_count]));
  }
  return out;
}

function skillPrompt(kind, r, hName, hint) {
  const ref = r.ref ? r.ref.path : uiText("skills.056");
  if (kind === "install" && !hint) return uiText("skills.080", [appName(), r.id, ref, hName]);
  if (kind === "install") return uiText("skills.079", [appName(), r.id, hName, hint, hName, ref]);
  if (kind === "update") return uiText("skills.078", [appName(), r.id, hName, ref]);
  return uiText("skills.077", [appName(), r.id]);
}

async function openSkillDir(r, path) {
  try { await api("/api/skills/open", { id: r.id, path }); toast(uiText("apps.065")); }
  catch (e) { if (e.message !== "401") toast(e.message); }
}

function showSkill(r) {
  if (SK.data.mode === "shared") return showSkillById(r.id);
  const names = SK.data.harnesses;
  const rec = r.record || {};
  const why = skWhy(r);
  const missing = names.filter(x => !r.installs[x.key]);
  const pick = h("select", { class: "input", "aria-label": uiText("skills.076") },
    missing.map(x => h("option", { value: x.key, text: x.name })));
  const installBox = h("div");
  const drawInstall = () => {
    const x = names.find(n => n.key === pick.value);
    put(installBox, x ? copyBox(skillPrompt("install", r, x.name, x.install_dir)) : null);
  };
  pick.addEventListener("change", drawInstall);
  drawInstall();
  const bad = Object.entries(r.installs).filter(([, v]) => v.state !== "same" && v.state !== "snapshot");
  const sharing = r.sharing || { public: false, members: [] };
  const shareToggle = h("input", { type: "checkbox", checked: sharing.public || null,
    onchange: async e => {
      const input = e.target;
      input.disabled = true;
      try {
        await api("/api/skills/share", { id: r.id, public: input.checked });
        SK.data = await api("/api/skills" + (SK.showUnlisted ? "?include_unlisted=1" : ""));
        drawSkills();
        showSkill(SK.data.skills.find(s => s.id === r.id));
        toast(uiText("skills.075"));
      } catch (error) {
        input.checked = sharing.public;
        input.disabled = false;
        if (error.message !== "401") toast(error.message);
      }
    } });
  drawer("Skill",
    h("h2", { text: r.name }),
    h("div", { class: "meta-line" }, h("span", { class: "pill mono", text: r.id }),
      rec.origin ? h("span", { class: "pill", text: uiLabel(rec.origin) }) : null,
      r.version ? h("span", { class: "pill", text: uiText("skills.010", [r.version]) }) : null,
      r.in_shared ? h("span", { class: "pill", text: uiText("skills.074") }) : null,
      r.listed === false ? h("span", { class: "pill red", text: uiText("api.unlisted") }) : null),
    h("p", { class: "app-line", text: rec.one_line || r.description || uiText("skills.073") }),
    rec.one_line && r.description ? h("p", { class: "note faint", text: r.description }) : null,
    h("div", { class: "block-title", text: uiText("skills.072") }),
    h("label", { class: "toggle" }, shareToggle, uiText("skills.071")),
    h("p", { class: "note", text: sharing.public ? uiText("skills.070") : uiText("skills.069") }),
    h("p", { class: "note", text: uiText("skills.067", [sharing.members.join("、") || uiText("skills.068")]) }),
    sharing.withheld ? h("div", { class: "box stack-tight" },
      h("div", { text: uiText("skills.066") }),
      sharing.withheld.length ? sharing.withheld.map(f => h("div", { class: "mono-wrap", text: `${f.path} (${uiLabel(f.reason)})` }))
        : h("div", { class: "note faint", text: uiText("skills.065") }),
      sharing.available === false ? h("p", { class: "note red-text", text: uiText("skills.064") }) : null,
      sharing.too_large ? h("p", { class: "note", text: uiText("skills.005", [ownerName()]) }) : null) : null,
    why.length ? h("div", { class: "box accent stack-tight" }, why.map(w => h("div", { text: w }))) : null,
    h("div", { class: "block-title", text: uiText("skills.063") }),
    h("div", { class: "sk-installs" }, names.map(x => {
      const v = r.installs[x.key];
      return h("div", { class: "sk-install" },
        h("div", { class: "sk-install-name", text: x.name + (x.snapshot ? uiText("skills.062") : "") }), skState(v),
        h("div", { class: "grow" }, v ? [h("div", { class: "mono-wrap faint", text: v.path }),
          v.shared ? h("div", { class: "note", text: sameCopyText(v) }) : null,
          v.newest ? h("div", { class: "note", text: uiText("skills.059", [md(v.newest), hm(v.newest), v.files]) }) : null]
          : h("div", { class: "note faint", text: uiText("skills.061") })),
        v && !v.dangling ? h("button", { class: "btn small", type: "button", onclick: () => openSkillDir(r, v.path) }, uiText("apps.041")) : null);
    }), r.shared_copy ? h("div", { class: "sk-install" },
      h("div", { class: "sk-install-name", text: uiText("skills.060") }), skState({ ...r.shared_copy, shared: false }),
      h("div", { class: "grow" }, h("div", { class: "mono-wrap faint", text: r.shared_copy.path }),
        r.shared_copy.newest ? h("div", { class: "note", text: uiText("skills.059", [md(r.shared_copy.newest), hm(r.shared_copy.newest), r.shared_copy.files]) }) : null),
      h("button", { class: "btn small", type: "button", onclick: () => openSkillDir(r, r.shared_copy.path) }, uiText("apps.041"))) : null),
    h("dl", { class: "kv" }, [
      [uiText("skills.058"), r.ref ? `${r.ref.path}（${rec.home ? uiText("skills.057") : uiText("skills.056")}）` : uiText("skills.055")],
      [uiText("apps.051"), rec.maintainer], [uiText("skills.054"), rec.upstream], [uiText("skills.053"), rec.upstream_version],
      [uiText("app.008"), rec.project ? rec.project.name : null],
    ].filter(([, v]) => v).map(([k, v]) => [h("dt", { text: k }), h("dd", { class: /[\\/]/.test(v) ? "mono-wrap" : null, text: v })])),
    rec.notes_html ? [h("div", { class: "block-title", text: uiText("app.130") }), trusted(rec.notes_html)] : null,
    bad.length ? [h("div", { class: "block-title", text: uiText("skills.052") }),
      h("p", { class: "note", text: uiText("skills.051") }),
      bad.map(([k]) => copyBox(skillPrompt("update", r, names.find(n => n.key === k).name)))] : null,
    missing.length ? [h("div", { class: "block-title", text: uiText("skills.050") }),
      h("div", { class: "form-btns left" }, pick), installBox] : null,
    !r.record ? [h("div", { class: "block-title", text: uiText("skills.049") }), copyBox(skillPrompt("record", r))] : null,
    h("details", { class: "tech" }, h("summary", { text: uiText("app.101") }),
      h("span", { class: "mono", text: uiText("skills.048", [SK.data.scanned_at, r.copies.length]) +
        (r.ref?.hash ? uiText("skills.047", [r.ref.hash]) : "") + (rec.rel ? uiText("skills.046", [rec.rel]) : "") })));
}

function families(rows) {
  const by = new Map();
  for (const r of rows) {
    const m = r.id.match(/^([a-z0-9]+)-/);
    const key = m ? m[1] : r.id;
    by.set(key, [...(by.get(key) || []), r]);
  }
  const out = [];
  for (const [key, list] of by) {
    if (list.length >= FAMILY_MIN) out.push({ family: key, rows: list });
    else out.push(...list);
  }
  return out.sort((a, b) => (a.family || a.id).localeCompare(b.family || b.id));
}

function familyCell(rows, key) {
  const vs = rows.map(r => r.installs[key]).filter(Boolean);
  if (!vs.length) return h("span", { class: "sk-cell none", text: "—" });
  const bad = vs.filter(v => v.state === "behind" || v.state === "broken").length;
  const shared = vs.every(v => v.shared);
  return h("span", { class: "sk-cell " + (bad ? "bad" : "ok") + (shared ? " shared" : "") },
    bad ? uiText("skills.042", [bad]) : `${shared ? uiText("skills.045") : ""}${vs.length}`);
}

function skillRow(r, nested) {
  const tr = h("tr", { class: "click" + (nested ? " nested" : ""), onclick: () => showSkill(r) },
    h("td", {}, h("div", { class: "cell-name" }, r.name,
      r.listed === false ? h("span", { class: "pill red", text: uiText("api.unlisted") }) : null,
      r.alert ? h("span", { class: "sk-flag", text: r.alert === "behind" ? uiText("skills.031") : uiText("skills.017") }) : null),
      h("div", { class: "cell-sub" + (r.alert ? " red-text" : ""), text: r.alert ? skWhy(r).join("；") : (r.record?.one_line || r.description || r.id) })),
    SK.data.harnesses.map(x => h("td", { class: "sk-col hide-sm" }, skState(r.installs[x.key]))),
    h("td", { class: "show-sm" }, h("span", { class: "cell-sub", text: uiText("skills.044", [Object.keys(r.installs).length]) })));
  return tr;
}

function familyRows(f) {
  const open = SK.open.has(f.family);
  const alerts = f.rows.filter(r => r.alert).length;
  const head = h("tr", { class: "click family", onclick: () => { open ? SK.open.delete(f.family) : SK.open.add(f.family); drawSkills(); } },
    h("td", {}, h("div", { class: "cell-name" }, icon(ICON.chev, null), ` ${f.family}-*`,
      h("span", { class: "n", text: uiText("skills.043", [f.rows.length]) }), alerts ? h("span", { class: "sk-flag", text: uiText("skills.042", [alerts]) }) : null),
      h("div", { class: "cell-sub", text: f.rows.slice(0, 4).map(r => r.id.slice(f.family.length + 1)).join("、") + "……" })),
    SK.data.harnesses.map(x => h("td", { class: "sk-col hide-sm" }, familyCell(f.rows, x.key))),
    h("td", { class: "show-sm" }, h("span", { class: "cell-sub", text: uiText("app.091", [f.rows.length]) })));
  if (open) head.classList.add("open");
  return [head, open ? f.rows.map(r => skillRow(r, true)) : null];
}

let skTable = null;
let skCount = null;
onWorkspaceReset(() => {
  Object.assign(SK, { data: null, q: "", view: "all", open: new Set(), showUnlisted: false });
  skTable = skCount = null;
});

function drawSkills() {
  const q = SK.q.trim().toLowerCase();
  const rows = SK.data.skills.filter(r => (SK.view === "all" || (SK.view === "alerts" ? r.alert : r.alert === SK.view))
    && (!q || [r.id, r.name, r.description, r.record?.one_line].join(" ").toLowerCase().includes(q)));
  skCount.textContent = uiText("app.091", [rows.length]) + (q || SK.view !== "all" ? uiText("skills.041", [SK.data.skills.length]) : "");
  const groups = q || SK.view !== "all" ? rows : families(rows);
  put(skTable, h("thead", {}, h("tr", {}, h("th", { text: "Skill" }),
    SK.data.harnesses.map(x => h("th", { class: "sk-col hide-sm", title: x.roots.length ? x.roots.join("\n") : uiText("skills.040"),
      text: x.name })), h("th", { class: "show-sm", text: uiText("skills.039") }))),
    h("tbody", {}, groups.length ? groups.map(g => g.family ? familyRows(g) : skillRow(g)) :
      h("tr", {}, h("td", { colspan: 9 }, empty(uiText("skills.014"), uiText("app.013"))))));
}

function pluginCard() {
  const hs = SK.data.harnesses.filter(x => x.plugins.length || x.builtin.length);
  return h("div", { class: "card sk-plugin-card" },
    h("div", { class: "card-title" }, h("h3", { text: uiText("skills.038") }), h("span", { class: "sub", text: uiText("skills.037") })),
    h("div", { class: "sk-plugins" }, hs.map(x => h("details", { class: "sk-plugin" },
      h("summary", {}, h("b", { text: x.name }), h("span", { class: "faint", text:
        [x.plugins.length ? uiText("skills.036", [x.plugins.length, x.plugins.reduce((n, p) => n + p.skills.length, 0)]) : null,
          x.builtin.length ? uiText("skills.035", [x.builtin.length]) : null].filter(Boolean).join(" · ") })),
      h("div", { class: "sk-plugin-body" },
        x.plugins.map(p => h("div", { class: "note" }, h("b", { text: `${p.plugin}（${p.skills.length}）` }), " ", p.skills.join("、"))),
        x.builtin.length ? h("div", { class: "note" }, h("b", { text: uiText("skills.034") }), " ", x.builtin.join("、")) : null)))));
}

async function renderSkills(fresh) {
  const page = document.getElementById("page");
  const query = new URLSearchParams();
  if (fresh && capability("manage_skill_share")) query.set("fresh", "1");
  if (SK.showUnlisted && capability("manage_skill_share")) query.set("include_unlisted", "1");
  try { SK.data = await api("/api/skills" + (query.size ? "?" + query : "")); } catch (e) {
    if (e.message !== "401") put(page, h("p", { class: "muted", text: e.message }));
    return;
  }
  if (SK.data.mode === "shared") {
    renderSharedSkills(page);
    return;
  }
  const all = SK.data.skills;
  const behind = all.filter(r => r.alert === "behind").length, broken = all.filter(r => r.alert === "broken").length;
  const nobody = SK.data.harnesses.filter(x => !x.roots.length && !x.snapshot).map(x => x.name);
  skTable = h("table", { class: "t sk-t" });
  skCount = h("div", { class: "count-line" });
  const seg = h("div", { class: "seg", role: "group", "aria-label": uiText("skills.033") });
  const drawSeg = () => put(seg, [["all", uiText("app.017")], ["alerts", uiText("skills.032")], ["behind", uiText("skills.031")], ["broken", uiText("skills.017")]].map(([v, t]) =>
    h("button", { type: "button", class: SK.view === v ? "on" : null, onclick: () => { SK.view = v; drawSeg(); drawSkills(); } }, t)));
  drawSeg();
  const q = h("input", { class: "input grow", placeholder: uiText("skills.016"), value: SK.q, "aria-label": uiText("skills.015") });
  q.addEventListener("input", () => { SK.q = q.value; drawSkills(); });
  put(page,
    h("div", { class: "page-head" },
      h("div", {}, h("h1", { text: uiText("skills.003") }),
        h("p", { class: "lead", text: uiText("skills.030") })),
      h("button", { class: "btn", type: "button", onclick: async () => { toast(uiText("skills.029")); await renderSkills(true); toast(uiText("skills.028")); } }, uiText("skills.027"))),
    h("div", { class: "stat-row" },
      h("div", { class: "card stat" }, h("b", { text: all.filter(r => r.listed !== false).length }), h("span", { text: uiText("skills.026") })),
      h("button", { class: "card stat" + (behind ? " warn" : ""), type: "button", onclick: () => { SK.view = "behind"; drawSeg(); drawSkills(); } },
        h("b", { text: behind }), h("span", { text: uiText("skills.025") })),
      h("button", { class: "card stat" + (broken ? " warn" : ""), type: "button", onclick: () => { SK.view = "broken"; drawSeg(); drawSkills(); } },
        h("b", { text: broken }), h("span", { text: uiText("skills.024") }))),
    h("div", { class: "card filters" }, q, seg,
      h("label", { class: "toggle" }, h("input", { type: "checkbox", checked: SK.showUnlisted || null,
        onchange: async e => { SK.showUnlisted = e.target.checked; await renderSkills(); } }), uiText("api.include_unlisted"))),
    skCount,
    h("div", { class: "card table-wrap" }, skTable),
    h("p", { class: "note faint foot-note", text: uiText("skills.023", []) +
      (nobody.length ? uiText("skills.022", [nobody.join("、")]) : "") + uiText("skills.021", [hm(SK.data.scanned_at)]) }),
    pluginCard());
  drawSkills();
}

// 首页模块：Skill 提醒
// 首页只在有 Skill 坏了时出现；版本不一致不提醒（伙伴自己在 Skill 广场看）
MODULES.skills = (m) => {
  const d = m.data;
  if (!d.broken) return null;
  const names = Object.fromEntries((d.harnesses || []).map(x => [x.key, x.name]));
  const line = r => Object.entries(r.installs).filter(([, v]) => v.state === "broken").map(([k]) => names[k]).join("、") + uiText("skills.020");
  return [secHead(m.name, null, h("a", { class: "more", href: "#/skills", text: uiText("skills.003") })),
    h("div", { class: "card" },
      h("div", { class: "card-title" }, h("h3", { text: uiText("skills.019", [d.broken]) }),
        h("span", { class: "sub", text: uiText("skills.018") })),
      h("div", { class: "list" }, d.items.map(r => h("div", { class: "list-row", role: "button", tabindex: "0",
        onclick: () => { location.hash = "#/skills"; } },
        h("span", { class: "pill red", text: uiText("skills.017") }),
        h("div", { class: "grow" }, h("div", { class: "cell-name", text: r.name }), h("div", { class: "cell-sub", text: line(r) })),
        icon(ICON.chev, null)))))];
};

function renderSharedSkills(page) {
  const grid = h("div", { class: "app-grid" });
  const q = h("input", { class: "input", placeholder: uiText("skills.016"), value: SK.q, "aria-label": uiText("skills.015") });
  const draw = () => {
    const rows = SK.data.skills.filter(r => [r.id, r.name, r.one_line, r.description].join(" ").toLowerCase().includes(SK.q.trim().toLowerCase()));
    put(grid, rows.length ? rows.map(r => h("button", { class: "card app-card", type: "button", onclick: () => showSkillById(r.id) },
      h("div", { class: "app-name", text: r.name }), h("div", { class: "app-desc", text: r.one_line || r.description }),
      r.version ? h("span", { class: "pill", text: uiText("skills.010", [r.version]) }) : null)) :
      h("div", { class: "card wide" }, empty(SK.data.skills.length ? uiText("skills.014") : uiText("skills.002"), SK.data.skills.length ? uiText("skills.013") : uiText("skills.012", [ownerName()]))));
  };
  q.addEventListener("input", () => { SK.q = q.value; draw(); });
  put(page, h("h1", { text: uiText("skills.003") }), h("p", { class: "lead", text: uiText("skills.011") }),
    h("div", { class: "card filters" }, q), grid);
  draw();
}

async function showSkillById(id) {
  if (SK.data && SK.data.mode !== "shared") {
    const row = SK.data.skills.find(r => r.id === id);
    if (row) return showSkill(row);
    return drawerError("Skill", new Error(uiText("app.004")));
  }
  drawerLoading("Skill");
  const ticket = layerVersion;
  let info;
  try { info = await api(`/api/skills/${encodeURIComponent(id)}`); }
  catch (e) { return drawerError("Skill", e); }
  if (ticket !== layerVersion) return;
  drawer("Skill", h("h2", { text: info.name }),
    h("div", { class: "meta-line" }, h("span", { class: "pill", text: info.id }), info.version ? h("span", { class: "pill", text: uiText("skills.010", [info.version]) }) : null),
    h("p", { class: "app-line", text: info.one_line || info.description }),
    h("div", { class: "block-title", text: "SKILL.md" }), trusted(info.html),
    info.truncated ? h("p", { class: "note", text: uiText("skills.009") }) : null,
    h("div", { class: "block-title", text: uiText("skills.008") }),
    h("table", { class: "t" }, h("thead", {}, h("tr", {}, h("th", { text: uiText("skills.007") }), h("th", { text: uiText("skills.006") }))),
      h("tbody", {}, info.files.map(f => h("tr", {}, h("td", { class: "mono-wrap", text: f.path }), h("td", { text: f.size }))))),
    info.too_large ? h("p", { class: "note", text: uiText("skills.005", [ownerName()]) }) :
      h("a", { class: "btn primary", href: info.download_url }, uiText("skills.004")));
}

MODULES.shared_skills = m => [secHead(m.name, null, h("a", { class: "more", href: "#/skills", text: uiText("skills.003") })),
  h("div", { class: "card pad" }, m.data.items.length ? m.data.items.map(r => h("button", { class: "btn", type: "button", onclick: () => showSkillById(r.id) }, r.name)) :
    empty(uiText("skills.002"), uiText("skills.001", [ownerName()])))];
