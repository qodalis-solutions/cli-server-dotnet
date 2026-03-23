using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Admin.Auth;
using Qodalis.Cli.Plugin.Admin.Controllers;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

#region Test Helpers

public class TestAuthor : ICliCommandAuthor
{
    public string Name { get; set; } = "Test Author";
    public string Email { get; set; } = "test@example.com";
}

public class TestModule : ICliModule
{
    public string Name { get; set; } = "test-module";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "A test module";
    public ICliCommandAuthor Author { get; set; } = new TestAuthor();
    public IEnumerable<ICliCommandProcessor> Processors { get; set; } = new List<ICliCommandProcessor>
    {
        new TestProcessor("test-cmd", "A test command"),
    };
}

#endregion

#region AdminConfig Tests

public class AdminConfigTests
{
    [Fact]
    public void ValidateCredentials_ValidCredentials_ReturnsTrue()
    {
        var config = new AdminConfig(NullLogger<AdminConfig>.Instance) { Username = "admin", Password = "secret" };
        Assert.True(config.ValidateCredentials("admin", "secret"));
    }

    [Fact]
    public void ValidateCredentials_WrongPassword_ReturnsFalse()
    {
        var config = new AdminConfig(NullLogger<AdminConfig>.Instance) { Username = "admin", Password = "secret" };
        Assert.False(config.ValidateCredentials("admin", "wrong"));
    }

    [Fact]
    public void ValidateCredentials_WrongUsername_ReturnsFalse()
    {
        var config = new AdminConfig(NullLogger<AdminConfig>.Instance) { Username = "admin", Password = "secret" };
        Assert.False(config.ValidateCredentials("wrong", "secret"));
    }

    [Fact]
    public void ValidateCredentials_CaseSensitive()
    {
        var config = new AdminConfig(NullLogger<AdminConfig>.Instance) { Username = "Admin", Password = "Secret" };
        Assert.False(config.ValidateCredentials("admin", "secret"));
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new AdminConfig(NullLogger<AdminConfig>.Instance);
        Assert.Equal("admin", config.Username);
        Assert.Equal("admin", config.Password);
        Assert.Equal(TimeSpan.FromHours(24), config.JwtExpiry);
    }

    [Fact]
    public void GetConfigSections_ReturnsExpectedStructure()
    {
        var config = new AdminConfig(NullLogger<AdminConfig>.Instance) { Username = "testuser" };
        var sections = config.GetConfigSections();
        var json = JsonSerializer.Serialize(sections);

        Assert.Contains("platform", json);
        Assert.Contains("dotnet", json);
        Assert.Contains("testuser", json);
        Assert.Contains("jwtExpiryHours", json);
        Assert.Contains("auth", json);
        Assert.Contains("environment", json);
        Assert.Contains("server", json);
    }

    [Fact]
    public void GetConfigSections_ContainsJwtExpiryHours()
    {
        var config = new AdminConfig(NullLogger<AdminConfig>.Instance) { JwtExpiry = TimeSpan.FromHours(48) };
        var sections = config.GetConfigSections();
        var json = JsonSerializer.Serialize(sections);

        Assert.Contains("48", json);
    }
}

#endregion

#region JwtService Tests

public class JwtServiceTests
{
    private static AdminConfig CreateConfigWithSecret()
    {
        return new AdminConfig(NullLogger<AdminConfig>.Instance)
        {
            Username = "admin",
            Password = "admin",
            JwtSecret = "test-secret-key-that-is-long-enough-for-hmac-sha256",
            JwtExpiry = TimeSpan.FromHours(1),
        };
    }

    [Fact]
    public void GenerateToken_ReturnsNonEmptyString()
    {
        var config = CreateConfigWithSecret();
        var service = new JwtService(config, NullLogger<JwtService>.Instance);

        var token = service.GenerateToken("admin");

        Assert.False(string.IsNullOrEmpty(token));
    }

    [Fact]
    public void GenerateToken_ProducesValidJwtFormat()
    {
        var config = CreateConfigWithSecret();
        var service = new JwtService(config, NullLogger<JwtService>.Instance);

        var token = service.GenerateToken("admin");

        // JWT tokens have 3 parts separated by dots
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
    }

