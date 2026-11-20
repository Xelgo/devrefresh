"use strict";

const root = document.querySelector("#app");
const state = { session: null, page: "databases", databases: null, databaseError: null, settings: null, audit: [] };
const esc = value => String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const formatDate = value => new Intl.DateTimeFormat("ru-RU", { dateStyle: "short", timeStyle: "short" }).format(new Date(value));
const formatSize = value => value >= 1024 ** 3 ? `${(value / 1024 ** 3).toFixed(1)} ГБ` : `${(value / 1024 ** 2).toFixed(1)} МБ`;
const databaseSizeLabel = row => row.sizeBytes == null ? "Размер недоступен" : formatSize(row.sizeBytes);

async function api(path, options = {}) {
  const response = await fetch(path, {
    credentials: "same-origin",
    ...options,
    headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": state.session?.csrfToken ?? "", ...options.headers }
  });
  const body = await response.json().catch(() => ({}));
  if (!response.ok) {
    const message = body.error || (response.status === 429 ? "Слишком много попыток. Повторите через минуту." : `Не удалось выполнить запрос (${response.status}).`);
    throw new Error(body.requestId ? `${message} Код обращения: ${body.requestId}` : message);
  }
  return body;
}

async function session() { state.session = await api("/api/session"); }
function brandMarkup() {
  return '<div class="brand"><div class="brand-product"><strong>DevRefresh</strong><small>Базы разработки 1С</small></div></div>';
}
function navIcon(name) {
  const paths = {
    databases: '<ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v7c0 4 16 4 16 0V5M4 12v7c0 4 16 4 16 0v-7"/>',
    jobs: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
    reduction: '<path d="M4 20V10m8 10V4m8 16v-7M2 20h20"/>',
    settings: '<path d="M4 7h16M4 17h16"/><circle cx="9" cy="7" r="3"/><circle cx="15" cy="17" r="3"/>',
    audit: '<path d="M8 4h12v17H4V4h4m0-2h8v4H8zM8 11h8m-8 5h8"/>'
  };
  return '<span class="nav-icon" aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">'+paths[name]+'</svg></span>';
}
function toast(message) {
  const element = document.querySelector("#toast");
  element.textContent = message;
  element.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => { element.hidden = true; }, 5500);
}
function result(form, message, error = false) {
  const element = form.querySelector(".form-result");
  element.textContent = message;
  element.className = `form-result ${error ? "error" : "success"}`;
}
async function busy(form, work) {
  const buttons = [...form.querySelectorAll("button")];
  buttons.forEach(button => { button.disabled = true; });
  form.setAttribute("aria-busy", "true");
  try { await work(); } catch (error) { result(form, error.message, true); }
  finally { buttons.forEach(button => { button.disabled = false; }); form.removeAttribute("aria-busy"); }
}

function authPage(setup) {
  stopDashboardJobs(true);
  root.innerHTML = `<main class="auth-page">
    <aside class="auth-aside">
      ${brandMarkup()}
      <div class="auth-story"><div class="eyebrow">Инструменты команды</div><h1>Свежие<br>базы.<br><span>Быстрый</span><br>старт.</h1><p>Восстанавливайте базы из резервных копий и готовьте окружение для разработки 1С.</p></div><div class="auth-art" aria-hidden="true"><i></i><i></i><i></i></div>
      <div class="auth-foot"><span>DevRefresh</span><span>Dev SQL Server / 1С</span></div>
    </aside>
    <section class="auth-main"><div class="auth-form">
      <div class="eyebrow">${setup ? "Начало работы" : "Администрирование"}</div>
      <h2>${setup ? "Создайте администратора" : "Вход администратора"}</h2>
      <p class="subtitle">${setup ? "Настройте первый доступ к сервису. Этот экран доступен только при открытии сайта на сервере." : "Используйте локальную учётную запись администратора для настройки сервиса."}</p>
      ${setup && !state.session.setupAllowed ? `<div class="notice error">Откройте сайт на самом сервере через 127.0.0.1, чтобы выполнить первоначальную настройку.</div>` : `
      <form id="${setup ? "setup-form" : "login-form"}">
        <div class="field"><label for="username">Имя администратора</label><input id="username" name="username" autocomplete="username" minlength="3" maxlength="64" required ${setup ? 'placeholder="Например, admin"' : ""}></div>
        <div class="field"><label for="password">Пароль</label><input id="password" name="password" type="password" autocomplete="${setup ? "new-password" : "current-password"}" ${setup ? 'minlength="12"' : ""} maxlength="256" required>${setup ? "<small>Минимум 12 символов. Пароля по умолчанию нет.</small>" : ""}</div>
        ${setup ? `<div class="field"><label for="confirm">Повторите пароль</label><input id="confirm" name="confirm" type="password" autocomplete="new-password" minlength="12" maxlength="256" required></div>
          <div class="field"><label for="windowsAdmins">Доменные администраторы <span class="muted">· необязательно</span></label><textarea id="windowsAdmins" name="windowsAdmins" placeholder="DOMAIN\\demo_owner"></textarea><small>Точные учётные записи, по одной в строке. Список можно изменить позже.</small></div>` : ""}
        <button class="button primary large" type="submit">${setup ? "Создать и перейти к настройкам" : "Войти в админку"} <span aria-hidden="true">→</span></button>
        <div class="form-result" role="status"></div>
      </form>`}
      ${!setup && state.session.windowsEnabled ? `<div class="auth-separator">корпоративный доступ</div><a class="button large" href="/auth/windows">Войти под доменной учётной записью</a><p class="auth-note">Разработчики входят через корпоративную учётную запись. Административные права выдаются отдельно.</p>` : ""}
    </div></section>
  </main>`;
}

