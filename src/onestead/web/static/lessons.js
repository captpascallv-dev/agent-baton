"use strict";

const LD = { batch: 1, inactive: false };
onWorkspaceReset(() => Object.assign(LD, { batch: 1, inactive: false }));

async function reviewLesson(values) {
  await api("/api/lessons/review", values);
  LD.batch = 1;
  await renderLessons();
}

function retireLesson(row) {
  const note = h("textarea", { class: "input", rows: 3, maxlength: 200, "aria-label": uiText("digest.note") });
  drawer(uiText("digest.retire_title", [row.title]), h("div", { class: "form" },
    h("label", {}, uiText("digest.note"), note),
    h("button", { class: "btn primary", type: "button", onclick: async e => {
      e.target.disabled = true;
      try { await reviewLesson({ retire: [{ id: row.record_id, note: note.value }] }); closeLayer(); toast(uiText("digest.retired")); }
      catch (error) { toast(error.message); e.target.disabled = false; }
    } }, uiText("digest.retire"))));
}

function lessonActions(row, all = false) {
  return h("div", { class: "actions" },
    row.state === "pending" ? h("button", { class: "btn", type: "button", onclick: async e => {
      e.target.disabled = true;
      try { await reviewLesson({ confirm: [row.record_id] }); toast(uiText("digest.confirmed")); }
      catch (error) { toast(error.message); e.target.disabled = false; }
    } }, uiText("digest.confirm")) : null,
    row.state !== "retired" ? h("button", { class: "btn", type: "button", onclick: () => retireLesson(row) }, uiText(all ? "digest.withdraw" : "digest.retire")) : null);
}

async function renderLessons() {
  const data = await api(`/api/lessons?batch=${LD.batch}&include_inactive=${LD.inactive ? 1 : 0}`);
  const digest = data.digest;
  const summary = h("section", { class: "lesson-block", "data-digest": "summary" },
    h("h2", { text: uiText("digest.summary") }),
    h("p", { class: "note faint", text: uiText("digest.period", [digest.since, digest.as_of]) }),
    h("p", { class: "note", text: uiText("digest.counts", [digest.judgment_count, digest.maintenance_count]) }));
  for (const item of digest.judgments) {
    const card = h("article", { class: "lesson-card" },
      h("h3", { text: `${item.number}. ${uiText(`digest.category.${item.category}`)}` }),
      item.waiting_days != null ? h("p", { class: "note faint", text: uiText("digest.waiting", [item.waiting_days]) }) : null);
    for (const row of item.records || [item]) card.append(h("div", { class: "lesson-entry" },
      h("h4", { text: row.title }), h("p", { class: "note faint", text: `${row.project_title} · ${uiText(`digest.kind.${row.kind}`)} · ${row.id}` }),
      h("p", { text: row.conclusion }), h("p", { class: "note", text: uiText("digest.source", [uiText(`digest.source.${row.source || "missing"}`)]) }), lessonActions(row)));
    if (item.category === "promotion") card.append(h("p", { class: "note", text: uiText("digest.promotion_hint") }));
    summary.append(card);
  }
  if (!digest.judgments.length) summary.append(h("p", { class: "note", text: uiText("digest.empty") }));
  const previous = h("button", { class: "btn", type: "button", disabled: LD.batch <= 1 || null, onclick: async () => { LD.batch--; await renderLessons(); } }, uiText("digest.previous"));
  const next = h("button", { class: "btn", type: "button", disabled: digest.remaining_batches === 0 || null, onclick: async () => { LD.batch++; await renderLessons(); } }, uiText("digest.next"));
  summary.append(h("div", { class: "actions" }, previous,
    h("span", { class: "note", text: uiText("digest.batches", [digest.batch, digest.batches, digest.remaining_batches]) }), next,
    h("button", { class: "btn primary", type: "button", onclick: async e => {
      e.target.disabled = true;
      try { await reviewLesson({ done: true }); toast(uiText("digest.reviewed")); }
      catch (error) { toast(error.message); e.target.disabled = false; }
    } }, uiText("digest.finish"))));
  const all = h("section", { class: "lesson-block", "data-digest": "all" }, h("h2", { text: uiText("digest.all") }),
    h("label", { class: "toggle" }, h("input", { type: "checkbox", checked: LD.inactive || null, onchange: async e => { LD.inactive = e.target.checked; await renderLessons(); } }), uiText("digest.show_inactive")));
  const groups = new Map();
  for (const row of data.items) {
    const key = row.project || "";
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(row);
  }
  for (const key of [...groups.keys()].sort()) {
    const rows = groups.get(key);
    all.append(h("h3", { text: rows[0].project_title }));
    for (const row of rows) all.append(h("details", { class: "lesson-card" },
      h("summary", { text: `${row.title} · ${uiText(`digest.state.${row.state}`)}` }), trusted(row.html), lessonActions(row, true)));
  }
  if (!data.items.length) all.append(h("p", { class: "note", text: uiText("digest.empty") }));
  put(document.getElementById("page"), h("div", { class: "page-head" }, h("h1", { text: uiText("digest.nav") })), summary, all);
}
