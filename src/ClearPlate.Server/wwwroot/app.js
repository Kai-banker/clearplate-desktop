'use strict';
const $ = id => document.getElementById(id);
let csrf = '', current = null, pendingId = '', editing = false, active = false, toastTimer, authEpoch = 0, slotsRenderKey = '';
async function api(path, body, raw = false, extraHeaders = {}) {
  const headers = {...extraHeaders};
  if (body !== undefined) { headers['X-CSRF-Token'] = csrf; if (!raw) headers['Content-Type'] = 'application/json'; }
  const response = await fetch('/api/' + path, {method: body === undefined ? 'GET' : 'POST', headers, credentials: 'same-origin', body: body === undefined ? undefined : raw ? body : JSON.stringify(body)});
  const result = await response.json().catch(() => ({}));
  if (response.status === 401) { showLogin(); }
  if (!response.ok) throw new Error(result.error || (response.status === 429 ? '尝试过于频繁，请一分钟后再试。' : '请求未完成，请稍后重试。'));
  return result;
}
function toast(message) { $('toast').textContent = message; $('toast').hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => $('toast').hidden = true, 6500); }
function showLogin() { authEpoch++; csrf = ''; current = null; pendingId = ''; editing = false; slotsRenderKey = ''; $('token').value = ''; if ($('confirm-dialog').open) $('confirm-dialog').close(); $('dashboard').hidden = true; $('logout').hidden = true; $('login-panel').hidden = false; }
function render(state) {
  current = state;
  $('dashboard').hidden = false; $('logout').hidden = false; $('login-panel').hidden = true;
  $('day').textContent = state.day + ' · 今日计划'; $('account').textContent = state.nickname;
  $('notice').textContent = state.needsLogin ? '登录已失效，计划暂停。请在已正常登录微信的设备取得新会话，再在右侧更新。' : state.status;
  $('notice').classList.toggle('attention', state.needsLogin || state.persistenceFault);
  if (!editing) { $('group').value = state.groupId; $('location').value = state.location; $('schedule').checked = state.scheduleEnabled; }
  $('session-status').textContent = !state.hasSession ? '尚未配置，先核验本人会话。' : state.needsLogin ? '登录需要更新；服务器不能独立重新登录微信。' : '已保存本人会话，可检查当前是否有效。';
  $('photo-name').textContent = state.photoName;
  const imageUrl = '/api/photo?v=' + encodeURIComponent(state.photoVersion ?? state.photoUpdatedAt);
  if ($('photo').getAttribute('src') !== imageUrl) $('photo').src = imageUrl;
  const renderKey = JSON.stringify([state.slots, state.busy, state.persistenceFault, state.hasSession, state.needsLogin, active, editing]);
  if (renderKey !== slotsRenderKey) {
  slotsRenderKey = renderKey;
  const rows = document.createDocumentFragment();
  for (const slot of state.slots) {
    const row = document.createElement('article'); row.className = 'slot';
    const time = document.createElement('span'); time.className = 'slot-time'; time.textContent = slot.slot;
    const text = document.createElement('div'), title = document.createElement('div'); title.className = 'slot-title';
    const dot = document.createElement('span'); dot.className = 'slot-state' + (slot.state === '成功' ? ' success' : slot.state.includes('未') ? ' attention' : ''); dot.setAttribute('aria-hidden', 'true');
    const label = document.createElement('span'); label.textContent = slot.state; title.append(dot, label);
    const detail = document.createElement('p'); detail.className = 'slot-detail'; detail.textContent = slot.detail; text.append(title, detail);
    const button = document.createElement('button'); button.className = 'quiet'; button.textContent = slot.state === '成功' ? '已完成' : slot.state.includes('可手动') ? '重试' : '立即打卡';
    button.disabled = active || editing || state.busy || state.persistenceFault || !state.hasSession || state.needsLogin || slot.state === '成功';
    if (editing) button.title = '请先保存设置';
    button.setAttribute('aria-label', slot.slot + ' ' + button.textContent);
    button.addEventListener('click', () => action(async () => { const r = await api('submit', {slot: slot.slot}); render(r.state); toast(r.state.status); }));
    row.append(time, text, button); rows.append(row);
  }
  $('slots').replaceChildren(rows);
  }
  $('check').disabled = active || state.busy || state.persistenceFault;
}
async function refresh() { const epoch = authEpoch; const result = await api('state'); if (epoch !== authEpoch) return; csrf = result.csrf; render(result.state); }
async function action(fn) {
  if (active) return; active = true;
  if (current) render(current);
  $('notice').textContent = '正在处理，请稍候…';
  try { await fn(); } catch (error) { toast(error.message); }
  finally { active = false; if (current) { try { await refresh(); } catch (error) { toast(error.message); } } }
}
$('login-form').addEventListener('submit', async event => {
  event.preventDefault(); const button = event.submitter; button.disabled = true; $('login-error').textContent = '';
  try { await api('login', {password: $('password').value}); $('password').value = ''; await refresh(); }
  catch (error) { $('login-error').textContent = error.message; }
  finally { button.disabled = false; }
});
$('logout').addEventListener('click', () => action(async () => { await api('logout', {}); showLogin(); }));
$('settings-form').addEventListener('input', () => { editing = true; if (current) render(current); });
$('settings-form').addEventListener('submit', event => { event.preventDefault(); action(async () => {
  const result = await api('settings', {groupId: $('group').value, location: $('location').value, scheduleEnabled: $('schedule').checked});
  editing = false; render(result.state); toast('设置已保存。');
}); });
$('check').addEventListener('click', () => action(async () => { const result = await api('check', {}); render(result.state); toast(result.needsUpdate ? '请在会话设置中更新本人登录。' : result.state.status); }));
$('session-form').addEventListener('submit', event => { event.preventDefault(); action(async () => {
  const result = await api('session/preview', {token: $('token').value}); $('token').value = ''; pendingId = result.id;
  $('candidate-name').textContent = result.nickname; $('accepted').checked = false; $('confirm-error').textContent = ''; $('confirm-dialog').showModal();
}); });
$('cancel-confirm').addEventListener('click', () => { pendingId = ''; $('confirm-dialog').close(); });
$('confirm-form').addEventListener('submit', async event => {
  event.preventDefault(); if (!$('accepted').checked || active) return;
  const button = event.submitter; button.disabled = true; $('cancel-confirm').disabled = true; active = true;
  try { const result = await api('session/confirm', {id: pendingId, accepted: true}); pendingId = ''; $('confirm-dialog').close(); render(result.state); toast('本人登录已核验并保存。'); }
  catch (error) { $('confirm-error').textContent = error.message; }
  finally { button.disabled = false; $('cancel-confirm').disabled = false; active = false; if (current) render(current); }
});
$('photo-file').addEventListener('change', event => {
  const file = event.target.files[0]; if (!file) return;
  if (file.size < 1024 || file.size > 20 * 1024 * 1024 || !['image/png', 'image/jpeg'].includes(file.type)) { toast('请选择 1 KB 至 20 MB 的 PNG / JPG 照片。'); event.target.value = ''; return; }
  action(async () => { const result = await api('photo', file, true, {'Content-Type': file.type, 'X-Photo-Name': encodeURIComponent(file.name.slice(0, 80))}); render(result.state); toast('照片已保存，后续计划将使用这张照片。'); });
  event.target.value = '';
});
setInterval(() => { if (current) refresh().catch(error => toast(error.message)); }, 10000);
$('server-time').textContent = '北京时间 · 08:00 / 12:00 / 18:00';
refresh().catch(() => showLogin());