function shell() {
  const user = state.session.user;
  root.innerHTML = `<div class="shell">
    <aside class="sidebar">
      ${brandMarkup()}
      <div class="nav-label">РАБОЧЕЕ ПРОСТРАНСТВО</div>
      <nav class="nav" aria-label="Основная навигация">
        <button data-page="databases" class="${state.page === "databases" ? "active" : ""}">${navIcon("databases")} ${user.isAdmin ? "Доступные базы" : "Мои базы"}</button>
        <button data-page="jobs" class="${state.page === "jobs" ? "active" : ""}">${navIcon("jobs")} Задания</button>
        <button data-page="connections" class="${state.page === "connections" ? "active" : ""}">${navIcon("settings")} Подключения MCP</button>
        ${user.isAdmin ? `<button data-page="reduction" class="${state.page === "reduction" ? "active" : ""}">${navIcon("reduction")} Анализ и сокращение</button>` : ""}
        ${user.isAdmin ? `<button data-page="settings" class="${state.page === "settings" ? "active" : ""}">${navIcon("settings")} Настройки</button>
          <button data-page="audit" class="${state.page === "audit" ? "active" : ""}">${navIcon("audit")} Журнал действий</button>` : ""}
      </nav>
      <div class="sidebar-bottom"><strong>Сервис баз разработки</strong>DevRefresh</div>
    </aside>
    <main class="main">
      <header class="topbar"><div class="environment"><span class="dot" aria-hidden="true"></span> DEV <span class="muted">/</span> Сервис баз 1С</div>
        <div class="identity"><div class="identity-info">${esc(user.name)}<small>${user.isAdmin ? "Администратор" : "Разработчик"}</small></div><div class="avatar" aria-hidden="true">${esc(user.name.split("\\").at(-1).slice(0, 2).toUpperCase())}</div><button class="text-button" data-action="logout">Выйти</button></div>
      </header>
      <div class="content" id="content"></div>
    </main>
  </div>`;
}

function heading(eyebrow, title, subtitle, action = "") {
  return `<div class="page-heading"><div><div class="eyebrow">${eyebrow}</div><h1>${title}</h1><p class="subtitle">${subtitle}</p></div>${action}</div>`;
}

function databasePage() {
  const data = state.databases;
  const rows = data?.databases ?? [];
  const readyCount = rows.filter(row => row.backup).length;
  const configured = data?.configured;
  document.querySelector("#content").innerHTML = `
    ${heading("Рабочее пространство", state.session.user.isAdmin ? "Базы разработки" : "Мои базы", "Доступные базы и резервные копии для вашего окружения.", '<button class="button" data-action="refresh">↻ &nbsp; Обновить</button>')}
    <div class="stats">
      <div class="stat"><div class="stat-label">Доступные базы</div><div class="stat-value">${data ? rows.length : "—"}</div><div class="stat-note">${state.session.user.isAdmin ? "На настроенном Dev SQL Server" : "По вашей доменной учётной записи"}</div></div>
      <div class="stat"><div class="stat-label">Найдены резервные файлы</div><div class="stat-value">${data ? readyCount : "—"}</div><div class="stat-note">Для доступных типов баз</div></div>
      <div class="stat"><div class="stat-label">Подключение к SQL Server</div><div class="stat-value">${state.databaseError ? "Ошибка" : configured ? "Доступно" : data ? "Не задано" : "Проверка"}</div><div class="stat-note">${configured ? "Список получен с сервера" : "Настраивается администратором"}</div></div>
    </div>
    <div id="dashboard-jobs" hidden></div>
    ${state.databaseError ? `<div class="notice error"><span class="symbol" aria-hidden="true">!</span><p>${esc(state.databaseError)}</p></div>` : ""}
    ${data?.backupError ? `<div class="notice"><span class="symbol" aria-hidden="true">!</span><p>${esc(data.backupError)}</p></div>` : ""}
    <section class="card"><div class="card-header"><div><h2>Базы и резервные копии</h2><p>${state.session.user.isAdmin ? "Администратору доступны все базы, соответствующие правилу имён." : `Владелец: ${esc(state.session.user.ownerKey ?? "не определён — требуется сопоставление учётной записи")}`}</p></div>${rows.length ? '<input id="database-search" class="search" aria-label="Поиск базы" placeholder="Найти базу…">' : '<span class="badge gray">Dev SQL Server</span>'}</div>
    ${!data && !state.databaseError ? '<div class="empty"><p>Получаем список баз…</p></div>' : !configured && !state.databaseError ? `<div class="empty"><div class="empty-icon" aria-hidden="true">⚙</div><h3>Подключите Dev SQL Server</h3><p>${state.session.user.isAdmin ? "Укажите сервер, способ подключения и каталоги резервных копий. После сохранения здесь появятся базы разработки." : "Администратор ещё не настроил подключение к серверу баз."}</p>${state.session.user.isAdmin ? '<button class="button primary" data-page="settings">Настроить подключение →</button>' : ""}</div>` : !rows.length ? `<div class="empty"><div class="empty-icon" aria-hidden="true">▤</div><h3>${state.databaseError ? "Список баз недоступен" : "Доступные базы не найдены"}</h3><p>${state.databaseError ? "После проверки подключения обновите список." : "Проверьте наличие своей Dev-базы или права на базы тестового контура, например uho_ke_tk."}</p></div>` : `<div class="table-wrap"><table><thead><tr><th>База / владелец</th><th>Тип</th><th>Резервный файл</th><th>Состояние</th><th></th></tr></thead><tbody>${rows.map((row, index) => `<tr data-database="${esc(row.name.toLowerCase())}"><td><div class="database-name"><strong class="mono">${esc(row.name)}</strong><span class="database-size" data-database-size="${esc(row.name.toLowerCase())}" title="Размер файлов данных и журнала SQL, включая свободное место внутри файлов">${databaseSizeLabel(row)}</span></div><small>${row.isTestContour ? "Тестовый контур" : esc(row.ownerKey)}</small></td><td class="mono">${esc(row.sourceKey)}</td><td>${(row.backup || row.dtBackup) ? `${esc((row.backup || row.dtBackup).name)}<small>${row.backup ? "" : ".dt / IBCMD · "}Изменён ${formatDate((row.backup || row.dtBackup).modifiedAt)} · ${formatSize((row.backup || row.dtBackup).size)}</small>` : `<span class="muted">Файл не найден</span>${row.backupSourceKey && row.backupSourceKey !== row.sourceKey ? `<small>Источник: ${esc(row.backupSourceKey)}</small>` : ""}`}</td><td class="database-task-cell"><span data-database-state="${esc(row.name.toLowerCase())}" class="badge ${row.state === "ONLINE" ? "" : "amber"}">${esc(row.state)}</span><div data-database-job="${esc(row.name.toLowerCase())}" hidden></div></td><td><button class="button" data-inspect="${index}">Подробнее →</button></td></tr>`).join("")}</tbody></table></div><div class="table-footer">${rows.length} баз · время отображается в часовом поясе браузера</div>`}
    </section>
    <div class="notice neutral"><span class="symbol" aria-hidden="true">↻</span><p>Восстановление выполняется в фоне. При сокращении можно разово отключить таблицы и изменить даты, сохранив административный профиль.</p></div>`;
  renderDashboardJobs();
}