    [Fact]
    public void ValidateToken_ValidToken_ReturnsClaimsPrincipal()
    {
        var config = CreateConfigWithSecret();
        var service = new JwtService(config, NullLogger<JwtService>.Instance);

        var token = service.GenerateToken("testuser");
        var principal = service.ValidateToken(token);

        Assert.NotNull(principal);
        var name = principal.FindFirst(ClaimTypes.Name)?.Value;
        Assert.Equal("testuser", name);
    }

    [Fact]
    public void ValidateToken_ValidToken_ContainsAdminRole()
    {
        var config = CreateConfigWithSecret();
        var service = new JwtService(config, NullLogger<JwtService>.Instance);

        var token = service.GenerateToken("admin");
        var principal = service.ValidateToken(token);

        Assert.NotNull(principal);
        Assert.True(principal.IsInRole("admin"));
    }

    [Fact]
    public void ValidateToken_ValidToken_ContainsAuthenticatedAtClaim()
    {
        var config = CreateConfigWithSecret();
        var service = new JwtService(config, NullLogger<JwtService>.Instance);

        var token = service.GenerateToken("admin");
        var principal = service.ValidateToken(token);

        Assert.NotNull(principal);
        var authenticatedAt = principal.FindFirst("authenticated_at")?.Value;
        Assert.NotNull(authenticatedAt);
    }

    [Fact]
    public void ValidateToken_InvalidToken_ReturnsNull()
    {
        var config = CreateConfigWithSecret();
        var service = new JwtService(config, NullLogger<JwtService>.Instance);

        var principal = service.ValidateToken("invalid.jwt.token");

        Assert.Null(principal);
    }

    [Fact]
    public void ValidateToken_TamperedToken_ReturnsNull()
    {
        var config = CreateConfigWithSecret();
        var service = new JwtService(config, NullLogger<JwtService>.Instance);

        var token = service.GenerateToken("admin");
        // Tamper with the token by modifying a character in the signature
        var tampered = token[..^2] + "xx";

        var principal = service.ValidateToken(tampered);

        Assert.Null(principal);
    }

    [Fact]
    public void ValidateToken_DifferentSecret_ReturnsNull()
    {
        var config1 = CreateConfigWithSecret();
        var service1 = new JwtService(config1, NullLogger<JwtService>.Instance);
        var token = service1.GenerateToken("admin");

        var config2 = new AdminConfig(NullLogger<AdminConfig>.Instance)
        {
            JwtSecret = "completely-different-secret-key-for-another-instance",
        };
        var service2 = new JwtService(config2, NullLogger<JwtService>.Instance);

        var principal = service2.ValidateToken(token);

        Assert.Null(principal);
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("bob")]
    [InlineData("admin")]
    public void GenerateToken_DifferentUsernames_ProduceDifferentTokens(string username)
    {
        var config = CreateConfigWithSecret();
        var service = new JwtService(config, NullLogger<JwtService>.Instance);

        var token = service.GenerateToken(username);
        var principal = service.ValidateToken(token);

        Assert.NotNull(principal);
        Assert.Equal(username, principal.FindFirst(ClaimTypes.Name)?.Value);
    }
}

#endregion

#region LogRingBuffer Tests

