using EMMA.Contracts.Plugins;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Sandboxing;
using EMMA.PluginHost.Services;
using Grpc.Net.Client;
using System.Net;
using System.Runtime.InteropServices;

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.Configure<PluginHostOptions>(builder.Configuration.GetSection("PluginHost"));
builder.Services.AddSingleton<PluginRegistry>();
builder.Services.AddSingleton<PluginManifestLoader>();
builder.Services.AddSingleton<IPluginSandboxManager>(sp =>
{
	var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PluginHostOptions>>();

	if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) // TODO RuntimeInformation does not report correctly with iOS and Android
	{
		return new WindowsPluginSandboxManager(options, sp.GetRequiredService<ILogger<WindowsPluginSandboxManager>>());
	}

	if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
	{
		return new LinuxPluginSandboxManager(options, sp.GetRequiredService<ILogger<LinuxPluginSandboxManager>>());
	}

	if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
	{
		return new MacOsPluginSandboxManager(options, sp.GetRequiredService<ILogger<MacOsPluginSandboxManager>>());
	}

	return new NoOpPluginSandboxManager(options, sp.GetRequiredService<ILogger<NoOpPluginSandboxManager>>());
});
builder.Services.AddSingleton<PluginHandshakeService>();
builder.Services.AddHostedService<PluginHandshakeHostedService>();

var app = builder.Build();

static PluginRecord? ResolvePluginRecord(PluginRegistry registry, string? pluginId)
{
	var snapshot = registry.GetSnapshot();
	if (snapshot.Count == 0)
	{
		return null;
	}

	if (string.IsNullOrWhiteSpace(pluginId))
	{
		return snapshot[0];
	}

	return snapshot.FirstOrDefault(record =>
		string.Equals(record.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
}

static HttpClient CreateHttpClient(Uri address)
{
	var handler = new SocketsHttpHandler
	{
		EnableMultipleHttp2Connections = true
	};

	var httpClient = new HttpClient(handler)
	{
		BaseAddress = address,
		DefaultRequestVersion = HttpVersion.Version20,
		DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
	};

	return httpClient;
}

app.MapGrpcService<PluginControlService>();
app.MapGet("/plugins", (PluginRegistry registry) => registry.GetSnapshot());
app.MapPost("/plugins/refresh", async (PluginHandshakeService handshake, PluginRegistry registry, CancellationToken cancellationToken) =>
{
	await handshake.RescanAsync(cancellationToken);
	return Results.Ok(registry.GetSnapshot());
});
app.MapGet("/probe/search", async (
	string? query,
	string? pluginId,
	PluginRegistry registry,
	CancellationToken cancellationToken) =>
{
	var record = ResolvePluginRecord(registry, pluginId);
	if (record is null)
	{
		return Results.NotFound(new { message = "No matching plugin record found." });
	}

	if (record.Manifest.Entry is null)
	{
		return Results.Problem("Plugin manifest has no entry.");
	}

	if (!string.Equals(record.Manifest.Entry.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
	{
		return Results.Problem($"Unsupported plugin protocol: {record.Manifest.Entry.Protocol}.");
	}

	if (string.IsNullOrWhiteSpace(record.Manifest.Entry.Endpoint))
	{
		return Results.Problem("Plugin manifest entry is missing endpoint.");
	}

	var address = new Uri(record.Manifest.Entry.Endpoint, UriKind.Absolute);
	using var httpClient = CreateHttpClient(address);
	using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
	{
		HttpClient = httpClient
	});

	var client = new SearchProvider.SearchProviderClient(channel);
	var response = await client.SearchAsync(new SearchRequest
	{
		Query = query ?? string.Empty
	}, cancellationToken: cancellationToken);

	var results = response.Results.Select(result => new
	{
		result.Id,
		result.Source,
		result.Title,
        result.MediaType
	});

	return Results.Ok(new
	{
		PluginId = record.Manifest.Id,
		Query = query ?? string.Empty,
		Count = response.Results.Count,
		Results = results
	});
});
app.MapGet("/", () => "EMMA plugin host is running.");

app.Run();

public partial class Program
{
}
