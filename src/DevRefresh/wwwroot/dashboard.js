"use strict";

const dashboard = { jobs: [], timer: null, generation: 0, controller: null, error: null,
  renderedHost: null, renderedKey: null, user: null, serverOffset: 0, visibleForMs: 300000, inventoryDone: new Set() };
const dashboardActive = job => job.status === "Queued" || job.status === "Running";
const dashboardTerminal = job => ["Succeeded", "Failed", "Interrupted"].includes(job.status);
const dashboardTitle = kind => ({ refresh: "Восстановление", analysis: "Анализ бэкапа", preview: "Проверка плана" }[kind] || kind);
const dashboardStatus = status => ({ Scheduled: "Запланировано", Queued: "В очереди", Running: "Выполняется", Succeeded: "Завершено", Failed: "Ошибка", Interrupted: "Прервано" }[status] || status);
const backgroundLabel = status => ({
  Queued: "Освобождение места в очереди",
  Running: "Фоновое освобождение места",
  Succeeded: "Освобождение места завершено",
  Failed: "Освобождение места не завершено",
  Interrupted: "Освобождение места прервано",
  Skipped: "Освобождение места пропущено"
}[status] || "");
const databaseReadyWhileRunning = job => job.status === "Running" && Boolean(job.readyAt);
const dashboardCheck = '<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="m5 12 4 4L19 6"/></svg>';

function visibleDashboardJobs(jobs, now, visibleForMs) {
  return jobs.filter(job => job.status === "Scheduled" || dashboardActive(job) || dashboardTerminal(job) &&
    Number.isFinite(Date.parse(job.updatedAt)) && now - Date.parse(job.updatedAt) < visibleForMs)
    .sort((a, b) => {
      const rank = job => job.status === "Running" ? 0 : job.status === "Queued" ? 1 : job.status === "Scheduled" ? 3 : 2;
      return rank(a) - rank(b) || (a.status === "Scheduled" ? Date.parse(a.scheduledAt) - Date.parse(b.scheduledAt)
        : dashboardActive(a) ? Date.parse(a.createdAt) - Date.parse(b.createdAt) : Date.parse(b.updatedAt) - Date.parse(a.updatedAt));
    });
}

function dashboardProgressValue(job, now) {
  if (job.status === "Succeeded") return { percent: 100, label: "100%" };
  const start = Date.parse(job.startedAt);
  const elapsed = Math.max(0, now - start);
  const estimate = job.estimatedDurationMs;
  if (!Number.isFinite(elapsed)) return { percent: null, label: "" };
  if (!Number.isFinite(estimate) || estimate <= 0)
    return { percent: null, label: `Прошло ${elapsed < 1000 ? "0 с" : jobDuration(elapsed)}` };
  const percent = Math.min(99, Math.floor(elapsed / estimate * 100));
  return { percent, label: elapsed >= estimate ? "≈ 99% · Дольше оценки" : `≈ ${percent}%` };
}

function dashboardProgress(job, compact = false) {
  if (databaseReadyWhileRunning(job)) {
    const background = backgroundLabel(job.backgroundStatus);
    return `<div class="dashboard-job-state success ${compact ? "compact" : ""}">
      <div class="dashboard-status-line"><span class="dashboard-status-icon" aria-hidden="true">${dashboardCheck}</span><strong>База готова к работе</strong></div>
      ${background ? `<small class="dashboard-stage">${esc(background)}${job.backgroundStatus === "Running" ? ` · ${esc(job.stage)}` : ""}</small>` : ""}
    </div>`;
  }
  const success = job.status === "Succeeded";
  const failed = ["Failed", "Interrupted"].includes(job.status);
  const status = success ? "success" : failed ? "failed" : job.status === "Running" ? "running" : "queued";
  const icon = success ? dashboardCheck : failed ? "!" : job.status === "Running" ? "↻" : "◷";
  return `<div class="dashboard-job-state ${status} ${compact ? "compact" : ""}">
    <div class="dashboard-status-line"><span class="dashboard-status-icon" aria-hidden="true">${icon}</span><strong>${esc(dashboardStatus(job.status))}</strong>
      ${job.status === "Running" || success ? '<small class="dashboard-metric" data-dashboard-metric></small>' : ""}</div>
    ${job.status === "Running" || success ? '<progress class="dashboard-progress" max="100" value="0" aria-label="Прогресс задания" aria-hidden="true"></progress>' : ""}
    ${job.status === "Running" ? `<small class="dashboard-stage">${esc(job.stage)}</small>` : ""}
    ${success && job.readyAt ? `<small class="dashboard-stage">${esc(job.stage)}${job.backgroundStatus ? ` · ${esc(backgroundLabel(job.backgroundStatus))}` : ""}</small>` : ""}
    ${job.status === "Scheduled" ? `<small class="dashboard-stage">${esc(formatDate(job.scheduledAt))}</small>` : ""}
  </div>`;
}

