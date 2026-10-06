using System.ComponentModel.DataAnnotations;

namespace FindEverything.Desktop.Hosting;

public sealed class HostLifecycleOptions
{
    public const string SectionName = "Host";

    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
