"use strict";
// ============================================================ core
const TOKEN = new URLSearchParams(location.search).get("t") || "";
const THEMES = ["light", "dark", "ink"];
const S = {
  state: null, project: null, page: "projects", lang: "uk",
  editor: null, manual: { rel: "", tree: {}, open: new Set([""]), file: null, assets: [], sel: null, typeFilter: "", nameFilter: "", findResults: null, checked: new Set(), picked: new Set() },
  fonts: { ttf: "", list: null, sel: null, report: null, log: [] },
  scan: { results: null, filter: "" },
  job: null, jobLogFrom: 0, undo: [], redo: [], logOnlyErrors: false,
};
const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
function t(k) { const d = I18N[S.lang] || I18N.uk; return d[k] ?? I18N.uk[k] ?? k; }
function esc(s) { return String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c])); }
function fmtSize(n) { n = +n || 0; return n < 1024 ? n + " B" : n < 1048576 ? (n / 1024).toFixed(1) + " KB" : (n / 1048576).toFixed(1) + " MB"; }

async function api(path, body, raw) {
  const opt = { headers: { "X-Token": TOKEN } };
  if (body !== undefined) { opt.method = "POST"; opt.body = JSON.stringify(body); opt.headers["Content-Type"] = "application/json"; }
  const quiet = path.startsWith("/api/jobs/");   // job polling is shown by the job box itself
  if (!quiet) busy(1);
  try {
    const r = await fetch(path, opt);
    if (raw) { if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || r.statusText); return r; }
    const j = await r.json().catch(() => ({ error: r.statusText }));
    if (!r.ok || (j && j.error && !Array.isArray(j))) throw new Error(j.error || r.statusText);
    return j;
  } finally { if (!quiet) busy(-1); }
}
// animated indicator in the status bar: visible while a request or a background operation runs,
// so on big games (thousands of files) it is clear that the launcher works and has not hung
let inflight = 0, busyTmr = null;
function busy(d) {
  inflight = Math.max(0, inflight + (d || 0));
  const on = inflight > 0 || !!S.job, el = $("#busy"); if (!el) return;
  clearTimeout(busyTmr);
  if (on) busyTmr = setTimeout(() => { el.classList.remove("hidden"); $("#busyText").textContent = S.job ? "" : t("busy"); }, S.job ? 0 : 250);
  else el.classList.add("hidden");
}
function q(params) { return Object.entries(params).map(([k, v]) => encodeURIComponent(k) + "=" + encodeURIComponent(v ?? "")).join("&"); }

function toast(msg, kind = "") {
  const d = document.createElement("div"); d.className = "toast " + kind; d.textContent = msg;
  $("#toastRoot").appendChild(d); setTimeout(() => d.remove(), kind === "err" ? 8000 : 4000);
}
function fail(e) { console.error(e); toast(String(e.message || e), "err"); }

function modal({ title, body, buttons, wide }) {
  return new Promise(resolve => {
    const bg = document.createElement("div"); bg.className = "modal-bg";
    const m = document.createElement("div"); m.className = "modal"; if (wide) m.style.width = "min(1100px, 95vw)";
    m.innerHTML = `<header>${esc(title)}</header><div class="body"></div><footer></footer>`;
    const b = $(".body", m); if (typeof body === "string") b.innerHTML = body; else if (body) b.appendChild(body);
    for (const bt of buttons || [{ label: t("ok"), value: true, cls: "primary" }]) {
      const x = document.createElement("button"); x.className = "btn " + (bt.cls || ""); x.textContent = bt.label;
      x.onclick = () => { close(bt.value); }; $("footer", m).appendChild(x);
    }
    function close(v) { bg.remove(); document.removeEventListener("keydown", onKey); resolve(v); }
    function onKey(e) { if (e.key === "Escape") close(null); }
    document.addEventListener("keydown", onKey);
    bg.appendChild(m); $("#modalRoot").appendChild(bg);
    m._close = close;
  });
}
const confirmBox = (text, yes = t("yes"), no = t("no")) => modal({ title: t("appName"), body: `<p>${esc(text)}</p>`, buttons: [{ label: no, value: false }, { label: yes, value: true, cls: "primary" }] });

/** Server-side file/folder picker (works offline; the browser cannot see real paths). */
function pickPath({ files = false, ext = "", start = "" } = {}) {
  return new Promise(async resolve => {
    const body = document.createElement("div");
    body.innerHTML = `<div class="row" style="margin-bottom:8px"><button class="btn small" id="pkUp">⬆ ${esc(t("up"))}</button><input type="text" id="pkPath" class="grow mono"></div><div class="tree" id="pkList" style="height:50vh"></div>`;
    let current = start, chosen = null;
    const p = modal({ title: files ? t("pickFile") : t("pickFolder"), body, buttons: [{ label: t("cancel"), value: null }, { label: t("choose"), value: "ok", cls: "primary" }] });
    async function load(path) {
      try {
        const r = await api("/api/fs/list?" + q({ path, files: files ? 1 : 0, ext }));
        current = r.path; $("#pkPath", body).value = r.path; body._parent = r.parent;
        const list = $("#pkList", body); list.innerHTML = "";
        for (const it of r.items) {
          const d = document.createElement("div"); d.className = "item"; d.innerHTML = (it.dir ? "📁 " : "📄 ") + esc(it.name) + (it.size ? `<span class="size">${fmtSize(it.size)}</span>` : "");
          d.onclick = () => { $$(".item", list).forEach(x => x.classList.remove("sel")); d.classList.add("sel"); chosen = it; $("#pkPath", body).value = it.path; };
          d.ondblclick = () => { if (it.dir) load(it.path); else { chosen = it; $(".modal footer .primary").click(); } };
          list.appendChild(d);
        }
        if (r.error) list.insertAdjacentHTML("beforeend", `<div class="muted">${esc(r.error)}</div>`);
      } catch (e) { fail(e); }
    }
    $("#pkUp", body).onclick = () => load(body._parent ?? "");
    $("#pkPath", body).onkeydown = e => { if (e.key === "Enter") load(e.target.value); };
    load(current);
    const v = await p;
    if (v !== "ok") return resolve(null);
    const typed = $("#pkPath", body).value.trim();
    resolve(typed || (chosen && chosen.path) || current);
  });
}

// ============================================================ state & chrome
async function refreshState() {
  try {
    S.state = await api("/api/state");
    const st = S.state.settings;
    S.lang = st.Language || "uk";
    document.documentElement.dataset.theme = THEMES.includes(st.Theme) ? st.Theme : "dark";
    document.documentElement.lang = S.lang;
    const sel = $("#projectSelect");
    const ids = S.state.projects.map(p => p.id);
    if (!S.project || !ids.includes(S.project)) S.project = localStorage.getItem("project") && ids.includes(localStorage.getItem("project")) ? localStorage.getItem("project") : ids[0] || null;
    sel.innerHTML = S.state.projects.length ? S.state.projects.map(p => `<option value="${p.id}" ${p.id === S.project ? "selected" : ""}>${esc(p.name)}</option>`).join("") : `<option>${esc(t("noProject"))}</option>`;
    $("#errBadge").textContent = S.state.errors; $("#errBadge").classList.toggle("hidden", !S.state.errors);
    $("#dirtyDot").classList.toggle("hidden", !S.state.open.some(o => o.dirty));
    $("#archInfo").textContent = `v${S.state.version} · ${S.state.arch}`;
  } catch (e) { $("#statusText").textContent = "⚠ " + e.message; }
}
function applyI18n() {
  $$("[data-i18n]").forEach(el => el.textContent = t(el.dataset.i18n));
  $$("[data-i18n-title]").forEach(el => el.title = t(el.dataset.i18nTitle));
  document.title = t("appName");
  hkTitles();
  setTimeout(resetTips);   // hover hints follow the interface language
}
const project = () => S.state?.projects.find(p => p.id === S.project) || null;
function needProject() { if (!S.project) { toast(t("needProject"), "warn"); go("projects"); return false; } return true; }

// the work is shown as 6 steps; manual search and ready-made files are tabs inside steps 2 and 5
const STEPS = ["projects", "auto", "editor", "fonts", "changes", "pack"];
const STEP_OF = { projects: 0, auto: 1, manual: 1, editor: 2, fonts: 3, changes: 4, batch: 4, pack: 5 };
const STEP_TABS = { 1: [["auto", "tab_auto"], ["manual", "tab_manual"]], 4: [["changes", "tab_changes"], ["batch", "tab_batch"]] };
function go(page) {
  S.page = page;
  const navPage = STEPS[STEP_OF[page]] || page;
  $$("#nav a").forEach(a => a.classList.toggle("active", a.dataset.page === navPage));
  render();
}
function stepHead() {
  const i = STEP_OF[S.page]; if (i == null) return null;
  const tabs = STEP_TABS[i], prev = STEPS[i - 1], next = STEPS[i + 1];
  const el = document.createElement("div"); el.className = "step-head";
  el.innerHTML = `<span class="step-kicker">${esc(t("stepOf").replace("{n}", i + 1).replace("{m}", STEPS.length))}</span>
    ${tabs ? `<div class="seg-tabs">${tabs.map(([p, k]) => `<button class="${p === S.page ? "on" : ""}" data-go="${p}">${esc(t(k))}</button>`).join("")}</div>` : ""}
    <span class="grow"></span>
    ${prev ? `<button class="btn small ghost" data-go="${prev}">← ${esc(t("step_" + prev))}</button>` : ""}
    ${next ? `<button class="btn small" data-go="${next}" ${S.project ? "" : "disabled"}>${esc(t("stepNext"))}: ${esc(t("step_" + next))} →</button>` : ""}`;
  el.onclick = e => { const b = e.target.closest("[data-go]"); if (b && !b.disabled) go(b.dataset.go); };
  return el;
}
function render() {
  applyI18n();
  $$(".ed-only").forEach(b => b.classList.toggle("hidden", S.page !== "editor"));
  const m = $("#main"); m.innerHTML = "";
  const head = stepHead(), page = document.createElement("div");
  if (head) m.appendChild(head);
  m.appendChild(page);
  const fn = { projects: pProjects, auto: pAuto, manual: pManual, editor: pEditor, fonts: pFonts, changes: pChanges, pack: pPack, log: pLog, settings: pSettings, help: pHelp, about: pAbout, batch: pBatch }[S.page];
  try { fn(page); } catch (e) { fail(e); }
}

async function saveSettings(patch) {
  const st = Object.assign({}, S.state.settings, patch);
  S.state = await api("/api/settings", st);
  await refreshState(); render();
}

// ============================================================ jobs (pause / resume / stop)
function trackJob(id, kind, onDone) {
  S.job = { id, kind, onDone }; S.jobLogFrom = 0;
  $("#jobBox").classList.remove("hidden"); $("#jobName").textContent = kind;
  busy();
  pollJob();
}
async function pollJob() {
  const job = S.job; if (!job) return;
  try {
    const j = await api(`/api/jobs/${job.id}?log=${S.jobLogFrom}`);
    if (S.job !== job) return;   // a newer job replaced this one while waiting
    S.jobLogFrom += j.log.length;
    if (job.onLog) job.onLog(j.log);
    $("#jobBar").style.width = Math.round(j.progress * 100) + "%";
    $("#jobPct").textContent = Math.round(j.progress * 100) + "% " + (j.status === "paused" ? "⏸" : "");
    $("#jobMsg").textContent = j.message || "";
    if (j.status === "running" || j.status === "paused") { setTimeout(pollJob, 500); return; }
    const done = job; S.job = null;
    $("#jobBox").classList.add("hidden"); busy();
    if (j.status === "done") toast(t("jobDone") + ": " + done.kind, "ok");
    else if (j.status === "cancelled") toast(t("jobCancelled"), "warn");
    else toast(t("jobError") + ": " + (j.error || ""), "err");
    done.onDone && await done.onDone(j);
    refreshState();
    runAfterJob();
  } catch (e) { if (S.job !== job) return; fail(e); S.job = null; $("#jobBox").classList.add("hidden"); busy(); runAfterJob(); }
}
// actions asked for while another operation runs (one operation at a time): started when it ends
S.afterJob = [];
function afterJob(fn) { if (!S.job) return fn(); S.afterJob.push(fn); }
function runAfterJob() { if (S.job || !S.afterJob.length) return; const fn = S.afterJob.shift(); Promise.resolve(fn()).catch(fail).finally(runAfterJob); }
async function jobCmd(cmd) {
  if (!S.job) return toast(t("noJob"), "warn");
  await api(`/api/jobs/${S.job.id}/${cmd}`, {});
}

// ============================================================ save / apply / exit
async function doSave(quick) {
  if (!S.project) return;
  try {
    if (S.editor) await flushEditor();
    const r = await api("/api/save", { project: S.project });
    if (!r.saved.length) { toast(t("nothingToSave"), "warn"); return; }
    toast(t("savedN") + r.saved.length, "ok");
    if (r.catalogs && r.catalogs.length) toast(t("catalogPatched") + ": " + r.catalogs.join(", "), "ok");
    if (r.askApply && !quick && await confirmBox(t("askApply") + "\n\n" + r.saved.join("\n")))
      { await api("/api/apply", { project: S.project, rels: r.saved }); toast(t("appliedN") + ": " + r.saved.length, "ok"); if (!(r.catalogs && r.catalogs.length)) await askCatalogs(); }
    await refreshState(); if (S.page === "editor") renderEditorHeader(); if (S.page === "changes") render();
  } catch (e) { fail(e); }
}
async function doApplyAll() {
  if (!needProject()) return;
  if (!await confirmBox(t("askApplyAll"))) return;
  try { const r = await api("/api/apply", { project: S.project }); toast(t("appliedN") + ": " + r.applied, "ok"); await askCatalogs(); if (S.page === "changes") render(); } catch (e) { fail(e); }
}
// Addressables catalog: offer to reset bundle CRCs so the game accepts modified bundles
async function askCatalogs(force) {
  try {
    const cats = (await api("/api/catalogs?" + q({ project: S.project }))).filter(c => c.crc > 0);
    if (!cats.length) { if (force) toast(t("catalogOk"), "ok"); return; }
    if (!force) {
      const ch = await api("/api/changed?" + q({ project: S.project }));
      if (!ch.some(f => /\.bundle$/i.test(f.rel) || !/catalog/i.test(f.rel))) return;
    }
    if (!await confirmBox(t("askCatalog") + "\n\n" + cats.map(c => c.rel).join("\n"))) return;
    const r = await api("/api/catalogs/patch", { project: S.project });
    if (r.patched.length && !S.state.settings.ReplaceInPlace) await api("/api/apply", { project: S.project, rels: r.patched });
    toast(t("catalogPatched") + ": " + r.patched.join(", "), "ok");
    if (S.page === "changes") render();
  } catch (e) { fail(e); }
}
async function doRestoreAll() {
  if (!needProject()) return;
  if (!await confirmBox(t("askRestoreAll"))) return;
  try { const r = await api("/api/restore", { project: S.project }); toast(t("restoreOne") + ": " + r.restored, "ok"); if (S.page === "changes") render(); } catch (e) { fail(e); }
}
async function doExit() {
  try {
    if (S.editor) await flushEditor();
    let r = await api("/api/exit", {});
    if (r.dirty) {
      const v = await modal({ title: t("exit"), body: `<p>${esc(t("exitDirty"))}</p><pre>${esc(r.dirty.join("\n"))}</pre>`,
        buttons: [{ label: t("cancel"), value: null }, { label: t("exitNoSave"), value: "nosave", cls: "danger" }, { label: t("exitSave"), value: "save", cls: "primary" }] });
      if (!v) return;
      if (v === "save") for (const p of S.state.projects) await api("/api/save", { project: p.id });
      await api("/api/exit", { force: true });
    }
    document.body.innerHTML = `<div class="empty" style="grid-column:1/3;grid-row:1/4;font-size:18px">${esc(t("bye"))}</div>`;
    setTimeout(() => window.close(), 600);
  } catch (e) { fail(e); }
}

// ============================================================ undo / redo (editor)
async function undo() {
  const a = S.undo.pop(); if (!a) return toast(t("nothingToUndo"), "warn");
  await sendEdit(a, true); S.redo.push(a);
}
async function redo() {
  const a = S.redo.pop(); if (!a) return;
  await sendEdit(a, false); S.undo.push(a);
}
async function sendEdit(a, back) {
  const value = back ? a.old : a.val;
  if (a.text) await api("/api/asset/edit", { ...a.target, text: value });
  else await api("/api/asset/edit", { ...a.target, changes: [{ id: a.id, col: a.col, value }] });
  if (S.editor && S.editor.key === a.key) {
    if (a.text) { const ta = $("#edText"); if (ta) ta.value = value; S.editor.lastText = value; }
    else { const ta = $(`textarea[data-id="${CSS.escape(a.id)}"][data-col="${a.col}"]`); if (ta) { ta.value = value; ta.dataset.orig = value; ta.classList.add("changed"); } updateRow(a.id, a.col, value); }
  }
  refreshState();
}

