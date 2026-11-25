"use strict";

const flow = { catalog: null, tab: "analysis", analysis: null, selection: new Map(), profileTarget: "", filter: "", hideSystem: true, top: 20, source: "", backupName: "", analysisJob: null, refresh: null, watching: new Set() };
const jobFinished = job => ["Succeeded", "Failed", "Interrupted"].includes(job.status);
const jobStatus = { Scheduled: "Запланировано", Queued: "В очереди", Running: "Выполняется", Succeeded: "Завершено", Failed: "Ошибка", Interrupted: "Прервано" };
const jobStateLabel = job => databaseReadyWhileRunning(job) ? "База готова к работе" : jobStatus[job.status] || job.status;
const jobTitle = kind => ({ analysis: "Анализ бэкапа", preview: "Проверка плана", refresh: "Восстановление базы", "shrink-build": "Подготовка Shrink-копии" }[kind] || kind);
const dateDefault = () => `${new Date().getFullYear()}-01-01`;
const mb = value => `${Number(value).toLocaleString("ru-RU", { maximumFractionDigits: 1 })} МБ`;
const number = value => Number(value ?? 0).toLocaleString("ru-RU");
const ruleDateLabel = table => table.dateColumn ? (table.dateParentKey ? "Дата владельца" : table.kind === "Документ" ? "Дата документа" : "Период") : "Без периода";

function selectAnalysisBackup(name) {
  const backup = flow.catalog?.backups.find(b => b.name === name);
  flow.backupName = backup?.name || "";
  flow.source = backup?.sourceKey || "";
}

function profileTargets(source) {
  const aliases = flow.catalog.backupAliases || {};
  const targets = Object.keys(aliases).filter(target => aliases[target] === source).sort();
  if (!Object.hasOwn(aliases, source)) {
    if (!targets.length || flow.catalog.devTypes?.includes(source)) targets.unshift(source);
    else targets.push(source);
  }
  return targets;
}

function selectProfileTarget(target) {
  flow.profileTarget = target;
  const previous = flow.catalog.profiles.find(p => p.sourceKey === target);
  flow.selection = new Map((previous?.rules || [])
    .filter(rule => flow.analysis.tables.some(t => t.key === rule.key && t.canSelect))
    .map(rule => [rule.key, { ...rule }]));
}

function backupAliasesMarkup(catalog) {
  const entries = Object.entries(catalog.backupAliases || {}).sort(([a], [b]) => a.localeCompare(b));
  const typeOptions = types => [...new Set(types)].sort().map(type => `<option value="${esc(type)}"></option>`).join("");
  const rows = entries.map(([target, source]) => {
    const backup = catalog.backups.find(file => file.sourceKey === source);
    return `<tr><td class="mono">${esc(target)}</td><td class="mono">${esc(source)}</td>
      <td>${backup ? esc(backup.name) : '<span class="muted">Файл не найден</span>'}</td>
      <td><div class="actions"><button type="button" class="button" data-alias-edit="${esc(target)}">Изменить</button>
      <button type="button" class="button" data-alias-remove="${esc(target)}" aria-label="Удалить соответствие ${esc(target)}">Удалить</button></div></td></tr>`;
  }).join("");
  return `<section class="card"><div class="card-header"><div><h2>Соответствия бэкапов</h2><p>Например, правило ut_ke → ut позволяет обновлять ut_ke_dev_demo_owner из ut_11-11-2026.bak.</p></div></div>
    <div class="card-body"><form id="backup-alias-form"><div class="analysis-controls">
      <div class="field"><label for="alias-target">Тип Dev-базы</label><input id="alias-target" name="target" list="alias-target-types" placeholder="ut_ke" maxlength="64" required>
      <datalist id="alias-target-types">${typeOptions([...(catalog.devTypes || []), ...entries.map(([target]) => target)])}</datalist></div>
      <div class="field"><label for="alias-source">Тип бэкапа</label><input id="alias-source" name="source" list="alias-source-types" placeholder="ut" maxlength="64" required>
      <datalist id="alias-source-types">${typeOptions([...catalog.backups.map(file => file.sourceKey), ...entries.map(([, source]) => source)])}</datalist></div>
      <button class="button primary" type="submit">Сохранить соответствие</button>
    </div><div class="form-result" role="status"></div></form></div>
    ${catalog.backupError ? `<div class="card-body error-inline">${esc(catalog.backupError)}</div>` : ""}
    ${rows ? `<div class="table-wrap"><table><thead><tr><th>Тип Dev-базы</th><th>Тип бэкапа</th><th>Копия для актуализации</th><th>Действия</th></tr></thead><tbody>${rows}</tbody></table></div>`
      : '<div class="empty"><p>Соответствия пока не настроены. Бэкапы подбираются по имени базы.</p></div>'}</section>`;
}

async function loadReduction() {
  if (flow.tab === "dev-analyses") return loadDevAnalyses();
  document.querySelector("#content").innerHTML = reductionHeader() + '<div class="loading">Загружаем типы баз и бэкапы…</div>';
  try {
    flow.catalog = await api("/api/admin/reduction/catalog");
    const pending = flow.catalog.analyses.find(j => !jobFinished(j));
    if (pending) flow.analysisJob = pending;
    if (state.page === "reduction" && flow.tab !== "dev-analyses") reductionPage();
    if (pending) pollAnalysis(pending.id);
  } catch (error) {
    if (state.page === "reduction" && flow.tab !== "dev-analyses")
      document.querySelector("#content").innerHTML = reductionHeader() + `<div class="notice error">${esc(error.message)}</div>`;
  }
}

const shrinkWeekdays = [[1, "Пн"], [2, "Вт"], [3, "Ср"], [4, "Чт"], [5, "Пт"], [6, "Сб"], [0, "Вс"]];
const shrinkDataDate = value => value.split("-").reverse().join(".");

function shrinkBuildMarkup(job) {
  return job ? `<span class="badge">${esc(jobStatus[job.status] || job.status)}</span>
    <button class="button" type="button" data-job-id="${esc(job.id)}">Отчёт</button>` : "";
}