function updateDashboardProgress(root, job, now) {
  const progress = root.querySelector(".dashboard-progress");
  if (!progress) return;
  const value = dashboardProgressValue(job, now);
  if (progress.value !== (value.percent ?? 0)) progress.value = value.percent ?? 0;
  const hidden = String(value.percent === null);
  if (progress.getAttribute("aria-hidden") !== hidden) progress.setAttribute("aria-hidden", hidden);
  const label = root.querySelector("[data-dashboard-metric]");
  if (label.textContent !== value.label) label.textContent = value.label;
}

function renderDashboardJobs() {
  const host = document.querySelector("#dashboard-jobs");
  if (!host || state.page !== "databases") return;
  const now = Date.now() + dashboard.serverOffset;
  const jobs = visibleDashboardJobs(dashboard.jobs, now, dashboard.visibleForMs);
  const running = jobs.filter(j => j.status === "Running" && !j.readyAt).length;
  const backgroundRunning = jobs.filter(j => j.backgroundStatus === "Running").length;
  const backgroundQueued = jobs.filter(j => j.backgroundStatus === "Queued").length;
  const ready = jobs.filter(j => j.status === "Running" && j.readyAt && !j.backgroundStatus).length;
  const queued = jobs.filter(j => j.status === "Queued").length;
  const scheduled = jobs.filter(j => j.status === "Scheduled").length;
  const completed = jobs.filter(j => j.status === "Succeeded").length;
  const failed = jobs.filter(j => j.status === "Failed" || j.status === "Interrupted").length;
  host.hidden = !jobs.length && !dashboard.error;
  const key = JSON.stringify([jobs.map(j => [j.id, j.kind, j.target, j.sourceKey, j.status, j.stage, j.scheduledAt, j.shrink, j.readyAt, j.backgroundStatus]), dashboard.error]);
  const changed = dashboard.renderedHost !== host || dashboard.renderedKey !== key;
  if (host.hidden && changed) host.replaceChildren();
  else if (!host.hidden && changed) {
    const status = running || backgroundRunning ? "running" : queued ? "queued" : failed || dashboard.error ? "failed" : scheduled ? "queued" : "success";
    const icon = status === "success" ? dashboardCheck : status === "failed" ? "!" : status === "queued" ? "◷" : "↻";
    const counts = [running ? `В работе: ${running}` : "", queued ? `В очереди: ${queued}` : "", scheduled ? `Запланировано: ${scheduled}` : "",
      backgroundRunning ? `Освобождение места: ${backgroundRunning}` : "", backgroundQueued ? `Ожидают освобождения места: ${backgroundQueued}` : "",
      ready ? `Базы готовы: ${ready}` : "",
      completed ? `Завершено: ${completed}` : "", failed ? `Ошибок: ${failed}` : ""].filter(Boolean).join(" · ");
    const rows = jobs.filter(j => j.status !== "Scheduled");
    host.innerHTML = `<aside class="dashboard-summary ${status}" aria-label="Состояние заданий">
      <div class="dashboard-summary-header"><div class="dashboard-status-line"><span class="dashboard-status-icon" aria-hidden="true">${icon}</span><strong>${esc(counts || "Состояние заданий временно недоступно")}</strong></div>
        <button type="button" class="text-button" data-page="jobs">Все задания →</button>
      </div>
      ${rows.length ? `<ul class="dashboard-job-list">${rows.map(job => `<li data-dashboard-job-id="${esc(job.id)}">
        <div class="dashboard-job-name"><strong>${esc(job.target || job.sourceKey)}</strong><small>${esc(dashboardTitle(job.kind))}${job.shrink ? " со свёрткой" : ""}</small></div>
        ${dashboardProgress(job)}
      </li>`).join("")}</ul>` : ""}
        ${dashboard.error ? '<small class="dashboard-summary-warning">Не удалось обновить состояние. Повторяем подключение…</small>' : ""}
    </aside>`;
  }
  dashboard.renderedHost = host;dashboard.renderedKey = key;
  const jobsById = new Map(jobs.map(job => [job.id, job]));
  for (const row of host.querySelectorAll("[data-dashboard-job-id]")) {
    const job = jobsById.get(row.dataset.dashboardJobId);
    if (job) updateDashboardProgress(row, job, now);
  }
  for (const element of document.querySelectorAll("[data-database-job]")) {
    const job = jobs.find(j => j.target?.toLowerCase() === element.dataset.databaseJob);
    const jobKey = JSON.stringify(job ? [job.id, job.status, job.stage, job.scheduledAt, job.readyAt, job.backgroundStatus] : []);
    if (element.dataset.jobKey !== jobKey) {
      element.innerHTML = job ? dashboardProgress(job, true) : "";
      element.dataset.jobKey = jobKey;
    }
    element.hidden = !job;
    if (job) updateDashboardProgress(element, job, now);
  }
}