// ============================================================ PAGE: projects
function pProjects(m) {
  const has = S.state.projects.length > 0;
  const addForm = `<div class="row"><input type="text" id="npPath" class="grow mono" placeholder="${esc(t("gamePath"))}"><button class="btn" id="npBrowse">${esc(t("browse"))}</button></div>
    <div class="row" style="margin-top:8px"><input type="text" id="npName" class="grow" placeholder="${esc(t("projectName"))}"><button class="btn primary" id="npAdd">${esc(t("add"))}</button></div>`;
  m.innerHTML = `<h1>${esc(t("projects_title"))}</h1><div class="sub">${esc(t("projects_sub"))}</div>
  <div id="plist"></div>
  ${has ? `<details class="card fold"><summary><b>＋ ${esc(t("addGame"))}</b></summary><div style="margin-top:12px">${addForm}</div></details>`
        : `<div class="card"><h2 style="margin-top:0">${esc(t("addGame"))}</h2>${addForm}</div>`}`;
  $("#npBrowse").onclick = async () => { const p = await pickPath({}); if (p) $("#npPath").value = p; };
  $("#npAdd").onclick = async () => {
    try { const r = await api("/api/projects/add", { path: $("#npPath").value, name: $("#npName").value }); S.project = r.id; localStorage.setItem("project", r.id); await refreshState(); go("auto"); }
    catch (e) { fail(e); }
  };
  const list = $("#plist");
  for (const p of S.state.projects) {
    const c = document.createElement("div"); c.className = "card";
    c.innerHTML = `<div class="row"><div class="grow"><b>${esc(p.name)}</b> ${p.id === S.project ? `<span class="kbadge k-i2">${esc(t("current"))}</span>` : ""} ${p.exists ? "" : `<span class="kbadge" style="color:var(--err)">${esc(t("notFound"))}</span>`}
      <div class="muted mono">${esc(p.gameRoot)}</div><div class="muted mono" style="font-size:12px">${esc(t("workspace"))}: ${esc(p.workDir)}</div></div>
      <button class="btn small primary" data-a="open">${esc(t(p.id === S.project ? "continue" : "open"))}</button>
      <details class="more"><summary class="btn small" title="${esc(t("more"))}">⋯</summary><div class="menu">
        <button class="btn small" data-a="game">${esc(t("openGameFolder"))}</button><button class="btn small" data-a="work">${esc(t("openWorkFolder"))}</button><button class="btn small" data-a="expdir" title="${esc(p.exports || "")}">${esc(t("exportDir"))}…</button>
        <button class="btn small danger" data-a="rm">${esc(t("remove"))}</button></div></details></div>`;
    c.onclick = async e => {
      const a = e.target.dataset.a; if (!a) return;
      try {
        if (a === "open") { S.project = p.id; localStorage.setItem("project", p.id); await refreshState(); go("auto"); }
        if (a === "expdir") await chooseExportDir(p.id, true);
        if (a === "game" || a === "work") { const r = await api("/api/open-folder", { project: p.id, which: a }); toast(r.dir); }
        if (a === "rm" && await confirmBox(t("confirmRemoveProject"))) { await api("/api/projects/remove", { id: p.id }); await refreshState(); render(); }
      } catch (er) { fail(er); }
    };
    list.appendChild(c);
  }
}

// ============================================================ PAGE: auto search
const kindBadge = k => `<span class="kbadge k-${esc(k)}">${esc({ stringtable: "StringTable", i2: "I2 Loc", textasset: "TextAsset", loose: "File", monobehaviour: "MonoBehaviour" }[k] || k)}</span>`;
async function pAuto(m) {
  if (!needProject()) return;
  const st = S.state.settings;
  m.innerHTML = `<h1>${esc(t("auto_title"))}</h1><div class="sub">${esc(t("auto_sub"))}</div>
  <div class="card">
    <div class="row"><span class="grow muted">${esc(t("scanHint"))}</span><button class="btn primary big" id="scanGo">🔍 ${esc(t("startScan"))}</button></div>
    <details class="fold" style="margin-top:10px"><summary class="muted">${esc(t("searchOptions"))}</summary>
    <div class="row" style="margin-top:10px"><b>${esc(t("customKeywords"))}</b><input type="text" id="kwNew" placeholder="${esc(t("addKeyword"))}" style="width:240px"><button class="btn small" id="kwAdd">+</button></div>
    <div class="chips" id="kwCustom" style="margin:8px 0"></div>
    <details><summary class="muted">${esc(t("defaultKeywords"))} (${S.state.defaultKeywords.length})</summary><div class="chips" style="margin-top:8px">${S.state.defaultKeywords.map(k => `<span class="chip">${esc(k)}</span>`).join("")}</div></details>
    <div class="row" style="margin-top:12px">
      <label class="chk"><input type="checkbox" id="kwDef" ${st.UseDefaultKeywords ? "checked" : ""}>${esc(t("useDefaults"))}</label>
      <label class="chk"><input type="checkbox" id="kwDeep" ${st.DeepScan ? "checked" : ""}>${esc(t("deepScan"))}</label></div>
    </details>
  </div>
  <div class="row" style="margin-bottom:8px"><h2 class="grow" style="margin:0">${esc(t("results"))} <span class="muted" id="resCount"></span></h2>
    <select id="resKind"><option value="">${esc(t("allKinds"))}</option></select>
    <select id="resSort"><option value="score">${esc(t("sortScore"))}</option><option value="name">${esc(t("sortName"))}</option><option value="kind">${esc(t("sortKind"))}</option><option value="file">${esc(t("sortFile"))}</option><option value="hits">${esc(t("sortHits"))}</option></select>
    <input type="text" id="resFilter" placeholder="${esc(t("filter"))}" value="${esc(S.scan.filter)}"></div>
  <div class="tbl-wrap" style="max-height:calc(100vh - 420px)"><table class="tbl clickable"><thead id="resHead"></thead><tbody id="resBody"></tbody></table></div>`;
  const drawKw = () => {
    $("#kwCustom").innerHTML = (S.state.settings.CustomKeywords || []).map((k, i) => `<span class="chip custom">${esc(k)}<button data-i="${i}">✕</button></span>`).join("") || `<span class="muted">—</span>`;
    $$("#kwCustom button").forEach(b => b.onclick = () => { const a = [...S.state.settings.CustomKeywords]; a.splice(+b.dataset.i, 1); saveSettings({ CustomKeywords: a }); });
  };
  drawKw();
  const addKw = () => { const v = $("#kwNew").value.trim(); if (!v) return; saveSettings({ CustomKeywords: [...(S.state.settings.CustomKeywords || []), v] }); };
  $("#kwAdd").onclick = addKw; $("#kwNew").onkeydown = e => { if (e.key === "Enter") addKw(); };
  $("#kwDef").onchange = e => saveSettings({ UseDefaultKeywords: e.target.checked });
  $("#kwDeep").onchange = e => saveSettings({ DeepScan: e.target.checked });
  $("#resFilter").oninput = e => { S.scan.filter = e.target.value; drawRes(); };
  $("#scanGo").onclick = async () => {
    try {
      const r = await api("/api/scan", { project: S.project, keywords: S.state.settings.CustomKeywords, useDefaults: $("#kwDef").checked, deep: $("#kwDeep").checked });
      trackJob(r.job, t("nav_auto"), j => { if (j.result) { S.scan.results = j.result; if (S.page === "auto") drawRes(); } });
    } catch (e) { fail(e); }
  };
  const resDef = { k: "score", d: -1 };
  $("#resKind").onchange = e => { S.scan.kind = e.target.value; drawRes(); };
  $("#resSort").onchange = e => { const k = e.target.value; S.sorts.auto = { k, d: ["score", "hits"].includes(k) ? -1 : 1 }; drawRes(); };
  function drawRes() {
    const body = $("#resBody"); if (!body) return;
    const res = S.scan.results || [];
    const kinds = [...new Set(res.map(h => h.kind))].sort();
    $("#resKind").innerHTML = `<option value="">${esc(t("allKinds"))}</option>` + kinds.map(k => `<option value="${esc(k)}" ${S.scan.kind === k ? "selected" : ""}>${esc(k)} (${res.filter(h => h.kind === k).length})</option>`).join("");
    const cur = S.sorts.auto || resDef; $("#resSort").value = cur.k;
    $("#resHead").innerHTML = `<tr>${th("auto", "score", t("score"), resDef)}${th("auto", "kind", t("kind"), resDef)}${th("auto", "name", t("asset"), resDef)}${th("auto", "file", t("file"), resDef)}${th("auto", "hits", t("matches"), resDef)}<th></th></tr>`;
    bindSort($("#resHead"), drawRes, resDef);
    const f = S.scan.filter.toLowerCase();
    const rows = sortRows("auto", res.filter(h => (!S.scan.kind || h.kind === S.scan.kind) && (!f || (h.rel + h.name + h.script + h.hits.join(" ")).toLowerCase().includes(f))),
      { score: h => h.score, kind: h => h.kind, name: h => h.name || "", file: h => h.rel, hits: h => h.hits.length }, resDef);
    $("#resCount").textContent = rows.length ? `(${rows.length})` : "";
    if (!rows.length) { body.innerHTML = `<tr><td colspan="6" class="empty">${esc(t("noResults"))}</td></tr>`; return; }
    body.innerHTML = rows.slice(0, 2000).map((h, i) => `<tr data-i="${res.indexOf(h)}"><td class="num">${h.score}</td><td>${kindBadge(h.kind)}</td>
      <td class="wrap"><b>${esc(h.name || "—")}</b><div class="muted" style="font-size:12px">${esc(h.script || h.type)}</div></td>
      <td class="wrap mono" style="font-size:12px">${esc(h.rel)}</td><td class="wrap muted" style="font-size:12px">${esc(h.hits.join(", "))}</td>
      <td><button class="btn small primary">${esc(t("open"))}</button></td></tr>`).join("");
    $$("#resBody tr").forEach(tr => { const h = res[+tr.dataset.i]; tr.ondblclick = () => openAsset(h.rel, h.file, h.pathId, h.name); $("button", tr).onclick = () => openAsset(h.rel, h.file, h.pathId, h.name); });
  }
  if (!S.scan.results) { try { S.scan.results = await api("/api/scan/last?" + q({ project: S.project })); } catch { S.scan.results = []; } }
  drawRes();
}