async function loadDatabases() {
  state.databases = null;
  state.databaseError = null;
  databasePage();
  try { state.databases = await api("/api/databases"); }
  catch (error) { state.databaseError = error.message; }
  if (state.page === "databases") databasePage();
}

const restoreFormatLabels = {bak:".bak (Full / Shrink)",dt:".dt / IBCMD"};
function restorePipelinesMarkup(settings) {
  const pipelines = settings.restorePipelines ?? [];
  return `<section class="card"><div class="card-header"><div><h2>Операции после восстановления</h2><p>Общее правило для вида базы и формата архива. Применяется ко всем базам разработчиков и _tk этого вида.</p></div></div>
    <div class="card-body"><form id="restore-pipeline-form"><div class="form-grid">
      <div class="field full"><label for="pipeline-source-type">Вид базы / тип архива</label><input id="pipeline-source-type" name="sourceType" placeholder="bp_gm" maxlength="64" required><small>Тип из имени архива без даты и расширения: bp_gm для bp_gm_18-11-2026.bak или bp_gm.dt. Если настроено соответствие имён, укажите тип файла бэкапа.</small></div>
      <div class="field full"><label for="pipeline-format">Формат архива</label><select id="pipeline-format" name="archiveFormat">${Object.entries(restoreFormatLabels).map(([key,label])=>`<option value="${key}">${label}</option>`).join("")}</select></div>
      <div class="field full"><label class="checkbox"><input id="pipeline-rewrite-domain" name="rewriteDomain" type="checkbox"><span>Подменять домен</span></label></div>
      <div class="field full"><label for="pipeline-domain">Целевой домен</label><input id="pipeline-domain" name="targetDomain" value="CORP" maxlength="253" placeholder="CORP"><small>Например: \\\\EXAMPLE_TEST\\demo_owner → \\\\CORP\\demo_owner. Обработка 1С меняет домен и записывает только пользователей с включённой ОС-аутентификацией.</small></div>
      </div><div class="form-actions"><button class="button primary" type="submit">Сохранить правило</button></div><div class="form-result" role="status"></div></form>
      <p class="muted">Правило .bak общее для Full, Shrink и копирования Dev-базы; правило .dt задаётся отдельно. Сервер: ${esc(settings.connection.server || "сначала сохраните подключение")}. Снятая галочка отключает подмену. Роли и пароли не задаются обработкой; связанные данные сохраняет платформа 1С.</p>
    </div>${pipelines.length ? `<div class="table-wrap"><table><thead><tr><th>Вид базы / сервер</th><th>Архив</th><th>Подмена домена</th><th></th></tr></thead><tbody>${pipelines.map((pipeline,index)=>`<tr><td>${esc(pipeline.sourceType)}<small>${esc(pipeline.server)}</small>${pipeline.migrationNotice ? `<small class="error-inline">${esc(pipeline.migrationNotice)}</small>` : ""}</td><td>${esc(restoreFormatLabels[pipeline.archiveFormat] ?? pipeline.archiveFormat)}</td><td>${pipeline.operations.length ? esc(pipeline.operations[0].targetDomain) : "Выключено"}</td><td><button type="button" class="button" data-pipeline-edit="${index}">Изменить</button> <button type="button" class="button" data-pipeline-delete="${index}">Удалить</button></td></tr>`).join("")}</tbody></table></div>` : '<div class="empty"><p>Правила пока не назначены.</p></div>'}</section>`;
}