function shrinkSchedulesMarkup(catalog) {
  const rows = catalog.devTypes.map(source => {
    const schedule = catalog.shrinkSchedules.find(s => s.sourceKey === source);
    const ready = catalog.shrinkBackups.find(b => b.sourceKey === source);
    const job = catalog.shrinkBuilds.find(j => j.sourceKey === source);
    const hasProfile = catalog.profiles.some(p => p.sourceKey === source);
    const details = ready ? `Данные на ${esc(shrinkDataDate(ready.dataDate))} · ${esc(formatSize(ready.size))}<small>Собрана ${esc(formatDate(ready.preparedAt))}</small>` : "Копии пока нет";
    return `<section class="card"><div class="card-header"><div><h2 class="mono">${esc(source)}</h2><p>${details}</p></div>
      <div class="actions">
        <span class="actions" data-shrink-build-status>${shrinkBuildMarkup(job)}</span>
        <button class="button" type="button" data-shrink-run="${esc(source)}" ${hasProfile && catalog.shrinkDirectoryConfigured ? "" : "disabled"}>Выполнить сейчас</button>
      </div>
      </div><div class="card-body">
      ${!hasProfile ? '<p class="muted">Для запуска сохраните профиль сокращения этого типа.</p>' : ""}
      <form class="shrink-schedule-form" data-source="${esc(source)}">
        <div class="analysis-controls">
          <label class="checkbox"><input type="checkbox" name="enabled" ${schedule?.enabled ? "checked" : ""}> Включить</label>
          <div class="actions" role="group" aria-label="Дни недели">${shrinkWeekdays.map(([day, label]) =>
            `<label class="checkbox"><input type="checkbox" name="days" value="${day}" ${schedule?.days.includes(day) ? "checked" : ""}> ${label}</label>`).join("")}</div>
          <div class="field"><label for="shrink-time-${esc(source)}">Время запуска</label><input id="shrink-time-${esc(source)}" name="time" type="time" value="${esc(schedule?.time || "02:00")}" required></div>
          <button class="button primary" type="submit">Сохранить</button>
        </div><div class="form-result" role="status"></div>
      </form></div></section>`;
  }).join("");
  return `<p class="subtitle">Время сервера — ${esc(catalog.scheduleTimeZone)}.</p>
    ${catalog.shrinkDirectoryConfigured ? "" : '<div class="notice">Укажите каталог готовых Shrink-копий в настройках.</div>'}
    ${rows || '<div class="empty"><p>Типы Dev-баз пока не найдены.</p></div>'}`;
}

function reductionHeader() {
  return `${heading("Администрирование", "Анализ и сокращение", "")}
    <nav class="actions reduction-tabs" aria-label="Разделы анализа">
      <button type="button" class="button ${flow.tab === "analysis" ? "primary" : ""}" aria-pressed="${flow.tab === "analysis"}" data-reduction-tab="analysis">Анализ таблиц</button>
      <button type="button" class="button ${flow.tab === "aliases" ? "primary" : ""}" aria-pressed="${flow.tab === "aliases"}" data-reduction-tab="aliases">Соответствия бэкапов</button>
      <button type="button" class="button ${flow.tab === "shrink" ? "primary" : ""}" aria-pressed="${flow.tab === "shrink"}" data-reduction-tab="shrink">Расписание Shrink</button>
      <button type="button" class="button ${flow.tab === "dev-analyses" ? "primary" : ""}" aria-pressed="${flow.tab === "dev-analyses"}" data-reduction-tab="dev-analyses">Анализ Dev-баз</button>
    </nav>`;
}

function reductionPage() {
  if (flow.tab === "dev-analyses") return renderDevAnalyses();
  const catalog = flow.catalog;
  if (!catalog) return;
  const header = reductionHeader();
  const content = document.querySelector("#content");
  if (flow.tab === "shrink") {
    content.innerHTML = header + shrinkSchedulesMarkup(catalog);
    return;
  }
  if (flow.tab === "aliases") {
    content.innerHTML = header + backupAliasesMarkup(catalog);
    return;
  }
  const analysis = flow.analysis;
  const completed = catalog.analyses.filter(j => j.status === "Succeeded");
  selectAnalysisBackup(catalog.backups.some(b => b.name === flow.backupName) ? flow.backupName : catalog.backups[0]?.name);
  content.innerHTML = `${header}
    <section class="card"><div class="card-body"><form id="analysis-form">
      <div class="analysis-controls">
        <div class="field"><label for="analysis-backup">Резервный файл</label><select id="analysis-backup" name="backupName" required>${catalog.backups.map(b => `<option value="${esc(b.name)}" ${b.name === flow.backupName ? "selected" : ""}>${esc(b.name)} · ${formatSize(b.size)}</option>`).join("")}</select></div>
        <div class="field"><label for="analysis-source">Тип бэкапа · из имени файла</label><input id="analysis-source" value="${esc(flow.source)}" readonly placeholder="Выберите .bak"><small>Например, bp_mko_11-11-2026.bak → bp_mko</small></div>
        <button class="button primary" type="submit" ${!catalog.backups.length ? "disabled" : ""}>Анализировать бэкап</button>
      </div><div class="form-result" role="status"></div>
    </form>
    ${completed.length ? `<div class="analysis-controls saved-analysis"><div class="field"><label for="analysis-existing">Завершённые анализы</label><select id="analysis-existing">${completed.map(j => `<option value="${j.id}" ${analysis?.id === j.id ? "selected" : ""}>${esc(j.sourceKey)} · ${formatDate(j.createdAt)} · ${esc(j.result?.configuration || "")}</option>`).join("")}</select></div><button class="button" data-action="open-analysis">Открыть анализ</button></div>` : ""}
    ${catalog.backupError ? `<p class="error-inline">${esc(catalog.backupError)}</p>` : ""}
    ${flow.analysisJob ? `<div class="notice neutral analysis-progress"><p><strong>${esc(jobStatus[flow.analysisJob.status])}</strong> · ${esc(flow.analysisJob.stage)}${flow.analysisJob.error ? `<br>${esc(flow.analysisJob.error)}` : ""}</p></div>` : ""}
    </div></section>
    ${analysis ? analysisMarkup(analysis) : `<section class="card"><div class="empty"><div class="empty-icon">▥</div><h3>Сначала проанализируйте копию</h3><p>Бэкап восстановится в закрытую служебную SQL-базу. После анализа временная база удаляется. Результаты доступны из сохранённого отчёта. Исходный файл и базы разработчиков при анализе не изменяются.</p></div></section>`}`;
  if (analysis) renderAnalysisRows();
}