public class LogRingBufferTests
{
    [Fact]
    public void Query_EmptyBuffer_ReturnsEmptyResult()
    {
        var buffer = new LogRingBuffer(100);
        var result = buffer.Query();

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public void Add_SingleEntry_CanBeQueried()
    {
        var buffer = new LogRingBuffer(100);
        buffer.Add(new LogEntry { Level = "INFO", Message = "test message", Source = "test" });

        var result = buffer.Query();

        Assert.Single(result.Items);
        Assert.Equal("test message", result.Items[0].Message);
    }

    [Fact]
    public void Query_MultipleEntries_ReturnsInReverseOrder()
    {
        var buffer = new LogRingBuffer(100);
        buffer.Add(new LogEntry { Message = "first" });
        buffer.Add(new LogEntry { Message = "second" });
        buffer.Add(new LogEntry { Message = "third" });

        var result = buffer.Query();

        Assert.Equal(3, result.Total);
        Assert.Equal("third", result.Items[0].Message);
        Assert.Equal("second", result.Items[1].Message);
        Assert.Equal("first", result.Items[2].Message);
    }

    [Fact]
    public void Query_FilterByLevel_ReturnsOnlyMatchingEntries()
    {
        var buffer = new LogRingBuffer(100);
        buffer.Add(new LogEntry { Level = "INFO", Message = "info msg" });
        buffer.Add(new LogEntry { Level = "ERROR", Message = "error msg" });
        buffer.Add(new LogEntry { Level = "INFO", Message = "another info" });

        var result = buffer.Query(level: "ERROR");

        Assert.Single(result.Items);
        Assert.Equal("error msg", result.Items[0].Message);
    }

    [Fact]
    public void Query_FilterByLevel_IsCaseInsensitive()
    {
        var buffer = new LogRingBuffer(100);
        buffer.Add(new LogEntry { Level = "INFO", Message = "info msg" });
        buffer.Add(new LogEntry { Level = "ERROR", Message = "error msg" });

        var result = buffer.Query(level: "error");

        Assert.Single(result.Items);
        Assert.Equal("error msg", result.Items[0].Message);
    }

    [Fact]
    public void Query_FilterBySearch_MatchesMessage()
    {
        var buffer = new LogRingBuffer(100);
        buffer.Add(new LogEntry { Message = "startup complete", Source = "app" });
        buffer.Add(new LogEntry { Message = "request received", Source = "http" });
        buffer.Add(new LogEntry { Message = "startup failed", Source = "db" });

        var result = buffer.Query(search: "startup");

        Assert.Equal(2, result.Total);
    }

    [Fact]
    public void Query_FilterBySearch_MatchesSource()
    {
        var buffer = new LogRingBuffer(100);
        buffer.Add(new LogEntry { Message = "msg1", Source = "MyController" });
        buffer.Add(new LogEntry { Message = "msg2", Source = "OtherService" });

        var result = buffer.Query(search: "Controller");

        Assert.Single(result.Items);
        Assert.Equal("msg1", result.Items[0].Message);
    }

    [Fact]
    public void Query_Pagination_RespectsLimitAndOffset()
    {
        var buffer = new LogRingBuffer(100);
        for (int i = 0; i < 10; i++)
        {
            buffer.Add(new LogEntry { Message = $"msg{i}" });
        }

        var result = buffer.Query(limit: 3, offset: 2);

        Assert.Equal(3, result.Items.Count);
        Assert.Equal(10, result.Total);
        Assert.Equal(3, result.Limit);
        Assert.Equal(2, result.Offset);
    }

    [Fact]
    public void CapacityOverflow_OldEntriesAreOverwritten()
    {
        var buffer = new LogRingBuffer(3);
        buffer.Add(new LogEntry { Message = "first" });
        buffer.Add(new LogEntry { Message = "second" });
        buffer.Add(new LogEntry { Message = "third" });
        buffer.Add(new LogEntry { Message = "fourth" });

        var result = buffer.Query();

        Assert.Equal(3, result.Total);
        // Most recent first — "first" should be gone
        Assert.Equal("fourth", result.Items[0].Message);
        Assert.Equal("third", result.Items[1].Message);
        Assert.Equal("second", result.Items[2].Message);
    }

    [Fact]
    public void Query_CombinedFilters_LevelAndSearch()
    {
        var buffer = new LogRingBuffer(100);
        buffer.Add(new LogEntry { Level = "INFO", Message = "user login", Source = "auth" });
        buffer.Add(new LogEntry { Level = "ERROR", Message = "user login failed", Source = "auth" });
        buffer.Add(new LogEntry { Level = "INFO", Message = "request handled", Source = "http" });

        var result = buffer.Query(level: "INFO", search: "login");

        Assert.Single(result.Items);
        Assert.Equal("user login", result.Items[0].Message);
    }

    [Fact]
    public void LoggerProvider_CreateLogger_ReturnsLogger()
    {
        var buffer = new LogRingBuffer(100);
        var logger = buffer.CreateLogger("TestCategory");

        Assert.NotNull(logger);
    }
}

#endregion

#region ModuleRegistry Tests

public class ModuleRegistryTests
{
    private static ModuleRegistry CreateRegistryWithModules(params ICliModule[] modules)
    {
        return new ModuleRegistry(modules.ToList());
    }

