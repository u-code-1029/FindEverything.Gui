using FindEverything.Profile.Abstractions;

namespace FindEverything.Profiles.SampleProjects;

public sealed class SampleProjectItem
{
    [CaptureField("customer", "customer", Header = "고객", Order = 10, Required = true)]
    public string Customer { get; set; } = string.Empty;

    [CaptureField("project", "project", Header = "프로젝트", Order = 20, Required = true)]
    public string Project { get; set; } = string.Empty;

    [CaptureField("year", "year", Header = "연도", Order = 30, Required = true)]
    public int Year { get; set; }

    [CaptureField(
        "captured-on",
        "capturedOn",
        Header = "수집일",
        Order = 40,
        ParseFormat = "yyyyMMdd",
        DisplayFormat = "yyyy-MM-dd")]
    public DateTime? CapturedOn { get; set; }

    [CaptureField("revision", "revision", Header = "리비전", Order = 50)]
    public int? Revision { get; set; }

    [CaptureField("approved", "approved", Header = "승인", Order = 60)]
    public bool? Approved { get; set; }

    [CaptureField("amount", "amount", Header = "금액", Order = 70, DisplayFormat = "N2")]
    public decimal? Amount { get; set; }
}
