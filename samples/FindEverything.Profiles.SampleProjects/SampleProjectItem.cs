using FindEverything.Profile.Abstractions;

namespace FindEverything.Profiles.SampleProjects;

public sealed class SampleProjectItem
{
    [CaptureField("customer", "customer", Header = "Customer", Order = 10, Required = true)]
    public string Customer { get; set; } = string.Empty;

    [CaptureField("project", "project", Header = "Project", Order = 20, Required = true)]
    public string Project { get; set; } = string.Empty;

    [CaptureField("year", "year", Header = "Year", Order = 30, Required = true)]
    public int Year { get; set; }

    [CaptureField(
        "captured-on",
        "capturedOn",
        Header = "Captured on",
        Order = 40,
        ParseFormat = "yyyyMMdd",
        DisplayFormat = "yyyy-MM-dd")]
    public DateTime? CapturedOn { get; set; }

    [CaptureField("revision", "revision", Header = "Revision", Order = 50)]
    public int? Revision { get; set; }

    [CaptureField("approved", "approved", Header = "Approved", Order = 60)]
    public bool? Approved { get; set; }

    [CaptureField("amount", "amount", Header = "Amount", Order = 70, DisplayFormat = "N2")]
    public decimal? Amount { get; set; }
}
