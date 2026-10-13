using System.IO;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ProfileListActivationTests
{
    [Fact]
    public async Task Constructor_does_no_listing_and_a_failed_activation_can_retry()
    {
        var service = new ListingService();
        using var vm = ProfilesValidationTests.CreateForUi(authoring: service);
        Assert.Equal(0, service.Calls);
        var first = vm.ActivateAsync();
        service.Requests[0].SetException(new IOException("Temporarily unavailable"));
        await first;
        vm.Deactivate();
        var second = vm.ActivateAsync();
        Assert.Equal(2, service.Calls);
        service.Requests[1].SetResult([new("new", "New", "new/profile.json")]);
        await second;
        Assert.Equal("new", Assert.Single(vm.EditableProfiles).Id);
    }

    [Fact]
    public async Task Quick_reentry_ignores_an_old_listing_that_finishes_after_the_new_one()
    {
        var service = new ListingService();
        using var vm = ProfilesValidationTests.CreateForUi(authoring: service);
        using var cancellation = new CancellationTokenSource();
        var first = vm.ActivateAsync(cancellation.Token);
        cancellation.Cancel();
        vm.Deactivate();
        var second = vm.ActivateAsync();
        Assert.Equal(2, service.Calls);
        service.Requests[1].SetResult([new("new", "New", "new/profile.json")]);
        await second;
        service.Requests[0].SetResult([new("old", "Old", "old/profile.json")]);
        await first;
        Assert.Equal("new", Assert.Single(vm.EditableProfiles).Id);
    }

    private sealed class ListingService : IProfileAuthoringService
    {
        public List<TaskCompletionSource<IReadOnlyList<EditableProfileSummary>>> Requests { get; } = [];
        public int Calls => Requests.Count;
        public string UserProfilesDirectory => Path.GetTempPath();
        public Task<IReadOnlyList<EditableProfileSummary>> ListAsync(CancellationToken cancellationToken = default)
        {
            var request = new TaskCompletionSource<IReadOnlyList<EditableProfileSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(request);
            return request.Task;
        }
        public Task<ProfileManifest?> LoadAsync(EditableProfileSummary profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ProfileDefinitionReview Validate(ProfileManifest manifest) => throw new NotSupportedException();
        public ProfileDefinitionTestResult Test(ProfileManifest manifest, string samplePath) => throw new NotSupportedException();
        public Task<ProfileSaveResult> SaveAndApplyAsync(ProfileManifest manifest, string? originalProfileId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
