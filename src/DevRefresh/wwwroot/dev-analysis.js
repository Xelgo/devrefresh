"use strict";

const devAnalysisPath = "/api/admin/reduction/dev-analyses";
const devAnalysis = { catalog: null, report: null, database: "", filter: "", top: 50, busy: false, error: "" };
const devAnalysisActive = () => state.page === "reduction" && flow.tab === "dev-analyses";

async function runDevAnalysisAction(action) {
  if (devAnalysis.busy) return;
  devAnalysis.busy = true;
  devAnalysis.error = "";
  if (devAnalysisActive()) renderDevAnalyses();
  try { await action(); }
  catch (error) { devAnalysis.error = error.message; }
  finally {
    devAnalysis.busy = false;
    if (devAnalysisActive()) renderDevAnalyses();
  }
}

async function loadDevAnalyses() {
  if (devAnalysis.busy && devAnalysisActive()) renderDevAnalyses();
  await runDevAnalysisAction(async () => {
    devAnalysis.catalog = await api(devAnalysisPath);
  });
}

function renderDevAnalyses() {
  const { catalog, report, database, busy, error } = devAnalysis;
  const databases = catalog?.databases ?? [];
  const reports = catalog?.reports ?? [];
  document.querySelector("#content").innerHTML = `${reductionHeader()}
    <section class="card"><div class="card-body">
      <form id="dev-analysis-form"><div class="analysis-controls">
        <div class="field"><label for="dev-analysis-database">Dev-база</label>
          <select id="dev-analysis-database" name="database" required ${busy || !databases.length ? "disabled" : ""}>
            <option value="">Выберите базу</option>
            ${databases.map(name => `<option value="${esc(name)}" ${name === database ? "selected" : ""}>${esc(name)}</option>`).join("")}
          </select>
        </div>
        <button type="submit" class="button primary" ${busy || !databases.length ? "disabled" : ""}>Анализировать Dev-базу</button>
      </div></form>
      ${busy ? '<p class="muted" role="status">Выполняется запрос…</p>' : ""}
      ${catalog?.databaseError ? `<p class="error-inline">${esc(catalog.databaseError)}</p>` : ""}
      ${catalog && !catalog.databaseError && !databases.length ? '<p class="muted">Доступных Dev-баз нет.</p>' : ""}
      ${error ? `<p class="error-inline" role="alert">${esc(error)}</p>` : ""}
    </div></section>
    <section class="card"><div class="card-header"><h2>Сохранённые анализы Dev-баз</h2></div>
      ${reports.length ? `<div class="table-wrap"><table><thead><tr><th>База</th><th>Дата анализа</th><th>Действия</th></tr></thead>
      <tbody>${reports.map(item => `<tr>
        <td class="mono">${esc(item.database)}</td><td>${formatDate(item.analyzedAt)}</td>
        <td><div class="actions">
          <button class="button" type="button" data-dev-analysis-open="${esc(item.id)}" ${busy ? "disabled" : ""}>Открыть</button>
          <button class="button" type="button" data-dev-analysis-remove="${esc(item.id)}" ${busy ? "disabled" : ""}
            aria-label="Удалить анализ ${esc(item.database)} за ${esc(formatDate(item.analyzedAt))}">Удалить</button>
        </div></td></tr>`).join("")}</tbody></table></div>` : '<div class="empty"><p>Сохранённых анализов пока нет.</p></div>'}
    </section>
    ${report ? devAnalysisReportMarkup(report) : ""}`;
  renderDevAnalysisRows();
}

function devAnalysisReportMarkup(report) {
  const files = report.fileSize;
  const used = bytes => bytes == null ? "" : ` · занято ${formatSize(bytes)}`;
  return `<section class="card" id="dev-analysis-report">
    <div class="card-header"><div>
      <h2 class="mono">${esc(report.database)}</h2>
      <p>${esc(report.configuration)} ${esc(report.configurationVersion)} · ${formatDate(report.analyzedAt)}</p>
    </div></div>
    <div class="card-body">
      <div class="detail-row"><span>Файлы данных</span><strong>${formatSize(files.dataBytes)}${used(files.dataUsedBytes)}</strong></div>
      <div class="detail-row"><span>Журнал транзакций</span><strong>${formatSize(files.logBytes)}${used(files.logUsedBytes)}</strong></div>
      ${files.recoveryModel ? `<div class="detail-row"><span>Модель восстановления</span><strong>${esc(files.recoveryModel)}</strong></div>` : ""}
      ${report.warnings.length ? `<details><summary>Предупреждения анализа · ${number(report.warnings.length)}</summary>
        ${report.warnings.slice(0, 30).map(warning => `<div>${esc(warning)}</div>`).join("")}</details>` : ""}
    </div>
    <div class="analysis-filter"><div class="actions">
      <input type="search" class="search" id="dev-analysis-search" aria-label="Поиск таблицы" placeholder="Объект 1С или SQL-таблица…" value="${esc(devAnalysis.filter)}">
      <select id="dev-analysis-top" aria-label="Число таблиц">${[20, 50, 100, 200].map(top => `<option value="${top}" ${top === devAnalysis.top ? "selected" : ""}>ТОП-${top}</option>`).join("")}</select>
    </div><span id="dev-analysis-count" class="muted" role="status"></span></div>
    <div class="table-wrap analysis-table"><table><thead><tr><th>Объект / SQL-таблица</th><th>Данные</th><th>Индексы</th><th>Занято</th><th>Зарезервировано</th><th>Строк ≈</th></tr></thead>
      <tbody id="dev-analysis-rows"></tbody></table></div></section>`;
}