function settingsPage() {
  const settings = state.settings;
  const value = settings.connection;
  const dt = value.dtRestore ?? {};
  document.querySelector("#content").innerHTML = `
    ${heading("Администрирование", "Настройки сервиса", "Подключение к серверу, расположение копий, доступ и журналы.")}
    <div class="settings-layout"><div>
      <section class="card"><div class="card-header"><h2>Обновление DevRefresh</h2></div>
      <div class="card-body" id="update-panel"><p role="status">Загружаем сведения о версии…</p></div></section>
      <section class="card"><div class="card-header"><div><h2>Подключение к Dev SQL Server</h2><p>Сначала проверьте соединение, затем сохраните параметры.</p></div><span class="badge ${value.configured ? "" : "gray"}">${value.configured ? "Настроено" : "Не настроено"}</span></div>
      <div class="card-body"><form id="connection-form">
        <div class="form-grid">
          <div class="field full"><label for="server">Адрес сервера <span class="required">*</span></label><input id="server" name="server" value="${esc(value.server)}" placeholder="DEV-SQL\\INSTANCE или DEV-SQL,1433" maxlength="256" required><small>Имя узла с экземпляром либо адрес с портом через запятую.</small></div>
          <div class="field full"><label for="authentication">Способ подключения</label><select id="authentication" name="authentication"><option value="sql" ${value.authentication === "sql" ? "selected" : ""}>SQL Server — логин и пароль</option><option value="windows" ${value.authentication === "windows" ? "selected" : ""}>Windows — учётная запись службы приложения</option></select></div>
          <div class="field sql-field"><label for="sql-username">SQL-пользователь <span class="required">*</span></label><input id="sql-username" name="username" value="${esc(value.username)}" maxlength="256" autocomplete="off"></div>
          <div class="field sql-field"><label for="sql-password">Пароль SQL Server</label><input id="sql-password" name="password" type="password" maxlength="1024" autocomplete="new-password" placeholder="${value.passwordConfigured ? "Сохранён · оставьте пустым" : "Введите пароль"}"><small>${value.passwordConfigured ? "Пустое поле сохраняет пароль для того же сервера и SQL-пользователя." : "Пароль хранится зашифрованным и не возвращается в браузер."}</small></div>
        </div>
        <hr class="divider"><h3>Каталоги резервных копий</h3>
        <div class="form-grid">
          <div class="field full"><label for="backup-directory">Путь для приложения <span class="required">*</span></label><input id="backup-directory" name="backupDirectory" value="${esc(value.backupDirectory)}" placeholder="\\\\fileserver\\backups\\1c" maxlength="2048" required><small>Каталог, который служба приложения может читать для отображения файлов .bak.</small></div>
          <div class="field full"><label for="sql-backup-directory">Путь для SQL Server <span class="required">*</span></label><input id="sql-backup-directory" name="sqlBackupDirectory" value="${esc(value.sqlBackupDirectory)}" placeholder="D:\\Backups\\1c или \\\\fileserver\\backups\\1c" maxlength="2048" required><small>Путь к тем же файлам со стороны сервера SQL. Может совпадать с путём приложения.</small></div>
        </div>
        <hr class="divider"><h3>Каталог готовых Shrink-копий</h3>
        <div class="form-grid">
          <div class="field full"><label for="shrink-backup-directory">Путь для приложения</label><input id="shrink-backup-directory" name="shrinkBackupDirectory" value="${esc(value.shrinkBackupDirectory)}" maxlength="2048"><small>Службе приложения нужны права на чтение и удаление файлов в этой папке.</small></div>
          <div class="field full"><label for="sql-shrink-backup-directory">Путь для SQL Server</label><input id="sql-shrink-backup-directory" name="sqlShrinkBackupDirectory" value="${esc(value.sqlShrinkBackupDirectory)}" maxlength="2048"><small>Путь к той же папке со стороны SQL Server. Службе SQL нужны права на чтение и запись.</small></div>
        </div>
        <hr class="divider"><h3>Временные .bak для копирования Dev-баз</h3>
        <p class="muted">Если оба поля пусты, используется каталог Shrink-копий.</p>
        <div class="form-grid">
          <div class="field full"><label for="copy-backup-directory">Путь для приложения</label><input id="copy-backup-directory" name="copyBackupDirectory" value="${esc(value.copyBackupDirectory)}" maxlength="2048"><small>DevRefresh читает и удаляет временные файлы в этой папке.</small></div>
          <div class="field full"><label for="sql-copy-backup-directory">Путь для SQL Server</label><input id="sql-copy-backup-directory" name="sqlCopyBackupDirectory" value="${esc(value.sqlCopyBackupDirectory)}" maxlength="2048"><small>Укажите путь к той же папке. Если она находится на сервере SQL, укажите здесь локальный путь к её диску, даже если DevRefresh обращается к папке по сети.</small></div>
        </div>
        <hr class="divider"><label class="checkbox"><input name="trustServerCertificate" type="checkbox" ${value.trustServerCertificate ? "checked" : ""}><span>Доверять сертификату SQL Server без проверки цепочки<small class="muted"> · для серверов с сертификатом, которому инфраструктура пока не доверяет</small></span></label>
        <hr class="divider"><h3>Архивы .dt и IBCMD</h3>
        <label class="checkbox"><input name="dtEnabled" type="checkbox" ${dt.enabled ? "checked" : ""}><span>Включить восстановление из .dt через IBCMD</span></label>
        <div class="form-grid">
          <div class="field full"><label for="dt-backup-directory">Отдельный каталог .dt</label><input id="dt-backup-directory" name="dtBackupDirectory" value="${esc(dt.backupDirectory ?? "")}" maxlength="2048"><small>Путь со стороны службы DevRefresh. Имена: uho_ke.dt или uho_ke_18-11-2026.dt. Применяются соответствия типов бэкапов.</small></div>
          <div class="field full"><label for="ibcmd-directory">Папка bin платформы 1С с IBCMD</label><input id="ibcmd-directory" name="ibcmdDirectory" value="${esc(dt.ibcmdDirectory ?? "")}" placeholder="C:\\Program Files\\1cv8\\8.3.27.2214\\bin" maxlength="2048"><small>Полная установленная Windows-платформа версии 8.3.20 или новее; в папке должен находиться ibcmd.exe.</small></div>
          <div class="field full"><label for="ibcmd-working-directory">Рабочий каталог IBCMD</label><input id="ibcmd-working-directory" name="ibcmdWorkingDirectory" value="${esc(dt.workingDirectory ?? "")}" maxlength="2048"><small>Пусто — каталог data/ibcmd установленного DevRefresh. Для каждого задания создаётся отдельная папка. Службе нужны права записи.</small></div>
          <div class="field"><label for="ibcmd-sql-username">Отдельный SQL-пользователь IBCMD</label><input id="ibcmd-sql-username" name="ibcmdSqlUsername" value="${esc(dt.sqlUsername ?? "")}" maxlength="256" autocomplete="off"><small>Пусто — использовать общий SQL-логин выше. При Windows-аутентификации DevRefresh укажите здесь SQL-логин для IBCMD.</small></div>
          <div class="field"><label for="ibcmd-sql-password">Пароль отдельного SQL-пользователя</label><input id="ibcmd-sql-password" name="ibcmdSqlPassword" type="password" maxlength="1024" autocomplete="new-password" placeholder="${dt.sqlPasswordConfigured ? "Сохранён · оставьте пустым" : "Пароль SQL Server"}"></div>
          <div class="field"><label for="ibcmd-timeout">Таймаут загрузки, минут</label><input id="ibcmd-timeout" name="ibcmdTimeoutMinutes" type="number" min="1" max="1440" value="${esc(dt.timeoutMinutes ?? 360)}"></div>
        </div>
        <p class="muted">Перед загрузкой .dt целевая SQL-база удаляется. IBCMD создаёт новую базу через infobase create --restore. Нужны только SQL-логин и пароль с правом создания баз. Если создание завершится ошибкой, прежняя база будет утрачена. Остановите фоновые задания и новые подключения 1С.</p>
        <button type="button" class="button" data-action="test-ibcmd">Проверить IBCMD</button>
        <hr class="divider"><h3>Кластер 1С для подмены домена</h3>
        <div class="form-grid">
          <div class="field full"><label for="oneC-cluster">Адрес кластера 1С</label><input id="oneC-cluster" name="oneCCluster" value="${esc(value.oneC?.clusterAddress ?? "")}" placeholder="SERVER-1C:1541" maxlength="256"><small>Имя информационной базы в кластере совпадает с именем SQL-базы.</small></div>
          <div class="field full"><label for="oneC-platform">Папка bin платформы 1С</label><input id="oneC-platform" name="oneCPlatform" value="${esc(value.oneC?.platformDirectory ?? "")}" placeholder="C:\\Program Files\\1cv8\\8.3.27.2214\\bin" maxlength="2048"><small>Пусто — использовать папку IBCMD выше. Нужна установленная платформа с comcntr.dll той же версии, что и сервер 1С.</small></div>
          <div class="field"><label for="oneC-timeout">Таймаут операции, секунд</label><input id="oneC-timeout" name="oneCTimeout" type="number" min="15" max="1800" value="${esc(value.oneC?.timeoutSeconds ?? 180)}"></div>
        </div>
        <p class="muted">Используется для правил «Подменять домен». Перед выполнением обеспечьте отсутствие новых подключений и фоновых заданий в целевой базе. Служба временно сохраняет список пользователей, входит в 1С и возвращает список перед запуском обработки. Пароли пользователей ИБ не требуются.</p>
        <hr class="divider"><h3>После актуализации</h3>
        <label class="checkbox"><input name="runDbccCheckDb" type="checkbox" ${value.runDbccCheckDb ? "checked" : ""}><span>Проверять целостность SQL-базы (DBCC CHECKDB)<small>Выполняется в конце актуализации. Настройка применяется к новым заданиям.</small></span></label>
        <div class="form-actions"><button type="button" class="button" data-action="test-connection">Проверить подключение</button><button type="submit" class="button primary">Сохранить настройки</button></div>
        <div class="form-result" role="status"></div>
      </form></div></section>
      <section class="card"><div class="card-header"><div><h2>Журналы DevRefresh</h2></div></div>
      <div class="card-body"><form id="log-retention-form">
        <div class="field"><label for="log-retention-days">Срок хранения файлов журнала, дней</label>
          <input id="log-retention-days" name="days" type="number" min="1" step="1" value="${esc(settings.logRetentionDays)}" required>
          <small>Файлы devrefresh-*.jsonl старше указанного срока удаляются при запуске сервиса и затем раз в час.</small></div>
        <div class="form-actions"><button type="submit" class="button primary">Сохранить срок хранения</button></div>
        <div class="form-result" role="status"></div>
      </form></div></section>
      ${restorePipelinesMarkup(settings)}
      <section class="card"><div class="card-header"><div><h2>Доступ и роли</h2><p>Локальный администратор, доменные администраторы и пользователи тестового контура.</p></div></div>
      <div class="card-body"><form id="security-form">
        <label class="checkbox"><input id="windows-enabled" name="windowsEnabled" type="checkbox" ${settings.windowsAuthentication.enabled ? "checked" : ""}><span>Включить корпоративный вход<small> · Windows / доменная учётная запись</small></span></label>
        <label class="checkbox"><input id="windows-auto-sign-in" name="windowsAutoSignIn" type="checkbox" ${settings.windowsAuthentication.autoSignIn ? "checked" : ""} ${!settings.windowsAuthentication.enabled ? "disabled" : ""}><span>Автоматически входить через домен с главной страницы</span></label>
        <p class="muted">Изменения применяются сразу. На домашнем компьютере оставьте корпоративный вход выключенным. Обычная форма входа доступна по /login.</p><div class="form-grid">
        <div class="field full"><label for="windows-admins">Доменные администраторы</label><textarea id="windows-admins" name="windowsAdmins" placeholder="DOMAIN\\demo_owner">${esc(settings.windowsAdmins.join("\n"))}</textarea><small>По одной учётной записи DOMAIN\\user в строке. Совпадение проверяется целиком; маски не поддерживаются.</small></div>
        <div class="field full"><label for="test-contour-operators">Обновление тестового контура (_tk)</label><textarea id="test-contour-operators" name="testContourOperators" placeholder="DOMAIN\\test_operator">${esc((settings.testContourOperators ?? []).join("\n"))}</textarea><small>Общий список для всех баз с окончанием _tk, например uho_ke_tk. По одной AD-учётке DOMAIN\\user в строке. Эти пользователи могут обновлять тестовый контур из соответствующего Full-, Shrink- или .dt-архива. Администраторы сохраняют полный доступ.</small></div>
        <div class="field"><label for="local-username">Локальный администратор</label><input id="local-username" value="${esc(settings.localUsername)}" readonly></div>
        <div class="field"><label for="new-password">Новый пароль <span class="muted">· необязательно</span></label><input id="new-password" name="newPassword" type="password" minlength="12" maxlength="256" autocomplete="new-password"><small>Оставьте пустым, чтобы сохранить текущий пароль.</small></div>
      </div><div class="form-actions"><button class="button primary" type="submit">Сохранить доступ</button></div><div class="form-result" role="status"></div></form></div></section>
    </div><aside class="aside-note"><h3>Подготовка окружения</h3>
      <div class="step"><span class="step-number">1</span><div><strong>Доступ к SQL Server</strong><small>Выделите служебную учётную запись с правами на нужные Dev-базы.</small></div></div>
      <div class="step"><span class="step-number">2</span><div><strong>Общий каталог копий</strong><small>Доступ к файлам нужен и приложению, и службе SQL Server.</small></div></div>
      <div class="step"><span class="step-number">3</span><div><strong>Корпоративный вход</strong><small>Доменные пользователи определяются через Windows Authentication.</small></div></div>
      <hr class="divider"><p>Пароли подключений зашифрованы. Пароль администратора хранится в виде проверочного хеша.</p>
    </aside></div>`;
  updateAuthenticationFields();
  loadUpdatePanel();
}