// ============================================================ PAGE: manual (UABEA-like)
async function pManual(m) {
  if (!needProject()) return;
  const M = S.manual;
  m.innerHTML = `<h1>${esc(t("manual_title"))}</h1><div class="sub">${esc(t("manual_sub"))}</div>
  <div class="row" style="margin-bottom:10px"><input type="text" id="ftText" class="grow" placeholder="${esc(t("findText"))}"><button class="btn" id="ftGo">🔍 ${esc(t("find"))}</button></div>
  <div class="split"><div class="pane"><div class="row" style="margin-bottom:6px"><h2 class="grow" style="margin:0">${esc(t("gameFiles"))}</h2></div>
    <div class="row tree-acts" id="treeActs"></div><div class="tree" id="tree"></div></div>
  <div class="pane" id="assetPane"></div></div>`;
  $("#ftGo").onclick = async () => {
    const text = $("#ftText").value.trim(); if (!text) return;
    try { const r = await api("/api/findtext", { project: S.project, text }); trackJob(r.job, t("find"), j => { M.findResults = j.result || []; if (S.page === "manual") drawAssets(); }); } catch (e) { fail(e); }
  };
  $("#ftText").onkeydown = e => { if (e.key === "Enter") $("#ftGo").click(); };
  await drawTree();
  drawAssets();
}
async function loadDir(rel) {
  const r = await api("/api/game/list?" + q({ project: S.project, rel }));
  S.manual.tree[rel] = r.items;
}
async function drawTree() {
  const M = S.manual, el = $("#tree"); if (!el) return;
  if (!M.tree[""]) { try { await loadDir(""); } catch (e) { fail(e); return; } }
  el.innerHTML = "";
  const add = (rel, depth) => {
    for (const it of M.tree[rel] || []) {
      const d = document.createElement("div");
      d.className = "item" + (it.unity ? " unity" : "") + (it.modified ? " mod" : "") + (M.file === it.rel ? " sel" : "");
      d.style.paddingLeft = (6 + depth * 14) + "px";
      d.innerHTML = (it.dir ? "" : `<input type="checkbox" class="tchk" ${M.checked.has(it.rel) ? "checked" : ""} title="${esc(t("replPick"))}">`) + (it.dir ? (M.open.has(it.rel) ? "📂 " : "📁 ") : it.unity ? "🧩 " : "📄 ") + esc(it.name) + (it.dir ? "" : `<span class="size">${fmtSize(it.size)}</span>`);
      const cb = $(".tchk", d);
      if (cb) { cb.onclick = e => e.stopPropagation(); cb.onchange = () => { if (cb.checked) M.checked.add(it.rel); else M.checked.delete(it.rel); drawTreeActs(); }; }
      d.onclick = async () => {
        if (it.dir) { if (M.open.has(it.rel)) M.open.delete(it.rel); else { M.open.add(it.rel); if (!M.tree[it.rel]) await loadDir(it.rel); } drawTree(); }
        else { M.file = it.rel; M.sel = null; M.picked.clear(); M.findResults = null; drawTree(); loadAssets(); }
      };
      el.appendChild(d);
      if (it.dir && M.open.has(it.rel)) add(it.rel, depth + 1);
    }
  };
  add("", 0);
  drawTreeActs();
}
// files ticked in the tree are replaced as whole files (bundles, .assets …) in one step
function drawTreeActs() {
  const M = S.manual, el = $("#treeActs"); if (!el) return;
  const n = M.checked.size;
  el.innerHTML = n ? `<span class="muted" style="width:100%">${esc(t("replChecked"))}: <b>${n}</b></span><button class="btn small primary" id="trRepl">⇄ ${esc(t("replFiles"))}</button><button class="btn small ghost" id="trNone">${esc(t("selNone"))}</button>`
    : `<span class="muted" style="font-size:12px">${esc(t("replHint"))}</span>`;
  if (!n) return;
  $("#trRepl").onclick = () => replaceFiles([...M.checked]);
  $("#trNone").onclick = () => { M.checked.clear(); drawTree(); };
}
async function replaceFiles(rels) {
  if (!needProject() || !rels.length) return;
  let pairs;
  if (rels.length === 1) {
    const src = await pickPath({ files: true });
    if (!src) return;
    pairs = [{ rel: rels[0], src }];
  } else {
    const dir = await pickPath({});
    if (!dir) return;
    let m; try { m = await api("/api/file/replace/match", { project: S.project, rels, dir }); } catch (e) { return fail(e); }
    const found = m.filter(x => x.src);
    const body = `<p>${esc(t("replFolderText"))}</p><div class="tbl-wrap" style="max-height:50vh"><table class="tbl"><thead><tr><th>${esc(t("file"))}</th><th>${esc(t("replFrom"))}</th><th>${esc(t("size"))}</th></tr></thead><tbody>${
      m.map(x => `<tr><td class="mono wrap" style="font-size:12px">${esc(x.rel)}</td><td class="mono wrap" style="font-size:12px">${x.src ? esc(x.src) : `<span style="color:var(--err)">${esc(t("replNoFile"))}</span>`}</td><td class="num">${x.src ? fmtSize(x.size) : ""}</td></tr>`).join("")}</tbody></table></div>`;
    if (!found.length) { await modal({ title: t("replFiles"), body, wide: true }); return; }
    if (await modal({ title: t("replFiles"), body, wide: true, buttons: [{ label: t("cancel"), value: false }, { label: t("replGo") + " (" + found.length + ")", value: true, cls: "primary" }] }) !== true) return;
    pairs = found.map(x => ({ rel: x.rel, src: x.src }));
  }
  const dirty = S.state.open.filter(o => o.project === S.project && o.dirty && pairs.some(p => p.rel.toLowerCase() === o.rel.toLowerCase()));
  if (dirty.length && !await confirmBox(t("replDirty") + "\n\n" + dirty.map(o => o.rel).join("\n"))) return;
  if (rels.length === 1 && !await confirmBox(t("replOneQ").replace("{f}", pairs[0].rel).replace("{s}", pairs[0].src))) return;
  try {
    const r = await api("/api/file/replace", { project: S.project, items: pairs });
    afterJob(() => trackJob(r.job, t("replFiles"), async j => {
      if (j.status !== "done" || !j.result) return;
      const res = j.result;
      toast(t("replDone") + ": " + res.ok + (res.failed ? " · " + t("batchFailed") + ": " + res.failed : ""), res.failed ? "warn" : "ok");
      if (res.catalogs && res.catalogs.length) toast(t("catalogPatched") + ": " + res.catalogs.join(", "), "ok");
      const M = S.manual;
      for (const p of pairs) { M.checked.delete(p.rel); const dir = p.rel.includes("/") || p.rel.includes("\\") ? p.rel.replace(/[\\/][^\\/]*$/, "") : ""; delete M.tree[dir]; }
      await refreshState();
      if (S.page === "manual") { await drawTree(); if (pairs.some(p => p.rel === M.file)) loadAssets(); }
      if (res.saved && res.saved.length && res.askApply && await confirmBox(t("askApply") + "\n\n" + res.saved.join("\n"))) {
        await api("/api/apply", { project: S.project, rels: res.saved }); toast(t("appliedN") + ": " + res.saved.length, "ok");
      }
    }));
  } catch (e) { fail(e); }
}
async function loadAssets() {
  const M = S.manual; const pane = $("#assetPane");
  pane.innerHTML = `<div class="empty">…</div>`;
  try { M.info = await api("/api/file/assets?" + q({ project: S.project, rel: M.file })); M.assets = M.info.assets || []; }
  catch (e) { M.info = { error: e.message }; M.assets = []; }
  drawAssets();
}
function drawAssets() {
  const M = S.manual, pane = $("#assetPane"); if (!pane) return;
  if (M.findResults) {
    const fd = { k: "file", d: 1 };
    const types = [...new Set(M.findResults.map(h => h.type))].sort();
    const list = sortRows("find", M.findResults.map((h, i) => ({ ...h, _i: i })).filter(h => !M.findType || h.type === M.findType),
      { name: h => h.name || "", type: h => h.type, file: h => h.rel, hits: h => (h.hits || []).length, count: h => h.count || 0 }, fd);
    pane.innerHTML = `<div class="row" style="margin-bottom:8px"><h2 class="grow" style="margin:0">${esc(t("find"))}: ${list.length}</h2>
      <select id="fdType"><option value="">${esc(t("allTypes"))}</option>${types.map(x => `<option ${x === M.findType ? "selected" : ""}>${esc(x)}</option>`).join("")}</select></div>
      <div class="tbl-wrap"><table class="tbl clickable"><thead><tr>${th("find", "name", t("asset"), fd)}${th("find", "type", t("type"), fd)}${th("find", "file", t("file"), fd)}${th("find", "hits", t("matches"), fd)}</tr></thead><tbody>${
      list.map(h => `<tr data-i="${h._i}"><td><b>${esc(h.name || "—")}</b> <span class="muted">#${esc(h.pathId)}</span></td><td>${esc(h.type)}</td><td class="mono wrap" style="font-size:12px">${esc(h.rel)}</td><td class="wrap muted" style="font-size:12px">${esc(h.hits[0] || "")}</td></tr>`).join("")}</tbody></table></div>`;
    bindSort(pane, drawAssets, fd);
    $("#fdType").onchange = e => { M.findType = e.target.value; drawAssets(); };
    $$("tbody tr", pane).forEach(tr => tr.ondblclick = () => { const h = M.findResults[+tr.dataset.i]; openAsset(h.rel, h.file, h.pathId, h.name); });
    return;
  }
  if (!M.file) { pane.innerHTML = `<div class="empty">${esc(t("selectFile"))}</div>`; return; }
  if (!M.info) return;
  if (M.info.error) { pane.innerHTML = `<div class="card" style="color:var(--err)">${esc(M.info.error)}</div>`; return; }
  if (M.info.loose) { pane.innerHTML = `<div class="card">${esc(t("notUnity"))} <button class="btn small primary" id="openLoose">${esc(t("edit"))}</button></div>`; $("#openLoose").onclick = () => openAsset(M.file, -1, "0", M.file); return; }
  const types = [...new Set(M.assets.map(a => a.type))].sort();
  pane.innerHTML = `<div class="card" style="padding:10px 14px"><div class="grid2" style="font-size:12px">
      <div><span class="muted">${esc(t("container"))}:</span> ${esc(M.info.container)} (${M.info.inner.length})</div><div><span class="muted">${esc(t("unityVersion"))}:</span> ${esc(M.info.unityVersion)}</div>
      <div><span class="muted">${esc(t("typeSource"))}:</span> ${esc(M.info.typeSource || "typetree")}</div><div><span class="muted">${esc(t("source"))}:</span> ${esc(t("src_" + M.info.source))}</div></div>
      <div class="row" style="margin-top:8px"><span class="grow mono muted" style="font-size:12px">${esc(M.file)}</span><button class="btn small" id="replWhole" title="${esc(t("replWholeTip"))}">⇄ ${esc(t("replWhole"))}</button></div></div>
    <div class="row" style="margin-bottom:8px"><select id="tyF"><option value="">${esc(t("allTypes"))}</option>${types.map(x => `<option ${x === M.typeFilter ? "selected" : ""}>${esc(x)}</option>`).join("")}</select>
      <input type="text" id="nmF" class="grow" placeholder="${esc(t("filter"))}" value="${esc(M.nameFilter)}"></div>
    <div class="row" style="margin-bottom:8px" id="assetActions"></div>
    <div class="tbl-wrap"><table class="tbl clickable"><thead id="aHead"></thead><tbody id="aBody"></tbody></table></div>`;
  $("#replWhole").onclick = () => replaceFiles([M.file]);
  $("#tyF").onchange = e => { M.typeFilter = e.target.value; drawRows(); };
  $("#nmF").oninput = e => { M.nameFilter = e.target.value; drawRows(); };
  const ad = { k: "pathId", d: 1 };
  let shown = [];
  const pk = a => a.file + "|" + a.pathId;
  function drawRows() {
    $("#aHead").innerHTML = `<tr><th style="width:28px"><input type="checkbox" id="aAll" title="${esc(t("selAll"))}"></th>${th("assets", "name", t("name"), ad)}${th("assets", "type", t("type"), ad)}${th("assets", "pathId", "PathID", ad)}${th("assets", "size", t("size"), ad)}${th("assets", "file", "#", ad)}</tr>`;
    bindSort($("#aHead"), drawRows, ad);
    const f = M.nameFilter.toLowerCase();
    const rows = sortRows("assets", M.assets.filter(a => (!M.typeFilter || a.type === M.typeFilter) && (!f || (a.name + " " + a.script + " " + a.pathId).toLowerCase().includes(f))),
      { name: a => a.name || "", type: a => a.type, pathId: a => a.pathId, size: a => +a.size, file: a => +a.file }, ad);
    shown = rows.slice(0, 5000);
    $("#aBody").innerHTML = shown.map(a => `<tr data-k="${a.file}|${a.pathId}" class="${M.sel && M.sel.pathId === a.pathId && M.sel.file === a.file ? "sel" : ""}"><td><input type="checkbox" class="aChk" ${M.picked.has(pk(a)) ? "checked" : ""}></td><td class="wrap"><b>${esc(a.name || "—")}</b>${a.script ? `<div class="muted" style="font-size:12px">${esc(a.script)}</div>` : ""}</td><td>${esc(a.type)}</td><td class="mono" style="font-size:12px">${esc(a.pathId)}</td><td class="num">${fmtSize(a.size)}</td><td class="num">${a.file}</td></tr>`).join("");
    $$("#aBody tr").forEach(tr => {
      const [file, pid] = tr.dataset.k.split("|"); const a = M.assets.find(x => x.file == file && x.pathId === pid);
      tr.onclick = () => { M.sel = a; $$("#aBody tr").forEach(x => x.classList.remove("sel")); tr.classList.add("sel"); drawActions(); };
      tr.ondblclick = () => openAsset(M.file, a.file, a.pathId, a.name || a.type);
      const cb = $(".aChk", tr);
      cb.onclick = e => e.stopPropagation(); cb.ondblclick = e => e.stopPropagation();
      cb.onchange = () => { if (cb.checked) M.picked.add(pk(a)); else M.picked.delete(pk(a)); syncAll(); drawActions(); };
    });
    $("#aAll").onchange = e => { for (const a of shown) { if (e.target.checked) M.picked.add(pk(a)); else M.picked.delete(pk(a)); } $$("#aBody .aChk").forEach(c => c.checked = e.target.checked); drawActions(); };
    syncAll();
  }
  function syncAll() {
    const all = $("#aAll"); if (!all) return;
    const n = shown.filter(a => M.picked.has(pk(a))).length;
    all.checked = n > 0 && n === shown.length; all.indeterminate = n > 0 && n < shown.length;
  }
  function drawActions() {
    const a = M.sel, box = $("#assetActions");
    const n = M.picked.size;
    const multi = n ? `<div class="row multi-acts" style="width:100%"><span class="grow">${esc(t("assetsPicked"))}: <b>${n}</b></span>
      <button class="btn small primary" id="aImpMany" title="${esc(t("impManyTip"))}">⇪ ${esc(t("impMany"))}</button><button class="btn small ghost" id="aNone">${esc(t("selNone"))}</button></div>` : "";
    if (!a) { box.innerHTML = multi; bindMulti(); return; }
    box.innerHTML = multi + `<b class="grow">${esc(a.name || a.type)} <span class="muted">#${esc(a.pathId)}</span></b>
      <button class="btn small primary" data-x="edit">✎ ${esc(t("edit"))}</button><button class="btn small" data-x="dump">{ } ${esc(t("viewDump"))}</button>
      <select data-x="exp"><option value="">${esc(t("export"))}…</option><option value="txt">${esc(t("exportTxt"))}</option><option value="json">${esc(t("exportJson"))}</option><option value="csv">${esc(t("exportCsv"))}</option><option value="dump">${esc(t("exportDump"))}</option><option value="dat">${esc(t("exportDat"))}</option></select>
      <button class="btn small" data-x="imp">⇪ ${esc(t("import"))}</button>`;
    const tgt = { project: S.project, rel: M.file, file: a.file, pathId: a.pathId };
    $('[data-x="edit"]', box).onclick = () => openAsset(M.file, a.file, a.pathId, a.name || a.type);
    $('[data-x="dump"]', box).onclick = async () => {
      try { const r = await api("/api/asset/dump?" + q(tgt), undefined, true); const txt = await r.text(); modal({ title: (a.name || a.type) + " — dump", body: `<pre class="mono">${esc(txt.length > 400000 ? txt.slice(0, 400000) + "\n…" : txt)}</pre>`, wide: true }); }
      catch (e) { fail(e); }
    };
    $('[data-x="exp"]', box).onchange = e => { if (e.target.value) { exportAsset(tgt, e.target.value); e.target.value = ""; } };
    $('[data-x="imp"]', box).onclick = () => chooseAndImport(tgt);
    bindMulti();
  }
  function bindMulti() {
    if (!$("#aImpMany")) return;
    $("#aImpMany").onclick = () => importMany(M.file, M.assets.filter(a => M.picked.has(pk(a))));
    $("#aNone").onclick = () => { M.picked.clear(); drawRows(); drawActions(); };
  }
  drawRows(); drawActions();
}

// each project keeps its own export folder; asked on the first export
async function chooseExportDir(pid, always) {
  const p = S.state.projects.find(x => x.id === pid); if (!p) return;
  if (p.exportDir && !always) return;
  toast(t("chooseExportDir"));
  const dir = await pickPath({ start: p.exportDir || p.exports || p.gameRoot });
  if (!dir && !always && !await confirmBox(t("exportDefaultAsk") + "\n" + p.exports)) return false;
  if (!dir && always) return;
  const r = await api("/api/projects/export-dir", { id: pid, dir: dir || p.exports });
  p.exportDir = dir || p.exports; p.exports = r.exports;
  if (always) toast(t("exportDir") + ": " + r.exports, "ok");
}
async function exportAsset(tgt, format) {
  try {
    if (await chooseExportDir(tgt.project || S.project) === false) return;
    const r = await api("/api/asset/export?" + q({ ...tgt, format }), undefined, true);
    await r.blob();
    toast(t("exported") + decodeURIComponent(r.headers.get("X-Saved-To") || ""), "ok");
  } catch (e) { fail(e); }
}
// many files into the assets ticked in one bundle: matched by asset name (or PathID in the file name); one file + one asset = direct
function importMany(rel, assets) {
  if (!assets.length) return;
  const inp = document.createElement("input"); inp.type = "file"; inp.multiple = true;
  inp.onchange = async () => {
    const files = [...inp.files]; if (!files.length) return;
    try {
      const list = []; for (const f of files) list.push({ name: f.name, data: await fileToB64(f) });
      const r = await api("/api/manual/import", { project: S.project, rel, lang: S.lang, targets: assets.map(a => ({ file: a.file, pathId: a.pathId })), files: list });
      if (!r.matched) {
        await modal({ title: t("impMany"), body: `<p>${esc(t("impNoMatch"))}</p>${r.noPlace.length ? `<pre class="mono" style="white-space:pre-wrap">${esc(r.noPlace.join("\n"))}</pre>` : ""}` });
        return;
      }
      if (r.noPlace.length) toast(t("impSomeNoMatch") + ": " + r.noPlace.length, "warn");
      afterJob(() => trackJob(r.job, t("impMany"), async j => {
        if (j.status !== "done" || !j.result) return;
        const res = j.result;
        await refreshState();
        if (S.page === "manual" && S.manual.file === rel) { S.manual.picked.clear(); loadAssets(); }
        const v = await modal({ title: t("impMany"), body: `<p><b>✔ ${esc(t("batchDone"))}: ${res.ok} · ${esc(t("batchSkipped"))}: ${res.skipped} · ${esc(t("batchFailed"))}: ${res.failed}</b></p>
          ${res.noPlace && res.noPlace.length ? `<p class="muted">${esc(t("impSomeNoMatch"))}: ${res.noPlace.length}</p>` : ""}${res.noFile ? `<p class="muted">${esc(t("impNoFile"))}: ${res.noFile}</p>` : ""}`,
          buttons: [{ label: "📄 " + t("batchReport"), value: "rep" }, { label: t("ok"), value: true, cls: "primary" }] });
        if (v === "rep") await showReport(res.report);
        if (res.saved && res.saved.length && res.askApply && await confirmBox(t("askApply") + "\n\n" + res.saved.join("\n"))) {
          await api("/api/apply", { project: S.project, rels: res.saved }); toast(t("appliedN") + ": " + res.saved.length, "ok");
          await askCatalogs();
        }
      }));
    } catch (e) { fail(e); }
  };
  inp.click();
}
function chooseAndImport(tgt) {
  const inp = document.createElement("input"); inp.type = "file"; inp.accept = ".txt,.json,.csv,.dat";
  inp.onchange = () => { if (inp.files[0]) importFile(inp.files[0], tgt); };
  inp.click();
}
async function fileToB64(f) {
  const buf = new Uint8Array(await f.arrayBuffer()); let s = ""; const ch = 0x8000;
  for (let i = 0; i < buf.length; i += ch) s += String.fromCharCode.apply(null, buf.subarray(i, i + ch));
  return btoa(s);
}
async function importFile(f, tgt) {
  try {
    const r = await api("/api/asset/import", { ...(tgt || { project: S.project, rel: "" }), name: f.name, data: await fileToB64(f) });
    toast(t("importedN") + r.applied, "ok");
    await refreshState();
    if (S.editor && S.editor.target.rel === r.rel && String(S.editor.target.pathId) === String(r.pathId)) { if (S.page === "editor") await loadEditor(true); renderEditorHeaderDirty(); }
  } catch (e) { fail(e); }
}