    [Fact]
    public void List_ReturnsAllModules()
    {
        var registry = CreateRegistryWithModules(
            new TestModule { Name = "mod-a", Description = "Module A" },
            new TestModule { Name = "mod-b", Description = "Module B" });

        var plugins = registry.List();

        Assert.Equal(2, plugins.Count);
    }

    [Fact]
    public void List_ContainsCorrectInfo()
    {
        var module = new TestModule
        {
            Name = "weather",
            Version = "2.0.0",
            Description = "Weather plugin",
            Author = new TestAuthor { Name = "John" },
        };
        var registry = CreateRegistryWithModules(module);

        var plugins = registry.List();
        var plugin = plugins[0];

        Assert.Equal("weather", plugin.Id);
        Assert.Equal("weather", plugin.Name);
        Assert.Equal("2.0.0", plugin.Version);
        Assert.Equal("Weather plugin", plugin.Description);
        Assert.Equal("John", plugin.Author);
        Assert.True(plugin.Enabled);
        Assert.Equal(1, plugin.ProcessorCount);
    }

    [Fact]
    public void GetById_ExistingModule_ReturnsPluginInfo()
    {
        var registry = CreateRegistryWithModules(
            new TestModule { Name = "my-plugin" });

        var plugin = registry.GetById("my-plugin");

        Assert.NotNull(plugin);
        Assert.Equal("my-plugin", plugin.Id);
    }

    [Fact]
    public void GetById_CaseInsensitive()
    {
        var registry = CreateRegistryWithModules(
            new TestModule { Name = "MyPlugin" });

        var plugin = registry.GetById("myplugin");

        Assert.NotNull(plugin);
        Assert.Equal("MyPlugin", plugin.Name);
    }

    [Fact]
    public void GetById_NonExistent_ReturnsNull()
    {
        var registry = CreateRegistryWithModules(
            new TestModule { Name = "existing" });

        var plugin = registry.GetById("nonexistent");

        Assert.Null(plugin);
    }

    [Fact]
    public void Toggle_ExistingModule_ReturnsToggleResult()
    {
        var registry = CreateRegistryWithModules(
            new TestModule { Name = "togglable" });

        var result = registry.Toggle("togglable");

        Assert.NotNull(result);
    }

    [Fact]
    public void Toggle_TogglesEnabledState()
    {
        var registry = CreateRegistryWithModules(
            new TestModule { Name = "togglable" });

        Assert.True(registry.IsEnabled("togglable"));

        registry.Toggle("togglable");
        Assert.False(registry.IsEnabled("togglable"));

        registry.Toggle("togglable");
        Assert.True(registry.IsEnabled("togglable"));
    }