let checkedRelease;

async function loadUpdatePanel({check = false, watch = false, deadline = Date.now() + 15 * 60 * 1000} = {}) {
  const panel = document.querySelector("#update-panel");
  if (!panel) return;
  try {
    const status = await api("/api/admin/update", {signal: AbortSignal.timeout(5000)});
    if (check) checkedRelease = await api("/api/admin/update/latest", {signal: AbortSignal.timeout(25000)});
    if (state.page !== "settings" || panel !== document.querySelector("#update-panel")) return;
    const {operation, unavailable, currentVersion} = status;
    const busy = operation?.state === "Running";
    const release = checkedRelease?.release;
    const available = checkedRelease?.available && release?.version !== currentVersion;
    const message = busy ? `Устанавливается версия ${operation.version}. Служба перезапустится.`
      : operation?.state === "Failed" ? `Обновление до ${operation.version} не завершилось. Проверьте журнал обновления на сервере.`
      : operation?.state === "Succeeded" ? `Версия ${operation.version} установлена.` : "";
    panel.innerHTML = `<dl><div class="detail-row"><dt>Установленная версия</dt><dd>${esc(currentVersion)}</dd></div>
      ${release ? `<div class="detail-row"><dt>Последний релиз</dt><dd>${esc(release.version)}${!available ? " · Новых обновлений нет" : ""}</dd></div>` : ""}</dl>
      ${release ? `<details><summary>Список изменений</summary><p class="update-release-notes">${esc(release.notes || "Описание релиза отсутствует.")}</p>
      <p><a href="${esc(release.url)}" target="_blank" rel="noopener noreferrer">Релиз на GitHub</a></p></details>` : ""}
      ${unavailable ? `<p class="muted">${esc(unavailable)}</p>` : ""}
      <div class="form-actions"><button type="button" class="button" data-action="check-update" ${busy ? "disabled" : ""}>Проверить версию</button>
      ${available && !unavailable ? `<button type="button" class="button primary" data-action="install-update" ${busy ? "disabled" : ""}>Обновить до ${esc(release.version)}</button>` : ""}</div>
      <p class="form-result" role="status">${esc(message)}</p>`;
    if (busy && Date.now() < deadline) setTimeout(() => { if (panel.isConnected) loadUpdatePanel({watch:true, deadline}); }, 3000);
    else if (watch && operation?.state === "Succeeded") location.reload();
  } catch (error) {
    if (!panel.isConnected) return;
    if (watch && Date.now() < deadline) {
      setTimeout(() => { if (panel.isConnected) loadUpdatePanel({watch:true, deadline}); }, 3000);
      return;
    }
    const result = panel.querySelector('[role="status"]');
    if (result) result.textContent = error.message;
    if (!panel.querySelector('button')) {
      const button = document.createElement('button');
      button.className = 'button'; button.dataset.action = 'check-update'; button.textContent = 'Проверить версию';
      panel.appendChild(button);
    }
  }
}

