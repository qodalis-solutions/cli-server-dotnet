using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Extensions;

public class CliBuilder
{
    private readonly IServiceCollection _services;

    internal CliBuilder(IServiceCollection services)
    {
        _services = services;
    }

    public CliBuilder AddProcessor<T>() where T : class, ICliCommandProcessor
    {
        _services.AddSingleton<ICliCommandProcessor, T>();
        return this;
    }

    public CliBuilder AddProcessor(ICliCommandProcessor processor)
    {
        _services.AddSingleton<ICliCommandProcessor>(processor);
        return this;
    }

    public CliBuilder AddProcessorsFromAssembly(Assembly assembly)
    {
        var processorTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(ICliCommandProcessor).IsAssignableFrom(t));

        foreach (var type in processorTypes)
        {
            _services.AddSingleton(typeof(ICliCommandProcessor), type);
        }

        return this;
    }
}
