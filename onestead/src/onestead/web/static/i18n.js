"use strict";
// 优先用户选择，其次浏览器语言，最后公司语言；存储禁用时，本次页面仍可切换。
const LANGUAGE_KEY = "onestead.ui.lang";
let chosenLanguage = null;
try { chosenLanguage = localStorage.getItem(LANGUAGE_KEY); } catch {}
function resolveLanguage(company, chosen = chosenLanguage, languages = navigator.languages || [navigator.language]) {
  if (chosen === "en" || chosen === "zh") return chosen;
  for (const value of languages || []) {
    if (/^zh/i.test(value || "")) return "zh";
    if (/^en/i.test(value || "")) return "en";
  }
  return company === "zh" ? "zh" : "en";
}
let language = resolveLanguage(document.documentElement?.dataset.companyLang);
const uiLocale = () => language === "zh" ? "zh-CN" : "en-US";
function uiText(key, args = []) {
  const table = language === "zh" ? I18N_ZH : I18N_EN;
  if (!Object.hasOwn(table, key)) throw new Error("Missing UI text: " + key);
  // 回调替换避免参数中的 $& 被当成 replacement 语法；参数只走 textContent 或转义。
  let template = table[key];
  if (language === "en") {
    const singular = { items: "item", apps: "app", changes: "change", updates: "update", days: "day", "to-dos": "to-do", decisions: "decision", alerts: "alert", tools: "tool", plugins: "plugin", Skills: "Skill", copies: "copy", files: "file" };
    template = template.replace(/\{(\d+)\} (open |broken )?(items|apps|changes|updates|days|to-dos|decisions|alerts|tools|plugins|Skills|copies|files)\b/g,
      (whole, n, prefix, noun) => +args[+n] === 1 ? `{${n}} ${prefix || ""}${singular[noun]}` : whole);
  }
  return template.replace(/\{(\d+)\}/g, (_, n) => String(args[+n] ?? ""));
}
function translatedLabels(keys) {
  return new Proxy(keys, { get: (target, key) => Object.hasOwn(target, key) ? uiText(target[key]) : undefined });
}
const labelKeys = new Map(Object.entries(I18N_ZH).filter(([, v]) => !/\{\d+\}/.test(v)).map(([k, v]) => [v, k]));
// 只给系统标签调用：状态、类型、字段名。项目名、正文、成员名等用户内容不能调用它。
function uiLabel(value) { const key = labelKeys.get(value); return key ? uiText(key) : value; }
const sentencePatterns = Object.entries(I18N_ZH).filter(([key, value]) => /^(activity|attention|file)\./.test(key) && /\{\d+\}/.test(value)).map(([key, value]) => {
  const captures = [];
  const source = value.split(/(\{\d+\})/).map(part => {
    if (/^\{\d+\}$/.test(part)) { captures.push(+part.slice(1, -1)); return "(.*?)"; }
    return part.replaceAll("\\", "\\\\").replace(/[.*+?^${}()|[\]]/g, "\\$&");
  }).join("");
  return { key, captures, pattern: new RegExp("^" + source + "$", "s") };
});
// 活动与提醒的固定句式是系统生成的；插值里的标题和正文仍原样显示。
function uiSentence(value) {
  if (language === "zh" || typeof value !== "string") return value;
  const exact = labelKeys.get(value);
  if (exact && /^(activity|attention|file)\./.test(exact)) return uiText(exact);
  for (const { key, captures, pattern } of sentencePatterns) {
    const match = pattern.exec(value);
    if (!match) continue;
    const args = [];
    captures.forEach((n, i) => { args[n] = match[i + 1]; });
    if (key === "activity.undo") args[0] = uiSentence(args[0]);
    if (key === "file.project" || key === "activity.safe_content") args[1] = uiLabel(args[1]);
    if (/^activity\.(edit_doc|new_doc)/.test(key)) args[0] = uiLabel(args[0]);
    if (/^activity\.(edit_doc_detail|new_doc_detail)/.test(key)) args[2] = uiSentence(args[2]);
    if (key === "activity.todo_next") args[0] = uiSentence(args[0]);
    if (/^activity\.(home_adopt|home_discard)$/.test(key) && args[0] === I18N_ZH["app.050"]) args[0] = uiText("app.050");
    if (key === "attention.summary_detail" || key === "attention.summary_lead") {
      if (args[0] === I18N_ZH["app.085"]) return uiText("attention.no_date") + (args[1] ? " " + uiText("attention.lead", [args[1]]) : "");
      args[0] = uiLabel(args[0]).replace(/^(\d+) 天前$/, "$1 days ago");
    }
    return uiText(key, args);
  }
  return value;
}
function applyStaticLanguage() {
  if (document.documentElement) document.documentElement.lang = uiLocale();
  for (const el of document.querySelectorAll("[data-i18n]")) el.textContent = uiText(el.dataset.i18n);
  for (const el of document.querySelectorAll("[data-i18n-aria]")) el.setAttribute("aria-label", uiText(el.dataset.i18nAria));
  const select = document.getElementById("language");
  if (select) select.value = language;
}
function initLanguage(company) {
  language = resolveLanguage(company);
  applyStaticLanguage();
}
async function selectLanguage(value) {
  if (value !== "en" && value !== "zh") return;
  chosenLanguage = value;
  try { localStorage.setItem(LANGUAGE_KEY, value); } catch {}
  language = value;
  applyStaticLanguage();
  // 重走当前路由，重建动态文字；只改显示语言，不改协议值或已保存的用户内容。
  if (S.me && !SESSION.stopped) { await loadMe(); await route(); }
}
document.getElementById("language")?.addEventListener("change", e => selectLanguage(e.target.value));
applyStaticLanguage();