function updateAuthenticationFields() {
  const form = document.querySelector("#connection-form");
  if (!form) return;
  const sql = form.elements.authentication.value === "sql";
  form.querySelectorAll(".sql-field").forEach(field => { field.hidden = !sql; });
  form.elements.username.required = sql;
  form.elements.password.required = sql && !state.settings.connection.passwordConfigured;
}

function settingsInput(form) {
  return {
    server: form.elements.server.value,
    authentication: form.elements.authentication.value,
    username: form.elements.username.value,
    password: form.elements.password.value || null,
    backupDirectory: form.elements.backupDirectory.value,
    sqlBackupDirectory: form.elements.sqlBackupDirectory.value,
    shrinkBackupDirectory: form.elements.shrinkBackupDirectory.value,
    sqlShrinkBackupDirectory: form.elements.sqlShrinkBackupDirectory.value,
    copyBackupDirectory: form.elements.copyBackupDirectory.value,
    sqlCopyBackupDirectory: form.elements.sqlCopyBackupDirectory.value,
    trustServerCertificate: form.elements.trustServerCertificate.checked,
    runDbccCheckDb: form.elements.runDbccCheckDb.checked,
    dtRestore: form.elements.dtEnabled ? {
      enabled: form.elements.dtEnabled.checked, backupDirectory: form.elements.dtBackupDirectory.value,
      ibcmdDirectory: form.elements.ibcmdDirectory.value, workingDirectory: form.elements.ibcmdWorkingDirectory.value,
      sqlUsername: form.elements.ibcmdSqlUsername.value, sqlPassword: form.elements.ibcmdSqlPassword.value || null,
      timeoutMinutes: form.elements.ibcmdTimeoutMinutes.valueAsNumber
    } : null,
    oneC: form.elements.oneCCluster ? {
      clusterAddress: form.elements.oneCCluster.value,platformDirectory: form.elements.oneCPlatform.value,
      timeoutSeconds: form.elements.oneCTimeout.valueAsNumber
    } : null
  };
}

