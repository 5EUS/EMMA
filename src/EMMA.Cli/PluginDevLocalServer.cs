using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EMMA.Cli;

public static class PluginDevLocalServer
{
  private static readonly object BackgroundGate = new();
  private static Task? _backgroundTask;
  private static CancellationTokenSource? _backgroundCancellation;
  private static int? _backgroundPort;

    public static async Task RunAsync(PluginDevApplication application, int port, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.Services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.SerializerOptions.WriteIndented = true;
        });
        builder.Services.AddSingleton(application);

        var app = builder.Build();

        app.MapGet("/", () => Results.Content(PluginDevLocalUi.Html, "text/html"));
        app.MapGet("/api/session", (PluginDevApplication backend) => backend.GetSessionSnapshot());
        app.MapGet("/api/logs", (PluginDevApplication backend) => backend.GetLogs());
        app.MapPost("/api/ui/diagnostics-level", async (UpdateUiDiagnosticsLevelRequest request, PluginDevApplication backend) =>
          await ExecuteAsync(() => Task.FromResult<object>(backend.UpdateUiDiagnosticsLevel(request.DiagnosticsLevel)), backend));
        app.MapPost("/api/logs/clear", async (PluginDevApplication backend) =>
          await ExecuteAsync(() =>
          {
            backend.ClearLogs();
            return Task.FromResult<object>(new MessageResponse("Console cleared."));
          }, backend));

        app.MapPost("/api/profiles/select", async (SelectProfileRequest request, PluginDevApplication backend) =>
            await ExecuteAsync(() => Task.FromResult<object>(backend.SelectProfile(request.Name)), backend));

        app.MapPost("/api/build", async (PluginDevApplication backend) =>
            await ExecuteAsync(async () => new MessageResponse(await backend.BuildAsync(CancellationToken.None)), backend));

        app.MapPost("/api/pack", async (PluginDevApplication backend) =>
          await ExecuteAsync(() => Task.FromResult<object>(backend.Pack()), backend));

        app.MapPost("/api/pack/open-directory", async (PluginDevApplication backend) =>
          await ExecuteAsync(() => Task.FromResult<object>(new OpenDirectoryResponse(backend.OpenPackDirectory())), backend));

        app.MapPost("/api/reload", async (PluginDevApplication backend) =>
            await ExecuteAsync(async () => new MessageResponse(await backend.ReloadAsync(CancellationToken.None)), backend));

        app.MapPost("/api/watch/start", async (PluginDevApplication backend) =>
          await ExecuteAsync(() => Task.FromResult<object>(backend.StartWatch()), backend));

        app.MapPost("/api/watch/stop", async (PluginDevApplication backend) =>
          await ExecuteAsync(() => Task.FromResult<object>(backend.StopWatch()), backend));

        app.MapPost("/api/scenarios/run", async (RunScenarioRequest request, PluginDevApplication backend) =>
            await ExecuteAsync(async () => await backend.RunScenarioAsync(request.Name, request.Query, CancellationToken.None), backend));

        application.RecordInfo($"Local session API started at http://127.0.0.1:{port}.");
        cancellationToken.ThrowIfCancellationRequested();
        await app.RunAsync();
    }

      public static string StartInBackground(PluginDevApplication application, int port)
      {
        lock (BackgroundGate)
        {
          if (_backgroundTask is { IsCompleted: false })
          {
            if (_backgroundPort == port)
            {
              return $"Plugin dev UI already running at http://127.0.0.1:{port}.";
            }

            throw new InvalidOperationException($"Plugin dev UI is already running at http://127.0.0.1:{_backgroundPort}. Stop that session before starting another port.");
          }

          _backgroundCancellation?.Dispose();
          _backgroundCancellation = new CancellationTokenSource();
          _backgroundPort = port;

          var cancellationToken = _backgroundCancellation.Token;
          _backgroundTask = Task.Run(async () =>
          {
            try
            {
              await RunAsync(application, port, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
              application.RecordError($"Local session API stopped unexpectedly: {ex.Message}");
            }
            finally
            {
              lock (BackgroundGate)
              {
                _backgroundCancellation?.Dispose();
                _backgroundCancellation = null;
                _backgroundTask = null;
                _backgroundPort = null;
              }
            }
          }, cancellationToken);

          return $"Serving plugin dev UI at http://127.0.0.1:{port} in the background.";
        }
      }

    private static async Task<IResult> ExecuteAsync(Func<Task<object>> action, PluginDevApplication backend)
    {
        try
        {
            return Results.Json(await action());
        }
        catch (Exception ex)
        {
            backend.RecordError(ex.Message);
            return Results.Json(new ErrorResponse(ex.Message), statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private sealed record SelectProfileRequest(string Name);
    private sealed record RunScenarioRequest(string Name, string? Query);
    private sealed record UpdateUiDiagnosticsLevelRequest(string DiagnosticsLevel);
    private sealed record MessageResponse(string Message);
    private sealed record OpenDirectoryResponse(string Directory);
    private sealed record ErrorResponse(string Error);
}

internal static class PluginDevLocalUi
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>EMMA Plugin Dev</title>
  <style>
    :root {
      --bg: #f3efe6;
      --panel: rgba(255,255,255,0.84);
      --panel-strong: rgba(255,255,255,0.92);
      --ink: #1c1b19;
      --muted: #655f55;
      --accent: #0f766e;
      --accent-2: #d97706;
      --info: #0f766e;
      --warning: #b45309;
      --error: #b91c1c;
      --line: rgba(28,27,25,0.12);
      --shadow: 0 18px 60px rgba(43,34,18,0.12);
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      font-family: "Segoe UI Variable", "Aptos", "Trebuchet MS", sans-serif;
      color: var(--ink);
      background:
        radial-gradient(circle at top left, rgba(15,118,110,0.14), transparent 26%),
        radial-gradient(circle at top right, rgba(217,119,6,0.18), transparent 28%),
        linear-gradient(180deg, #f7f3eb 0%, var(--bg) 100%);
      min-height: 100vh;
    }
    .shell {
      max-width: 1280px;
      margin: 0 auto;
      padding: 28px;
    }
    .hero {
      display: grid;
      gap: 12px;
      margin-bottom: 20px;
    }
    .eyebrow {
      letter-spacing: 0.14em;
      text-transform: uppercase;
      color: var(--accent);
      font-size: 12px;
      font-weight: 700;
    }
    h1 {
      margin: 0;
      font-size: clamp(28px, 4vw, 46px);
      line-height: 1;
      font-family: Georgia, "Iowan Old Style", serif;
      font-weight: 600;
    }
    .sub {
      color: var(--muted);
      max-width: 760px;
      font-size: 15px;
      line-height: 1.5;
    }
    .grid {
      display: grid;
      grid-template-columns: 360px 1fr;
      gap: 20px;
    }
    .card {
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 22px;
      box-shadow: var(--shadow);
      backdrop-filter: blur(12px);
    }
    .card > .inner {
      padding: 18px;
    }
    .stack {
      display: grid;
      gap: 18px;
    }
    .section-title {
      margin: 0 0 12px;
      font-size: 15px;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      color: var(--muted);
    }
    .kv { display: grid; gap: 8px; }
    .kv div { display: grid; gap: 3px; }
    .label { font-size: 12px; color: var(--muted); text-transform: uppercase; letter-spacing: 0.08em; }
    .value { font-size: 14px; }
    .control-row { display: grid; gap: 10px; }
    select, input, button {
      width: 100%;
      border-radius: 14px;
      border: 1px solid var(--line);
      padding: 11px 12px;
      font: inherit;
      background: var(--panel-strong);
      color: var(--ink);
    }
    button {
      cursor: pointer;
      font-weight: 700;
      transition: transform 120ms ease, background 120ms ease;
    }
    button:hover { transform: translateY(-1px); }
    .btn-accent { background: linear-gradient(135deg, var(--accent), #155e75); color: white; border: 0; }
    .btn-warm { background: linear-gradient(135deg, var(--accent-2), #b45309); color: white; border: 0; }
    .inline { display: grid; grid-template-columns: 1fr auto; gap: 10px; }
    .badge-row { display: flex; gap: 8px; flex-wrap: wrap; }
    .badge {
      border-radius: 999px;
      padding: 6px 10px;
      background: rgba(15,118,110,0.1);
      color: var(--accent);
      font-size: 12px;
      font-weight: 700;
    }
    .diag { display: grid; gap: 8px; }
    .diag-item, .log-item {
      border: 1px solid var(--line);
      border-radius: 14px;
      padding: 10px 12px;
      background: rgba(255,255,255,0.55);
    }
    .diag-item { border-left-width: 6px; }
    .diag-item.info { border-color: rgba(15,118,110,0.2); background: rgba(240,253,250,0.88); }
    .diag-item.warning { border-color: rgba(180,83,9,0.24); background: rgba(255,247,237,0.88); }
    .diag-item.error { border-color: rgba(185,28,28,0.24); background: rgba(254,242,242,0.88); }
    .meta-chips { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
    .chip {
      border-radius: 999px;
      padding: 5px 9px;
      font-size: 11px;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      border: 1px solid transparent;
    }
    .chip.info { color: var(--info); background: rgba(15,118,110,0.1); border-color: rgba(15,118,110,0.16); }
    .chip.warning { color: var(--warning); background: rgba(217,119,6,0.12); border-color: rgba(217,119,6,0.18); }
    .chip.error { color: var(--error); background: rgba(185,28,28,0.12); border-color: rgba(185,28,28,0.18); }
    .chip.type { color: var(--muted); background: rgba(101,95,85,0.08); border-color: rgba(101,95,85,0.14); }
    .scenario-help { color: var(--muted); font-size: 13px; line-height: 1.45; }
    .toolbar-row { display: flex; justify-content: space-between; gap: 12px; align-items: center; }
    .toolbar-row button { width: auto; min-width: 132px; }
    .toolbar-inline { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
    .toolbar-inline label {
      color: var(--muted);
      font-size: 12px;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      font-weight: 700;
    }
    .toolbar-inline select { width: auto; min-width: 160px; padding-right: 30px; }
    .log-panel {
      min-height: 320px;
      max-height: 640px;
      overflow: auto;
      display: grid;
      gap: 8px;
    }
    .log-meta {
      display: flex;
      justify-content: space-between;
      gap: 10px;
      margin-bottom: 4px;
      color: var(--muted);
      font-size: 11px;
      text-transform: uppercase;
      letter-spacing: 0.08em;
    }
    .log-message {
      font-family: "Cascadia Code", "Consolas", monospace;
      white-space: pre-wrap;
      font-size: 12px;
      line-height: 1.5;
    }
    .empty { color: var(--muted); font-size: 14px; }
    @media (max-width: 960px) {
      .grid { grid-template-columns: 1fr; }
      .shell { padding: 18px; }
    }
  </style>
</head>
<body>
  <div class="shell">
    <header class="hero">
      <div class="eyebrow">EMMA Plugin Dev Platform</div>
      <h1>Session API and browser control surface</h1>
      <div class="sub">Refer to the documentation for more details.</div>
    </header>

    <main class="grid">
      <section class="stack">
        <article class="card"><div class="inner">
          <h2 class="section-title">Session</h2>
          <div class="kv" id="session-meta"></div>
        </div></article>

        <article class="card"><div class="inner stack">
          <div>
            <h2 class="section-title">Profile</h2>
            <div class="control-row">
              <select id="profile-select"></select>
              <button class="btn-accent" id="profile-switch">Switch profile</button>
            </div>
          </div>
          <div>
            <h2 class="section-title">Actions</h2>
            <div class="control-row">
              <button id="build-btn">Build active profile</button>
              <button id="pack-btn">Pack active profile</button>
              <button id="open-pack-dir-btn">Open pack directory</button>
              <button id="reload-btn">Reload active runtime</button>
            </div>
          </div>
          <div>
            <h2 class="section-title">Watch</h2>
            <div class="kv" id="watch-meta"></div>
            <div class="control-row">
              <button id="watch-start-btn">Start watch</button>
              <button id="watch-stop-btn">Stop watch</button>
            </div>
          </div>
          <div>
            <h2 class="section-title">Scenario</h2>
            <div class="control-row">
              <select id="scenario-select" aria-label="scenario selection"></select>
              <div class="scenario-help" id="scenario-description"></div>
              <input id="scenario-query" value="naruto" aria-label="scenario query" />
              <button class="btn-warm" id="scenario-btn">Run scenario</button>
            </div>
          </div>
        </div></article>
      </section>

      <section class="stack">
        <article class="card"><div class="inner">
          <div class="toolbar-row">
            <h2 class="section-title">Diagnostics</h2>
            <div class="toolbar-inline">
              <label for="diagnostics-level">Level</label>
              <select id="diagnostics-level" aria-label="diagnostics level">
                <option value="info">Info+</option>
                <option value="warning">Warning+</option>
                <option value="error">Error only</option>
              </select>
            </div>
          </div>
          <div class="diag" id="diagnostics"></div>
        </div></article>

        <article class="card"><div class="inner">
          <div class="toolbar-row">
            <div class="log-meta"><span>Operation log</span><span id="log-status">idle</span></div>
            <button id="clear-logs-btn">Clear console</button>
          </div>
          <div class="log-panel" id="logs"></div>
        </div></article>
      </section>
    </main>
  </div>

  <script>
    const sessionMeta = document.getElementById('session-meta');
    const profileSelect = document.getElementById('profile-select');
    const watchMeta = document.getElementById('watch-meta');
    const diagnostics = document.getElementById('diagnostics');
    const diagnosticsLevel = document.getElementById('diagnostics-level');
    const logs = document.getElementById('logs');
    const logStatus = document.getElementById('log-status');
    const scenarioSelect = document.getElementById('scenario-select');
    const scenarioDescription = document.getElementById('scenario-description');
    const scenarioQuery = document.getElementById('scenario-query');
    let refreshEpoch = 0;
    let suppressRefreshRender = false;
    let lastSessionProfileName = null;
    let pendingProfileName = null;
    let profileSwitchInFlight = false;
    let lastScenarioName = null;
    let diagnosticsLevelInFlight = false;

    const severityRank = { info: 0, warning: 1, error: 2 };

    function normalizeSeverity(item) {
      return (item.severity || (item.isError ? 'error' : 'info') || 'info').toLowerCase();
    }

    function normalizeDiagnosticsLevel(level) {
      return severityRank[level] === undefined ? 'info' : level;
    }

    function shouldRenderDiagnostic(item, minimumLevel) {
      const severity = normalizeSeverity(item);
      return (severityRank[severity] ?? 0) >= (severityRank[minimumLevel] ?? 0);
    }

    function renderScenarioCatalog(session) {
      const scenarios = session.scenarios || [];
      const selectedName = scenarios.some(item => item.name === lastScenarioName)
        ? lastScenarioName
        : (scenarios[0]?.name || '');

      scenarioSelect.innerHTML = '';
      scenarios.forEach(item => {
        const option = document.createElement('option');
        option.value = item.name;
        option.textContent = item.displayName;
        option.selected = item.name === selectedName;
        scenarioSelect.appendChild(option);
      });

      scenarioSelect.disabled = scenarios.length === 0;
      scenarioQuery.disabled = scenarios.length === 0;
      document.getElementById('scenario-btn').disabled = scenarios.length === 0;

      const selected = scenarios.find(item => item.name === selectedName) || scenarios[0] || null;
      if (!selected) {
        lastScenarioName = null;
        scenarioDescription.textContent = 'No scenarios are available for the active runtime adapter.';
        scenarioQuery.value = '';
        scenarioQuery.placeholder = 'Scenario query';
        return;
      }

      lastScenarioName = selected.name;
      scenarioDescription.textContent = selected.description;
      scenarioQuery.placeholder = selected.queryLabel || 'Query';
      scenarioQuery.disabled = !selected.supportsQuery;
      if (!scenarioQuery.value || scenarioQuery.dataset.scenario !== selected.name) {
        scenarioQuery.value = selected.defaultQuery || '';
      }

      scenarioQuery.dataset.scenario = selected.name;
    }

    async function api(path, options = {}) {
      const response = await fetch(path, {
        headers: { 'content-type': 'application/json', ...(options.headers || {}) },
        ...options
      });

      const body = await response.json().catch(() => ({}));
      if (!response.ok) {
        throw new Error(body.error || response.statusText || 'Request failed');
      }

      return body;
    }

    function renderSession(session) {
      lastSessionProfileName = session.profile.name;
      if (pendingProfileName === session.profile.name) {
        pendingProfileName = null;
      }

      const availableProfileNames = new Set(session.availableProfiles.map(profile => profile.name));
      const selectedProfileName = pendingProfileName && availableProfileNames.has(pendingProfileName)
        ? pendingProfileName
        : session.profile.name;

      sessionMeta.innerHTML = '';
      const fields = [
        ['Session', session.id],
        ['State', session.state],
        ['Profile', session.profile.name],
        ['Plugin', session.pluginId],
        ['Host', session.profile.hostUrl],
        ['Runtime', `${session.profile.runtimeTarget} / ${session.profile.executionMode}`],
        ['Adapter', session.runtimeAdapterName],
        ['Manifest', session.manifestPath || '<not found>']
      ];

      fields.forEach(([label, value]) => {
        const wrap = document.createElement('div');
        wrap.innerHTML = `<div class="label">${label}</div><div class="value">${value}</div>`;
        sessionMeta.appendChild(wrap);
      });

      profileSelect.innerHTML = '';
      session.availableProfiles.forEach(profile => {
        const option = document.createElement('option');
        option.value = profile.name;
        option.textContent = `${profile.name} (${profile.runtimeTarget}/${profile.executionMode})`;
        option.selected = profile.name === selectedProfileName;
        profileSelect.appendChild(option);
      });

      watchMeta.innerHTML = '';
      const watchFields = [
        ['Status', session.watch.status],
        ['Enabled', String(session.watch.isEnabled)],
        ['Behavior', session.watch.behavior],
        ['Globs', session.watch.watchGlobs.length ? session.watch.watchGlobs.join(', ') : '<none>'],
        ['Last Change', session.watch.lastChangedUtc ? `${new Date(session.watch.lastChangedUtc).toLocaleTimeString()} · ${session.watch.lastChangedPath}` : '<none>'],
        ['Last Reload', session.watch.lastReloadUtc ? `${new Date(session.watch.lastReloadUtc).toLocaleTimeString()} · ${session.watch.lastReloadMessage}` : '<none>']
      ];

      watchFields.forEach(([label, value]) => {
        const wrap = document.createElement('div');
        wrap.innerHTML = `<div class="label">${label}</div><div class="value">${value}</div>`;
        watchMeta.appendChild(wrap);
      });

      renderScenarioCatalog(session);

      const selectedDiagnosticsLevel = normalizeDiagnosticsLevel(session.ui?.diagnosticsLevel || diagnosticsLevel.value || 'info');
      if (!diagnosticsLevelInFlight) {
        diagnosticsLevel.value = selectedDiagnosticsLevel;
      }

      diagnostics.innerHTML = '';
      const visibleDiagnostics = session.diagnostics.filter(item => shouldRenderDiagnostic(item, selectedDiagnosticsLevel));
      if (!visibleDiagnostics.length) {
        diagnostics.innerHTML = '<div class="empty">No diagnostics.</div>';
      } else {
        visibleDiagnostics.forEach(item => {
          const node = document.createElement('div');
          const severity = normalizeSeverity(item);
          const type = (item.type || 'general').toLowerCase();
          node.className = `diag-item ${severity}`;
          node.innerHTML = `<div class="log-meta"><div class="meta-chips"><span class="chip ${severity}">${severity}</span><span class="chip type">${type}</span></div><span>${item.code}</span></div><div>${item.message}</div>`;
          diagnostics.appendChild(node);
        });
      }
    }

    function renderLogs(entries) {
      logs.innerHTML = '';
      if (!entries.length) {
        logs.innerHTML = '<div class="empty">No log entries yet.</div>';
        return;
      }

      entries.slice().reverse().forEach(entry => {
        const node = document.createElement('div');
        node.className = 'log-item';
        node.innerHTML = `<div class="log-meta"><span>${entry.level}</span><span>${new Date(entry.timestampUtc).toLocaleTimeString()}</span></div><div class="log-message"></div>`;
        node.querySelector('.log-message').textContent = entry.message;
        logs.appendChild(node);
      });
    }

    function deriveStatusLabel(session, entries) {
      const latestInfo = entries.length ? entries[entries.length - 1] : null;
      const latestMessage = latestInfo?.message || '';

      if (session.watch.status === 'error') {
        return 'watch error';
      }

      if (session.watch.status === 'change-detected') {
        return 'watch change detected';
      }

      if (session.watch.status === 'reload-pending') {
        return 'watch reload pending';
      }

      if (session.watch.status === 'reloading') {
        return latestMessage.startsWith('Watch build started')
          ? 'watch building'
          : 'watch reloading';
      }

      if (latestInfo) {
        const ageMs = Date.now() - new Date(latestInfo.timestampUtc).getTime();
        if (ageMs <= 15000) {
          if (latestMessage.startsWith('Watch build completed')) {
            return 'watch build completed';
          }

          if (latestMessage.startsWith('Watch reload completed')) {
            return 'watch reload completed';
          }

          if (latestMessage.startsWith('Watch detected')) {
            return 'watch change detected';
          }
        }
      }

      return session.watch.isEnabled ? 'watching' : 'idle';
    }

    async function refresh() {
      const currentEpoch = ++refreshEpoch;
      const [session, entries] = await Promise.all([
        api('/api/session'),
        api('/api/logs')
      ]);

      if (suppressRefreshRender || currentEpoch !== refreshEpoch) {
        return;
      }

      renderSession(session);
      renderLogs(entries);
      logStatus.textContent = deriveStatusLabel(session, entries);
    }

    async function perform(label, action) {
      suppressRefreshRender = true;
      refreshEpoch += 1;
      logStatus.textContent = label;
      try {
        await action();
      } catch (error) {
        alert(error.message);
      } finally {
        suppressRefreshRender = false;
        logStatus.textContent = 'idle';
        await refresh();
      }
    }

    async function switchProfile(name) {
      if (!name || profileSwitchInFlight || name === lastSessionProfileName) {
        pendingProfileName = null;
        return;
      }

      pendingProfileName = name;
      profileSwitchInFlight = true;
      try {
        await perform('switching profile', () => api('/api/profiles/select', {
          method: 'POST',
          body: JSON.stringify({ name })
        }));
        pendingProfileName = null;
      } finally {
        profileSwitchInFlight = false;
      }
    }

    profileSelect.addEventListener('change', () => {
      pendingProfileName = profileSelect.value;
      return switchProfile(profileSelect.value);
    });

    document.getElementById('profile-switch').addEventListener('click', () => switchProfile(profileSelect.value));

    document.getElementById('build-btn').addEventListener('click', () => perform('building', () => api('/api/build', { method: 'POST' })));
    document.getElementById('pack-btn').addEventListener('click', () => perform('packing', () => api('/api/pack', { method: 'POST' })));
    document.getElementById('open-pack-dir-btn').addEventListener('click', () => perform('opening pack directory', () => api('/api/pack/open-directory', { method: 'POST' })));
    document.getElementById('reload-btn').addEventListener('click', () => perform('reloading', () => api('/api/reload', { method: 'POST' })));
    document.getElementById('watch-start-btn').addEventListener('click', () => perform('starting watch', () => api('/api/watch/start', { method: 'POST' })));
    document.getElementById('watch-stop-btn').addEventListener('click', () => perform('stopping watch', () => api('/api/watch/stop', { method: 'POST' })));
    document.getElementById('clear-logs-btn').addEventListener('click', () => perform('clearing console', () => api('/api/logs/clear', { method: 'POST' })));
    diagnosticsLevel.addEventListener('change', async () => {
      diagnosticsLevelInFlight = true;
      try {
        await perform('saving diagnostics filter', () => api('/api/ui/diagnostics-level', {
          method: 'POST',
          body: JSON.stringify({ diagnosticsLevel: diagnosticsLevel.value })
        }));
      } finally {
        diagnosticsLevelInFlight = false;
      }
    });
    scenarioSelect.addEventListener('change', () => {
      lastScenarioName = scenarioSelect.value;
      scenarioQuery.dataset.scenario = '';
      refresh();
    });
    document.getElementById('scenario-btn').addEventListener('click', () => perform('running scenario', () => api('/api/scenarios/run', {
      method: 'POST',
      body: JSON.stringify({ name: scenarioSelect.value, query: scenarioQuery.value })
    })));

    refresh();
    setInterval(refresh, 2500);
  </script>
</body>
</html>
""";
}