    [Fact]
    public void Toggle_NonExistent_ReturnsNull()
    {
        var registry = CreateRegistryWithModules(
            new TestModule { Name = "existing" });

        var result = registry.Toggle("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public void IsEnabled_DefaultTrue()
    {
        var registry = CreateRegistryWithModules(
            new TestModule { Name = "mod" });

        Assert.True(registry.IsEnabled("mod"));
    }

    [Fact]
    public void List_EmptyModules_ReturnsEmptyList()
    {
        var registry = CreateRegistryWithModules();

        var plugins = registry.List();

        Assert.Empty(plugins);
    }

    [Fact]
    public void IsAllowed_EnabledModule_ReturnsTrue()
    {
        var processor = new TestProcessor("test-cmd", "A test command");
        var module = new TestModule
        {
            Name = "test-module",
            Processors = new List<ICliCommandProcessor> { processor },
        };
        var registry = CreateRegistryWithModules(module);

        Assert.True(registry.IsAllowed(processor));
    }

    [Fact]
    public void IsAllowed_DisabledModule_ReturnsFalse()
    {
        var processor = new TestProcessor("test-cmd", "A test command");
        var module = new TestModule
        {
            Name = "test-module",
            Processors = new List<ICliCommandProcessor> { processor },
        };
        var registry = CreateRegistryWithModules(module);

        registry.Toggle("test-module");

        Assert.False(registry.IsAllowed(processor));
    }

    [Fact]
    public void IsAllowed_UnknownProcessor_ReturnsTrue()
    {
        var registry = CreateRegistryWithModules(new TestModule { Name = "mod" });
        var unknownProcessor = new TestProcessor("unknown", "Not in any module");

        Assert.True(registry.IsAllowed(unknownProcessor));
    }

    [Fact]
    public async Task DisabledModule_BlocksCommandExecution()
    {
        var processor = new TestProcessor("weather", "Weather command");
        var module = new TestModule
        {
            Name = "weather-plugin",
            Processors = new List<ICliCommandProcessor> { processor },
        };
        var moduleRegistry = CreateRegistryWithModules(module);

        var commandRegistry = new CliCommandRegistry(NullLogger<CliCommandRegistry>.Instance);
        commandRegistry.Register(processor);

        var executor = new CliCommandExecutorService(
            commandRegistry,
            NullLogger<CliCommandExecutorService>.Instance,
            new ICliProcessorFilter[] { moduleRegistry });

        // Command works when enabled
        var response = await executor.ExecuteAsync(new CliProcessCommand { Command = "weather" });
        Assert.Equal(0, response.ExitCode);

        // Disable the module
        moduleRegistry.Toggle("weather-plugin");

        // Command is blocked
        response = await executor.ExecuteAsync(new CliProcessCommand { Command = "weather" });
        Assert.Equal(1, response.ExitCode);
        var json = JsonSerializer.Serialize(response.Outputs);
        Assert.Contains("disabled", json);

        // Re-enable
        moduleRegistry.Toggle("weather-plugin");

        // Command works again
        response = await executor.ExecuteAsync(new CliProcessCommand { Command = "weather" });
        Assert.Equal(0, response.ExitCode);
    }
}

#endregion

#region StatusController Tests

public class AdminStatusControllerTests
{
    [Fact]
    public void GetStatus_ReturnsOkWithExpectedShape()
    {
        var esm = new CliEventSocketManager(NullLogger<CliEventSocketManager>.Instance);
        var registry = new CliCommandRegistry(NullLogger<CliCommandRegistry>.Instance);
        registry.Register(new TestProcessor("test", "Test command"));

        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var controller = new StatusController(esm, registry, serviceProvider);
        var result = controller.GetStatus() as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);

        Assert.Contains("uptimeSeconds", json);
        Assert.Contains("memoryUsageMb", json);
        Assert.Contains("platform", json);
        Assert.Contains("dotnet", json);
        Assert.Contains("os", json);
        Assert.Contains("activeWsConnections", json);
        Assert.Contains("registeredCommands", json);
        Assert.Contains("startedAt", json);

        esm.Dispose();
    }

    [Fact]
    public void GetStatus_RegisteredCommandsCount_MatchesRegistry()
    {
        var esm = new CliEventSocketManager(NullLogger<CliEventSocketManager>.Instance);
        var registry = new CliCommandRegistry(NullLogger<CliCommandRegistry>.Instance);
        registry.Register(new TestProcessor("cmd1", "Command 1"));
        registry.Register(new TestProcessor("cmd2", "Command 2"));

        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var controller = new StatusController(esm, registry, serviceProvider);
        var result = controller.GetStatus() as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"registeredCommands\":2", json);

        esm.Dispose();
    }

    [Fact]
    public void GetStatus_ActiveWsConnections_ZeroByDefault()
    {
        var esm = new CliEventSocketManager(NullLogger<CliEventSocketManager>.Instance);
        var registry = new CliCommandRegistry(NullLogger<CliCommandRegistry>.Instance);

        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var controller = new StatusController(esm, registry, serviceProvider);
        var result = controller.GetStatus() as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"activeWsConnections\":0", json);

        esm.Dispose();
    }
}

#endregion

#region PluginsController Tests