function analysisMarkup(analysis) {
  const targets = profileTargets(analysis.sourceKey);
  if (!targets.includes(flow.profileTarget)) selectProfileTarget(targets[0] || "");
  return `<div class="stats">
    <div class="stat"><div class="stat-label">Конфигурация</div><div class="stat-value compact">${esc(analysis.configuration)}</div><div class="stat-note">${esc(analysis.configurationVersion)} · тип ${esc(analysis.sourceKey)}</div></div>
    <div class="stat"><div class="stat-label">Таблицы / занято</div><div class="stat-value">${number(analysis.tables.length)}</div><div class="stat-note">${mb(analysis.tables.reduce((sum, t) => sum + t.usedMB, 0))} · данные и индексы</div></div>
    <div class="stat"><div class="stat-label">Выбрано для сокращения</div><div id="selected-rule-count" class="stat-value">${flow.selection.size}</div><div class="stat-note">Настройки сохраняются для типа базы</div></div>
  </div>
  ${analysis.warnings.length ? `<div class="notice"><p>При разборе структуры есть предупреждения: ${analysis.warnings.length}. Объекты с неполной картой недоступны для очистки.<details><summary>Показать предупреждения</summary>${analysis.warnings.slice(0, 30).map(w => `<div>${esc(w)}</div>`).join("")}</details></p></div>` : ""}
  <section class="card"><div class="card-header"><div><h2>ТОП тяжёлых таблиц</h2><p>${esc(analysis.backupName)} · анализ ${formatDate(analysis.analyzedAt)}. Счётчики строк приблизительные.</p></div><div class="actions"><input id="reduction-search" class="search" placeholder="Объект 1С или SQL-таблица…" aria-label="Поиск таблицы" value="${esc(flow.filter)}"><select id="reduction-top" aria-label="Размер списка">${[20, 50, 100, 200].map(n => `<option value="${n}" ${flow.top === n ? "selected" : ""}>ТОП-${n}</option>`).join("")}</select></div></div>
    <div class="card-body analysis-controls"><div class="field"><label for="profile-target">Тип Dev-базы для профиля</label>
      <select id="profile-target" ${targets.length ? "" : "disabled"}>${targets.length ? targets.map(target => `<option value="${esc(target)}" ${target === flow.profileTarget ? "selected" : ""}>${esc(target)}</option>`).join("") : '<option>Нет подходящих типов Dev-баз</option>'}</select>
      <small>${targets.length ? "При смене типа загружаются его сохранённые правила." : "Задайте соответствие для этого бэкапа."}</small></div></div>
    <div class="analysis-filter"><label class="checkbox" title="Скрывать все таблицы, недоступные для самостоятельного выбора и очистки"><input id="reduction-hide-system" type="checkbox" ${flow.hideSystem ? "checked" : ""}><span>Скрывать служебные таблицы</span></label><span id="analysis-row-count" class="muted" role="status"></span></div>
    <div class="table-wrap analysis-table"><table><thead><tr><th>Выбор</th><th>Объект / SQL-таблица</th><th>Данные</th><th>Индексы</th><th>Всего</th><th>Строк</th><th>Удалить раньше</th></tr></thead><tbody id="analysis-rows"></tbody></table></div>
    <div class="card-body"><div class="notice neutral"><p>Без периода выбранная таблица очищается полностью. Для регистра и его итогов нужна галочка на самом регистре. Чтобы очистить «ВерсииОбъектов», включите регистр в профиль Shrink.</p></div>
    <div class="actions"><button class="button primary" data-action="save-reduction" ${flow.profileTarget ? "" : "disabled"}>Сохранить профиль${flow.profileTarget ? " для " + esc(flow.profileTarget) : ""}</button><button class="button" data-action="preview-reduction">Проверить план без удаления</button><span id="profile-save-result" role="status" class="muted"></span></div></div>
  </section>`;
}

function renderAnalysisRows() {
  if (!flow.analysis || !document.querySelector("#analysis-rows")) return;
  const query = flow.filter.toLowerCase();
  const available = flow.analysis.tables.filter(t => !flow.hideSystem || t.canSelect);
  const matched = available.filter(t => `${t.logicalName} ${t.table}`.toLowerCase().includes(query));
  const rows = matched.slice(0, flow.top);
  document.querySelector("#analysis-row-count").textContent = `Показано ${number(rows.length)} из ${number(matched.length)}${flow.hideSystem ? ` · скрыто недоступных: ${number(flow.analysis.tables.length - available.length)}` : ""}`;
  document.querySelector("#analysis-rows").innerHTML = rows.map(t => {
    const chosen = flow.selection.has(t.key);
    return `<tr>
      <td><input type="checkbox" data-rule-key="${esc(t.key)}" aria-label="Выбрать ${esc(t.logicalName)}" ${chosen ? "checked" : ""} ${!t.canSelect ? "disabled" : ""}></td>
      <td class="object-name"><strong>${esc(t.logicalName)}</strong><small class="mono">${esc(t.schema)}.${esc(t.table)}</small>${!t.canSelect ? `<small>${esc(t.reason)}</small>` : `<small>${esc(t.kind)} · ${ruleDateLabel(t)}</small>`}</td>
      <td>${mb(t.dataMB)}</td><td>${mb(t.indexMB)}</td><td><strong>${mb(t.usedMB)}</strong></td><td>${number(t.rows)}</td>
      <td>${t.canSelect && t.dateColumn ? `<input type="date" data-rule-date="${esc(t.key)}" value="${esc(flow.selection.get(t.key)?.before || dateDefault())}" min="1900-01-01" ${!chosen ? "disabled" : ""} aria-label="Дата отсечения ${esc(t.logicalName)}"><small>${ruleDateLabel(t)}</small>` : t.canSelect ? '<span class="badge amber">Очистить полностью</span>' : '<span class="muted">—</span>'}</td>
    </tr>`;
  }).join("") || '<tr><td colspan="7">Таблицы не найдены.</td></tr>';
  document.querySelector("#selected-rule-count").textContent = flow.selection.size;
}

