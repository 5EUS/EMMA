using System.Net;
using EMMA.Contracts.Plugins;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Sandboxing;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EMMA.Tests.PluginHost;

public sealed class PluginHandshakeTests
{
    [Fact]
    public async Task HandshakeAllAsync_RegistersHealthyPlugin()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var app = BuildPluginServer<MockPluginControlService>();
        await app.StartAsync();

        var address = GetServerAddress(app);
        var manifestPath = Path.Combine(tempRoot, "demo.plugin.json");
        await File.WriteAllTextAsync(manifestPath, $"{{\n  \"id\": \"demo\",\n  \"name\": \"Demo Plugin\",\n  \"version\": \"1.0.0\",\n  \"protocol\": \"grpc\",\n  \"endpoint\": \"{address}\",\n  \"capabilities\": {{\n    \"cpuBudgetMs\": 300,\n    \"memoryMb\": 256\n  }},\n  \"permissions\": {{\n    \"domains\": [\"example.com\"],\n    \"paths\": [\"data\"]\n  }}\n}}\n");

        var options = Options.Create(new PluginHostOptions
        {
            ManifestDirectory = tempRoot,
            HandshakeOnStartup = true,
            HandshakeTimeoutSeconds = 5,
            SandboxRootDirectory = Path.Combine(tempRoot, "sandbox")
        });

        var registry = new PluginRegistry();
        var sanitizer = new PluginPermissionSanitizer(options, NullLogger<PluginPermissionSanitizer>.Instance);
        var loader = new PluginManifestLoader(options, sanitizer, NullLogger<PluginManifestLoader>.Instance);
        var sandbox = new NoOpPluginSandboxManager(options, NullLogger<NoOpPluginSandboxManager>.Instance);
        var signatureOptions = Options.Create(new PluginSignatureOptions());
        var verifier = new HmacPluginSignatureVerifier(signatureOptions);
        var resolver = new PluginEntrypointResolver(options);
        var endpointAllocator = new PluginEndpointAllocator();
        var processManager = new PluginProcessManager(
            options,
            sandbox,
            resolver,
            signatureOptions,
            verifier,
            NullLogger<PluginProcessManager>.Instance);
        var handshake = new PluginHandshakeService(
            loader,
            registry,
            sandbox,
            processManager,
            sanitizer,
            endpointAllocator,
            options,
            NullLogger<PluginHandshakeService>.Instance);

        await handshake.HandshakeAllAsync(CancellationToken.None);

        var snapshot = registry.GetSnapshot();
        Assert.Single(snapshot);
        Assert.True(snapshot[0].Status.Success);
        Assert.Contains("health", snapshot[0].Status.Capabilities);
        Assert.Equal(300, snapshot[0].Status.CpuBudgetMs);
        Assert.Equal(256, snapshot[0].Status.MemoryMb);
        Assert.Contains("example.com", snapshot[0].Status.Domains);
        var expectedPath = Path.GetFullPath(Path.Combine(options.Value.SandboxRootDirectory, "demo", "data"));
        Assert.Contains(expectedPath, snapshot[0].Status.Paths);

