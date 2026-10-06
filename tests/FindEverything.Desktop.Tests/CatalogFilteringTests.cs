using System.Globalization;
using System.Windows.Controls;
using System.Windows.Documents;
using FindEverything.Application.Catalog;
using FindEverything.Desktop.Behaviors;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class CatalogFilteringTests
{
    [Fact]
    public void Catalog_item_filter_uses_the_same_values_that_are_displayed()
    {
        var fields = new[]
        {
            Field("client", "고객", 0, ProfileFieldValueKind.String),
            Field("collectedOn", "수집일", 1, ProfileFieldValueKind.DateTime, "yyyy-MM-dd"),
            Field("amount", "금액", 2, ProfileFieldValueKind.Decimal, "N2"),
            Field("note", "메모", 3, ProfileFieldValueKind.String),
        };
        var values = new Dictionary<string, object?>
        {
            ["client"] = "Apollo",
            ["collectedOn"] = new DateTime(2026, 10, 7),
            ["amount"] = 125000.5m,
            ["note"] = null,
        };
        var item = new CatalogItemViewModel(
            new CatalogItem(
                @"C:\Archive\Apollo\[Literal]",
                @"Apollo\[Literal]",
                "sample",
                new object(),
                values,
                CoveragePending: false),
            fields);

        var displayedAmount = ((IFormattable)values["amount"]!).ToString(
            "N2",
            CultureInfo.CurrentCulture);

        Assert.Equal("2026-10-07", item.DisplayValues["collectedOn"]);
        Assert.Equal(displayedAmount, item.DisplayValues["amount"]);
        Assert.Equal("—", item.DisplayValues["note"]);
        Assert.True(item.Matches("  APOLLO  "));
        Assert.True(item.Matches("2026-10-07"));
        Assert.True(item.Matches(displayedAmount));
        Assert.True(item.Matches("[Literal]"));
        Assert.True(item.Matches("완료"));
        Assert.True(item.Matches("—"));
        Assert.False(item.Matches("not-present"));
    }

    [Fact]
    public async Task Highlighting_is_case_insensitive_literal_and_preserves_the_source_text()
    {
        var completion = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunHighlightingTest(completion))
        {
            IsBackground = true,
            Name = "FindEverything highlighting test",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var exception = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(exception);
    }

    private static ProfileFieldDescriptor Field(
        string id,
        string header,
        int order,
        ProfileFieldValueKind kind,
        string? displayFormat = null) =>
        new(
            id,
            id,
            header,
            order,
            Required: false,
            kind,
            IsNullable: true,
            ParseFormat: null,
            displayFormat);

    private static void RunHighlightingTest(TaskCompletionSource<Exception?> completion)
    {
        try
        {
            const string source = "Apollo + apollo [x]";
            var textBlock = new TextBlock();
            TextHighlighting.SetDisplayText(textBlock, source);
            TextHighlighting.SetHighlightText(textBlock, "  APOLLO  ");

            var runs = textBlock.Inlines.OfType<Run>().ToArray();
            Assert.Equal(source, string.Concat(runs.Select(static run => run.Text)));
            Assert.Equal(2, runs.Count(static run => run.Background is not null));

            TextHighlighting.SetHighlightText(textBlock, "[");
            runs = textBlock.Inlines.OfType<Run>().ToArray();
            Assert.Equal(source, string.Concat(runs.Select(static run => run.Text)));
            Assert.Single(runs, static run => run.Background is not null);

            TextHighlighting.SetHighlightText(textBlock, string.Empty);
            runs = textBlock.Inlines.OfType<Run>().ToArray();
            Assert.Single(runs);
            Assert.Equal(source, runs[0].Text);
            Assert.Null(runs[0].Background);
            completion.TrySetResult(null);
        }
        catch (Exception exception)
        {
            completion.TrySetResult(exception);
        }
    }
}