const auditLabels = { "setup.completed": "Первичная настройка", "login.local": "Вход администратора", "login.domain": "Доменный вход", "login.failed": "Неудачный вход", "logout": "Выход", "settings.saved": "Настройки подключения", "security.saved": "Настройки доступа", "connection.test": "Проверка соединения", "connection.failed": "Ошибка соединения", "backup.alias.saved": "Соответствие бэкапов сохранено", "backup.alias.removed": "Соответствие бэкапов удалено", "log.retention.saved": "Срок хранения журналов", "refresh.scheduled": "Запланировано восстановление", "mcp.authorized": "MCP: согласие на доступ", "mcp.token.issued": "MCP: код обменян на токен", "mcp.token.refreshed": "MCP: токен обновлён", "mcp.revoked": "MCP: доступ отозван" };
function auditPage() {
  document.querySelector("#content").innerHTML = `${heading("Администрирование", "Журнал действий", "Последние 100 событий: входы, изменения настроек и проверки подключения.", '<button class="button" data-page="audit">↻ &nbsp; Обновить</button>')}
    <section class="card"><div class="table-wrap"><table><thead><tr><th>Время</th><th>Пользователь</th><th>Действие</th><th>Результат</th></tr></thead><tbody>${state.audit.map(entry => `<tr><td>${formatDate(entry.at)}</td><td class="mono">${esc(entry.actor)}</td><td>${esc(auditLabels[entry.action] ?? entry.action)}</td><td>${esc(entry.detail)}</td></tr>`).join("")}</tbody></table></div>${!state.audit.length ? '<div class="empty"><p>Событий пока нет.</p></div>' : ""}<div class="table-footer">Время отображается в часовом поясе браузера.</div></section>`;
}

async function navigate(page) {
  if (!["databases", "jobs", "connections"].includes(page) && !state.session.user.isAdmin) return;
  stopDashboardJobs();
  if (typeof loadJobs === "function") clearTimeout(loadJobs.timer);
  state.page = page;
  shell();
  if (page === "databases") { const loading = loadDatabases();startDashboardJobs();return loading; }
  if (page === "reduction") return loadReduction();
  if (page === "jobs") return loadJobs();
  if (page === "connections") return loadMcpConnections();
  document.querySelector("#content").innerHTML = '<div class="loading">Загружаем данные…</div>';
  try {
    if (page === "settings") { state.settings = await api("/api/admin/settings"); if (state.page === page) settingsPage(); }
    if (page === "audit") { state.audit = await api("/api/admin/audit"); if (state.page === page) auditPage(); }
  } catch (error) { document.querySelector("#content").innerHTML = `<div class="notice error">${esc(error.message)}</div>`; }
}

function inspectDatabase(index) {
  const row = state.databases?.databases[index];
  if (!row) return;
  return openRefreshDialog(row);
}

document.addEventListener("submit", async event => {
  const form = event.target;
  if (!(form instanceof HTMLFormElement) || form.method === "dialog") return;
  event.preventDefault();
  if (form.id === "setup-form" || form.id === "login-form") {
    const setup = form.id === "setup-form";
    return busy(form, async () => {
      if (setup && form.elements.password.value !== form.elements.confirm.value) throw new Error("Пароли не совпадают.");
      const payload = { username: form.elements.username.value.trim(), password: form.elements.password.value };
      if (setup) payload.windowsAdmins = form.elements.windowsAdmins.value.split(/\r?\n/);
      await api(setup ? "/api/setup" : "/api/login", { method: "POST", body: JSON.stringify(payload) });
      await session();
      history.replaceState(null, "", "/");
      await navigate(setup ? "settings" : "databases");
    });
  }
  if (form.id === "connection-form") return busy(form, async () => {
    const saved = await api("/api/admin/settings", { method: "PUT", body: JSON.stringify(settingsInput(form)) });
    state.settings.connection = saved;
    settingsPage();
    toast("Настройки подключения сохранены.");
  });
  if (form.id === "log-retention-form") return busy(form, async () => {
    const saved = await api("/api/admin/log-retention", { method: "PUT", body: JSON.stringify({ days: form.elements.days.valueAsNumber }) });
    state.settings.logRetentionDays = saved.days;
    result(form, "Срок хранения сохранён. Сервис применит его при следующей очистке.");
  });
  if (form.id === "security-form") return busy(form, async () => {
    await api("/api/admin/security", { method: "PUT", body: JSON.stringify({ windowsAdmins: form.elements.windowsAdmins.value.split(/\r?\n/), newPassword: form.elements.newPassword.value || null,
      testContourOperators: form.elements.testContourOperators.value.split(/\r?\n/),
      windowsEnabled: form.elements.windowsEnabled.checked, windowsAutoSignIn: form.elements.windowsEnabled.checked && form.elements.windowsAutoSignIn.checked }) });
    await session();
    if (!state.session.user) { location.assign("/login"); return; }
    await navigate(state.session.user.isAdmin ? "settings" : "databases");
    toast("Настройки доступа сохранены.");
  });
  if (form.id === "restore-pipeline-form") return busy(form, async () => {
    await api("/api/admin/restore-pipelines", {method:"PUT",body:JSON.stringify({
      sourceType:form.elements.sourceType.value.trim(),archiveFormat:form.elements.archiveFormat.value,
      rewriteDomain:form.elements.rewriteDomain.checked,targetDomain:form.elements.targetDomain.value.trim()})});
    await navigate("settings");toast("Правило операций сохранено.");
  });
});