public class AdminPluginsControllerTests
{
    private static (PluginsController controller, ModuleRegistry registry) CreateController(params ICliModule[] modules)
    {
        var registry = new ModuleRegistry(modules.ToList());
        var controller = new PluginsController(registry);
        return (controller, registry);
    }

    [Fact]
    public void List_ReturnsOkWithPlugins()
    {
        var (controller, _) = CreateController(
            new TestModule { Name = "plugin-a" },
            new TestModule { Name = "plugin-b" });

        var result = controller.List() as OkObjectResult;

        Assert.NotNull(result);
        var plugins = result.Value as List<PluginInfo>;
        Assert.NotNull(plugins);
        Assert.Equal(2, plugins.Count);
    }

    [Fact]
    public void GetById_Existing_ReturnsOk()
    {
        var (controller, _) = CreateController(
            new TestModule { Name = "weather", Version = "1.0.0" });

        var result = controller.GetById("weather") as OkObjectResult;

        Assert.NotNull(result);
        var plugin = result.Value as PluginInfo;
        Assert.NotNull(plugin);
        Assert.Equal("weather", plugin.Name);
    }

    [Fact]
    public void GetById_NonExistent_ReturnsNotFound()
    {
        var (controller, _) = CreateController(
            new TestModule { Name = "weather" });

        var result = controller.GetById("nonexistent");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void Toggle_Existing_ReturnsOk()
    {
        var (controller, _) = CreateController(
            new TestModule { Name = "weather" });

        var result = controller.Toggle("weather") as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("enabled", json);
        Assert.Contains("message", json);
    }

    [Fact]
    public void Toggle_Existing_TogglesState()
    {
        var (controller, registry) = CreateController(
            new TestModule { Name = "weather" });

        Assert.True(registry.IsEnabled("weather"));

        controller.Toggle("weather");
        Assert.False(registry.IsEnabled("weather"));
    }

    [Fact]
    public void Toggle_NonExistent_ReturnsNotFound()
    {
        var (controller, _) = CreateController(
            new TestModule { Name = "weather" });

        var result = controller.Toggle("nonexistent");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void List_EmptyModules_ReturnsEmptyList()
    {
        var (controller, _) = CreateController();

        var result = controller.List() as OkObjectResult;

        Assert.NotNull(result);
        var plugins = result.Value as List<PluginInfo>;
        Assert.NotNull(plugins);
        Assert.Empty(plugins);
    }
}

#endregion

#region ConfigController Tests

public class AdminConfigControllerTests
{
    [Fact]
    public void GetConfig_ReturnsOkWithConfigSections()
    {
        var config = new AdminConfig(NullLogger<AdminConfig>.Instance) { Username = "admin" };
        var controller = new ConfigController(config);

        var result = controller.GetConfig() as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("server", json);
        Assert.Contains("auth", json);
        Assert.Contains("environment", json);
        Assert.Contains("dotnet", json);
    }

    [Fact]
    public void UpdateConfig_UpdatesUsername()
    {
        var config = new AdminConfig(NullLogger<AdminConfig>.Instance) { Username = "admin", Password = "admin" };
        var controller = new ConfigController(config);

        var request = new UpdateConfigRequest
        {
            Auth = new AuthConfigUpdate { Username = "newadmin" }
        };

        var result = controller.UpdateConfig(request) as OkObjectResult;

        Assert.NotNull(result);
        Assert.Equal("newadmin", config.Username);
    }

    [Fact]
    public void UpdateConfig_UpdatesPassword()
    {
        var config = new AdminConfig(NullLogger<AdminConfig>.Instance) { Username = "admin", Password = "admin" };
        var controller = new ConfigController(config);

        var request = new UpdateConfigRequest
        {
            Auth = new AuthConfigUpdate { Password = "newpass" }
        };

        controller.UpdateConfig(request);

        Assert.Equal("newpass", config.Password);
    }

    [Fact]
    public void UpdateConfig_UpdatesJwtExpiryHours()
    {
        var config = new AdminConfig(NullLogger<AdminConfig>.Instance);
        var controller = new ConfigController(config);

        var request = new UpdateConfigRequest
        {
            Auth = new AuthConfigUpdate { JwtExpiryHours = 48 }
        };

        controller.UpdateConfig(request);

        Assert.Equal(TimeSpan.FromHours(48), config.JwtExpiry);
    }

    [Fact]
    public void UpdateConfig_NullAuth_DoesNotThrow()
    {
        var config = new AdminConfig(NullLogger<AdminConfig>.Instance);
        var controller = new ConfigController(config);

        var request = new UpdateConfigRequest { Auth = null };

        var result = controller.UpdateConfig(request) as OkObjectResult;

        Assert.NotNull(result);
        // Config should remain unchanged
        Assert.Equal("admin", config.Username);
    }

    [Fact]
    public void UpdateConfig_InvalidJwtExpiry_DoesNotUpdate()
    {
        var config = new AdminConfig(NullLogger<AdminConfig>.Instance) { JwtExpiry = TimeSpan.FromHours(24) };
        var controller = new ConfigController(config);

        var request = new UpdateConfigRequest
        {
            Auth = new AuthConfigUpdate { JwtExpiryHours = -1 }
        };

        controller.UpdateConfig(request);

        Assert.Equal(TimeSpan.FromHours(24), config.JwtExpiry);
    }

    [Fact]
    public void UpdateConfig_ReturnsSuccessMessage()
    {
        var config = new AdminConfig(NullLogger<AdminConfig>.Instance);
        var controller = new ConfigController(config);

        var request = new UpdateConfigRequest
        {
            Auth = new AuthConfigUpdate { Username = "new" }
        };

        var result = controller.UpdateConfig(request) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("Configuration updated", json);
    }
}

#endregion

#region LogsController Tests

public class AdminLogsControllerTests
{
    private static (LogsController controller, LogRingBuffer buffer) CreateController()
    {
        var buffer = new LogRingBuffer(1000);
        var controller = new LogsController(buffer);
        return (controller, buffer);
    }

    [Fact]
    public void GetLogs_EmptyBuffer_ReturnsEmptyEntries()
    {
        var (controller, _) = CreateController();

        var result = controller.GetLogs() as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":0", json);
        Assert.Contains("\"entries\":", json);
    }

    [Fact]
    public void GetLogs_WithEntries_ReturnsEntries()
    {
        var (controller, buffer) = CreateController();
        buffer.Add(new LogEntry { Level = "INFO", Message = "hello", Source = "test" });
        buffer.Add(new LogEntry { Level = "ERROR", Message = "oops", Source = "test" });

        var result = controller.GetLogs() as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":2", json);
        Assert.Contains("hello", json);
        Assert.Contains("oops", json);
    }

