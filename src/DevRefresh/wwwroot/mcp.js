"use strict";

async function loadMcpConnections() {
  const content = document.querySelector("#content");
  content.innerHTML = '<div class="loading">Получаем подключения…</div>';
  try {
    const data = await api("/api/mcp/connections");
    if (state.page !== "connections") return;
    content.innerHTML = `${heading("Личный доступ", "Подключения MCP", "Подключайте AI-клиенты через браузер и управляйте их доступом.")}
      ${!data.enabled ? '<div class="notice">MCP пока выключен. Администратору нужно настроить HTTPS-адрес службы и включить MCP.</div>' : `
      <section class="card"><div class="card-body"><h2>Подключение через OAuth</h2>
        <p>Добавьте в своём MCP-клиенте удалённый сервер по этому адресу и выберите вход через OAuth.</p>
        <div class="field"><label for="mcp-url">Адрес MCP</label><input id="mcp-url" class="mono" value="${esc(data.url)}" readonly></div>
        <p>Клиент откроет страницу devRefresh. Войдите доменной учёткой и разрешите просмотр или восстановление доступных вам баз.</p>
        <p class="muted">Пароль SQL и токены в чат передавать не нужно. Если клиент использует внешний HTTPS-адрес возврата, администратор должен разрешить его в настройках сервера.</p>
        ${!data.domainConfirmed ? '<a class="button" href="/auth/windows?returnUrl=%2Fconnections">Войти доменной учёткой</a>' : ""}
      </div></section>
      <section class="card"><div class="card-body"><h2>Разрешённые подключения</h2>
      ${data.connections.length ? `<div class="table-wrap"><table><thead><tr><th>Клиент</th><th>Создано</th><th>Разрешения</th><th></th></tr></thead><tbody>${data.connections.map(connection => `<tr>
        <td>${esc(connection.client)}</td><td>${connection.createdAt ? formatDate(connection.createdAt) : "—"}</td>
        <td>${connection.scopes.includes("devrefresh.restore") ? "Просмотр и восстановление" : "Просмотр"}</td>
        <td><button class="button" data-mcp-revoke="${esc(connection.id)}">Отозвать доступ</button></td></tr>`).join("")}</tbody></table></div>` : '<p class="muted">Подключений пока нет.</p>'}
        <p class="muted">Отзыв запрещает новые обращения этого подключения. Уже запущенное восстановление продолжит работу.</p>
      </div></section>`}`;
  } catch (error) {
    if (state.page === "connections") content.innerHTML = `<div class="notice error">${esc(error.message)}</div>`;
  }
}

document.addEventListener("click", async event => {
  const button = event.target.closest("[data-mcp-revoke]");
  if (!button) return;
  button.disabled = true;
  try {
    await api(`/api/mcp/connections/${encodeURIComponent(button.dataset.mcpRevoke)}`, { method: "DELETE" });
    await loadMcpConnections();
    toast("Доступ подключения отозван.");
  } catch (error) { toast(error.message); button.disabled = false; }
});
