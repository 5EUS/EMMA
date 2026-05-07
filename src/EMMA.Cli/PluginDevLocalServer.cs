using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace EMMA.Cli;

public static class PluginDevLocalServer
{
    public static async Task RunAsync(PluginDevApplication application, int port, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
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

        app.MapPost("/api/profiles/select", async (SelectProfileRequest request, PluginDevApplication backend) =>
            await ExecuteAsync(() => Task.FromResult<object>(backend.SelectProfile(request.Name)), backend));

        app.MapPost("/api/build", async (PluginDevApplication backend) =>
            await ExecuteAsync(async () => new MessageResponse(await backend.BuildAsync(CancellationToken.None)), backend));

        app.MapPost("/api/reload", async (PluginDevApplication backend) =>
            await ExecuteAsync(async () => new MessageResponse(await backend.ReloadAsync(CancellationToken.None)), backend));

        app.MapPost("/api/scenarios/run", async (RunScenarioRequest request, PluginDevApplication backend) =>
            await ExecuteAsync(async () => await backend.RunScenarioAsync(request.Name, request.Query, CancellationToken.None), backend));

        application.RecordInfo($"Local session API started at http://127.0.0.1:{port}.");
        cancellationToken.ThrowIfCancellationRequested();
        await app.RunAsync();
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
    private sealed record MessageResponse(string Message);
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
    .diag-item.error { border-color: rgba(185,28,28,0.24); background: rgba(254,242,242,0.8); }
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
      <div class="sub">Phase 5 puts the CLI and browser on the same session backend. Use this page to inspect the active session, switch profiles, run a smoke scenario, and watch operation logs without leaving the repo.</div>
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
              <button id="reload-btn">Reload active runtime</button>
            </div>
          </div>
          <div>
            <h2 class="section-title">Scenario</h2>
            <div class="control-row">
              <input id="scenario-query" value="naruto" aria-label="scenario query" />
              <button class="btn-warm" id="scenario-btn">Run paged-smoke</button>
            </div>
          </div>
        </div></article>
      </section>

      <section class="stack">
        <article class="card"><div class="inner">
          <h2 class="section-title">Diagnostics</h2>
          <div class="diag" id="diagnostics"></div>
        </div></article>

        <article class="card"><div class="inner">
          <div class="log-meta"><span>Operation log</span><span id="log-status">idle</span></div>
          <div class="log-panel" id="logs"></div>
        </div></article>
      </section>
    </main>
  </div>

  <script>
    const sessionMeta = document.getElementById('session-meta');
    const profileSelect = document.getElementById('profile-select');
    const diagnostics = document.getElementById('diagnostics');
    const logs = document.getElementById('logs');
    const logStatus = document.getElementById('log-status');
    const scenarioQuery = document.getElementById('scenario-query');

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
        option.selected = profile.name === session.profile.name;
        profileSelect.appendChild(option);
      });

      diagnostics.innerHTML = '';
      if (!session.diagnostics.length) {
        diagnostics.innerHTML = '<div class="empty">No diagnostics.</div>';
      } else {
        session.diagnostics.forEach(item => {
          const node = document.createElement('div');
          node.className = `diag-item ${item.isError ? 'error' : ''}`;
          node.innerHTML = `<div class="log-meta"><span>${item.isError ? 'error' : 'info'}</span><span>${item.code}</span></div><div>${item.message}</div>`;
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

    async function refresh() {
      const [session, entries] = await Promise.all([
        api('/api/session'),
        api('/api/logs')
      ]);

      renderSession(session);
      renderLogs(entries);
    }

    async function perform(label, action) {
      logStatus.textContent = label;
      try {
        await action();
      } catch (error) {
        alert(error.message);
      } finally {
        logStatus.textContent = 'idle';
        await refresh();
      }
    }

    document.getElementById('profile-switch').addEventListener('click', () => perform('switching profile', () => api('/api/profiles/select', {
      method: 'POST',
      body: JSON.stringify({ name: profileSelect.value })
    })));

    document.getElementById('build-btn').addEventListener('click', () => perform('building', () => api('/api/build', { method: 'POST' })));
    document.getElementById('reload-btn').addEventListener('click', () => perform('reloading', () => api('/api/reload', { method: 'POST' })));
    document.getElementById('scenario-btn').addEventListener('click', () => perform('running scenario', () => api('/api/scenarios/run', {
      method: 'POST',
      body: JSON.stringify({ name: 'paged-smoke', query: scenarioQuery.value })
    })));

    refresh();
    setInterval(refresh, 2500);
  </script>
</body>
</html>
""";
}