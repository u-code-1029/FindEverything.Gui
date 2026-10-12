using System.ComponentModel.DataAnnotations;
using FindEverything.Application.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class PresentationSettingsOptionsTests
{
    [Fact]
    public void Presentation_defaults_are_ten_candidates_and_three_seconds()
    {
        Assert.Equal(10, new IndexingOptions().UiUpdateBatchSize);
        Assert.Equal(3, new NotificationOptions().AutoDismissSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void Scan_batch_must_be_between_one_and_one_thousand(int batchSize)
    {
        var options = new IndexingOptions { UiUpdateBatchSize = batchSize };
        Assert.Throws<ValidationException>(() => Validator.ValidateObject(options, new ValidationContext(options), true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Notification_duration_must_be_between_one_and_sixty_seconds(int seconds)
    {
        var options = new NotificationOptions { AutoDismissSeconds = seconds };
        Assert.Throws<ValidationException>(() => Validator.ValidateObject(options, new ValidationContext(options), true));
    }

    [Fact]
    public void Application_registration_binds_and_validates_notifications()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:AutoDismissSeconds"] = "7",
            ["Indexing:UiUpdateBatchSize"] = "25",
        }).Build();
        var services = new ServiceCollection().AddFindEverythingApplication(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(7, provider.GetRequiredService<IValidatedSettingsState<NotificationOptions>>().Current.AutoDismissSeconds);
        Assert.Equal(25, provider.GetRequiredService<IValidatedSettingsState<IndexingOptions>>().Current.UiUpdateBatchSize);
    }

    [Fact]
    public void Invalid_notification_configuration_cannot_be_published_at_startup()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:AutoDismissSeconds"] = "0",
        }).Build();
        var services = new ServiceCollection().AddFindEverythingApplication(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IValidatedSettingsState<NotificationOptions>>());
    }
}
