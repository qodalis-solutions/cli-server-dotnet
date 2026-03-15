namespace Qodalis.Cli.Plugin.Jobs.EfCore;

internal class JobStateEntity
{
    public int Id { get; set; }
    public required string JobId { get; set; }
    public required string Status { get; set; }
    public DateTime? LastRunAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