async function openAnalysis(id) {
  flow.analysis = await api(`/api/admin/reduction/analyses/${encodeURIComponent(id)}`);
  flow.source = flow.analysis.sourceKey;
  flow.backupName = flow.analysis.backupName;
  const targets = profileTargets(flow.source);
  selectProfileTarget(targets.includes(flow.profileTarget) ? flow.profileTarget : targets[0] || "");
  flow.filter = "";
  reductionPage();
}

async function pollAnalysis(id, continuing = false) {
  if (!continuing && flow.watching.has(id)) return;
  flow.watching.add(id);
  try {
    const job = await api(`/api/jobs/${id}`);
    flow.analysisJob = job;
    if (state.page === "reduction" && flow.tab === "analysis") {
      const old = document.querySelector(".analysis-progress");
      if (old) old.innerHTML = `<p><strong>${esc(jobStatus[job.status])}</strong> · ${esc(job.stage)}${job.error ? `<br>${esc(job.error)}` : ""}</p>`;
    }
    if (!jobFinished(job)) return setTimeout(() => pollAnalysis(id, true), 1500);
    flow.watching.delete(id);
    if (job.status === "Succeeded") {
      if (state.page === "reduction" && flow.tab === "analysis") {
        const catalog = await api("/api/admin/reduction/catalog");
        if (state.page === "reduction" && flow.tab === "analysis") {
          flow.catalog = catalog;
          await openAnalysis(id);
        }
      }
      toast("Анализ бэкапа завершён.");
    } else { if (state.page === "reduction" && flow.tab === "analysis") reductionPage(); toast(job.error || "Анализ прерван."); }
  } catch (error) { flow.watching.delete(id);toast(error.message); }
}

async function loadJobs() {
  try {
    const jobs = await api("/api/jobs");
    if (state.page !== "jobs") return;
    document.querySelector("#content").innerHTML = `${heading("Рабочее пространство", "Задания", "Анализ, подготовка копий и восстановление выполняются в фоне.")}
      <section class="card"><div class="table-wrap"><table><thead><tr><th>Время / операция</th><th>База / тип</th><th>Состояние</th><th>Этап</th><th></th></tr></thead><tbody>${jobs.map(j => `<tr><td>${formatDate(j.createdAt)}<small>${esc(jobTitle(j.kind))}</small></td><td><strong class="mono">${esc(j.target || "Служебная копия")}</strong><small>${esc(j.sourceKey)}</small></td><td><span class="badge ${databaseReadyWhileRunning(j) || j.status === "Succeeded" ? "" : j.status === "Failed" || j.status === "Interrupted" ? "amber" : "gray"}">${esc(jobStateLabel(j))}</span></td><td>${esc(j.stage)}${jobFinished(j) && j.backgroundStatus ? `<small>${esc(backgroundLabel(j.backgroundStatus))}</small>` : ""}${j.scheduledAt ? `<small>Запуск не раньше ${esc(formatDate(j.scheduledAt))}</small>` : ""}${j.error ? `<small class="error-inline">${esc(j.error)}</small>` : ""}</td><td><button class="button" data-job-id="${j.id}">Отчёт</button></td></tr>`).join("")}</tbody></table></div>${!jobs.length ? '<div class="empty">Заданий пока нет.</div>' : ""}</section>`;
    clearTimeout(loadJobs.timer);
    if (jobs.some(j => !jobFinished(j))) loadJobs.timer = setTimeout(() => { if (state.page === "jobs") loadJobs(); }, 2000);
  } catch (error) { toast(error.message); }
}

function countsMarkup(result) {
  return `<p>Строк к удалению / удалено: <strong>${number(result.totalRows)}</strong></p>
    <div class="table-wrap report-table"><table><thead><tr><th>Объект</th><th>Таблица</th><th>Строк</th><th>Причина</th></tr></thead><tbody>${(result.deleted || []).map(row => `<tr><td>${esc(row.object)}</td><td class="mono">${esc(row.table)}</td><td>${number(row.rows)}</td><td>${esc(row.reason)}</td></tr>`).join("")}</tbody></table></div>
    ${(result.notes || []).map(n => `<div class="notice"><p>${esc(n)}</p></div>`).join("")}`;
}

function jobDuration(ms) {
  if (ms < 1000) return ms === 0 ? "< 1 мс" : `${ms} мс`;
  const seconds = Math.floor(ms / 1000);
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor(seconds % 3600 / 60);
  return [hours ? `${hours} ч` : "", minutes ? `${minutes} мин` : "", `${seconds % 60} с`].filter(Boolean).join(" ");
}

function jobTimingMarkup(job) {
  const report = job.executionReport;
  if (!job.startedAt) return jobFinished(job) ? '<p class="muted">Хронометраж этого задания не записан.</p>' : "";
  const time = new Intl.DateTimeFormat("ru-RU", { dateStyle: "short", timeStyle: "medium" });
  return `<section class="job-report-section"><h3>Время выполнения</h3>
    <div class="detail-row"><span>Начало</span><strong>${esc(time.format(new Date(job.startedAt)))}</strong></div>
    ${report ? `<div class="detail-row"><span>Завершение</span><strong>${esc(time.format(new Date(job.finishedAt)))}</strong></div>
      <div class="detail-row"><span>Общая длительность</span><strong>${esc(jobDuration(report.durationMs))}</strong></div>
      <div class="table-wrap report-table"><table><thead><tr><th>Этап</th><th>Длительность</th></tr></thead>
      <tbody>${report.stages.map(stage => `<tr><td><strong>${esc(stage.name)}</strong>
        ${stage.tables.length ? `<details class="job-table-timings"><summary>По таблицам · ${number(stage.tables.length)}</summary>
          <div class="table-wrap"><table><thead><tr><th>Объект / таблица</th><th>Операция</th><th>Время</th></tr></thead>
          <tbody>${stage.tables.map(row => `<tr><td>${esc(row.object)}<small class="mono">${esc(row.table)}</small></td><td>${esc(row.operation)}</td><td>${esc(jobDuration(row.durationMs))}</td></tr>`).join("")}</tbody></table></div>
        </details>` : ""}</td><td>${esc(jobDuration(stage.durationMs))}</td></tr>`).join("")}</tbody></table></div>` : ""}
  </section>`;
}