// ============================================================ PAGE: editor
function openAsset(rel, file, pathId, title) {
  S.editor = { target: { project: S.project, rel, file, pathId: String(pathId) }, title: title || rel, key: `${S.project}|${rel}|${file}|${pathId}`, offset: 0, limit: 200, filter: "", data: null };
  go("editor");
}
async function loadEditor(refresh) {
  const E = S.editor; if (!E) return;
  try {
    const so = E.sort ? { sort: E.sort.c, desc: E.sort.d < 0 ? 1 : 0 } : {};
    E.data = await api("/api/asset/table?" + q({ ...E.target, offset: E.offset, limit: E.limit, filter: E.filter, refresh: refresh ? 1 : 0, ...so }));
    E.lastText = E.data.text;
  } catch (e) { fail(e); E.data = { error: e.message }; }
  if (S.page === "editor") drawEditorBody();
}
function pEditor(m) {
  const E = S.editor;
  if (!E) { m.innerHTML = `<h1>${esc(t("editor_title"))}</h1><div class="empty">${esc(t("editor_empty"))}<div style="margin-top:14px"><button class="btn primary" id="edFind">🔍 ${esc(t("step_auto"))}</button></div></div>`; $("#edFind").onclick = () => go("auto"); return; }
  m.innerHTML = `<div id="edHead"></div><div id="edBody"><div class="empty">…</div></div>`;
  renderEditorHeader();
  loadEditor(false);
}
function renderEditorHeader() {
  const E = S.editor, el = $("#edHead"); if (!E || !el) return;
  const dirty = S.state?.open.find(o => o.project === E.target.project && o.rel.toLowerCase() === E.target.rel.toLowerCase())?.dirty;
  el.innerHTML = `<div class="row" style="margin-bottom:6px"><h1 class="grow" style="margin:0">${esc(E.title)}</h1>
    <span class="kbadge" style="${dirty ? "color:var(--warn);border-color:var(--warn)" : ""}">${esc(dirty ? t("unsaved") : t("saved"))}</span></div>
    <div class="muted mono" style="font-size:12px;margin-bottom:10px">${esc(E.target.rel)} ${E.target.file >= 0 ? "· #" + esc(E.target.pathId) : ""} <span id="edInfo"></span></div>
    <div class="row" style="margin-bottom:10px">
      <input type="text" id="edFilter" placeholder="${esc(t("search"))}" value="${esc(E.filter)}" style="width:260px">
      <span class="pager" id="edPager"></span><span class="grow"></span><span id="edLang"></span>
      <select id="edExp"><option value="">${esc(t("export"))}…</option><option value="txt">${esc(t("exportTxt"))}</option><option value="json">${esc(t("exportJson"))}</option><option value="csv">${esc(t("exportCsv"))}</option>${E.target.file >= 0 ? `<option value="dump">${esc(t("exportDump"))}</option><option value="dat">${esc(t("exportDat"))}</option>` : ""}</select>
      <button class="btn small" id="edImp">⇪ ${esc(t("import"))}</button><button class="btn small" id="edReload">⟳ ${esc(t("reload"))}</button>
      <button class="btn small danger" id="edDiscard">${esc(t("discard"))}</button><button class="btn small" id="edClose">${esc(t("close"))}</button></div>`;
  let ft; $("#edFilter").oninput = e => { clearTimeout(ft); ft = setTimeout(() => { E.filter = e.target.value; E.offset = 0; loadEditor(false); }, 300); };
  $("#edExp").onchange = e => { if (e.target.value) { flushEditor().then(() => exportAsset(E.target, e.target.value)); e.target.value = ""; } };
  $("#edImp").onclick = () => chooseAndImport(E.target);
  $("#edReload").onclick = () => loadEditor(true);
  $("#edDiscard").onclick = async () => { if (!await confirmBox(t("discard") + "?")) return; await api("/api/discard", { project: E.target.project, rel: E.target.rel }); S.undo = []; S.redo = []; await refreshState(); renderEditorHeader(); loadEditor(true); };
  $("#edClose").onclick = async () => { await flushEditor(); S.editor = null; render(); };
  drawLangBtn();
}
function drawLangBtn() {
  const E = S.editor, el = $("#edLang"); if (!E || !el) return;
  const d = E.data, langs = d?.languages;
  if (!langs || !langs.length) { el.innerHTML = ""; return; }
  const label = d.replace ? t("replActive") + " " + d.replace.name : t("ukLang");
  el.innerHTML = `<button class="btn small ${d.replace ? "primary" : ""}" id="edAddLang" title="${esc(t("t_addLang"))}">🇺🇦 ${esc(label)}</button>`;
  $("#edAddLang").onclick = languageDialog;
}
async function languageDialog() {
  const E = S.editor, d = E?.data; if (!d?.languages) return;
  await flushEditor();
  const en = d.languages.findIndex(l => /english|\[en|^en\b/i.test(l));
  const def = d.replace ? d.replace.lang : (en >= 0 ? en : 0);
  const opts = sel => d.languages.map((l, i) => `<option value="${i}" ${i === sel ? "selected" : ""}>${esc(l)}</option>`).join("");
  const body = document.createElement("div");
  body.innerHTML = `
    <label class="row" style="margin-bottom:6px;gap:8px"><input type="radio" name="alMode" value="replace" checked> <b>${esc(t("replMode"))}</b></label>
    <div id="alReplace" style="margin:0 0 12px 26px">
      <div class="row" style="margin-bottom:6px"><label style="width:200px">${esc(t("replWhich"))}</label><select id="alLang" class="grow">${opts(def)}</select></div>
      <p class="muted" style="margin:0">${esc(t("replNote"))}</p></div>
    ${d.canAddLang ? `<label class="row" style="margin-bottom:6px;gap:8px"><input type="radio" name="alMode" value="add"> <b>${esc(t("addLang"))}</b></label>
    <div id="alAdd" style="margin:0 0 4px 26px;display:none">
      <p class="muted">${esc(t("addLangWhere"))}: <span class="mono">${esc(d.langWhere || "")}</span></p>
      <div class="row" style="margin-bottom:6px"><label style="width:200px">${esc(t("addLangName"))}</label><input type="text" id="alName" class="grow" value="Ukrainian"></div>
      <div class="row" style="margin-bottom:6px"><label style="width:200px">${esc(t("addLangCode"))}</label><input type="text" id="alCode" class="grow" value="uk"></div>
      <div class="row" style="margin-bottom:6px"><label style="width:200px">${esc(t("addLangCopy"))}</label><select id="alCopy" class="grow">
        <option value="-1">${esc(t("addLangEmpty"))}</option>${opts(en >= 0 ? en : 0)}</select></div>
      <p class="muted" style="margin:0">${esc(t("addLangNote"))}</p></div>` : ""}
    <div class="row" style="margin-top:14px;gap:10px"><button class="btn small" id="alDll">🔎 ${esc(t("dllFind"))}</button><span class="muted" style="font-size:12px">${esc(t("dllFindNote"))}</span></div>`;
  $("#alDll", body).onclick = () => { body.closest(".modal")._close(null); dllLangDialog(); };
  $$('input[name="alMode"]', body).forEach(rb => rb.onchange = () => {
    const add = $('input[name="alMode"]:checked', body).value === "add";
    $("#alReplace", body).style.display = add ? "none" : ""; const a = $("#alAdd", body); if (a) a.style.display = add ? "" : "none";
  });
  const buttons = [{ label: t("cancel"), value: false }];
  if (d.replace) buttons.push({ label: t("replOff"), value: "off" });
  buttons.push({ label: t("ok"), value: true, cls: "primary" });
  const ok = await modal({ title: t("ukLang"), body, buttons });
  if (!ok) return;
  try {
    if (ok === "off") await api("/api/asset/replacelang", { ...E.target, off: true });
    else if ($('input[name="alMode"]:checked', body).value === "replace") {
      await api("/api/asset/replacelang", { ...E.target, lang: +$("#alLang", body).value });
      toast(t("replDone") + " " + d.languages[+$("#alLang", body).value], "ok");
    } else {
      const r = await api("/api/asset/addlang", { ...E.target, name: $("#alName", body).value.trim() || "Ukrainian", code: $("#alCode", body).value.trim() || "uk", copyFrom: +$("#alCopy", body).value });
      toast(t("addLangDone") + r.applied, "ok");
      S.undo = []; S.redo = [];
    }
    E.offset = 0; await refreshState(); renderEditorHeader(); loadEditor(true);
  } catch (e) { fail(e); }
}
// ---- languages in the game code (Assembly-CSharp.dll, Mono builds)
async function dllLangDialog() {
  if (!needProject()) return;
  try {
    const r = await api("/api/dll/langscan", { project: S.project });
    trackJob(r.job, t("dllScan"), async j => { if (j.status === "done" && j.result) showDllLangs(j.result); });
  } catch (e) { fail(e); }
}
function showDllLangs(res) {
  const body = document.createElement("div");
  if (!res.managed) body.innerHTML = `<p>${esc(res.il2cpp ? t("dllIl2cpp") : t("dllNoManaged"))}</p>`;
  else if (!res.hits.length) body.innerHTML = `<p>${esc(t("dllNone"))}</p>`;
  else body.innerHTML = `<p class="muted">${esc(t("dllIntro"))}</p>` + res.hits.map((h, i) => `<div class="card" style="padding:12px 14px;margin-bottom:10px">
      <div class="row" style="gap:8px;margin-bottom:6px"><span class="kbadge">${esc(t("dllKind_" + h.kind))}</span><b class="mono grow" style="font-size:12px">${esc(h.where)}</b><span class="muted mono" style="font-size:11px">${esc(h.dll.split(/[\\/]/).pop())}</span></div>
      <div class="muted" style="font-size:12px;margin-bottom:8px">${h.items.map(esc).join(" · ")}</div>
      ${h.hasUk ? `<span class="kbadge st-ok">${esc(t("dllHasUk"))}</span>`
        : h.canAdd ? `<div class="row" style="gap:8px"><input type="text" class="grow" data-v="${i}" value="${esc(h.suggest)}"><button class="btn small primary" data-add="${i}">${esc(t("dllAdd"))}</button></div>`
        : `<span class="muted" style="font-size:12px">${esc(t("dllManual"))}</span>`}</div>`).join("");
  $$("[data-add]", body).forEach(b => b.onclick = async () => {
    const h = res.hits[+b.dataset.add], value = $(`[data-v="${b.dataset.add}"]`, body).value.trim();
    try {
      const r = await api("/api/dll/langadd", { project: S.project, id: h.id, value });
      toast(t("dllAdded") + ": " + value, "ok");
      body.closest(".modal")._close(null);
      if (r.askApply && await confirmBox(t("askApply") + "\n\n" + r.saved.join("\n"))) { await api("/api/apply", { project: S.project, rels: r.saved }); toast(t("appliedN"), "ok"); }
      await refreshState(); dllLangDialog();
    } catch (e) { fail(e); }
  });
  modal({ title: t("dllTitle"), body, wide: true, buttons: [{ label: t("close"), value: false }] });
}
function updateRow(id, col, value) { const r = S.editor?.data?.rows?.find(x => x.id === id); if (r) r.cells[col] = value; }
function drawEditorBody() {
  const E = S.editor, el = $("#edBody"); if (!E || !el) return;
  const d = E.data;
  if (!d || d.error) { el.innerHTML = `<div class="card" style="color:var(--err)">${esc(d?.error || "")}</div>`; return; }
  $("#edInfo").textContent = "· " + d.info;
  drawLangBtn();
  const pager = $("#edPager");
  if (d.kind === "text" || d.kind === "loose") {
    pager.innerHTML = "";
    if (d.binary) { el.innerHTML = `<div class="card">${esc(t("binary"))}</div>`; return; }
    el.innerHTML = `<textarea id="edText" class="editor-text" spellcheck="false"></textarea>`;
    const ta = $("#edText"); ta.value = d.text || "";
    let tm; ta.oninput = () => { clearTimeout(tm); tm = setTimeout(commitText, 700); };
    ta.onblur = commitText;
    return;
  }
  const pages = Math.max(1, Math.ceil(d.total / E.limit)), cur = Math.floor(E.offset / E.limit) + 1;
  pager.innerHTML = `<button class="btn small" id="pgPrev" ${cur <= 1 ? "disabled" : ""}>‹</button><span class="muted">${cur}/${pages} ${esc(t("page"))} · ${d.total} ${esc(t("rows"))}</span><button class="btn small" id="pgNext" ${cur >= pages ? "disabled" : ""}>›</button>`;
  $("#pgPrev").onclick = () => { E.offset = Math.max(0, E.offset - E.limit); loadEditor(false); };
  $("#pgNext").onclick = () => { E.offset += E.limit; loadEditor(false); };
  const rp = d.replace;
  // "replace a language" mode: key | original text of that language (read-only) | Ukrainian (written into that column)
  const ed = rp ? new Set([rp.col]) : new Set(d.editable);
  const cols = rp ? [0, "orig", rp.col] : d.columns.map((_, i) => i);
  const head = c => c === "orig" ? `${t("replOrig")} (${rp.name})` : rp && c === rp.col ? `${t("replUk")} (${t("replInstead")} ${rp.name})` : d.columns[c];
  const cell = (r, c) => {
    if (c === "orig") return `<td class="orig" style="white-space:pre-wrap;color:var(--muted)">${esc(r.orig || "")}</td>`;
    const v = r.cells[c] ?? "";
    return ed.has(c) ? `<td><textarea class="cell-edit" rows="${Math.min(8, Math.max(1, Math.ceil(v.length / 70) + v.split("\n").length - 1))}" data-id="${esc(r.id)}" data-col="${c}">${esc(v)}</textarea></td>`
      : `<td class="key">${esc(v)}</td>`;
  };
  // click a header: A→Z, Z→A, back to the game's order (sorted on the server, all pages)
  const sc = c => c === "orig" ? rp.lang : c;
  const thE = c => { const on = E.sort && E.sort.c === sc(c); return `<th class="sortable${on ? " on" : ""}" data-ec="${sc(c)}" title="${esc(t("sortHint"))}" style="${ed.has(c) || c === "orig" ? "" : "width:22%"}">${esc(head(c))}${on ? (E.sort.d > 0 ? " ▲" : " ▼") : ""}</th>`; };
  el.innerHTML = `<div class="tbl-wrap" style="max-height:calc(100vh - 260px)"><table class="tbl"><thead><tr>${cols.map(thE).join("")}</tr></thead><tbody>${
    d.rows.map(r => `<tr>${cols.map(c => cell(r, c)).join("")}</tr>`).join("")}</tbody></table></div>`;
  $$("th[data-ec]", el).forEach(h => h.onclick = async () => {
    const c = +h.dataset.ec, s0 = E.sort;
    E.sort = !s0 || s0.c !== c ? { c, d: 1 } : s0.d > 0 ? { c, d: -1 } : null;
    await flushEditor(); E.offset = 0; loadEditor(false);
  });
  $$(".cell-edit", el).forEach(ta => {
    ta.dataset.orig = ta.value;
    ta.onchange = () => commitCell(ta);
    ta.onkeydown = e => { if (e.key === "Enter" && e.ctrlKey) { e.preventDefault(); ta.blur(); } };
  });
}
async function commitCell(ta) {
  const E = S.editor; if (!E) return;
  const id = ta.dataset.id, col = +ta.dataset.col, old = ta.dataset.orig, val = ta.value;
  if (old === val) return;
  try {
    await api("/api/asset/edit", { ...E.target, changes: [{ id, col, value: val }] });
    ta.dataset.orig = val; ta.classList.add("changed"); updateRow(id, col, val);
    S.undo.push({ key: E.key, target: { ...E.target }, id, col, old, val }); S.redo = [];
    await refreshState(); renderEditorHeaderDirty();
  } catch (e) { fail(e); }
}
async function commitText() {
  const E = S.editor, ta = $("#edText"); if (!E || !ta) return;
  if (ta.value === E.lastText) return;
  const old = E.lastText, val = ta.value;
  try {
    await api("/api/asset/edit", { ...E.target, text: val });
    E.lastText = val; S.undo.push({ key: E.key, target: { ...E.target }, text: true, old, val }); S.redo = [];
    await refreshState(); renderEditorHeaderDirty();
  } catch (e) { fail(e); }
}
function renderEditorHeaderDirty() { const f = $("#edFilter"); const v = f && f.value; const focus = document.activeElement; renderEditorHeader(); if (f) $("#edFilter").value = v; if (focus && focus.id === "edFilter") $("#edFilter").focus(); drawPagerOnly(); }
function drawPagerOnly() { const d = S.editor?.data; if (d && d.kind !== "text" && d.kind !== "loose") { const E = S.editor; const pages = Math.max(1, Math.ceil(d.total / E.limit)), cur = Math.floor(E.offset / E.limit) + 1; const p = $("#edPager"); if (p) { p.innerHTML = `<button class="btn small" id="pgPrev" ${cur <= 1 ? "disabled" : ""}>‹</button><span class="muted">${cur}/${pages} ${esc(t("page"))} · ${d.total} ${esc(t("rows"))}</span><button class="btn small" id="pgNext" ${cur >= pages ? "disabled" : ""}>›</button>`; $("#pgPrev").onclick = () => { E.offset = Math.max(0, E.offset - E.limit); loadEditor(false); }; $("#pgNext").onclick = () => { E.offset += E.limit; loadEditor(false); }; } } else if (d) { const i = $("#edInfo"); if (i) i.textContent = "· " + d.info; } }
async function flushEditor() {
  const a = document.activeElement;
  if (a && a.classList && a.classList.contains("cell-edit")) await commitCell(a);
  if ($("#edText")) await commitText();
}

// ============================================================ sorting helper (click a column header)
S.sorts = S.sorts || {};
function sortRows(id, rows, getters, def) {
  const s = S.sorts[id] || def; if (!s || !getters[s.k]) return rows;
  const g = getters[s.k];
  return [...rows].sort((a, b) => {
    const x = g(a), y = g(b);
    const r = typeof x === "number" && typeof y === "number" ? x - y : String(x ?? "").localeCompare(String(y ?? ""), undefined, { numeric: true, sensitivity: "base" });
    return s.d * r;
  });
}
function th(id, k, label, def) {
  const s = S.sorts[id] || def; const on = s && s.k === k;
  return `<th class="sortable${on ? " on" : ""}" data-sort="${k}" data-sid="${id}" title="${esc(t("sortHint"))}">${esc(label)}${on ? (s.d > 0 ? " ▲" : " ▼") : ""}</th>`;
}
function bindSort(root, redraw, def) {
  $$("th.sortable", root).forEach(h => h.onclick = () => {
    const id = h.dataset.sid, k = h.dataset.sort, s = S.sorts[id] || def;
    S.sorts[id] = s && s.k === k ? { k, d: -s.d } : { k, d: ["score", "size", "count", "hits", "chars"].includes(k) ? -1 : 1 };
    redraw();
  });
}

// ============================================================ PAGE: import into game (drop files, the launcher finds their place)
S.batch = S.batch || { items: [], need: false, result: null, add: true, analyzed: false };
async function batchAdd(files) {
  if (!needProject()) return;
  const list = [];
  for (const f of files) list.push({ name: f.name, data: await fileToB64(f) });
  if (!list.length) return;
  try {
    const r = await api("/api/batch/add", { project: S.project, files: list });
    S.batch.items = r.items; S.batch.need = r.needDecision; S.batch.result = null;
    toast(t("batchAdded") + ": " + list.length, "ok");
    if (S.page !== "batch") go("batch"); else drawBatch();
    batchAnalyze();
  } catch (e) { fail(e); }
}
async function batchAnalyze() {
  try {
    const r = await api("/api/batch/analyze", { project: S.project });
    trackJob(r.job, t("batchAnalyze"), j => {
      if (j.status !== "done" || !j.result) return;
      S.batch.items = j.result.items; S.batch.need = j.result.needDecision; S.batch.analyzed = true;
      if (S.page === "batch") drawBatch();
      const work = S.batch.items.filter(i => i.include && i.target && (i.content === "translation"));
      const warn = S.batch.items.filter(i => i.include && ["similar", "renamed"].includes(i.status));
      if (warn.length) toast(t("batchWarnSimilar") + ": " + warn.map(i => i.name).join(", "), "warn");
      // nothing to decide: same structure everywhere → replace right away (as asked), the report lists everything
      if (!S.batch.need && work.length) batchApply("update");
    });
  } catch (e) { fail(e); }
}
async function batchApply(mode, ids) {
  try {
    const r = await api("/api/batch/apply", { project: S.project, mode, add: S.batch.add, lang: S.lang, ids });
    trackJob(r.job, mode === "check" ? t("batchCheck") : t("batchUpdate"), async j => {
      if (j.status !== "done" || !j.result) return;
      S.batch.result = j.result;
      try { const st = await api("/api/batch?" + q({ project: S.project })); S.batch.items = st.items; S.batch.need = st.needDecision; } catch { }
      if (S.page === "batch") drawBatch();
      if (j.result.saved && j.result.saved.length) {
        await refreshState();
        if (j.result.askApply && await confirmBox(t("askApply") + "\n\n" + j.result.saved.join("\n"))) {
          await api("/api/apply", { project: S.project, rels: j.result.saved }); toast(t("appliedN") + ": " + j.result.saved.length, "ok");
          await askCatalogs();
        }
      }
    });
  } catch (e) { fail(e); }
}
async function showReport(name) {
  try {
    const r = await api("/api/report?" + q({ project: S.project, name }), undefined, true);
    const text = await r.text();
    await modal({ title: name, wide: true, body: `<pre class="report-pre">${esc(text)}</pre>`, buttons: [{ label: t("openFolder"), value: "f" }, { label: t("ok"), value: true, cls: "primary" }] })
      .then(v => { if (v === "f") api("/api/open-folder", { project: S.project, which: "reports" }); });
  } catch (e) { fail(e); }
}
async function pickTarget(it) {
  const body = document.createElement("div");
  body.innerHTML = `<input type="text" id="tgQ" class="mono" style="width:100%" placeholder="${esc(t("batchFindPh"))}" value="${esc(it.nameHint || "")}">
    <div class="muted" id="tgInfo" style="font-size:12px;margin-top:6px"></div>
    <div class="tbl-wrap" style="margin-top:8px;max-height:55vh"><table class="tbl clickable"><thead id="tgHead"></thead><tbody id="tgList"></tbody></table></div>`;
  let chosen = null, found = [], poll = null, seq = 0;
  const tgDef = { k: "", d: 1 };
  const okBtn = () => body.closest(".modal")?.querySelector("footer .primary");
  // the game index of a big game is built for minutes: show what is indexed so far and refresh while it grows
  const load = async () => {
    const my = ++seq; clearTimeout(poll);
    $("#tgInfo", body).innerHTML = `<span class="spin-inline"></span> ${esc(t("busy"))}`;
    let r; try { r = await api("/api/batch/find?" + q({ project: S.project, q: $("#tgQ", body).value })); } catch (e) { $("#tgInfo", body).textContent = "⚠ " + e.message; return; }
    if (my !== seq || !body.isConnected) return;
    found = Array.isArray(r) ? r : r.items || [];
    $("#tgInfo", body).innerHTML = r.building ? `<span class="spin-inline"></span> ${esc(t("indexBuilding").replace("{n}", r.indexed))}` : "";
    draw();
    if (r.building) poll = setTimeout(() => { if (body.isConnected) load(); }, 3000);
  };
  const draw = () => {
    $("#tgHead", body).innerHTML = `<tr>${th("target", "name", t("asset"), tgDef)}${th("target", "file", t("file"), tgDef)}</tr>`;
    bindSort($("#tgHead", body), draw, tgDef);
    const list = sortRows("target", found, { name: c => c.name || "", file: c => c.rel }, tgDef);
    $("#tgList", body).innerHTML = list.map((c, i) => `<tr data-i="${i}" class="${chosen && chosen.key === c.key ? "sel" : ""}"><td><b>${esc(c.name || "—")}</b> <span class="muted">${esc(c.type)}${c.loose ? "" : " #" + esc(c.pathId)}</span></td><td class="mono wrap" style="font-size:12px">${esc(c.rel)}</td></tr>`).join("") || `<tr><td class="empty">${esc(t("nothingFound"))}</td></tr>`;
    $$("#tgList tr[data-i]", body).forEach(tr => {
      tr.onclick = () => { chosen = list[+tr.dataset.i]; $$("#tgList tr", body).forEach(x => x.classList.remove("sel")); tr.classList.add("sel"); const b = okBtn(); if (b) b.disabled = false; };
      tr.ondblclick = () => { tr.onclick(); okBtn()?.click(); };
    });
  };
  let tmr; $("#tgQ", body).oninput = () => { clearTimeout(tmr); tmr = setTimeout(load, 300); };
  const p = modal({ title: t("batchPickTarget") + ": " + it.name, body, wide: true, buttons: [{ label: t("cancel"), value: null }, { label: t("batchUseTarget"), value: "ok", cls: "primary" }] });
  okBtn().disabled = true;   // the place is a row of the list: nothing chosen = nothing to do
  load();
  const v = await p; clearTimeout(poll); seq++;
  if (v !== "ok" || !chosen) return;
  if (!await setTarget(it, { key: chosen.key })) return;
  const cur = S.batch.items.find(x => x.id === it.id);
  toast(t("batchTargetSet") + ": " + (chosen.name || chosen.rel), "ok");
  // before, the choice only changed the row and waited for «Виконати»; now the file can be replaced right away
  if (cur && cur.target && await confirmBox(t("batchApplyOneQ").replace("{f}", it.name).replace("{t}", (chosen.name || "") + " · " + chosen.rel), t("batchApplyNow"), t("batchLater"))) {
    if (S.job) toast(t("batchQueued"), "");
    afterJob(() => { if (S.batch.items.some(x => x.id === it.id)) return batchApply("update", [it.id]); });
  }
}
async function setTarget(it, body) {
  try { const r = await api("/api/batch/target", { project: S.project, id: it.id, ...body }); S.batch.items = r.items; S.batch.need = r.needDecision; drawBatch(); return true; } catch (e) { fail(e); return false; }
}
async function pickNewPlace(it) {
  const dir = await pickPath({ start: S.state.projects.find(p => p.id === S.project)?.gameRoot || "" });
  if (!dir) return;
  await setTarget(it, { newRel: dir.replace(/[\\/]+$/, "") + "/" + it.name });
}

const BATCH_STATUS = { exact: "ok", moved: "ok", renamed: "warn", similar: "warn", notfound: "err", newfile: "ok", new: "" };
const BATCH_CONTENT = { identical: "ok", translation: "ok", different: "warn", unknown: "err", error: "err", newfile: "ok" };
function pBatch(m) {
  if (!needProject()) return;
  m.innerHTML = `<h1>${esc(t("nav_batch"))}</h1><div class="sub">${esc(t("batch_sub"))}</div>
  <div class="card dropzone" id="bDrop"><div style="font-size:30px">⇪</div><div><b>${esc(t("batchDrop"))}</b></div><div class="muted" style="font-size:12px;margin:4px 0 10px">${esc(t("batchDropHint"))}</div>
    <button class="btn primary" id="bPick">${esc(t("batchPick"))}</button><input type="file" id="bFiles" multiple class="hidden"></div>
  <div id="bBody"></div>`;
  $("#bPick").onclick = () => $("#bFiles").click();
  $("#bFiles").onchange = e => { batchAdd([...e.target.files]); e.target.value = ""; };
  drawBatch();
  if (!S.batch.items.length) api("/api/batch?" + q({ project: S.project })).then(r => { S.batch.items = r.items; S.batch.need = r.needDecision; drawBatch(); }).catch(() => { });
}
function drawBatch() {
  const box = $("#bBody"); if (!box) return;
  const B = S.batch, items = B.items || [];
  const def = { k: "name", d: 1 };
  const rows = sortRows("batch", items, {
    name: i => i.name, target: i => i.target ? i.target.name : "", status: i => ["notfound", "similar", "renamed", "moved", "exact", "newfile"].indexOf(i.status),
    content: i => ["error", "unknown", "different", "translation", "identical"].indexOf(i.content), size: i => i.size,
  }, def);
  let html = "";
  if (B.result) html += `<div class="card" style="border-color:var(--ok)"><div class="row"><b class="grow">✔ ${esc(t("batchDone"))}: ${B.result.ok} · ${esc(t("batchSkipped"))}: ${B.result.skipped} · ${esc(t("batchFailed"))}: ${B.result.failed}</b>
    <button class="btn small primary" id="bRep">📄 ${esc(t("batchReport"))}</button></div></div>`;
  if (items.length && (B.analyzed || items.some(i => i.status !== "new"))) {
    // decision first: nothing preselected, option 1 is only marked as recommended
    const opt = (v, n, rec) => `<label class="opt ${B.mode === v ? "on" : ""}"><input type="radio" name="bMode" value="${v}" ${B.mode === v ? "checked" : ""}>
      <span class="opt-body"><b>${n}. ${esc(t("batchOpt" + n))}${rec ? ` <span class="rec-badge">${esc(t("recommended"))}</span>` : ""}</b><span class="muted">${esc(t("batchOpt" + n + "d"))}</span></span></label>`;
    html += `<div class="card decision ${B.need ? "need" : ""}" style="margin-bottom:14px">
      <h2 style="margin-top:0">${esc(B.need ? t("batchNeedTitle") : t("batchReadyTitle"))}</h2>
      <p class="muted" style="margin-top:0">${esc(B.need ? t("batchNeedText") : t("batchReadyText"))}</p>
      <div class="opts">${opt("update", 1, true)}${opt("check", 2)}${opt("none", 3)}</div>
      <div class="row" style="margin-top:12px"><label class="chk grow"><input type="checkbox" id="bAdd" ${B.add ? "checked" : ""}>${esc(t("batchAddMissing"))}</label>
        <button class="btn primary" id="bGo" ${B.mode ? "" : "disabled"}>${esc(t("batchGo"))}</button></div></div>`;
  }
  if (items.length) {
    html += `<div class="row" style="margin-bottom:8px"><h2 class="grow" style="margin:0">${esc(t("batchFiles"))} (${items.length})</h2>
      <button class="btn small" id="bAnalyze">🔍 ${esc(t("batchAnalyze"))}</button><button class="btn small danger" id="bClear">${esc(t("batchClear"))}</button></div>
    <div class="tbl-wrap"><table class="tbl"><thead><tr><th></th>${th("batch", "name", t("file"), def)}${th("batch", "target", t("batchTarget"), def)}${th("batch", "status", t("batchMatch"), def)}${th("batch", "content", t("batchContent"), def)}<th></th></tr></thead><tbody>${
      rows.map(it => {
        const st = it.stats || {};
        const statsTxt = it.stats ? [st.changed ? `~${st.changed}` : "", st.added ? `−${st.added}` : "", st.newInGame ? `+${st.newInGame}` : "", st.order ? "⇅" : ""].filter(Boolean).join(" ") : "";
        const cands = it.cands || [];
        const tgt = it.status === "newfile" ? `<span class="muted">${esc(t("batchNewAt"))}</span> <span class="mono" style="font-size:12px">${esc(it.newRel)}</span>`
          : cands.length ? `<select data-id="${it.id}" class="bTgt" style="max-width:100%">${cands.map(c => `<option value="${esc(c.key)}" ${it.target && it.target.key === c.key ? "selected" : ""}>${esc((c.name || "—") + " · " + c.type + (c.loose ? "" : " #" + c.pathId) + " · " + Math.round(Math.min(1, c.score) * 100) + "%")}</option>`).join("")}</select>
             <div class="mono muted" style="font-size:11px;margin-top:2px">${esc(it.target ? it.target.rel : "")}</div>`
          : `<span style="color:var(--err)">${esc(t("batchNotFound"))}</span>`;
        return `<tr><td><input type="checkbox" class="bInc" data-id="${it.id}" ${it.include ? "checked" : ""} title="${esc(t("batchInclude"))}"></td>
          <td class="wrap"><b>${esc(it.name)}</b><div class="muted" style="font-size:12px">${esc(it.format || "")} · ${fmtSize(it.size)}</div></td>
          <td class="wrap" style="min-width:260px">${tgt}<div style="margin-top:4px"><button class="btn small bPickT" data-id="${it.id}">${esc(t("batchOther"))}</button>${!it.target ? ` <button class="btn small bNew" data-id="${it.id}">${esc(t("batchAddNew"))}</button>` : ""}</div></td>
          <td><span class="kbadge st-${BATCH_STATUS[it.status] || ""}">${esc(t("bst_" + it.status))}</span>${it.target && it.target.why ? `<div class="muted" style="font-size:11px">${esc(it.target.why)}</div>` : ""}</td>
          <td class="wrap" style="min-width:190px"><span class="kbadge st-${BATCH_CONTENT[it.content] || ""}">${esc(it.content ? t("bct_" + it.content) : "—")}</span> <span class="mono" style="font-size:12px">${esc(statsTxt)}</span>
            ${it.error ? `<div style="color:var(--err);font-size:12px">${esc(it.error)}</div>` : ""}${it.note ? `<div style="color:var(--warn);font-size:12px">${esc(it.note)}</div>` : ""}
            ${it.sample && it.sample.length ? `<details style="font-size:12px"><summary class="muted">${esc(t("batchDetails"))}</summary><pre class="mono" style="white-space:pre-wrap;margin:4px 0">${esc(it.sample.join("\n"))}</pre></details>` : ""}</td>
          <td><button class="btn small bDel" data-id="${it.id}" title="${esc(t("remove"))}">✕</button></td></tr>`;
      }).join("")}</tbody></table></div>
    <div class="muted" style="font-size:12px;margin-top:6px">${esc(t("batchLegend"))}</div>`;
  } else if (!B.result) html += `<div class="empty">${esc(t("batchEmpty"))}</div>`;
  box.innerHTML = html;
  bindSort(box, drawBatch, def);
  const find = id => items.find(i => i.id === id);
  $$(".bTgt", box).forEach(s => s.onchange = () => setTarget(find(s.dataset.id), { key: s.value }));
  $$(".bPickT", box).forEach(b => b.onclick = () => pickTarget(find(b.dataset.id)));
  $$(".bNew", box).forEach(b => b.onclick = () => pickNewPlace(find(b.dataset.id)));
  $$(".bInc", box).forEach(c => c.onchange = async () => { try { const r = await api("/api/batch/include", { project: S.project, id: c.dataset.id, include: c.checked }); B.items = r.items; B.need = r.needDecision; drawBatch(); } catch (e) { fail(e); } });
  $$(".bDel", box).forEach(b => b.onclick = async () => { const r = await api("/api/batch/remove", { project: S.project, id: b.dataset.id }); B.items = r.items; B.need = r.needDecision; drawBatch(); });
  if ($("#bRep", box)) $("#bRep", box).onclick = () => showReport(B.result.report);
  if ($("#bAnalyze", box)) $("#bAnalyze", box).onclick = batchAnalyze;
  if ($("#bClear", box)) $("#bClear", box).onclick = async () => { const r = await api("/api/batch/remove", { project: S.project }); B.items = r.items; B.need = false; B.result = null; B.analyzed = false; drawBatch(); };
  if ($("#bAdd", box)) $("#bAdd", box).onchange = e => B.add = e.target.checked;
  $$("input[name=bMode]", box).forEach(r => r.onchange = () => { B.mode = r.value; $$(".opt", box).forEach(l => l.classList.toggle("on", l.contains(r))); $("#bGo", box).disabled = false; });
  if ($("#bGo", box)) $("#bGo", box).onclick = () => B.mode === "none" ? toast(t("batchNothing"), "") : batchApply(B.mode);
}

// ============================================================ PAGE: fonts
const FONT_DEFAULT_WORDS = ["SDF", "Atlas", "Font"];
const RUNNABLE = k => k === "TMP" || k === "TMP legacy" || k === "TMP?" || k === "Unity Font";
async function pFonts(m) {
  if (!needProject()) return;
  const F = S.fonts, st = S.state.settings;
  F.prev = F.prev || { i: 0, bg: "dark", fit: true };
  const mode = st.FontSearchMode === "words" ? "words" : "all";
  m.innerHTML = `<h1>${esc(t("fonts_title"))}</h1><div class="sub">${esc(t("fonts_sub"))}</div>
  <div class="card">
    <div class="row"><h2 class="grow" style="margin:0">1. ${esc(t("stepSearch"))}</h2><button class="btn primary" id="fScan">🔍 ${esc(t("findFonts"))}</button></div>
    <div class="seg" style="margin-top:10px">
      <label class="${mode === "all" ? "on" : ""}"><input type="radio" name="fmode" value="all" ${mode === "all" ? "checked" : ""}><b>${esc(t("modeAll"))}</b><span class="muted">${esc(t("modeAllHint"))}</span></label>
      <label class="${mode === "words" ? "on" : ""}"><input type="radio" name="fmode" value="words" ${mode === "words" ? "checked" : ""}><b>${esc(t("modeWords"))}</b><span class="muted">${esc(t("modeWordsHint"))}</span></label>
    </div>
    <div id="fWordsBox" class="${mode === "words" ? "" : "hidden"}" style="margin-top:10px">
      <div class="row"><label class="chk"><input type="checkbox" id="fkDef" ${st.FontUseDefault !== false ? "checked" : ""}>${esc(t("fontStdWords"))}</label>
        <span class="chips">${FONT_DEFAULT_WORDS.map(w => `<span class="chip" style="${st.FontUseDefault === false ? "opacity:.4" : ""}">${esc(w)}</span>`).join("")}</span></div>
      <div class="row" style="margin-top:8px"><b>${esc(t("fontOwnWords"))}</b><input type="text" id="fkNew" placeholder="${esc(t("addKeyword"))}" style="width:240px"><button class="btn small" id="fkAdd">+</button><span class="chips" id="fkList"></span></div>
    </div>
    <div class="row" style="margin-top:10px"><label class="chk"><input type="checkbox" id="fkTex" ${st.FontOrphanTextures ? "checked" : ""}>${esc(t("fontOrphans"))}</label></div>
    <div class="row" style="margin-top:10px"><input type="text" id="fFilter" class="grow" placeholder="${esc(t("filter"))}" value="${esc(F.filter || "")}"><span class="muted" id="fCount"></span></div>
    <div class="tbl-wrap" style="margin-top:8px;max-height:320px"><table class="tbl clickable"><thead id="fHead"></thead><tbody id="fList"></tbody></table></div>
    <div class="muted" style="font-size:12px;margin-top:6px">${esc(t("fontClickHint"))}</div>
  </div>
  <div class="card" id="fPrevCard">
    <div class="row"><h2 class="grow" style="margin:0">2. <span id="fPrevTitle">${esc(t("stepAtlas"))}</span></h2>
      <span class="pager" id="fPrevPager"></span>
      <select id="fPrevSrc"><option value="">${esc(t("srcCurrent"))}</option><option value="orig">${esc(t("srcOriginal"))}</option></select>
      <select id="fPrevBg"><option value="dark">${esc(t("bgDark"))}</option><option value="light">${esc(t("bgLight"))}</option><option value="checker">${esc(t("bgChecker"))}</option></select>
      <button class="btn small" id="fPrevFit"></button>
      <button class="btn small" id="fPrevSave">💾 PNG</button><button class="btn small" id="fTtfSave" style="display:none">💾 TTF</button></div>
    <div class="muted mono" style="font-size:12px;margin:6px 0" id="fPrevInfo"></div>
    <div class="atlas-view" id="fPrevBox"><div class="empty">${esc(t("atlasPick"))}</div></div>
  </div>
  <div class="card"><h2 style="margin-top:0">3. ${esc(t("stepNew"))}</h2>
    <div class="field"><label>${esc(t("ttf"))}</label><div class="row"><input type="text" id="fTtf" class="grow mono" value="${esc(F.ttf)}"><button class="btn" id="fBrowse">${esc(t("browse"))}</button></div>
    <div class="muted" style="font-size:12px;margin-top:4px">${esc(t("uploadTtf"))}</div><div class="chips" id="fUploaded" style="margin-top:6px"></div></div>
    <h2>${esc(t("options"))}</h2><div class="grid2">
    <div class="field"><label>${esc(t("sampSize"))}</label><input type="number" id="oSize" placeholder="${esc(t("auto"))}"></div>
    <div class="field"><label>${esc(t("atlas"))}</label><select id="oAtlas"><option value="">${esc(t("auto"))}</option><option>1024x1024</option><option>2048x2048</option><option>4096x4096</option><option>2048x1024</option></select></div>
    <div class="field"><label>${esc(t("padding"))}</label><input type="number" id="oPad" placeholder="${esc(t("auto"))}"></div>
    <div class="field"><label>${esc(t("extraChars"))}</label><input type="text" id="oChars"></div>
    <div class="field"><label>${esc(t("charsFile"))}</label><div class="row"><input type="text" id="oCharsFile" class="grow mono"><button class="btn small" id="oCfBrowse">…</button></div></div></div>
    <div class="row" style="margin-top:10px"><label class="chk"><input type="checkbox" id="oOnly">${esc(t("onlyOriginal"))}</label><label class="chk"><input type="checkbox" id="oBitmap">${esc(t("bitmap"))}</label><label class="chk"><input type="checkbox" id="oNoKern">${esc(t("noKerning"))}</label>
    <span class="grow"></span><span class="muted" id="fRunFor"></span><button class="btn primary" id="fRun">▶ ${esc(t("createFont"))}</button></div></div>
  <div class="card"><h2 style="margin-top:0">${esc(t("nav_log"))}</h2><div class="log joblog" id="fLog"></div></div>
  <div class="card ${F.report ? "" : "hidden"}" id="fRepCard"><h2 style="margin-top:0">${esc(t("report"))}</h2><div id="fRep"></div></div>`;
  $$('input[name="fmode"]').forEach(r => r.onchange = () => saveSettings({ FontSearchMode: r.value }));
  $("#fTtf").onchange = e => F.ttf = e.target.value;
  $("#fBrowse").onclick = async () => { const p = await pickPath({ files: true, ext: ".ttf,.otf" }); if (p) { F.ttf = p; $("#fTtf").value = p; } };
  $("#oCfBrowse").onclick = async () => { const p = await pickPath({ files: true, ext: ".txt,.json,.csv" }); if (p) $("#oCharsFile").value = p; };
  try { const up = await api("/api/fonts/ttfs?" + q({ project: S.project })); $("#fUploaded").innerHTML = up.map(p => `<span class="chip" style="cursor:pointer" data-p="${esc(p)}">${esc(p.split(/[\\/]/).pop())}</span>`).join(""); $$("#fUploaded .chip").forEach(c => c.onclick = () => { F.ttf = c.dataset.p; $("#fTtf").value = F.ttf; }); } catch { }

  // ---- keywords
  const words = () => S.state.settings.FontKeywords || [];
  const drawWords = () => {
    $("#fkList").innerHTML = words().map((k, i) => `<span class="chip custom">${esc(k)}<button data-i="${i}">✕</button></span>`).join("") || `<span class="muted">—</span>`;
    $$("#fkList button").forEach(b => b.onclick = () => { const a = [...words()]; a.splice(+b.dataset.i, 1); saveSettings({ FontKeywords: a }); });
  };
  drawWords();
  const addWord = () => { const v = $("#fkNew").value.trim(); if (!v || words().some(w => w.toLowerCase() === v.toLowerCase())) return; saveSettings({ FontKeywords: [...words(), v] }); };
  $("#fkAdd").onclick = addWord; $("#fkNew").onkeydown = e => { if (e.key === "Enter") addWord(); };
  $("#fkDef").onchange = e => saveSettings({ FontUseDefault: e.target.checked });
  $("#fkTex").onchange = e => saveSettings({ FontOrphanTextures: e.target.checked });

  // ---- results
  const visible = () => {
    const f = (F.filter || "").toLowerCase();
    return (F.list || []).map((x, i) => [x, i]).filter(([x]) => !f || (x.name + " " + x.kind + " " + x.rel + " " + (x.pairTex || "") + " " + (x.matched || "")).toLowerCase().includes(f));
  };
  const atlasCell = x => (x.atlases || []).map(a => a.external ? "↗ " + a.name : `${a.w}×${a.h} ${a.format}${a.w * a.h <= 1 ? " · " + t("dynAtlas") : ""}`).join("<br>");
  const pairCell = x => {
    if (x.link === "ttf") return `<span class="muted">${esc(x.kind === "TTF" ? t("pairTtfFile") : t("pairTtf"))}</span>`;
    if (x.link === "none") return `<span style="color:var(--err)">✕ ${esc(t("pairNone"))}</span>`;
    const lab = { "ref+name": t("pairRefName"), "ref+word": t("pairRefWord"), ref: t("pairRef"), name: t("pairName"), word: t("pairWord") }[x.link] || "";
    return `<span style="color:var(--ok)">✔</span> MonoBehaviour ⇄ Texture2D<div class="muted">${esc(x.pairTex ? "«" + x.pairTex + "»" : "")} · ${esc(lab)}</div>`;
  };
  const drawList = () => {
    const b = $("#fList"); if (!b) return;
    const fDef = { k: "", d: 1 };
    $("#fHead").innerHTML = `<tr><th></th>${th("fonts", "name", t("name"), fDef)}${th("fonts", "kind", t("kind"), fDef)}${th("fonts", "chars", t("chars"), fDef)}${th("fonts", "link", t("fontPair"), fDef)}${th("fonts", "atlas", t("atlas"), fDef)}${th("fonts", "file", t("file"), fDef)}</tr>`;
    bindSort($("#fHead"), drawList, fDef);
    if (!F.list) { b.innerHTML = `<tr><td colspan="7" class="empty">${esc(t("fontScanFirst"))}</td></tr>`; $("#fCount").textContent = ""; return; }
    const v = sortRows("fonts", visible(), { name: ([x]) => x.name || "", kind: ([x]) => x.kind, chars: ([x]) => x.chars || 0, link: ([x]) => x.link || "",
      atlas: ([x]) => Math.max(0, ...(x.atlases || []).map(a => (a.w || 0) * (a.h || 0))), file: ([x]) => x.rel }, fDef);
    $("#fCount").textContent = v.length + " / " + F.list.length;
    if (!v.length) { b.innerHTML = `<tr><td colspan="7" class="empty">${esc(t("nothingFound"))}</td></tr>`; return; }
    b.innerHTML = v.map(([f, i]) => `<tr data-i="${i}" class="${F.sel === i ? "sel" : ""}"><td>${RUNNABLE(f.kind) ? `<input type="radio" name="fsel" ${F.sel === i ? "checked" : ""}>` : ""}</td>
      <td><a class="link">${esc(f.name || "—")}</a></td><td><span class="kbadge ${f.kind.startsWith("TMP") ? "k-stringtable" : f.kind === "Texture2D" ? "k-textasset" : "k-monobehaviour"}">${esc(f.kind)}</span></td>
      <td class="num">${f.chars || ""}</td><td style="font-size:12px">${pairCell(f)}</td><td class="mono" style="font-size:12px;white-space:nowrap">${atlasCell(f)}</td>
      <td class="mono wrap" style="font-size:12px">${esc(f.rel)}<span class="muted"> #${esc(f.pathId)}</span></td></tr>`).join("");
    $$("#fList tr").forEach(tr => tr.onclick = e => { F.sel = +tr.dataset.i; F.prev.i = 0; drawList(); showAtlas(); if (e.target.closest("a.link")) $("#fPrevCard").scrollIntoView({ behavior: "smooth", block: "start" }); });
  };
  $("#fFilter").oninput = e => { F.filter = e.target.value; drawList(); };
  drawList();

  // ---- atlas preview
  let prevUrl = null;
  // name/word pairs (no reference inside the font) → show the paired texture itself
  const atlasTarget = f => {
    const a = (f.atlases || [])[0];
    if ((f.link === "name" || f.link === "word") && a && a.file != null) return { file: a.file, pathId: a.pathId, i: 0 };
    return { file: f.file ?? 0, pathId: f.pathId, i: F.prev.i };
  };
  const applyView = () => {
    const box = $("#fPrevBox"); if (!box) return;
    box.className = "atlas-view bg-" + F.prev.bg + (F.prev.fit ? " fit" : "");
    $("#fPrevBg").value = F.prev.bg; $("#fPrevSrc").value = F.prev.src || "";
    $("#fPrevFit").textContent = F.prev.fit ? "🔍 100%" : "⤢ " + t("fitView");
  };
  const showAtlas = async () => {
    const card = $("#fPrevCard"); if (!card) return;
    const f = F.list && F.list[F.sel];
    $("#fRunFor").textContent = f && RUNNABLE(f.kind) ? (f.name || "") + " →" : "";
    if (!f) return;
    $("#fPrevTitle").textContent = t("atlasOf") + " " + (f.name || "#" + f.pathId);
    $("#fPrevPager").innerHTML = ""; $("#fPrevInfo").textContent = "";
    applyView();
    const isTtf = f.kind === "Unity Font" || f.kind === "TTF";
    $("#fPrevSave").style.display = isTtf ? "none" : ""; $("#fTtfSave").style.display = isTtf ? "" : "none";
    if (isTtf) return showTtf(f);
    $("#fPrevBox").innerHTML = `<div class="empty">${esc(t("loading"))}</div>`;
    const params = { project: S.project, rel: f.rel, ...atlasTarget(f), src: F.prev.src || "", r: Date.now() };
    try {
      const r = await api("/api/fonts/atlas?" + q(params), undefined, true);
      const info = JSON.parse(decodeURIComponent(r.headers.get("X-Atlas") || "%7B%7D"));
      const blob = await r.blob();
      if (F.list[F.sel] !== f) return;
      if (prevUrl) URL.revokeObjectURL(prevUrl);
      prevUrl = URL.createObjectURL(blob);
      $("#fPrevBox").innerHTML = `<img src="${prevUrl}" alt="">`;
      $("#fPrevInfo").textContent = `${info.name} · ${info.w}×${info.h} · ${info.format} · ${fmtSize(blob.size)} PNG · ${info.source === "modified" ? t("src_modified") : info.source === "original" ? t("srcOriginal") : t("src_game")}` + (info.w * info.h <= 1 ? " · " + t("dynAtlasLong") : "");
      if (info.count > 1) {
        $("#fPrevPager").innerHTML = `<button class="btn small" id="apPrev" ${info.index <= 0 ? "disabled" : ""}>‹</button><span class="muted">${info.index + 1}/${info.count}</span><button class="btn small" id="apNext" ${info.index >= info.count - 1 ? "disabled" : ""}>›</button>`;
        $("#apPrev").onclick = () => { F.prev.i--; showAtlas(); }; $("#apNext").onclick = () => { F.prev.i++; showAtlas(); };
      }
    } catch (e) { $("#fPrevBox").innerHTML = `<div class="empty" style="color:var(--err)">${esc(e.message || e)}</div>`; }
  };
  // TTF fonts have no atlas: render a sample with the font itself
  const ttfParams = f => ({ project: S.project, rel: f.rel, file: f.file ?? 0, pathId: f.pathId || "" });
  const showTtf = async f => {
    $("#fPrevTitle").textContent = t("fontOf") + " " + (f.name || "#" + f.pathId);
    $("#fPrevBox").innerHTML = `<div class="empty">${esc(t("loading"))}</div>`;
    try {
      const r = await api("/api/fonts/ttf?" + q(ttfParams(f)), undefined, true);
      const buf = await r.arrayBuffer();
      if (F.list[F.sel] !== f) return;
      const fam = "tlprev" + Date.now();
      const face = new FontFace(fam, buf); await face.load(); document.fonts.add(face);
      const sample = t("fontSample");
      $("#fPrevBox").innerHTML = `<div class="ttf-prev" style="font-family:'${fam}',sans-serif">
        ${[40, 26, 18, 14].map(px => `<div style="font-size:${px}px">${esc(sample)}</div>`).join("")}
        <div style="font-size:18px">АБВГҐДЕЄЖЗИІЇЙКЛМНОПРСТУФХЦЧШЩЬЮЯ<br>абвгґдеєжзиіїйклмнопрстуфхцчшщьюя ʼ<br>ABCDEFGHIJKLMNOPQRSTUVWXYZ abcdefghijklmnopqrstuvwxyz<br>0123456789 .,:;!?«»—()</div></div>`;
      $("#fPrevInfo").textContent = `${f.name} · ${f.kind === "TTF" ? f.rel : "Font (m_FontData)"} · ${fmtSize(buf.byteLength)}`;
    } catch (e) { $("#fPrevBox").innerHTML = `<div class="empty" style="color:var(--err)">${esc(e.message || e)}</div>`; }
  };
  $("#fTtfSave").onclick = async () => {
    const f = F.list && F.list[F.sel]; if (!f) return;
    try { if (await chooseExportDir(S.project) === false) return; const r = await api("/api/fonts/ttf?" + q({ ...ttfParams(f), save: 1 })); toast(t("savedTo") + " " + r.path, "ok"); } catch (e) { fail(e); }
  };
  F.showAtlas = showAtlas;
  applyView();
  $("#fPrevBg").onchange = e => { F.prev.bg = e.target.value; applyView(); };
  $("#fPrevSrc").onchange = e => { F.prev.src = e.target.value; showAtlas(); };
  $("#fPrevFit").onclick = () => { F.prev.fit = !F.prev.fit; applyView(); };
  $("#fPrevSave").onclick = async () => {
    const f = F.list && F.list[F.sel]; if (!f) return;
    try { if (await chooseExportDir(S.project) === false) return; const r = await api("/api/fonts/atlas?" + q({ project: S.project, rel: f.rel, ...atlasTarget(f), src: F.prev.src || "", save: 1 })); toast(t("savedTo") + " " + r.path, "ok"); } catch (e) { fail(e); }
  };
  if (F.sel != null && F.list) showAtlas();

  // ---- log / report
  const drawLog = () => { $("#fLog").innerHTML = F.log.map(l => `<div class="${/ПОМИЛКА|ERROR|УВАГА|^!/.test(l) ? "error" : ""}">${esc(l)}</div>`).join(""); $("#fLog").scrollTop = 1e9; };
  drawLog();
  const drawReport = () => {
    if (!F.report) return; $("#fRepCard").classList.remove("hidden");
    $("#fRep").innerHTML = `${F.report.reportPng ? `<img class="report-img" src="/api/report?${q({ project: S.project, name: F.report.reportPng, t: TOKEN })}&r=${Date.now()}">` : ""}
      ${F.report.reportTxt ? `<div style="margin-top:8px"><a href="/api/report?${q({ project: S.project, name: F.report.reportTxt, t: TOKEN })}" target="_blank">${esc(F.report.reportTxt)}</a></div>` : ""}`;
  };
  drawReport();

  $("#fScan").onclick = async () => {
    try {
      const st2 = S.state.settings;
      if (st2.FontSearchMode === "words" && !words().length && st2.FontUseDefault === false) return toast(t("needWord"), "warn");
      const r = await api("/api/fonts/scan", { project: S.project, mode: st2.FontSearchMode || "all", words: words(), useDefault: st2.FontUseDefault !== false, orphans: !!st2.FontOrphanTextures });
      F.log = []; drawLog();
      trackJob(r.job, t("findFonts"), j => {
        F.list = j.result || [];
        const rank = k => k === "TMP" ? 0 : k === "TMP legacy" ? 1 : k === "TMP?" ? 2 : k === "Unity Font" ? 3 : 4;
        F.list.sort((a, b) => rank(a.kind) - rank(b.kind) || (b.chars || 0) - (a.chars || 0));
        F.sel = F.list.length ? 0 : null; F.prev.i = 0;
        if (S.page === "fonts") { drawList(); showAtlas(); }
      });
      if (S.job) S.job.onLog = lines => { F.log.push(...lines.map(l => l.replace(/^\[\d\d:\d\d:\d\d\] /, ""))); if (S.page === "fonts") drawLog(); };
    } catch (e) { fail(e); }
  };
  $("#fRun").onclick = async () => {
    F.ttf = $("#fTtf").value.trim();
    if (F.sel == null || !F.list) return toast(t("selectFont"), "warn");
    const f = F.list[F.sel];
    if (!RUNNABLE(f.kind)) return toast(t("selectFontNotTex"), "warn");
    if (!F.ttf) return toast(t("needTtf"), "warn");
    try {
      const r = await api("/api/fonts/run", { project: S.project, rel: f.rel, font: f.name, pathId: f.pathId, ttf: F.ttf, size: $("#oSize").value, atlas: $("#oAtlas").value, padding: $("#oPad").value,
        chars: $("#oChars").value, charsFile: $("#oCharsFile").value, onlyOriginal: $("#oOnly").checked, bitmap: $("#oBitmap").checked, noKerning: $("#oNoKern").checked });
      F.log = []; drawLog();
      trackJob(r.job, t("createFont"), async j => {
        if (j.status === "done" && j.result) {
          F.report = j.result; if (S.page === "fonts") { drawReport(); F.prev.i = 0; showAtlas(); }
          const rels = j.result.saved || [j.result.rel];
          if (j.result.askApply && await confirmBox(t("askApply") + "\n\n" + rels.join("\n"))) { await api("/api/apply", { project: S.project, rels }); toast(t("appliedN"), "ok"); }
        }
      });
      if (S.job) S.job.onLog = lines => { F.log.push(...lines.map(l => l.replace(/^\[\d\d:\d\d:\d\d\] /, ""))); if (S.page === "fonts") drawLog(); };
    } catch (e) { fail(e); }
  };
}
async function uploadTtf(f) {
  if (!needProject()) return;
  try { const r = await api("/api/fonts/upload", { project: S.project, name: f.name, data: await fileToB64(f) }); S.fonts.ttf = r.path; toast("TTF: " + f.name, "ok"); if (S.page === "fonts") render(); } catch (e) { fail(e); }
}

// ============================================================ PAGE: changes
async function pChanges(m) {
  if (!needProject()) return;
  m.innerHTML = `<h1>${esc(t("changes_title"))}</h1><div class="sub">${esc(t("changes_sub"))}</div>
  <div class="row" style="margin-bottom:10px"><button class="btn primary big" id="cApply">⬇ ${esc(t("applyAll"))}</button><span class="grow"></span>
    <details class="more"><summary class="btn small">${esc(t("more"))} ▾</summary><div class="menu right">
      <button class="btn small" id="cRestore">⟲ ${esc(t("restoreAll"))}</button>
      <button class="btn small" data-o="modified">${esc(t("openModified"))}</button><button class="btn small" data-o="original">${esc(t("openOriginal"))}</button><button class="btn small" data-o="exports">${esc(t("openExports"))}</button></div></details></div>
  <div class="tbl-wrap"><table class="tbl"><thead id="cHead"></thead><tbody id="cBody"><tr><td colspan="5" class="empty">…</td></tr></tbody></table></div>
  <div class="card" id="cCat" style="margin-top:14px;display:none"></div>`;
  $("#cApply").onclick = doApplyAll;
  api("/api/catalogs?" + q({ project: S.project })).then(cats => {
    if (!cats.length || !$("#cCat")) return;
    const need = cats.filter(c => c.crc > 0).length;
    $("#cCat").style.display = "";
    $("#cCat").innerHTML = `<div class="row"><div class="grow"><b>${esc(t("catalogTitle"))}</b><div class="muted" style="margin-top:4px">${esc(need ? t("catalogNeed") : t("catalogOk"))}</div>
      <div class="mono muted" style="font-size:12px;margin-top:6px">${cats.map(c => esc(c.rel) + (c.crc > 0 ? " · CRC: " + c.crc : " ✓")).join("<br>")}</div></div>
      <button class="btn ${need ? "primary" : ""}" id="cCatBtn" ${need ? "" : "disabled"}>${esc(t("catalogUpdate"))}</button></div>`;
    $("#cCatBtn").onclick = () => askCatalogs(true);
  }).catch(() => {}); $("#cRestore").onclick = doRestoreAll;
  $$("[data-o]", m).forEach(b => b.onclick = async () => { try { const r = await api("/api/open-folder", { project: S.project, which: b.dataset.o }); toast(r.dir); } catch (e) { fail(e); } });
  try {
    const all = await api("/api/changed?" + q({ project: S.project }));
    const cDef = { k: "", d: 1 };
    $("#cHead").innerHTML = `<tr>${th("changes", "file", t("file"), cDef)}${th("changes", "size", t("size"), cDef)}${th("changes", "status", t("status"), cDef)}<th></th><th></th></tr>`;
    bindSort($("#cHead"), () => render(), cDef);
    const list = sortRows("changes", all, { file: f => f.rel, size: f => f.size, status: f => (f.applied ? 1 : 0) }, cDef);
    $("#cBody").innerHTML = list.length ? list.map((f, i) => `<tr data-i="${i}"><td class="mono wrap" style="font-size:12px">${esc(f.rel)}<div class="muted">${esc(f.modifiedAt)}</div></td><td class="num">${fmtSize(f.size)}</td>
      <td><span class="kbadge ${f.applied ? "k-i2" : "k-monobehaviour"}">${esc(f.applied ? t("applied") : t("notApplied"))}</span> ${f.hasOriginal ? `<span class="kbadge">${esc(t("hasOriginal"))}</span>` : ""}</td>
      <td><button class="btn small" data-a="apply">${esc(t("applyOne"))}</button> <button class="btn small" data-a="restore" ${f.hasOriginal ? "" : "disabled"}>${esc(t("restoreOne"))}</button></td>
      <td><button class="btn small danger" data-a="del">${esc(t("deleteMod"))}</button></td></tr>`).join("") : `<tr><td colspan="5" class="empty">${esc(t("noChanges"))}</td></tr>`;
    $$("#cBody tr[data-i]").forEach(tr => tr.onclick = async e => {
      const a = e.target.dataset.a; if (!a) return; const f = list[+tr.dataset.i];
      try {
        if (a === "apply") await api("/api/apply", { project: S.project, rels: [f.rel] });
        if (a === "restore") await api("/api/restore", { project: S.project, rels: [f.rel] });
        if (a === "del" && await confirmBox(t("deleteMod") + "?\n" + f.rel)) await api("/api/discard", { project: S.project, rel: f.rel, deleteModified: true });
        render();
      } catch (er) { fail(er); }
    });
  } catch (e) { fail(e); }
}

// ============================================================ PAGE: pack (step 6) — archive for other players
async function pPack(m) {
  if (!needProject()) return;
  m.innerHTML = `<h1>${esc(t("pack_title"))}</h1><div class="sub">${esc(t("pack_sub"))}</div><div id="pkBody"><div class="empty">${esc(t("loading"))}</div></div>`;
  let pv;
  try { pv = await api("/api/pack?" + q({ project: S.project })); } catch (e) { return fail(e); }
  const def = { k: "file", d: 1 };
  const draw = () => {
    const box = $("#pkBody"); if (!box) return;
    const rows = sortRows("pack", pv.files, { file: f => f.rel, size: f => f.size }, def);
    box.innerHTML = `<div class="card"><div class="row" style="align-items:flex-start;gap:16px">
        <div class="grow"><div class="muted" style="font-size:12px">${esc(t("pack_name"))}</div>
          <div class="mono" style="font-size:18px;font-weight:600;margin:2px 0 6px">📦 ${esc(pv.name)}</div>
          <div class="muted">${esc(t("pack_count").replace("{n}", pv.files.length).replace("{s}", fmtSize(pv.total)))}</div>
          <div class="muted mono" style="font-size:12px;margin-top:4px">${esc(t("pack_where"))}: ${esc(pv.exports)}</div></div>
        <button class="btn primary big" id="pkGo" ${pv.files.length ? "" : "disabled"}>📦 ${esc(t("pack_go"))}</button></div>
      ${pv.catalogNeed ? `<div class="row" style="margin-top:12px;padding:10px 12px;border-radius:10px;background:var(--drop)"><span class="grow">⚠ ${esc(t("pack_catalog"))}</span><button class="btn small" id="pkCat">${esc(t("catalogUpdate"))}</button></div>` : ""}
      ${pv.skipped.length ? `<div class="muted" style="margin-top:10px;font-size:12px">${esc(t("pack_skipped"))}: <span class="mono">${pv.skipped.map(esc).join(", ")}</span></div>` : ""}</div>
      <h2>${esc(t("pack_files"))}</h2>
      <div class="tbl-wrap"><table class="tbl"><thead id="pkHead"><tr>${th("pack", "file", t("file"), def)}${th("pack", "size", t("size"), def)}</tr></thead><tbody>${
        rows.length ? rows.map(f => `<tr><td class="mono wrap" style="font-size:12px">${esc(f.rel)} ${f.catalog ? `<span class="kbadge">${esc(t("pack_isCatalog"))}</span>` : ""}</td><td class="num">${fmtSize(f.size)}</td></tr>`).join("")
          : `<tr><td colspan="2" class="empty">${esc(t("pack_empty"))}</td></tr>`}</tbody></table></div>`;
    bindSort($("#pkHead"), draw, def);
    if ($("#pkCat")) $("#pkCat").onclick = async () => { await askCatalogs(true); render(); };
    $("#pkGo").onclick = async () => {
      try {
        if (await chooseExportDir(S.project) === false) return;
        const r = await api("/api/pack", { project: S.project });
        trackJob(r.job, t("pack_go"), async j => {
          if (j.status !== "done" || !j.result) return;
          toast(t("pack_done") + " " + j.result.path, "ok");
          try { await api("/api/open-folder", { project: S.project, which: "exports" }); } catch { }
        });
      } catch (e) { fail(e); }
    };
  };
  draw();
}

// ============================================================ PAGE: log
async function pLog(m) {
  m.innerHTML = `<div class="row" style="margin-bottom:10px"><h1 class="grow" style="margin:0">${esc(t("log_title"))}</h1><label class="chk"><input type="checkbox" id="lErr" ${S.logOnlyErrors ? "checked" : ""}>${esc(t("onlyErrors"))}</label><button class="btn small" id="lClear">${esc(t("clear"))}</button></div><div class="log" id="logBox"></div>`;
  $("#lErr").onchange = e => { S.logOnlyErrors = e.target.checked; render(); };
  $("#lClear").onclick = async () => { await api("/api/log/clear", {}); await refreshState(); render(); };
  try {
    const r = await api("/api/log?from=0");
    const items = r.items.filter(i => !S.logOnlyErrors || i.level !== "info");
    $("#logBox").innerHTML = items.map(i => `<div class="${i.level}">${esc(i.t)} [${esc(i.level)}] ${esc(i.msg)}</div>`).join("") || `<div class="muted">—</div>`;
    $("#logBox").scrollTop = 1e9;
  } catch (e) { fail(e); }
}

// ============================================================ PAGE: settings & help
function pSettings(m) {
  const s = S.state.settings;
  m.innerHTML = `<h1>${esc(t("settings_title"))}</h1><div class="card"><div class="grid2">
    <div class="field"><label>${esc(t("language"))}</label><select id="sLang"><option value="uk" ${s.Language === "uk" ? "selected" : ""}>Українська</option><option value="en" ${s.Language === "en" ? "selected" : ""}>English</option></select></div>
    <div class="field"><label>${esc(t("theme"))}</label><select id="sTheme">${THEMES.map(th => `<option value="${th}" ${s.Theme === th ? "selected" : ""}>${esc(t(th))}</option>`).join("")}</select></div></div>
    <div style="margin-top:14px;display:flex;flex-direction:column;gap:10px">
      <label class="chk"><input type="checkbox" id="sBackup" ${s.BackupOriginals ? "checked" : ""}>${esc(t("backupOriginals"))}</label>
      <label class="chk"><input type="checkbox" id="sInPlace" ${s.ReplaceInPlace ? "checked" : ""}>${esc(t("replaceInPlace"))}</label>
      <label class="chk"><input type="checkbox" id="sAsk" ${s.AskBeforeReplace ? "checked" : ""}>${esc(t("askBeforeReplace"))}</label></div>
    <div class="field" style="margin-top:14px"><label>${esc(t("workspaceRoot"))}</label><div class="row"><input type="text" id="sWs" class="grow mono" value="${esc(s.WorkspaceRoot)}" placeholder="${esc(S.state.defaultWorkspaceRoot || "")}"><button class="btn" id="sWsD" title="${esc(S.state.defaultWorkspaceRoot || "")}">${esc(t("byDefault"))}</button><button class="btn" id="sWsB">${esc(t("browse"))}</button></div></div>
    <div class="row" style="margin-top:16px"><span class="muted grow mono" style="font-size:12px">data: ${esc(S.state.dataDir)} · ${esc(S.state.os)}</span><button class="btn primary" id="sSave">${esc(t("saveSettings"))}</button></div></div>${hotkeysCard()}`;
  bindHotkeysCard();
  $("#sWsB").onclick = async () => { const p = await pickPath({}); if (p) $("#sWs").value = p; };
  // theme and language apply at once, without "Save settings"
  $("#sTheme").onchange = e => saveSettings({ Theme: e.target.value }).catch(fail);
  $("#sLang").onchange = e => saveSettings({ Language: e.target.value }).catch(fail);
  $("#sWsD").onclick = () => { $("#sWs").value = S.state.defaultWorkspaceRoot || ""; };
  $("#sSave").onclick = () => saveSettings({ Language: $("#sLang").value, Theme: $("#sTheme").value, BackupOriginals: $("#sBackup").checked, ReplaceInPlace: $("#sInPlace").checked, AskBeforeReplace: $("#sAsk").checked, WorkspaceRoot: $("#sWs").value }).then(() => toast(t("saved"), "ok")).catch(fail);
}
// ============================================================ about / welcome / links
async function openLink(key) {
  const url = ((S.state.links || {})[key] || "").trim();
  if (!url) { toast(t("linkNotSet"), "warn"); return; }
  try { await api("/api/open-url", { url }); } catch (e) { fail(e); }
}
function aboutHtml() {
  return `<div class="about">
    <div class="ua-flag"><span></span><span></span></div>
    <h2 style="margin-top:14px">${esc(t("appName"))} <span class="muted" style="font-weight:400">v${esc(S.state.version)}</span></h2>
    <p>${esc(t("aboutText"))}</p>
    <p>${t("aboutAuthors")}</p>
    <p class="ua-glory">${esc(t("aboutUkraine"))}</p>
    <p><b>${esc(t("aboutGlory"))}</b></p>
    <p class="muted" style="font-size:12px">${esc(t("aboutThirdParty"))}</p>
  </div>`;
}
function pAbout(m) {
  m.innerHTML = `<h1>${esc(t("nav_about"))}</h1><div class="card">${aboutHtml()}
    <div class="row" style="margin-top:16px"><button class="btn primary" id="abDonate">♥ ${esc(t("donate"))}</button><button class="btn" id="abBugs">🐞 Bugs (Discord)</button>
</div></div>`;
  $("#abDonate").onclick = () => openLink("donate"); $("#abBugs").onclick = () => openLink("bugs");
}
async function showWelcome() {
  const logo = $(".side-brand .logo")?.outerHTML || "";
  await modal({ title: t("welcomeTitle"), body: `<div class="welcome">${logo}<p>${t("welcomeText")}</p></div>`, buttons: [{ label: t("welcomeStart"), value: true, cls: "primary" }] });
  try { await saveSettings({ WelcomeShown: true }); } catch { }
}
function pHelp(m) { m.innerHTML = `<h1>${esc(t("help_title"))}</h1><div class="card help">${HELP[S.lang] || HELP.uk}</div>`; }

// ============================================================ tooltips
// Elements whose own text (or placeholder) equals an I18N text get the matching TIPS hint; titles become the same styled tooltip.
let tipIndex = null, tipLang = null;
const tipNorm = x => (x || "").replace(/[\u2026:▾▲▼⇅]+\s*$/g, "").replace(/^[^\p{L}\p{N}]+/u, "").replace(/^\d+\.\s*/, "").replace(/\s+/g, " ").trim().toLowerCase();
function tipFor(text) {
  if (tipLang !== S.lang) {
    tipLang = S.lang; tipIndex = new Map();
    const tips = (window.TIPS && (TIPS[S.lang] || TIPS.uk)) || {}, dict = I18N[S.lang] || I18N.uk;
    for (const k of Object.keys(tips)) { const v = tipNorm(String(dict[k] ?? I18N.uk[k] ?? "")); if (v) (tipIndex.get(v) || tipIndex.set(v, []).get(v)).push({ k, tip: tips[k] || (TIPS.uk || {})[k] }); }
    if (TIPS.uk) for (const k of Object.keys(TIPS.uk)) { const v = tipNorm(String(dict[k] ?? "")); if (v && !tipIndex.has(v)) tipIndex.set(v, [{ k, tip: TIPS.uk[k] }]); }
  }
  const n = tipNorm(text); return n ? tipIndex.get(n) : null;
}
// action hints that only make sense on buttons (the same word is also a column or badge elsewhere)
const TIP_BUTTON_ONLY = new Set(["restoreOne", "applyOne", "open", "dllAdd", "edit", "export", "import", "remove"]);
function resetTips() {
  $$("[data-tip-auto]").forEach(el => { delete el.dataset.tip; delete el.dataset.tipAuto; });
  $$("[data-tip-done]").forEach(el => delete el.dataset.tipDone);
  addTips(document.body);
}
function addTips(root) {
  if (!root || !root.querySelectorAll || !window.TIPS) return;
  for (const el of root.querySelectorAll("[title]")) { if (el.title) { el.dataset.tip = el.title; delete el.dataset.tipAuto; } el.removeAttribute("title"); }
  for (const el of root.querySelectorAll("button, a, label, th, summary, h1, h2, h3, b, span, .kbadge, input[placeholder]")) {
    if (el.dataset.tipDone) continue;
    let own = el.placeholder || "";
    if (!own) for (const n of el.childNodes) if (n.nodeType === 3) own += n.textContent;
    if (!own.trim() && el.children.length === 1 && !el.children[0].children.length) own = el.children[0].textContent;
    if (!own.trim()) continue;
    el.dataset.tipDone = "1";
    // the same text can be a step name and a button: pick by where the element is
    const hits = tipFor(own) || [], inStep = !!el.closest("a.step");
    const hit = hits.find(h => h.k.startsWith("step_") === inStep) || hits[0];
    if (!hit || (TIP_BUTTON_ONLY.has(hit.k) && !el.closest("button"))) continue;
    const tip = hit.tip;
    const field = el.tagName === "LABEL" && el.parentElement && el.parentElement.classList.contains("field") ? el.parentElement : null;
    const target = field || el.closest("button, a, label, th, summary, .opt, .kbadge, .chip") || el;
    const cur = target.dataset.tip || "";
    if (!cur) target.dataset.tipAuto = "1";
    if (!cur.includes(tip)) target.dataset.tip = cur && target.tagName === "TH" ? tip + "\n" + cur : (cur && cur !== own.trim() ? cur + "\n" + tip : tip);
  }
}
(function tipsInit() {
  const box = document.createElement("div"); box.className = "tipbox"; box.setAttribute("role", "tooltip");
  let timer = null, cur = null;
  const hide = () => { clearTimeout(timer); timer = null; cur = null; box.classList.remove("on"); };
  const show = el => {
    box.textContent = el.dataset.tip; if (!box.isConnected) document.body.appendChild(box);
    box.classList.add("on");
    const r = el.getBoundingClientRect(), b = box.getBoundingClientRect();
    let x = Math.min(Math.max(8, r.left + r.width / 2 - b.width / 2), innerWidth - b.width - 8);
    let y = r.bottom + 8; if (y + b.height > innerHeight - 8) y = r.top - b.height - 8;
    box.style.left = x + "px"; box.style.top = Math.max(8, y) + "px";
  };
  document.addEventListener("mouseover", e => {
    const el = e.target.closest && e.target.closest("[data-tip], [title]");
    if (el && el.title) { el.dataset.tip = el.title; delete el.dataset.tipAuto; el.removeAttribute("title"); }
    if (el === cur) return; hide();
    if (!el || !el.dataset.tip) return;
    cur = el; timer = setTimeout(() => { if (cur === el && el.isConnected) show(el); }, 450);
  });
  document.addEventListener("mousedown", hide, true); document.addEventListener("scroll", hide, true); document.addEventListener("keydown", hide, true);
  new MutationObserver(ms => { for (const m of ms) for (const n of m.addedNodes) { const el = n.nodeType === 1 ? n : n.parentElement; if (el) addTips(el.parentElement || el); } })
    .observe(document.documentElement, { childList: true, subtree: true });
  addEventListener("load", () => addTips(document.body));
})();

// ============================================================ wiring
const ACTS = {
  save: () => doSave(false), quicksave: () => doSave(true), undo, redo,
  pause: () => jobCmd("pause"), resume: () => jobCmd("resume"), stop: () => jobCmd("cancel"),
  apply: doApplyAll, restore: doRestoreAll, exit: doExit,
  theme: () => saveSettings({ Theme: THEMES[(THEMES.indexOf(S.state.settings.Theme) + 1) % THEMES.length] }),
  lang: () => saveSettings({ Language: S.lang === "uk" ? "en" : "uk" }),
  log: () => go("log"), about: () => go("about"), donate: () => openLink("donate"), bugs: () => openLink("bugs"),
};
$("#nav").onclick = e => {
  const a = e.target.closest("a[data-page]"); if (a) return go(a.dataset.page);
  const b = e.target.closest("button[data-act]"); if (b) ACTS[b.dataset.act]?.();
};
$("#jobBtns").onclick = e => { const b = e.target.closest("button[data-act]"); if (b) ACTS[b.dataset.act]?.(); };
document.addEventListener("click", e => { $$("details.more[open]").forEach(d => { if (!d.contains(e.target)) d.open = false; }); });
$("#projectSelect").onchange = e => { S.project = e.target.value; localStorage.setItem("project", S.project); S.scan.results = null; S.manual = { rel: "", tree: {}, open: new Set([""]), file: null, assets: [], sel: null, typeFilter: "", nameFilter: "", findResults: null, checked: new Set(), picked: new Set() }; S.fonts.list = null; render(); };
$("#toolbar").onclick = e => { const b = e.target.closest("button[data-act]"); if (b) ACTS[b.dataset.act]?.(); };
// hotkeys: configurable in Settings (S.state.settings.Hotkeys keeps only the changed ones, "" = no key)
const HOTKEYS = [
  ["quicksave", "Ctrl+S"], ["save", "Ctrl+Shift+S"], ["apply", "Ctrl+G"], ["restore", ""],
  ["undo", "Ctrl+Z"], ["redo", "Ctrl+X"],
  ["about", "F1"], ["help", "Shift+F1"], ["settings", "Ctrl+,"], ["log", "Ctrl+L"],
  ["pause", ""], ["resume", ""], ["stop", ""], ["theme", "Ctrl+Alt+T"], ["lang", "Ctrl+Alt+L"],
];
const HK_SAVE = new Set(["quicksave", "save"]);   // these also work while a dialog is open
const HK_EDIT = new Set(["Ctrl+Z", "Ctrl+Y", "Ctrl+Shift+Z", "Ctrl+X", "Ctrl+C", "Ctrl+V", "Ctrl+A"]);   // text-editing keys of the browser
function hotkey(act) { const o = S.state?.settings?.Hotkeys || {}; return act in o ? o[act] : (HOTKEYS.find(h => h[0] === act) || [, ""])[1]; }
const HK_CODES = { Comma: ",", Period: ".", Slash: "/", Backslash: "\\", Semicolon: ";", Quote: "'", BracketLeft: "[", BracketRight: "]", Minus: "-", Equal: "=", Backquote: "`", Space: "Space", Enter: "Enter", Tab: "Tab", Backspace: "Backspace", Delete: "Delete", Insert: "Insert", Home: "Home", End: "End", PageUp: "PageUp", PageDown: "PageDown", ArrowUp: "Up", ArrowDown: "Down", ArrowLeft: "Left", ArrowRight: "Right" };
// combo from the physical key (e.code), so Ctrl+S stays Ctrl+S on the Ukrainian layout
function comboOf(e) {
  const c = e.code || "";
  let k = /^Key[A-Z]$/.test(c) ? c.slice(3) : /^Digit\d$/.test(c) ? c.slice(5) : /^Numpad\d$/.test(c) ? "Num" + c.slice(6) : /^F\d{1,2}$/.test(c) ? c : HK_CODES[c];
  if (!k) return null;
  return [(e.ctrlKey || e.metaKey) && "Ctrl", e.altKey && "Alt", e.shiftKey && "Shift", k].filter(Boolean).join("+");
}
const HK_RUN = {
  quicksave: () => doSave(true), save: () => doSave(false), apply: () => doApplyAll(), restore: () => doRestoreAll(),
  undo: () => undo(), redo: () => redo(), about: () => go("about"), help: () => go("help"), settings: () => go("settings"), log: () => go("log"),
  pause: ACTS.pause, resume: ACTS.resume, stop: ACTS.stop, theme: ACTS.theme, lang: ACTS.lang,
};
function hkTitles() {   // toolbar tooltips show the current key
  $$("[data-act][data-i18n-title]").forEach(el => {
    const k = hotkey(el.dataset.act);
    el.title = t(el.dataset.i18nTitle).replace(/\s*\((Ctrl|Shift|Alt|F\d)[^)]*\)\s*$/, "") + (k ? ` (${k})` : "");
  });
}
document.addEventListener("focusin", e => { if (e.target.matches?.("input, textarea")) e.target._typed = false; });
document.addEventListener("input", e => { if (e.target.matches?.("input, textarea")) e.target._typed = true; });
document.addEventListener("keydown", e => {
  if (S.hkCapture || e.repeat) return;
  const combo = comboOf(e); if (!combo) return;
  const act = HOTKEYS.map(h => h[0]).find(a => hotkey(a) === combo); if (!act) return;
  if ($("#modalRoot").children.length && !HK_SAVE.has(act)) return;
  const field = e.target && e.target.matches?.("input, textarea, select, [contenteditable]");
  const plain = !/(Ctrl|Alt)\+/.test(combo) && !/^F\d/.test(combo);
  if (field && plain) return;   // a bare letter key while typing is just typing
  if (field && HK_EDIT.has(combo)) {
    // in a text field the browser's own undo / cut / copy / paste win once something is typed or selected
    const sel = e.target.selectionStart != null && e.target.selectionStart !== e.target.selectionEnd;
    if (e.target._typed || sel || /^Ctrl\+[CVA]$/.test(combo)) return;
  }
  e.preventDefault(); HK_RUN[act]?.();
});
// settings card: click a key field, press the new combination (Esc cancels, Backspace / Delete clears)
function hotkeysCard() {
  return `<div class="card"><div class="row" style="margin-bottom:6px"><h2 class="grow" style="margin:0">${esc(t("hk_title"))}</h2><button class="btn small" id="hkResetAll">${esc(t("hk_resetAll"))}</button></div>
    <div class="sub" style="margin-bottom:10px">${esc(t("hk_sub"))}</div>
    <div class="hk-grid">${HOTKEYS.map(([a, d]) => { const k = hotkey(a); return `<div class="hk-row"><span class="grow">${esc(t("hk_" + a))}</span>
      <button class="hk-key ${k ? "" : "none"}" data-hk="${a}">${esc(k || t("hk_none"))}</button>
      <button class="btn small hk-def" data-hkdef="${a}" title="${esc(t("hk_reset") + (d ? ": " + d : ""))}" ${k === d ? "disabled" : ""}>↺</button></div>`; }).join("")}</div></div>`;
}
function bindHotkeysCard() {
  const save = map => saveSettings({ Hotkeys: map }).catch(fail);
  const setKey = (act, combo) => {
    const map = { ...(S.state.settings.Hotkeys || {}) };
    const other = combo && HOTKEYS.map(h => h[0]).find(a => a !== act && hotkey(a) === combo);
    if (other) { map[other] = ""; toast(t("hk_moved").replace("{k}", combo).replace("{a}", t("hk_" + other)), "warn"); }
    const def = HOTKEYS.find(h => h[0] === act)[1];
    if (combo === def) delete map[act]; else map[act] = combo;
    for (const [a, d] of HOTKEYS) if (map[a] === d) delete map[a];
    save(map);
  };
  $$("[data-hk]").forEach(b => b.onclick = () => {
    $$("[data-hk].wait").forEach(x => { x.classList.remove("wait"); x.textContent = hotkey(x.dataset.hk) || t("hk_none"); });
    b.classList.add("wait"); b.textContent = t("hk_press"); S.hkCapture = true;
    const done = () => { S.hkCapture = false; document.removeEventListener("keydown", onKey, true); b.removeEventListener("blur", cancel); };
    const cancel = () => { done(); b.classList.remove("wait"); b.textContent = hotkey(b.dataset.hk) || t("hk_none"); };
    const onKey = e => {
      if (["Control", "Shift", "Alt", "Meta"].includes(e.key)) return;
      e.preventDefault(); e.stopPropagation();
      if (e.code === "Escape") { cancel(); return; }
      if ((e.code === "Backspace" || e.code === "Delete") && !e.ctrlKey && !e.altKey && !e.shiftKey) { done(); setKey(b.dataset.hk, ""); return; }
      const combo = comboOf(e);
      if (!combo) return;
      if (!/(Ctrl|Alt)\+/.test(combo) && !/^(Shift\+)?F\d/.test(combo)) { toast(t("hk_needMod"), "warn"); return; }
      done(); setKey(b.dataset.hk, combo);
    };
    document.addEventListener("keydown", onKey, true); b.addEventListener("blur", cancel);
  });
  $$("[data-hkdef]").forEach(b => b.onclick = () => { const map = { ...(S.state.settings.Hotkeys || {}) }; delete map[b.dataset.hkdef];
    const d = HOTKEYS.find(h => h[0] === b.dataset.hkdef)[1]; const other = d && HOTKEYS.map(h => h[0]).find(a => a !== b.dataset.hkdef && hotkey(a) === d); if (other) map[other] = ""; save(map); });
  $("#hkResetAll").onclick = () => save({});
}
// drag & drop: .ttf → fonts; anything else → import into the open asset (or matched by exported file name)
let dragDepth = 0;
window.addEventListener("dragenter", e => { if (e.dataTransfer?.types?.includes("Files")) { dragDepth++; $("#dropOverlay").classList.remove("hidden"); } });
window.addEventListener("dragleave", () => { if (--dragDepth <= 0) { dragDepth = 0; $("#dropOverlay").classList.add("hidden"); } });
window.addEventListener("dragover", e => e.preventDefault());
window.addEventListener("drop", async e => {
  e.preventDefault(); dragDepth = 0; $("#dropOverlay").classList.add("hidden");
  const rest = [];
  for (const f of e.dataTransfer.files) {
    if (/\.(ttf|otf)$/i.test(f.name)) { await uploadTtf(f); continue; }
    if (S.editor && S.page === "editor") await importFile(f, S.editor.target);
    else if (S.page === "manual" && S.manual.sel) await importFile(f, { project: S.project, rel: S.manual.file, file: S.manual.sel.file, pathId: S.manual.sel.pathId });
    else rest.push(f);   // anywhere else: the launcher finds the place in the game itself
  }
  if (rest.length) batchAdd(rest);
});
window.addEventListener("beforeunload", e => { if (S.state?.open.some(o => o.dirty)) { e.preventDefault(); e.returnValue = ""; } });
setInterval(() => { refreshState(); }, 4000);

(async function init() {
  await refreshState();
  go(S.state && S.state.projects.length ? "auto" : "projects");
  if (S.state && !S.state.settings.WelcomeShown) showWelcome();
})();
