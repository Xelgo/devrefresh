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
    ${!data && !state.databaseError ? '<div class="empty"><p>Получаем список баз…</p></div>' : !configured && !state.databaseError ? `<div class="empty"><div class="empty-icon" aria-hidden="true">⚙</div><h3>Подключите Dev SQL Server</h3><p>${state.session.user.isAdmin ? "Укажите сервер, способ подключения и каталоги резервных копий. После сохранения здесь появятся базы разработки." : "Администратор ещё не настроил подключение к серверу баз."}</p>${state.session.user.isAdmin ? '<button class="button primary" data-page="settings">Настроить подключение →</button>' : ""}</div>` : !rows.length ? `<div class="empty"><div class="empty-icon" aria-hidden="true">▤</div><h3>${state.databaseError ? "Список баз недоступен" : "Доступные базы не найдены"}</h3><p>${state.databaseError ? "После проверки подключения обновите список." : "Проверьте наличие базы с именем вида bp_gm_dev_demo_owner и права учётной записи подключения."}</p></div>` : `<div class="table-wrap"><table><thead><tr><th>База / владелец</th><th>Тип</th><th>Резервный файл</th><th>Состояние</th><th></th></tr></thead><tbody>${rows.map((row, index) => `<tr data-database="${esc(row.name.toLowerCase())}"><td><div class="database-name"><strong class="mono">${esc(row.name)}</strong><span class="database-size" data-database-size="${esc(row.name.toLowerCase())}" title="Размер файлов данных и журнала SQL, включая свободное место внутри файлов">${databaseSizeLabel(row)}</span></div><small>${esc(row.ownerKey)}</small></td><td class="mono">${esc(row.sourceKey)}</td><td>${row.backup ? `${esc(row.backup.name)}<small>Изменён ${formatDate(row.backup.modifiedAt)} · ${formatSize(row.backup.size)}</small>` : `<span class="muted">Файл не найден</span>${row.backupSourceKey && row.backupSourceKey !== row.sourceKey ? `<small>Источник: ${esc(row.backupSourceKey)}</small>` : ""}`}</td><td class="database-task-cell"><span data-database-state="${esc(row.name.toLowerCase())}" class="badge ${row.state === "ONLINE" ? "" : "amber"}">${esc(row.state)}</span><div data-database-job="${esc(row.name.toLowerCase())}" hidden></div></td><td><button class="button" data-inspect="${index}">Подробнее →</button></td></tr>`).join("")}</tbody></table></div><div class="table-footer">${rows.length} баз · время отображается в часовом поясе браузера</div>`}
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