function jobFileSizesMarkup(report, backgroundStatus) {
  if (!report) return "";
  const sizes = report.fileSizes;
  const optimization = report.fileOptimization;
  const size = value => value == null ? "—" : formatSize(value);
  const after = sizes.find(row => row.stage === "После освобождения места");
  const tolerance = 64 * 1024 ** 2;
  return `<section class="job-report-section"><h3>Файлы базы и освобождение места</h3>
    ${optimization ? `<p>Цель для файлов данных — ${esc(size(optimization.dataTargetBytes))}, для журнала — ${esc(size(optimization.logTargetBytes))}.</p>`
      : backgroundStatus ? "" : '<p class="muted">Цель по размеру файлов не рассчитана.</p>'}
    ${sizes.length ? `<div class="table-wrap report-table"><table><thead><tr><th>Момент замера</th><th>Файлы данных</th><th>Занято в файлах данных</th><th>Свободно в файлах данных</th><th>Журнал</th><th>Занято журналом</th><th>Всего на диске</th></tr></thead>
      <tbody>${sizes.map(row => `<tr><td>${esc(row.stage)}${row.recoveryModel ? `<small>Модель восстановления · ${esc(row.recoveryModel)}</small>` : ""}</td><td>${esc(size(row.dataBytes))}</td><td>${esc(size(row.dataUsedBytes))}</td>
        <td>${esc(size(row.dataUsedBytes == null ? null : Math.max(0, row.dataBytes - row.dataUsedBytes)))}</td>
        <td>${esc(size(row.logBytes))}${row.logReuseWait ? `<small class="mono">${esc(row.logReuseWait)}</small>` : ""}</td>
        <td>${esc(size(row.logUsedBytes))}</td><td><strong>${esc(size(row.dataBytes + row.logBytes))}</strong></td></tr>`).join("")}</tbody></table></div>`
      : '<p class="muted">Замеры размеров файлов недоступны.</p>'}
    ${optimization && after && optimization.dataTargetBytes != null && after.dataBytes > optimization.dataTargetBytes + tolerance
      ? '<p class="notice neutral">Файлы данных остались больше расчётной цели.</p>' : ""}
    ${optimization && after && after.logBytes > optimization.logTargetBytes + tolerance
      ? `<p class="notice neutral">Файлы журнала остались больше расчётной цели${after.logReuseWait ? ` · ${esc(after.logReuseWait)}` : ""}.</p>` : ""}
  </section>`;
}

function binaryDataCleanupMarkup(report) {
  if (!report) return "";
  return `<section class="job-report-section"><h3>Очистка BinaryData</h3>
    <div class="detail-row"><span>Ключи из полностью очищенных таблиц</span><strong>${number(report.candidateKeys)}</strong></div>
    <div class="detail-row"><span>Ключи с оставшимися ссылками</span><strong>${number(report.retainedKeys)}</strong></div>
    <div class="detail-row"><span>Удалено блоков</span><strong>${number(report.deletedBlocks)} · ${esc(formatSize(report.payloadBytes))}</strong></div>
  </section>`;
}

async function openJob(id) {
  const job = await api(`/api/jobs/${id}`);
  const dialog = document.querySelector("#job-dialog");
  dialog.classList.toggle("wide", Boolean(job.executionReport));
  const sqlCheck = { passed: "пройдена", skipped: "отключена в настройках" }[job.result?.sqlCheck] ?? "";
  dialog.innerHTML = `<h2>${esc(jobTitle(job.kind))}</h2><p><strong>${esc(jobStateLabel(job))}</strong>${job.stage ? ` · ${esc(job.stage)}` : ""}</p><p class="mono">${esc(job.target || job.sourceKey)} · ${esc(job.id)}</p>
    ${job.sourceDatabase ? `<div class="detail-row"><span>База-источник</span><strong class="mono">${esc(job.sourceDatabase)}</strong></div>` : ""}
    ${job.scheduledAt ? `<p>Запуск не раньше ${esc(formatDate(job.scheduledAt))} по времени браузера.</p>` : ""}
    ${job.error ? `<div class="notice error"><p>${esc(job.error)}</p></div>` : ""}
    ${job.readyAt ? `<div class="detail-row"><span>Доступ открыт</span><strong>${esc(formatDate(job.readyAt))}</strong></div>` : ""}
    ${job.backgroundStatus ? `<div class="detail-row"><span>${esc(backgroundLabel(job.backgroundStatus))}</span>${job.backgroundError ? `<small>${esc(job.backgroundError)}</small>` : ""}</div>` : ""}
    ${jobTimingMarkup(job)}
    ${binaryDataCleanupMarkup(job.executionReport?.binaryDataCleanup)}
    ${["refresh", "shrink-build"].includes(job.kind) ? jobFileSizesMarkup(job.executionReport,job.backgroundStatus) : ""}
    ${job.result ? job.kind === "analysis" ? `<p>${esc(job.result.configuration)} ${esc(job.result.configurationVersion)} · ${number(job.result.tableCount)} таблиц · ${mb(job.result.usedMB)}</p><p>Временная база удалена. Результаты анализа сохранены.</p>` : job.kind === "preview" ? `<p>План проверен. Временная база удалена.</p>${countsMarkup(job.result)}` : job.result.shrinkBackup ? `<p>Готовый архив ${esc(job.result.shrinkBackup.name)} · ${esc(formatSize(job.result.shrinkBackup.size))}. Данные на ${esc(shrinkDataDate(job.result.shrinkBackup.dataDate))}.</p>` : job.result.restoreMode === "dt" ? `<p>База восстановлена из ${esc(job.result.backup)} через IBCMD ${esc(job.result.ibcmdVersion)}.${sqlCheck ? ` Проверка SQL-базы ${esc(sqlCheck)}.` : ""}</p>` : job.result.restoreMode === "prepared-shrink" ? `<p>Восстановлена готовая Shrink-копия. Данные на ${esc(shrinkDataDate(job.result.shrinkDataDate))}.</p>` : `<p>${job.sourceDatabase ? "База обновлена копией другой Dev-базы." : "Бэкап восстановлен непосредственно в целевую базу."} ПД не обрабатывались. Сокращение: ${job.result.shrink ? "да" : "нет"}.${sqlCheck ? ` Проверка SQL-базы ${esc(sqlCheck)}.` : ""}</p>${countsMarkup(job.result)}` : ""}
    ${postRestoreResultsMarkup(job.result?.postRestoreOperations)}
    ${ibcmdCleanupMarkup(job.result)}
    <div class="dialog-actions"><button class="button" data-close="job-dialog">Закрыть</button></div>`;
  dialog.showModal();
}