    [Fact]
    public void GetLogs_FilterByLevel_ReturnsFiltered()
    {
        var (controller, buffer) = CreateController();
        buffer.Add(new LogEntry { Level = "INFO", Message = "info1", Source = "src" });
        buffer.Add(new LogEntry { Level = "ERROR", Message = "error1", Source = "src" });

        var result = controller.GetLogs(level: "ERROR") as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":1", json);
        Assert.Contains("error1", json);
    }

    [Fact]
    public void GetLogs_FilterBySearch_ReturnsMatching()
    {
        var (controller, buffer) = CreateController();
        buffer.Add(new LogEntry { Level = "INFO", Message = "startup complete", Source = "app" });
        buffer.Add(new LogEntry { Level = "INFO", Message = "request handled", Source = "http" });

        var result = controller.GetLogs(search: "startup") as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":1", json);
        Assert.Contains("startup complete", json);
    }

    [Fact]
    public void GetLogs_Pagination_RespectsLimitAndOffset()
    {
        var (controller, buffer) = CreateController();
        for (int i = 0; i < 20; i++)
        {
            buffer.Add(new LogEntry { Message = $"msg{i}", Source = "test", Level = "INFO" });
        }

        var result = controller.GetLogs(limit: 5, offset: 3) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":20", json);
        Assert.Contains("\"limit\":5", json);
        Assert.Contains("\"offset\":3", json);
    }

    [Fact]
    public void GetLogs_EntriesContainExpectedFields()
    {
        var (controller, buffer) = CreateController();
        buffer.Add(new LogEntry { Level = "WARN", Message = "warning msg", Source = "svc" });

        var result = controller.GetLogs() as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("timestamp", json);
        Assert.Contains("level", json);
        Assert.Contains("message", json);
        Assert.Contains("source", json);
    }
}