function settingsPage() {
  const settings = state.settings;
  const value = settings.connection;
  document.querySelector("#content").innerHTML = `
    ${heading("Администрирование", "Настройки сервиса", "Подключение к серверу, расположение копий, доступ и журналы.")}
    <div class="settings-layout"><div>
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
        <hr class="divider"><label class="checkbox"><input name="trustServerCertificate" type="checkbox" ${value.trustServerCertificate ? "checked" : ""}><span>Доверять сертификату SQL Server без проверки цепочки<small class="muted"> · для серверов с сертификатом, которому инфраструктура пока не доверяет</small></span></label>
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
      <section class="card"><div class="card-header"><div><h2>Доступ администраторов</h2><p>Локальный администратор и точный список доменных учётных записей.</p></div></div>
      <div class="card-body"><form id="security-form">
        <label class="checkbox"><input id="windows-enabled" name="windowsEnabled" type="checkbox" ${settings.windowsAuthentication.enabled ? "checked" : ""}><span>Включить корпоративный вход<small> · Windows / доменная учётная запись</small></span></label>
        <label class="checkbox"><input id="windows-auto-sign-in" name="windowsAutoSignIn" type="checkbox" ${settings.windowsAuthentication.autoSignIn ? "checked" : ""} ${!settings.windowsAuthentication.enabled ? "disabled" : ""}><span>Автоматически входить через домен с главной страницы</span></label>
        <p class="muted">Изменения применяются сразу. На домашнем компьютере оставьте корпоративный вход выключенным. Обычная форма входа доступна по /login.</p><div class="form-grid">
        <div class="field full"><label for="windows-admins">Доменные администраторы</label><textarea id="windows-admins" name="windowsAdmins" placeholder="DOMAIN\\demo_owner">${esc(settings.windowsAdmins.join("\n"))}</textarea><small>По одной учётной записи DOMAIN\\user в строке. Совпадение проверяется целиком; маски не поддерживаются.</small></div>
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
    trustServerCertificate: form.elements.trustServerCertificate.checked,
    runDbccCheckDb: form.elements.runDbccCheckDb.checked
  };
}

const auditLabels = { "setup.completed": "Первичная настройка", "login.local": "Вход администратора", "login.domain": "Доменный вход", "login.failed": "Неудачный вход", "logout": "Выход", "settings.saved": "Настройки подключения", "security.saved": "Настройки доступа", "connection.test": "Проверка соединения", "connection.failed": "Ошибка соединения", "backup.alias.saved": "Соответствие бэкапов сохранено", "backup.alias.removed": "Соответствие бэкапов удалено", "log.retention.saved": "Срок хранения журналов", "refresh.scheduled": "Запланировано восстановление" };
function auditPage() {
  document.querySelector("#content").innerHTML = `${heading("Администрирование", "Журнал действий", "Последние 100 событий: входы, изменения настроек и проверки подключения.", '<button class="button" data-page="audit">↻ &nbsp; Обновить</button>')}
    <section class="card"><div class="table-wrap"><table><thead><tr><th>Время</th><th>Пользователь</th><th>Действие</th><th>Результат</th></tr></thead><tbody>${state.audit.map(entry => `<tr><td>${formatDate(entry.at)}</td><td class="mono">${esc(entry.actor)}</td><td>${esc(auditLabels[entry.action] ?? entry.action)}</td><td>${esc(entry.detail)}</td></tr>`).join("")}</tbody></table></div>${!state.audit.length ? '<div class="empty"><p>Событий пока нет.</p></div>' : ""}<div class="table-footer">Время отображается в часовом поясе браузера.</div></section>`;
}

async function navigate(page) {
  if (!["databases", "jobs"].includes(page) && !state.session.user.isAdmin) return;
  stopDashboardJobs();
  if (typeof loadJobs === "function") clearTimeout(loadJobs.timer);
  state.page = page;
  shell();
  if (page === "databases") { const loading = loadDatabases();startDashboardJobs();return loading; }
  if (page === "reduction") return loadReduction();
  if (page === "jobs") return loadJobs();
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
  if (!(form instanceof HTMLFormElement)) return;
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
      windowsEnabled: form.elements.windowsEnabled.checked, windowsAutoSignIn: form.elements.windowsEnabled.checked && form.elements.windowsAutoSignIn.checked }) });
    await session();
    if (!state.session.user) { location.assign("/login"); return; }
    await navigate(state.session.user.isAdmin ? "settings" : "databases");
    toast("Настройки доступа сохранены.");
  });
});

document.addEventListener("click", async event => {
  const button = event.target.closest("button");
  if (!button) return;
  try {
    if (button.dataset.page) await navigate(button.dataset.page);
    if (button.dataset.inspect !== undefined) inspectDatabase(Number(button.dataset.inspect));
    if (button.dataset.action === "close-dialog") document.querySelector("#database-dialog").close();
    if (button.dataset.action === "refresh") await loadDatabases();
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
  } catch (error) { toast(error.message); }
});
document.addEventListener("change", event => {
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
    await navigate("databases");
  } catch (error) { root.innerHTML = `<div class="loading error-inline">${esc(error.message)} Обновите страницу после проверки сервиса.</div>`; }
}
start();