function postRestoreResultsMarkup(operations) {
  if (!operations?.length) return "";
  return `<h3>Операции после восстановления</h3>${operations.map(operation=>`<p>Подмена домена → ${esc(operation.targetDomain)}: записано пользователей ${number(operation.changed)}.</p>`).join("")}`;
}

function ibcmdCleanupMarkup(result) {
  if (result?.restoreMode !== "dt" || typeof result.ibcmdWorkingDirectoryCleaned !== "boolean") return "";
  return result.ibcmdWorkingDirectoryCleaned
    ? `<p>Временные файлы IBCMD удалены.</p>`
    : `<div class="notice"><p>База восстановлена, но временные файлы IBCMD удалить не удалось. Подробности — в журнале сервера.</p></div>`;
}

async function openRefreshDialog(row) {
  const dialog = document.querySelector("#database-dialog");
  dialog.classList.add("wide");
  dialog.innerHTML = `<h2 id="dialog-title">${esc(row.name)}</h2><p>Проверяем параметры актуализации…</p>`;
  dialog.showModal();
  try {
    const options = await api(`/api/refresh/options?database=${encodeURIComponent(row.name)}`);
    const sources = options.copySources || [];
    const shrink = options.shrinkBackup;
    const defaultSource = preferredRefreshSource(options);
    const dt = options.dtBackup;
    const archiveDate = options.backupDate ? shrinkDataDate(options.backupDate)
      : options.backupModifiedAt ? `файл изменён ${formatDate(options.backupModifiedAt)}` : "дата недоступна";
    flow.refresh = { row, options };
    dialog.innerHTML = `<form id="refresh-form"><div class="eyebrow">Актуализация базы</div><h2 id="dialog-title" class="mono">${esc(row.name)}</h2>
      <div class="field"><label for="refresh-source-mode">Источник актуализации</label><select id="refresh-source-mode" name="sourceMode" required>
        <option value="shrink" ${defaultSource === "shrink" ? "selected" : shrink ? "" : "disabled"}>Shrink-копия${shrink ? ` / ${esc(shrinkDataDate(shrink.dataDate))} / ${esc(refreshArchiveSize(shrink.size))}` : " / недоступна"}</option>
        <option value="archive" ${defaultSource === "archive" ? "selected" : ""}>Full-копия${options.backupName ? ` / ${esc(archiveDate)} / ${esc(refreshArchiveSize(options.backupSize))}` : " / недоступна"}</option>
        <option value="database" ${defaultSource === "database" ? "selected" : sources.length && options.copyBackupConfigured ? "" : "disabled"}>Другая Dev-база${!options.copyBackupConfigured ? " / укажите каталог временных .bak" : sources.length ? "" : " / нет доступных баз"}</option>
        <option value="dt" ${defaultSource === "dt" ? "selected" : dt ? "" : "disabled"}>.dt через IBCMD${dt ? ` / ${esc(dt.backupDate ? shrinkDataDate(dt.backupDate) : formatDate(dt.modifiedAt))} / ${esc(refreshArchiveSize(dt.size))}` : " / недоступен"}</option>
      </select></div>
      <div id="refresh-database-source" class="field" hidden><label for="refresh-source-database">База-источник</label>
        <select id="refresh-source-database" name="sourceDatabase" disabled><option value="">Выберите базу того же типа</option>
          ${sources.map(source => `<option value="${esc(source.name)}">${esc(source.name)}${source.sizeBytes == null ? "" : ` · ${esc(formatSize(source.sizeBytes))}`}</option>`).join("")}
        </select><small>Источник остаётся доступным во время создания .bak.</small></div>
      <div class="detail-row"><span>Активные SQL-соединения базы назначения</span><strong>${options.activeSessions}</strong></div>
      <div class="notice"><p id="refresh-publish-notice"></p></div>
      <div class="refresh-timing">
        <label class="checkbox"><input id="refresh-run-now" name="runNow" type="checkbox" checked aria-controls="refresh-schedule"><span>Выполнить сейчас</span></label>
        <div class="field" id="refresh-schedule" hidden>
          <label for="refresh-scheduled-at">Дата и время запуска</label>
          <input id="refresh-scheduled-at" name="scheduledAt" type="datetime-local" aria-describedby="refresh-schedule-help" disabled>
          <small id="refresh-schedule-help">Часовой пояс браузера — ${esc(Intl.DateTimeFormat().resolvedOptions().timeZone)}. Архив выбирается при запуске задания.</small>
        </div>
      </div>
      <div class="form-result" role="status"></div><div class="dialog-actions actions"><button type="button" class="button" data-close="database-dialog">Отмена</button><button id="refresh-submit" class="button primary" type="submit">Восстановить базу</button></div></form>`;
    syncRefreshSource(dialog.querySelector("#refresh-form"));
  } catch (error) { dialog.innerHTML = `<h2 id="dialog-title">Не удалось подготовить запуск</h2><p class="error-inline">${esc(error.message)}</p><button class="button" data-close="database-dialog">Закрыть</button>`; }
}

