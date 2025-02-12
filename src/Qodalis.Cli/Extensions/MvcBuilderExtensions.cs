using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Controllers;

namespace Qodalis.Cli.Extensions;

public static class MvcBuilderExtensions
{
    public static IMvcBuilder AddCli(this IMvcBuilder builder)
    {
        builder.PartManager.ApplicationParts
            .Add(new AssemblyPart(typeof(CliController).Assembly));

        return builder;
    }
}