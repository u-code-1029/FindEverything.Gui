using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class SelectionOutputFormatterTests
{
    [Fact]
    public async Task Streaming_formatter_keeps_separators_even_when_items_render_as_empty_strings()
    {
        var formatter = new SelectionOutputFormatter();
        var format = new SelectionOutputFormatDefinition("test", "Test", "{Field:value}", "|", null);
        var item = SelectionOutputItem.FromPath("example", fields:
            new Dictionary<string, object?> { ["value"] = string.Empty });

        Assert.Equal("||", await formatter.FormatAsync(format, StreamItems(item, 3)));
    }

    [Fact]
    public async Task Streaming_formatter_enforces_the_same_output_limit_as_sync_formatting()
    {
        var formatter = new SelectionOutputFormatter();
        var format = new SelectionOutputFormatDefinition("test", "Test", "{Field:value}", "", null);
        var item = SelectionOutputItem.FromPath("example", fields:
            new Dictionary<string, object?> { ["value"] = new string('x', 1_000_000) });

        await Assert.ThrowsAsync<InvalidOperationException>(() => formatter.FormatAsync(format, StreamItems(item, 34)));
    }

    [Fact]
    public async Task Cancelled_streaming_format_does_not_return_partial_output()
    {
        var formatter = new SelectionOutputFormatter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => formatter.FormatAsync(
            SelectionOutputFormatDefaults.FullPathLines, StreamItems(SelectionOutputItem.FromPath("example"), 2), cancellation.Token));
    }

    private static async IAsyncEnumerable<SelectionOutputItem> StreamItems(SelectionOutputItem item, int count)
    {
        for (var index = 0; index < count; index++) yield return item;
        await Task.CompletedTask;
    }

    [Fact]
    public void Format_replaces_common_and_profile_tokens_for_every_selected_item()
    {
        var formatter = new SelectionOutputFormatter();
        var format = new SelectionOutputFormatDefinition(
            "test",
            "테스트",
            "{Name}|{FolderPath}|{Field:Client}",
            Environment.NewLine,
            "projects");
        var items = new[]
        {
            new SelectionOutputItem(
                @"C:\Data\one.txt",
                @"C:\Data",
                "one.txt",
                new Dictionary<string, object?> { ["client"] = "Contoso" }),
            new SelectionOutputItem(
                @"C:\Data\two.txt",
                @"C:\Data",
                "two.txt",
                new Dictionary<string, object?> { ["Client"] = null }),
        };

        var result = formatter.Format(format, items);

        Assert.Equal(
            $"one.txt|C:\\Data|Contoso{Environment.NewLine}two.txt|C:\\Data|",
            result);
    }

    [Fact]
    public void Format_supports_literal_braces_and_the_built_in_full_path_format()
    {
        var formatter = new SelectionOutputFormatter();
        var custom = new SelectionOutputFormatDefinition(
            "braces",
            "중괄호",
            "{{{Name}}}",
            ", ",
            null);
        var items = new[]
        {
            SelectionOutputItem.FromPath(@"C:\Data\one.txt", @"C:\Data"),
            SelectionOutputItem.FromPath(@"C:\Data\two.txt", @"C:\Data"),
        };

        Assert.Equal("{one.txt}, {two.txt}", formatter.Format(custom, items));
        Assert.Equal(
            $"C:\\Data\\one.txt{Environment.NewLine}C:\\Data\\two.txt",
            formatter.Format(SelectionOutputFormatDefaults.FullPathLines, items));
    }

    [Fact]
    public void Validation_rejects_unknown_tokens_and_fields_outside_the_selected_profile()
    {
        var formatter = new SelectionOutputFormatter();

        var validation = formatter.ValidateTemplate(
            "{Unknown} {Field:Client} {Field:Missing}",
            ["Client"]);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("지원하지 않는 토큰", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.Contains("Missing", StringComparison.Ordinal));
        Assert.DoesNotContain(validation.Errors, error => error.Contains("'Client'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\\r\\n", "\r\n")]
    [InlineData("\\t", "\t")]
    [InlineData(" | ", " | ")]
    [InlineData("\\\\n", "\\n")]
    public void Separator_text_round_trips(string visible, string expected)
    {
        var decoded = OutputFormatsViewModel.DecodeSeparator(visible);

        Assert.Equal(expected, decoded);
        Assert.Equal(visible, OutputFormatsViewModel.EncodeSeparator(decoded));
    }
}