document.addEventListener("click", async event => {
  const button = event.target.closest("button");
  if (!button) return;
  try {
    if (button.dataset.pipelineEdit !== undefined) {
      const pipeline=state.settings.restorePipelines[Number(button.dataset.pipelineEdit)];
      if (pipeline.server.toLowerCase() !== state.settings.connection.server.toLowerCase()) throw new Error("Сначала выберите SQL Server, к которому относится правило.");
      const form=document.querySelector("#restore-pipeline-form");
      form.elements.sourceType.value=pipeline.sourceType;form.elements.archiveFormat.value=pipeline.archiveFormat;
      form.elements.rewriteDomain.checked=pipeline.operations.length>0;
      form.elements.targetDomain.value=pipeline.targetDomain ?? pipeline.operations[0]?.targetDomain ?? "CORP";
      form.elements.targetDomain.required=form.elements.rewriteDomain.checked;form.elements.sourceType.focus();
    }
    if (button.dataset.pipelineDelete !== undefined) {
      const pipeline=state.settings.restorePipelines[Number(button.dataset.pipelineDelete)];
      button.disabled=true;
      try { await api(`/api/admin/restore-pipelines/${encodeURIComponent(pipeline.sourceType)}/${encodeURIComponent(pipeline.archiveFormat)}?server=${encodeURIComponent(pipeline.server)}`,{method:"DELETE"});await navigate("settings");toast("Правило удалено."); }
      finally {button.disabled=false;}
    }
    if (button.dataset.page) await navigate(button.dataset.page);
    if (button.dataset.inspect !== undefined) inspectDatabase(Number(button.dataset.inspect));
    if (button.dataset.action === "close-dialog") document.querySelector("#database-dialog").close();
    if (button.dataset.action === "refresh") await loadDatabases();
    if (button.dataset.action === "check-update") {
      button.disabled = true;
      try { await loadUpdatePanel({check:true}); } finally { button.disabled = false; }
    }
    if (button.dataset.action === "install-update") {
      const version = checkedRelease?.release.version;
      if (!version || !confirm(`Установить DevRefresh ${version}? Служба перезапустится, сайт будет временно недоступен.`)) return;
      button.disabled = true;
      try {
        await api("/api/admin/update", {method:"POST",body:JSON.stringify({version})});
        const panel = document.querySelector("#update-panel");
        if (panel) panel.innerHTML = '<p role="status">Обновление запущено. Ожидаем перезапуска службы…</p>';
        await loadUpdatePanel({watch:true});
      } finally { button.disabled = false; }
    }
    if (button.dataset.action === "logout") {
      stopDashboardJobs(true);
      await api("/api/logout", { method: "POST", body: "{}" });
      location.assign("/login");
    }
    if (button.dataset.action === "test-connection") {
      const form = button.closest("form");
      if (!form.reportValidity()) return;
      await busy(form, async () => {
        result(form, "Проверяем подключение…");
        const value = await api("/api/admin/connection/test", { method: "POST", body: JSON.stringify(settingsInput(form)) });
        result(form, `Соединение установлено: ${value.serverName}\nSQL Server ${value.version} · ${value.edition}\nПараметры не сохранены. Нажмите «Сохранить настройки», чтобы применить их.`);
      });
    }
    if (button.dataset.action === "test-ibcmd") {
      const form = button.closest("form");
      if (!form.reportValidity()) return;
      await busy(form, async () => {
        result(form, "Проверяем запуск IBCMD…");
        const value = await api("/api/admin/ibcmd/test", { method: "POST", body: JSON.stringify(settingsInput(form)) });
        result(form, `IBCMD ${value.version} запускается. Проверка не загружала .dt и не проверяла вход в базу. Сохраните настройки, чтобы применить их.`);
      });
    }
  } catch (error) { toast(error.message); }
});
document.addEventListener("change", event => {
  if (event.target.id === "pipeline-rewrite-domain") event.target.form.elements.targetDomain.required=event.target.checked;
  if (event.target.id === "authentication") updateAuthenticationFields();
  if (event.target.id === "windows-enabled") {
    const auto = document.querySelector("#windows-auto-sign-in");
    auto.disabled = !event.target.checked;
    if (auto.disabled) auto.checked = false;
  }
});
document.addEventListener("input", event => {
  if (event.target.id !== "database-search") return;
  const query = event.target.value.toLowerCase().trim();
  document.querySelectorAll("tr[data-database]").forEach(row => { row.hidden = !row.dataset.database.includes(query); });
});

async function start() {
  try {
    await session();
    if (state.session.setupRequired) return authPage(true);
    if (!state.session.user) {
      if (state.session.windowsAutoSignIn && location.pathname === "/") return location.replace("/auth/windows");
      return authPage(false);
    }
    await navigate(location.pathname === "/connections" ? "connections" : "databases");
  } catch (error) { root.innerHTML = `<div class="loading error-inline">${esc(error.message)} Обновите страницу после проверки сервиса.</div>`; }
}
start();
