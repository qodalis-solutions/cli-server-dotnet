namespace Qodalis.Cli.Abstractions;

public interface ICliCommandAuthor
{
    string Name { get; }

    string Email { get; }
}