        await app.StopAsync();
        try
        {
            Directory.Delete(tempRoot, true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task HandshakeAllAsync_SanitizesRuntimePermissions()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var app = BuildPluginServer<MockPluginControlServiceRuntimePaths>();
        await app.StartAsync();

        var address = GetServerAddress(app);
        var manifestPath = Path.Combine(tempRoot, "demo.plugin.json");
        await File.WriteAllTextAsync(manifestPath, $"{{\n  \"id\": \"demo\",\n  \"name\": \"Demo Plugin\",\n  \"version\": \"1.0.0\",\n  \"protocol\": \"grpc\",\n  \"endpoint\": \"{address}\",\n  \"capabilities\": {{\n    \"cpuBudgetMs\": 300,\n    \"memoryMb\": 256\n  }}\n}}\n");

        var options = Options.Create(new PluginHostOptions
        {
            ManifestDirectory = tempRoot,
            HandshakeOnStartup = true,
            HandshakeTimeoutSeconds = 5,
            SandboxRootDirectory = Path.Combine(tempRoot, "sandbox")
        });

        var registry = new PluginRegistry();
        var sanitizer = new PluginPermissionSanitizer(options, NullLogger<PluginPermissionSanitizer>.Instance);
        var loader = new PluginManifestLoader(options, sanitizer, NullLogger<PluginManifestLoader>.Instance);
        var sandbox = new NoOpPluginSandboxManager(options, NullLogger<NoOpPluginSandboxManager>.Instance);
        var signatureOptions = Options.Create(new PluginSignatureOptions());
        var verifier = new HmacPluginSignatureVerifier(signatureOptions);
        var resolver = new PluginEntrypointResolver(options);
        var endpointAllocator = new PluginEndpointAllocator();
        var processManager = new PluginProcessManager(
            options,
            sandbox,
            resolver,
            signatureOptions,
            verifier,
            NullLogger<PluginProcessManager>.Instance);
        var handshake = new PluginHandshakeService(
            loader,
            registry,
            sandbox,
            processManager,
            sanitizer,
            endpointAllocator,
            options,
            NullLogger<PluginHandshakeService>.Instance);

        await handshake.HandshakeAllAsync(CancellationToken.None);

        var snapshot = registry.GetSnapshot();
        Assert.Single(snapshot);

        var expectedPath = Path.GetFullPath(Path.Combine(options.Value.SandboxRootDirectory, "demo", "runtime-data"));
        Assert.Single(snapshot[0].Status.Paths);
        Assert.Equal(expectedPath, snapshot[0].Status.Paths[0]);

        await app.StopAsync();
        try
        {
            Directory.Delete(tempRoot, true);
        }
        catch
        {
        }
    }

    private static WebApplication BuildPluginServer<TService>() where TService : class
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
            });
        });

        builder.Services.AddGrpc();

        var app = builder.Build();
        app.MapGrpcService<TService>();

        return app;
    }

    private static string GetServerAddress(IApplicationBuilder app)
    {
        var server = app.ApplicationServices.GetRequiredService<IServer>();
        var addressFeature = server.Features.Get<IServerAddressesFeature>();
        var address = addressFeature?.Addresses.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(address))
        {
            throw new InvalidOperationException("Failed to determine plugin server address.");
        }

        return address;
    }

    private sealed class MockPluginControlService : PluginControl.PluginControlBase
    {
        public override Task<HealthResponse> GetHealth(HealthRequest request, ServerCallContext context)
        {
            return Task.FromResult(new HealthResponse
            {
                Status = "ok",
                Version = "1.0.0",
                Message = "mock"
            });
        }

        public override Task<CapabilitiesResponse> GetCapabilities(CapabilitiesRequest request, ServerCallContext context)
        {
            var response = new CapabilitiesResponse
            {
                Budgets = new CapabilityBudgets
                {
                    CpuBudgetMs = 250,
                    MemoryMb = 128
                },
                Permissions = new CapabilityPermissions()
            };
            response.Capabilities.Add("health");
            response.Capabilities.Add("capabilities");
            response.Permissions.Domains.Add("response.example");
            response.Permissions.Paths.Add("/response");
            return Task.FromResult(response);
        }
    }

    private sealed class MockPluginControlServiceRuntimePaths : PluginControl.PluginControlBase
    {
        public override Task<HealthResponse> GetHealth(HealthRequest request, ServerCallContext context)
        {
            return Task.FromResult(new HealthResponse
            {
                Status = "ok",
                Version = "1.0.0",
                Message = "mock"
            });
        }

        public override Task<CapabilitiesResponse> GetCapabilities(CapabilitiesRequest request, ServerCallContext context)
        {
            var response = new CapabilitiesResponse
            {
                Budgets = new CapabilityBudgets
                {
                    CpuBudgetMs = 250,
                    MemoryMb = 128
                },
                Permissions = new CapabilityPermissions()
            };
            response.Capabilities.Add("health");
            response.Capabilities.Add("capabilities");
            response.Permissions.Paths.Add("/response");
            response.Permissions.Paths.Add("../escape");
            response.Permissions.Paths.Add("runtime-data");
            response.Permissions.Paths.Add(string.Empty);
            response.Permissions.Paths.Add("runtime-data");
            return Task.FromResult(response);
        }
    }
}