function stopDashboardJobs(clear = false) {
  dashboard.generation++;
  clearTimeout(dashboard.timer);
  dashboard.controller?.abort();dashboard.controller = null;
  if (clear) { dashboard.jobs = [];dashboard.error = null;dashboard.user = null;dashboard.inventoryDone.clear();dashboard.renderedHost = null;dashboard.renderedKey = null; }
}

function startDashboardJobs() {
  stopDashboardJobs();
  if (state.page !== "databases" || !state.session?.user) return;
  const user = `${state.session.user.kind}:${state.session.user.name}`;
  if (dashboard.user !== user) { dashboard.jobs = [];dashboard.error = null;dashboard.inventoryDone.clear(); }
  dashboard.user = user;
  renderDashboardJobs();pollDashboardJobs(dashboard.generation);
}

function rememberDashboardJob(job) {
  if (state.page !== "databases") return;
  dashboard.jobs = dashboard.jobs.filter(j => j.id !== job.id).concat(job);
  renderDashboardJobs();startDashboardJobs();
}

async function refreshDashboardDatabaseStates(generation, signal) {
  try {
    const current = await api("/api/databases", { signal });
    if (generation !== dashboard.generation || state.page !== "databases" || !state.databases) return;
    const lookup = new Map((current.databases || []).map(row => [row.name.toLowerCase(), row]));
    for (const row of state.databases.databases || []) {
      const updated = lookup.get(row.name.toLowerCase());if (!updated) continue;
      const status = updated.state;row.state = status;row.sizeBytes = updated.sizeBytes ?? null;
      for (const badge of document.querySelectorAll("[data-database-state]")) {
        if (badge.dataset.databaseState !== row.name.toLowerCase()) continue;
        badge.textContent = status;badge.className = `badge ${status === "ONLINE" ? "" : "amber"}`;
      }
      for (const size of document.querySelectorAll("[data-database-size]")) {
        if (size.dataset.databaseSize === row.name.toLowerCase()) size.textContent = databaseSizeLabel(row);
      }
    }
  } catch (error) { /* Inventory may be temporarily unavailable while restore is running. */ }
}

async function pollDashboardJobs(generation) {
  if (generation !== dashboard.generation || state.page !== "databases" || !state.session?.user) return;
  const controller = new AbortController();dashboard.controller = controller;
  try {
    const result = await api("/api/jobs/dashboard", { signal: controller.signal });
    if (generation !== dashboard.generation || state.page !== "databases") return;
    const serverTime = Date.parse(result.serverTime);
    if (Number.isFinite(serverTime)) dashboard.serverOffset = serverTime - Date.now();
    dashboard.visibleForMs = Number(result.completedVisibleSeconds || 300) * 1000;
    dashboard.jobs = result.jobs;dashboard.error = null;
    const inventoryKey = j => j.id + (dashboardTerminal(j) ? ":finished" : ":ready");
    const finished = result.jobs.filter(j => j.kind === "refresh" && (j.readyAt || dashboardTerminal(j)) && !dashboard.inventoryDone.has(inventoryKey(j)));
    const ids = new Set(result.jobs.map(inventoryKey));
    dashboard.inventoryDone = new Set([...dashboard.inventoryDone].filter(id => ids.has(id)));
    renderDashboardJobs();
    if (finished.length) {
      await refreshDashboardDatabaseStates(generation, controller.signal);
      if (generation === dashboard.generation) for (const job of finished) dashboard.inventoryDone.add(inventoryKey(job));
    }
  } catch (error) {
    if (generation !== dashboard.generation || controller.signal.aborted) return;
    dashboard.error = error.message;renderDashboardJobs();
  } finally {
    if (generation === dashboard.generation && state.page === "databases" && state.session?.user)
      dashboard.timer = setTimeout(() => pollDashboardJobs(generation), !dashboard.error && dashboard.jobs.some(dashboardActive) ? 2000 : 5000);
  }
}
