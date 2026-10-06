using FindEverything.Application.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class ValidatedSettingsStateTests
{
    [Fact]
    public void Current_exposes_the_validated_options_value()
    {
        var services = new ServiceCollection();
        services.AddOptions<IndexingOptions>().Configure(options => options.SearchPageSize = 321);
        services.AddSingleton<IValidatedSettingsState<IndexingOptions>, ValidatedSettingsStateForTest<IndexingOptions>>();

        using var provider = services.BuildServiceProvider();

        Assert.Equal(321, provider.GetRequiredService<IValidatedSettingsState<IndexingOptions>>().Current.SearchPageSize);
    }

    private sealed class ValidatedSettingsStateForTest<T>(IOptions<T> options) : IValidatedSettingsState<T>
        where T : class
    {
        public T Current { get; } = options.Value;
    }
}