function refreshArchiveSize(bytes) {
  if (bytes == null) return "размер недоступен";
  return `${(bytes / 1024 ** 3).toLocaleString("ru-RU", { maximumFractionDigits: 3 })} ГБ`;
}

function preferredRefreshSource(options, scheduled = false) {
  if (options.shrinkBackup || (scheduled && options.shrinkConfigured)) return "shrink";
  if (options.backupName || scheduled) return "archive";
  if (options.copyBackupConfigured && options.copySources?.length) return "database";
  if (options.dtConfigured && options.dtBackup) return "dt";
  return "archive";
}

function syncRefreshSource(form) {
  const { options } = flow.refresh;
  const mode = form.elements.sourceMode;
  const scheduled = !form.elements.runNow.checked;
  const shrinkAvailable = Boolean(options.shrinkBackup || (scheduled && options.shrinkConfigured));
  const dtAvailable = Boolean(options.dtConfigured && (options.dtBackup || scheduled));
  const available = {shrink: shrinkAvailable, archive: scheduled || Boolean(options.backupName),
    database: Boolean(options.copyBackupConfigured && options.copySources?.length), dt: dtAvailable};
  mode.options[0].disabled = !shrinkAvailable;
  if (mode.options[1]) mode.options[1].disabled = !available.archive;
  if (mode.options[3]) mode.options[3].disabled = !dtAvailable;
  if (!available[mode.value]) mode.value = preferredRefreshSource(options, scheduled);
  const copy = mode.value === "database";
  const shrink = mode.value === "shrink";
  const dt = mode.value === "dt";
  const source = form.elements.sourceDatabase;
  source.disabled = !copy;
  source.required = copy;
  if (!options.shrinkBackup)
    mode.options[0].textContent = `Shrink-копия / ${shrinkAvailable ? "ожидается" : "недоступна"}`;
  form.querySelector("#refresh-database-source").hidden = !copy;
  form.querySelector("#refresh-publish-notice").textContent = copy
    ? "DevRefresh создаст временный .bak базы-источника, восстановит его в выбранную базу и удалит файл."
    : dt ? `.dt удаляет целевую SQL-базу, затем IBCMD создаёт новую базу из архива. Прежние данные будут утрачены даже при ошибке создания. ${options.dtBackup ? `Архив: ${options.dtBackup.name}.` : "Свежий архив будет выбран при запуске задания."}`
    : shrink ? "В базу назначения заливается готовая копия, подготовленная по профилю администратора."
    : options.backupUnavailableReason || "Бэкап восстанавливается в базу назначения с сохранением записей. DevRefresh переведёт базу в SIMPLE и освободит неиспользуемое место в файлах.";
  const hasSource = copy ? Boolean(source.value) : available[mode.value];
  const submit = form.querySelector("#refresh-submit");
  submit.disabled = !hasSource || form.getAttribute("aria-busy") === "true";
  submit.textContent = scheduled ? "Запланировать актуализацию" : copy ? "Скопировать базу" : "Восстановить базу";
}

document.addEventListener("submit", async event => {
  const form = event.target;
  if (!(form instanceof HTMLFormElement)) return;
  if (form.id === "backup-alias-form") {
    event.preventDefault();
    await busy(form, async () => {
      const target = form.elements.target.value.trim();
      const alias = await api(`/api/admin/reduction/backup-aliases/${encodeURIComponent(target)}`, {
        method: "PUT", body: JSON.stringify({ backupSourceKey: form.elements.source.value.trim() })
      });
      flow.catalog.backupAliases = { ...flow.catalog.backupAliases, [alias.targetType]: alias.backupSourceKey };
      flow.profileTarget = "";
      if (state.page === "reduction" && flow.tab === "aliases") reductionPage();
      toast(`Соответствие сохранено · ${alias.targetType} → ${alias.backupSourceKey}`);
    });
  }
  if (form.id === "analysis-form") {
    event.preventDefault();
    await busy(form, async () => {
      selectAnalysisBackup(form.elements.backupName.value);
      const job = await api("/api/admin/reduction/analyses", { method: "POST", body: JSON.stringify({ backupName: flow.backupName }) });
      flow.analysisJob = job;reductionPage();pollAnalysis(job.id);
    });
  }
  if (form.matches(".shrink-schedule-form")) {
    event.preventDefault();
    await busy(form, async () => {
      const schedule = await api(`/api/admin/reduction/shrink-schedules/${encodeURIComponent(form.dataset.source)}`, {
        method: "PUT", body: JSON.stringify({ enabled: form.elements.enabled.checked,
          days: new FormData(form).getAll("days").map(Number), time: form.elements.time.value })
      });
      flow.catalog.shrinkSchedules = flow.catalog.shrinkSchedules.filter(s => s.sourceKey !== schedule.sourceKey).concat(schedule);
      result(form, "Расписание сохранено.");
    });
  }
  if (form.id === "refresh-form") {
    event.preventDefault();
    if (form.getAttribute("aria-busy") === "true" || !form.reportValidity() || form.querySelector("#refresh-submit").disabled) return;
    await busy(form, async () => {
      const refresh = flow.refresh;
      const sourceMode = form.elements.sourceMode.value;
      let scheduledAt = null;
      if (!form.elements.runNow.checked) {
        const local = form.elements.scheduledAt.value;
        const date = new Date(local);
        if (!Number.isFinite(date.getTime())) throw new Error("Укажите дату и время запуска.");
        const entered = new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
        if (entered !== local) throw new Error("Укажите время, которое существует в часовом поясе браузера.");
        scheduledAt = date.toISOString();
      }
      const request = { database: refresh.row.name, shrink: sourceMode === "shrink", pii: false,
        sourceDatabase: sourceMode === "database" ? form.elements.sourceDatabase.value : null,
        confirmReplace: true, confirmDisconnect: true, scheduledAt };
      if (sourceMode === "dt") request.dt = true;
      const confirmation = document.querySelector("#refresh-confirmation");
      confirmation.querySelector("#refresh-confirmation-target").textContent = request.dt ? `Я подтверждаю удаление SQL-базы ${request.database} и создание новой из .dt. При ошибке прежняя база не восстановится автоматически.` : `Я подтверждаю замену содержимого в базе ${request.database}.`;
      confirmation.returnValue = "cancel";
      confirmation.showModal();
      await new Promise(resolve => confirmation.addEventListener("close", resolve, { once: true }));
      if (confirmation.returnValue !== "confirm") return;
      const job = await api("/api/refresh", { method: "POST", body: JSON.stringify(request) });
      document.querySelector("#database-dialog").close();
      toast(job.scheduledAt ? `Запланировано на ${formatDate(job.scheduledAt)}` : `Задание принято: ${job.id}`);
      if (state.page !== "databases") await navigate("databases");
      rememberDashboardJob(job);
    });
    if (form.isConnected) syncRefreshSource(form);
  }
});

