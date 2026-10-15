using FindEverything.Application.Profiles;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class ProfileSampleCancellationTests
{
    [Fact]
    public void Already_cancelled_samples_do_not_probe_paths_or_map_profiles()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canonicalizer = new Canonicalizer();
        var profile = new Profile();

        Assert.Throws<OperationCanceledException>(() => new ProfileSampleEvaluator(canonicalizer)
            .Evaluate(profile, Path.GetTempPath(), cancellation.Token));
        Assert.Equal(0, canonicalizer.Calls);
        Assert.Equal(0, profile.Calls);
    }

    [Fact]
    public void Cancellation_after_unc_canonicalization_prevents_followup_file_mapping()
    {
        using var cancellation = new CancellationTokenSource();
        var canonicalizer = new Canonicalizer { AfterProbe = cancellation.Cancel };
        var profile = new Profile();

        Assert.Throws<OperationCanceledException>(() => new ProfileSampleEvaluator(canonicalizer)
            .Evaluate(profile, Path.GetTempPath(), cancellation.Token));
        Assert.Equal(1, canonicalizer.Calls);
        Assert.Equal(0, profile.Calls);
    }

    [Fact]
    public void The_sample_passes_its_token_to_the_runtime_mapping_contract()
    {
        using var cancellation = new CancellationTokenSource();
        var profile = new TokenAwareProfile();
        var result = new ProfileSampleEvaluator(new Canonicalizer())
            .Evaluate(profile, Path.GetTempPath(), cancellation.Token);
        Assert.Equal(cancellation.Token, profile.Token);
        Assert.Equal(ProfileMapStatus.NoMatch, result.Mapping.Status);
    }

    [Fact]
    public void Legacy_mapping_that_returns_after_cancellation_cannot_publish_a_sample()
    {
        using var cancellation = new CancellationTokenSource();
        var profile = new Profile { AfterMap = cancellation.Cancel };
        Assert.Throws<OperationCanceledException>(() => new ProfileSampleEvaluator(new Canonicalizer())
            .Evaluate(profile, Path.GetTempPath(), cancellation.Token));
        Assert.Equal(1, profile.Calls);
    }

    private sealed class Canonicalizer : IProfilePathCanonicalizer
    {
        public Action? AfterProbe { get; init; }
        public int Calls { get; private set; }
        public string Canonicalize(string path) { Calls++; AfterProbe?.Invoke(); return Path.GetFullPath(path); }
    }
    private class Profile : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new("sample", "1", "Sample", ProfileCandidateKind.Directory, [], []);
        public Action? AfterMap { get; init; }
        public int Calls { get; private set; }
        public ProfileMapResult Map(ProfilePathCandidate candidate) { Calls++; AfterMap?.Invoke(); return ProfileMapResult.NoMatch(); }
    }
    private sealed class TokenAwareProfile : Profile, ILoadedProfile
    {
        public CancellationToken Token { get; private set; }
        public ProfileMapResult Map(ProfilePathCandidate candidate, CancellationToken cancellationToken)
        { Token = cancellationToken; return ProfileMapResult.NoMatch(); }
    }
}
