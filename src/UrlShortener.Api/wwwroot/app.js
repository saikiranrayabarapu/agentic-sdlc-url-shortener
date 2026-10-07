/* URL Shortener web UI. Plain JavaScript, no build step, no external dependencies.
   Talks to the API on the same origin: POST /api/links, GET /api/links/{code}/stats, DELETE /api/links/{code}.
   Links live on the server (SQLite). The "recent links" list is a per-browser convenience kept in localStorage,
   because the API deliberately has no "list every link" endpoint (it would expose everyone's links to everyone).
   Each entry also keeps the link's manage token (returned once at creation); deleting a link requires it. */
(function () {
  'use strict';

  const STORAGE_KEY = 'urlshortener.recent.v1';
  const MAX_RECENT = 50;

  const byId = (id) => document.getElementById(id);
  const form = byId('shorten-form');
  const urlInput = byId('url');
  const aliasInput = byId('alias');
  const expiresInput = byId('expires');
  const shortenBtn = byId('shorten-btn');
  const result = byId('result');
  const shortUrlInput = byId('short-url');
  const openLink = byId('open-link');
  const resultOriginal = byId('result-original');
  const recentList = byId('recent');
  const emptyRecent = byId('empty-recent');
  const itemTemplate = byId('recent-item');
  const errorEls = { form: byId('form-error'), url: byId('url-error'), alias: byId('alias-error'), expires: byId('expires-error') };

  byId('alias-prefix').textContent = location.origin + '/';

  // ---------------------------------------------------------------- storage (recent list, this browser only)
  function loadRecent() {
    try {
      const parsed = JSON.parse(localStorage.getItem(STORAGE_KEY) || '[]');
      return Array.isArray(parsed) ? parsed : [];
    } catch (_) {
      return [];
    }
  }

  function saveRecent(list) {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(list.slice(0, MAX_RECENT)));
    } catch (_) {
      // Storage disabled or full. The link still exists on the server; only the convenience list is lost.
    }
  }

  function remember(link) {
    const list = loadRecent().filter((l) => l.code !== link.code);
    list.unshift(link);
    saveRecent(list);
    renderRecent();
  }

  function forget(code) {
    saveRecent(loadRecent().filter((l) => l.code !== code));
    renderRecent();
  }

  // ---------------------------------------------------------------- api
  async function api(path, init) {
    const response = await fetch(path, init);
    let body = null;
    if ((response.headers.get('content-type') || '').includes('json')) {
      try { body = await response.json(); } catch (_) { body = null; }
    }
    return { ok: response.ok, status: response.status, body: body };
  }

  const JSON_HEADERS = { 'Accept': 'application/json' };
  function linkPath(code) { return '/api/links/' + encodeURIComponent(code); }
  function statsPath(code) { return linkPath(code) + '/stats'; }

  // ---------------------------------------------------------------- helpers
  function el(tag, text, className) {
    const node = document.createElement(tag);
    if (text !== undefined) node.textContent = text;
    if (className) node.className = className;
    return node;
  }

  function plural(n, word) { return n + ' ' + word + (n === 1 ? '' : 's'); }

  function formatDateTime(iso) {
    const d = new Date(iso);
    return isNaN(d) ? String(iso) : d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
  }

  function formatDay(isoDate) {
    const d = new Date(isoDate + 'T00:00:00');
    return isNaN(d) ? String(isoDate) : d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
  }

  function describe(link) {
    const parts = ['Created ' + formatDateTime(link.createdAt)];
    if (link.isCustomAlias) parts.push('custom alias');
    if (link.expiresAt) {
      const expired = new Date(link.expiresAt) <= new Date();
      parts.push((expired ? 'Expired ' : 'Expires ') + formatDateTime(link.expiresAt));
    }
    return parts.join(' - ');
  }

  function clearErrors() {
    Object.keys(errorEls).forEach((key) => { errorEls[key].textContent = ''; });
  }

  function setBusy(busy) {
    shortenBtn.disabled = busy;
    shortenBtn.textContent = busy ? 'Shortening...' : 'Shorten URL';
  }

  async function copyText(text, button) {
    let copied = false;
    if (navigator.clipboard && window.isSecureContext) {
      try { await navigator.clipboard.writeText(text); copied = true; } catch (_) { copied = false; }
    }
    if (!copied) {
      // Plain http over a LAN is not a secure context, so fall back to the legacy selection-based copy.
      const scratch = el('textarea');
      scratch.value = text;
      scratch.setAttribute('readonly', '');
      scratch.style.position = 'fixed';
      scratch.style.opacity = '0';
      document.body.appendChild(scratch);
      scratch.select();
      try { copied = document.execCommand('copy'); } catch (_) { copied = false; }
      document.body.removeChild(scratch);
    }
    const original = button.textContent;
    button.textContent = copied ? 'Copied' : 'Copy failed';
    button.disabled = true;
    setTimeout(() => { button.textContent = original; button.disabled = false; }, 1500);
  }

  // ---------------------------------------------------------------- shorten form
  function showErrors(status, body) {
    clearErrors();
    if (status === 400 && body && body.errors) {
      const e = body.errors;
      if (e.url) errorEls.url.textContent = e.url.join(' ');
      if (e.customAlias) errorEls.alias.textContent = e.customAlias.join(' ');
      if (e.expiresAt) errorEls.expires.textContent = e.expiresAt.join(' ');
      if (!e.url && !e.customAlias && !e.expiresAt) errorEls.form.textContent = body.title || 'The request was not valid.';
      return;
    }
    if (status === 409) { errorEls.alias.textContent = (body && body.detail) || 'That alias is already taken.'; return; }
    if (status === 429) { errorEls.form.textContent = 'Too many links created in the last minute. Wait a moment and try again.'; return; }
    if (status === 0) { errorEls.form.textContent = 'Could not reach the server. Is it running?'; return; }
    errorEls.form.textContent = (body && (body.detail || body.title)) || ('The server returned status ' + status + '.');
  }

  function showResult(link) {
    shortUrlInput.value = link.shortUrl;
    openLink.href = link.shortUrl;
    resultOriginal.textContent = link.originalUrl;
    result.hidden = false;
    shortUrlInput.focus();
    shortUrlInput.select();
  }

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    clearErrors();

    const url = urlInput.value.trim();
    if (!url) { errorEls.url.textContent = 'Enter a link to shorten.'; urlInput.focus(); return; }

    const payload = { url: url };
    const alias = aliasInput.value.trim();
    if (alias) payload.customAlias = alias;
    if (expiresInput.value) {
      const when = new Date(expiresInput.value);
      if (isNaN(when)) { errorEls.expires.textContent = 'Enter a valid date and time.'; return; }
      payload.expiresAt = when.toISOString();
    }

    setBusy(true);
    try {
      const { ok, status, body } = await api('/api/links', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (!ok) { showErrors(status, body); return; }
      showResult(body);
      remember({
        code: body.code,
        shortUrl: body.shortUrl,
        originalUrl: body.originalUrl,
        createdAt: body.createdAt,
        expiresAt: body.expiresAt,
        isCustomAlias: body.isCustomAlias,
        manageToken: body.manageToken
      });
    } catch (_) {
      showErrors(0, null);
    } finally {
      setBusy(false);
    }
  });

  byId('copy-btn').addEventListener('click', (e) => copyText(shortUrlInput.value, e.currentTarget));

  byId('another-btn').addEventListener('click', () => {
    form.reset();
    clearErrors();
    result.hidden = true;
    urlInput.focus();
  });

  byId('refresh-btn').addEventListener('click', refreshClicks);

  // ---------------------------------------------------------------- recent links
  function renderRecent() {
    const list = loadRecent();
    recentList.replaceChildren();
    emptyRecent.hidden = list.length > 0;
    list.forEach((link) => recentList.appendChild(renderItem(link)));
    refreshClicks();
  }

  function renderItem(link) {
    const node = itemTemplate.content.firstElementChild.cloneNode(true);
    node.dataset.code = link.code;

    const short = node.querySelector('.short');
    short.href = link.shortUrl;
    short.textContent = link.shortUrl;

    const original = node.querySelector('.original');
    original.textContent = link.originalUrl;
    original.title = link.originalUrl;

    node.querySelector('.meta').textContent = describe(link);
    node.querySelector('.clicks').textContent = '... clicks';
    node.querySelector('.copy').addEventListener('click', (e) => copyText(link.shortUrl, e.currentTarget));
    node.querySelector('.stats').addEventListener('click', (e) => toggleStats(node, link, e.currentTarget));
    node.querySelector('.delete').addEventListener('click', () => deleteLink(link));
    return node;
  }

  async function refreshClicks() {
    const items = Array.from(recentList.children);
    await Promise.all(items.map(async (node) => {
      const badge = node.querySelector('.clicks');
      try {
        const { ok, status, body } = await api(statsPath(node.dataset.code), { headers: JSON_HEADERS });
        if (ok) { badge.textContent = plural(body.totalClicks, 'click'); node.classList.remove('missing'); }
        else if (status === 404) { badge.textContent = 'deleted on server'; node.classList.add('missing'); }
        else badge.textContent = 'clicks unavailable';
      } catch (_) {
        badge.textContent = 'clicks unavailable';
      }
    }));
  }

  async function toggleStats(node, link, button) {
    const panel = node.querySelector('.stats-panel');
    if (!panel.hidden) { panel.hidden = true; button.textContent = 'Stats'; return; }

    panel.replaceChildren(el('p', 'Loading...', 'muted'));
    panel.hidden = false;
    button.textContent = 'Hide stats';
    try {
      const { ok, status, body } = await api(statsPath(link.code), { headers: JSON_HEADERS });
      if (!ok) {
        panel.replaceChildren(el('p', status === 404 ? 'This link no longer exists on the server.' : 'Stats are unavailable right now.', 'muted'));
        return;
      }
      renderStats(panel, body);
    } catch (_) {
      panel.replaceChildren(el('p', 'Could not reach the server.', 'muted'));
    }
  }

  function renderStats(panel, stats) {
    const fragment = document.createDocumentFragment();
    const summary = plural(stats.totalClicks, 'click') + ' in total'
      + (stats.lastClickedAt ? ', last on ' + formatDateTime(stats.lastClickedAt) : '');
    fragment.appendChild(el('p', summary, 'stats-summary'));

    const table = el('table', undefined, 'stats-table');
    table.createCaption().textContent = 'Clicks per day, last 7 days';
    const tbody = table.createTBody();
    (stats.clicksPerDay || []).forEach((day) => {
      const row = tbody.insertRow();
      row.insertCell().textContent = formatDay(day.date);
      row.insertCell().textContent = String(day.clicks);
    });
    fragment.appendChild(table);

    fragment.appendChild(el('p', 'Top referrers', 'stats-heading'));
    const referrers = stats.topReferrers || [];
    if (referrers.length === 0) {
      fragment.appendChild(el('p', 'No clicks in the last 7 days.', 'muted'));
    } else {
      const list = el('ul', undefined, 'referrers');
      referrers.forEach((r) => list.appendChild(el('li', r.referrer + ': ' + plural(r.clicks, 'click'))));
      fragment.appendChild(list);
    }
    panel.replaceChildren(fragment);
  }

  async function deleteLink(link) {
    if (!window.confirm('Delete ' + link.shortUrl + '?\nAnyone who opens it afterwards will get "not found".')) return;
    clearErrors();
    try {
      const { ok, status } = await api(linkPath(link.code), {
        method: 'DELETE',
        headers: { 'X-Manage-Token': link.manageToken || '', 'Accept': 'application/json' }
      });
      if (ok || status === 404) forget(link.code);
      else if (status === 403) errorEls.form.textContent = 'This browser does not hold the manage token for ' + link.shortUrl + ', so it cannot delete that link. Only the browser that created a link can delete it.';
      else errorEls.form.textContent = 'Could not delete the link (server returned ' + status + ').';
    } catch (_) {
      errorEls.form.textContent = 'Could not reach the server.';
    }
  }

  // ---------------------------------------------------------------- init
  renderRecent();
  urlInput.focus();
})();