document.addEventListener("click", async event => {
  const button = event.target.closest("button");if (!button) return;
  try {
    if (button.dataset.reductionTab && flow.tab !== button.dataset.reductionTab) {
      flow.tab = button.dataset.reductionTab;
      if (flow.tab === "aliases" && flow.catalog) reductionPage(); else await loadReduction();
    }
    if (button.dataset.shrinkRun) {
      const source = button.dataset.shrinkRun;
      button.disabled = true;
      try {
        const job = await api(`/api/admin/reduction/shrink-builds/${encodeURIComponent(source)}`, { method: "POST" });
        flow.catalog.shrinkBuilds = flow.catalog.shrinkBuilds.filter(j => j.sourceKey !== source).concat(job);
        const status = button.parentElement?.querySelector("[data-shrink-build-status]");
        if (status) status.innerHTML = shrinkBuildMarkup(job);
        toast(`Сборка Shrink-копии поставлена в очередь · ${source}`);
      } finally { button.disabled = false; }
    }
    if (button.dataset.aliasEdit) {
      const form = document.querySelector("#backup-alias-form");
      form.elements.target.value = button.dataset.aliasEdit;
      form.elements.source.value = flow.catalog.backupAliases[button.dataset.aliasEdit];
      form.elements.source.focus();
    }
    if (button.dataset.aliasRemove) {
      button.disabled = true;
      try {
        const { targetType } = await api(`/api/admin/reduction/backup-aliases/${encodeURIComponent(button.dataset.aliasRemove)}`, { method: "DELETE" });
        delete flow.catalog.backupAliases[targetType];
        flow.profileTarget = "";
        if (state.page === "reduction" && flow.tab === "aliases") reductionPage();
        toast(`Соответствие для ${targetType} удалено`);
      } finally { button.disabled = false; }
    }
    if (button.dataset.close) document.getElementById(button.dataset.close).close();
    if (button.dataset.jobId) await openJob(button.dataset.jobId);
    if (button.dataset.action === "open-analysis") await openAnalysis(document.querySelector("#analysis-existing").value);
    if (["save-reduction", "preview-reduction"].includes(button.dataset.action)) {
      if (!flow.analysis) return;
      button.disabled = true;
      try {
        const payload = { analysisId: flow.analysis.id, rules: [...flow.selection.values()] };
        if (button.dataset.action === "save-reduction") {
          const profile = await api(`/api/admin/reduction/profiles/${encodeURIComponent(flow.profileTarget)}`, { method: "PUT", body: JSON.stringify(payload) });
          flow.catalog.profiles = flow.catalog.profiles.filter(p => p.sourceKey !== profile.sourceKey).concat(profile);
          const status = document.querySelector("#profile-save-result");
          if (status) status.textContent = `Сохранено · ${new Date().toLocaleTimeString("ru-RU")}`;
          toast("Профиль сокращения сохранён.");
        } else {
          const job = await api("/api/admin/reduction/preview", { method: "POST", body: JSON.stringify(payload) });
          toast("Проверка плана запущена. Следите за выполнением на главной странице.");
          await navigate("databases");rememberDashboardJob(job);
        }
      } finally { button.disabled = false; }
    }
  } catch (error) { toast(error.message); }
});

document.addEventListener("change", event => {
  const input = event.target;
  if (input.id === "profile-target") {
    selectProfileTarget(input.value);
    reductionPage();
    document.querySelector("#profile-target")?.focus();
  }
  if (input.id === "analysis-backup") {
    selectAnalysisBackup(input.value);
    if (flow.analysis && flow.analysis.backupName !== flow.backupName) {
      flow.analysis = null;flow.selection.clear();flow.filter = "";
    }
    reductionPage();
  }
  if (input.dataset.ruleKey) {
    const t = flow.analysis.tables.find(t => t.key === input.dataset.ruleKey);
    if (input.checked) flow.selection.set(t.key, { key: t.key, before: t.dateColumn ? dateDefault() : null }); else flow.selection.delete(t.key);
    renderAnalysisRows();
  }
  if (input.dataset.ruleDate) flow.selection.set(input.dataset.ruleDate, { key: input.dataset.ruleDate, before: input.value });
  if (input.id === "reduction-top") { flow.top = Number(input.value);renderAnalysisRows(); }
  if (input.id === "reduction-hide-system") { flow.hideSystem = input.checked;renderAnalysisRows(); }
  if (input.id === "refresh-source-mode" || input.id === "refresh-source-database") syncRefreshSource(input.form);
  if (input.id === "refresh-run-now") {
    const scheduled = !input.checked;
    const date = input.form.elements.scheduledAt;
    document.querySelector("#refresh-schedule").hidden = !scheduled;
    date.disabled = !scheduled;
    date.required = scheduled;
    syncRefreshSource(input.form);
    if (scheduled) date.focus();
  }
});
document.addEventListener("input", event => { if (event.target.id === "reduction-search") { flow.filter = event.target.value;renderAnalysisRows(); } });