#endregion

#region WsClientsController Tests

public class AdminWsClientsControllerTests
{
    [Fact]
    public void GetClients_NoConnections_ReturnsEmptyWithZeroTotal()
    {
        var esm = new CliEventSocketManager(NullLogger<CliEventSocketManager>.Instance);
        var controller = new WsClientsController(esm);

        var result = controller.GetClients() as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":0", json);
        Assert.Contains("\"clients\":", json);

        esm.Dispose();
    }

    [Fact]
    public void GetClients_ReturnsExpectedShape()
    {
        var esm = new CliEventSocketManager(NullLogger<CliEventSocketManager>.Instance);
        var controller = new WsClientsController(esm);

        var result = controller.GetClients() as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("clients", json);
        Assert.Contains("total", json);

        esm.Dispose();
    }
}

#endregion

#region AuthController Tests

public class AdminAuthControllerTests
{
    private static AuthController CreateAuthController(AdminConfig? config = null)
    {
        config ??= new AdminConfig(NullLogger<AdminConfig>.Instance)
        {
            Username = "admin",
            Password = "secret",
            JwtSecret = "test-secret-key-that-is-long-enough-for-hmac-sha256",
            JwtExpiry = TimeSpan.FromHours(1),
        };
        var jwtService = new JwtService(config, NullLogger<JwtService>.Instance);
        var controller = new AuthController(config, jwtService);

        // Set up HttpContext with a mock
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };

        return controller;
    }

    [Fact]
    public void Login_ValidCredentials_ReturnsOkWithToken()
    {
        var controller = CreateAuthController();

        var result = controller.Login(new LoginRequest
        {
            Username = "admin",
            Password = "secret",
        }) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("token", json);
        Assert.Contains("expiresIn", json);
        Assert.Contains("username", json);
        Assert.Contains("admin", json);
    }

    [Fact]
    public void Login_InvalidCredentials_ReturnsUnauthorized()
    {
        var controller = CreateAuthController();

        var result = controller.Login(new LoginRequest
        {
            Username = "admin",
            Password = "wrong",
        });

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public void Login_InvalidCredentials_ReturnsErrorMessage()
    {
        var controller = CreateAuthController();

        var result = controller.Login(new LoginRequest
        {
            Username = "admin",
            Password = "wrong",
        }) as UnauthorizedObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("INVALID_CREDENTIALS", json);
    }

    [Fact]
    public void Me_WithAuthenticatedUser_ReturnsUserInfo()
    {
        var config = new AdminConfig(NullLogger<AdminConfig>.Instance)
        {
            Username = "admin",
            Password = "secret",
            JwtSecret = "test-secret-key-that-is-long-enough-for-hmac-sha256",
            JwtExpiry = TimeSpan.FromHours(1),
        };
        var jwtService = new JwtService(config, NullLogger<JwtService>.Instance);
        var controller = new AuthController(config, jwtService);

        var httpContext = new DefaultHttpContext();
        // Simulate the middleware setting AdminUser
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "admin"),
            new Claim("authenticated_at", DateTime.UtcNow.ToString("o")),
        };
        var identity = new ClaimsIdentity(claims, "test");
        var principal = new ClaimsPrincipal(identity);
        httpContext.Items["AdminUser"] = principal;

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = controller.Me() as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("admin", json);
        Assert.Contains("authenticatedAt", json);
    }

    [Fact]
    public void Me_WithoutAuthenticatedUser_ReturnsUnauthorized()
    {
        var controller = CreateAuthController();
        // HttpContext.Items["AdminUser"] is not set

        var result = controller.Me();

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public void Me_WithoutAuthenticatedUser_ReturnsUnauthorizedCode()
    {
        var controller = CreateAuthController();

        var result = controller.Me() as UnauthorizedObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("UNAUTHORIZED", json);
    }
}

#endregion