function renderDevAnalysisRows() {
  const body = document.querySelector("#dev-analysis-rows");
  if (!body || !devAnalysis.report) return;
  const query = devAnalysis.filter.trim().toLowerCase();
  const tables = devAnalysis.report.tables.filter(table =>
    `${table.logicalName} ${table.schema}.${table.table}`.toLowerCase().includes(query));
  const shown = tables.slice(0, devAnalysis.top);
  document.querySelector("#dev-analysis-count").textContent = `Показано ${number(shown.length)} из ${number(tables.length)}`;
  body.innerHTML = shown.map(table => `<tr>
    <td class="object-name"><strong>${esc(table.logicalName)}</strong><small class="mono">${esc(table.schema)}.${esc(table.table)}</small><small>${esc(table.kind)}</small></td>
    <td>${mb(table.dataMB)}</td><td>${mb(table.indexMB)}</td><td><strong>${mb(table.usedMB)}</strong></td>
    <td>${mb(table.reservedMB)}</td><td>${number(table.rows)}</td></tr>`).join("")
    || '<tr><td colspan="6">Таблицы не найдены.</td></tr>';
}

function showDevAnalysis(report) {
  devAnalysis.report = report;
  devAnalysis.filter = "";
}

document.addEventListener("submit", async event => {
  const form = event.target;
  if (form.id !== "dev-analysis-form") return;
  event.preventDefault();
  const database = form.elements.database.value;
  if (!database) return;
  devAnalysis.database = database;
  await runDevAnalysisAction(async () => {
    const report = await api(devAnalysisPath, { method: "POST", body: JSON.stringify({ database }) });
    devAnalysis.catalog.reports.unshift({ id: report.id, database: report.database, analyzedAt: report.analyzedAt });
    showDevAnalysis(report);
  });
  if (devAnalysisActive() && !devAnalysis.error) document.querySelector("#dev-analysis-report")?.scrollIntoView({ block: "start" });
});

document.addEventListener("click", async event => {
  const button = event.target.closest("button");
  if (!button) return;
  if (button.dataset.devAnalysisOpen) {
    const id = button.dataset.devAnalysisOpen;
    await runDevAnalysisAction(async () => showDevAnalysis(await api(`${devAnalysisPath}/${encodeURIComponent(id)}`)));
    if (devAnalysisActive() && !devAnalysis.error) document.querySelector("#dev-analysis-report")?.scrollIntoView({ block: "start" });
  }
  if (button.dataset.devAnalysisRemove) {
    const id = button.dataset.devAnalysisRemove;
    const report = devAnalysis.catalog.reports.find(item => item.id === id);
    if (!report || !window.confirm(`Удалить отчёт по базе ${report.database} за ${formatDate(report.analyzedAt)}?`)) return;
    await runDevAnalysisAction(async () => {
      await api(`${devAnalysisPath}/${encodeURIComponent(id)}`, { method: "DELETE" });
      devAnalysis.catalog.reports = devAnalysis.catalog.reports.filter(item => item.id !== id);
      if (devAnalysis.report?.id === id) devAnalysis.report = null;
    });
  }
});

document.addEventListener("change", event => {
  if (event.target.id === "dev-analysis-database") devAnalysis.database = event.target.value;
  if (event.target.id === "dev-analysis-top") {
    devAnalysis.top = Number(event.target.value);
    renderDevAnalysisRows();
  }
});
document.addEventListener("input", event => {
  if (event.target.id === "dev-analysis-search") {
    devAnalysis.filter = event.target.value;
    renderDevAnalysisRows();
  }
});
