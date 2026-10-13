using System.Globalization;
using System.Windows.Controls;
using System.Windows.Documents;
using System.IO;
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
            fields,
            new TestAppLocalizer());

        var displayedAmount = ((IFormattable)values["amount"]!).ToString(
            "N2",
            CultureInfo.GetCultureInfo("ko-KR"));

        Assert.Equal("2026-10-07", item.DisplayValues["collectedOn"]);
        Assert.Equal(displayedAmount, item.DisplayValues["amount"]);
        Assert.Equal("—", item.DisplayValues["note"]);
        Assert.Same(item.DisplayValues, item.ExportDisplayValues);
        Assert.True(item.Matches("  APOLLO  "));
        Assert.True(item.Matches("2026-10-07"));
        Assert.True(item.Matches(displayedAmount));
        Assert.True(item.Matches("[Literal]"));
        Assert.True(item.Matches("완료"));
        Assert.True(item.Matches("—"));
        Assert.True(item.Matches("Apollo 2026-10-07"));
        Assert.False(item.Matches("Apollo not-present"));
        Assert.False(item.Matches("not-present"));
    }

    [Fact]
    public void Worker_mapping_uses_the_captured_app_culture_not_the_worker_ambient_culture()
    {
        var ambient = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var item = new CatalogItemViewModel(new CatalogItem(@"C:\culture-test", "culture-test", "rule",
                new object(), new Dictionary<string, object?> { ["amount"] = 1234.5m }, false),
                [Field("amount", "Amount", 0, ProfileFieldValueKind.Decimal, "N2")],
                new TestAppLocalizer("en-US"));
            Assert.Equal("1,234.50", item.DisplayValues["amount"]);
        }
        finally { CultureInfo.CurrentCulture = ambient; }
    }

    [Fact]
    public void Catalog_item_uses_mapped_values_by_default_and_switches_every_output_to_raw()
    {
        var field = new ProfileFieldDescriptor(
            "owner",
            "alias",
            "담당자",
            0,
            Required: true,
            ProfileFieldValueKind.String,
            IsNullable: false,
            ParseFormat: null,
            DisplayFormat: null)
        {
            ValueMappings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ZX-42"] = "홍길동",
            },
        };
        var item = new CatalogItemViewModel(
            new CatalogItem(
                @"C:\Archive\A",
                "A",
                "sample",
                new object(),
                new Dictionary<string, object?> { ["owner"] = "ZX-42" },
                CoveragePending: false),
            [field],
            new TestAppLocalizer(
                "en-US",
                new Dictionary<string, string>
                {
                    ["Loc.Common.Completed"] = "Complete",
                    ["Loc.Catalog.Alias.Tooltip"] = "Value mapping: {0} → {1}",
                }));

        Assert.Equal("Complete", item.CoverageText);
        Assert.Equal("홍길동", item.DisplayValues["owner"]);
        Assert.Equal("홍길동", item.ExportDisplayValues["owner"]);
        Assert.Equal("홍길동", item.OutputValues["owner"]);
        Assert.True(item.MappedValueIndicators["owner"]);
        Assert.Equal("Value mapping: ZX-42 → 홍길동", item.MappedValueTooltips["owner"]);
        Assert.True(item.Matches("홍길동"));
        Assert.False(item.Matches("ZX-42"));

        item.SetShowOriginalValues(true);

        Assert.Equal("ZX-42", item.DisplayValues["owner"]);
        Assert.Equal("ZX-42", item.ExportDisplayValues["owner"]);
        Assert.Equal("ZX-42", item.OutputValues["owner"]);
        Assert.True(item.Matches("ZX-42"));
        Assert.False(item.Matches("홍길동"));
    }

    [Fact]
    public void User_input_fields_participate_in_filter_copy_and_export_values()
    {
        var field = new ProfileFieldDescriptor(
            "review-note",
            string.Empty,
            "검토 메모",
            10,
            Required: false,
            ProfileFieldValueKind.String,
            IsNullable: true,
            ParseFormat: null,
            DisplayFormat: null)
        {
            SourceKind = ProfileFieldSourceKind.UserInput,
            Placeholder = "메모 입력",
            DefaultValue = "확인 필요",
        };
        var changes = 0;
        var item = new CatalogItemViewModel(
            new CatalogItem(
                Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-user-input")),
                "catalog-user-input",
                "sample",
                new object(),
                new Dictionary<string, object?>(),
                CoveragePending: false),
            [field],
            new TestAppLocalizer(),
            persistedUserInputValues: null,
            persistUserInput: null,
            userInputChanged: () => changes++);

        Assert.Equal("확인 필요", item.DisplayValues["review-note"]);
        Assert.True(item.Matches("확인"));

        item.UserInputValues["review-note"].Value = "승인 완료";

        Assert.Equal(1, changes);
        Assert.Equal("승인 완료", item.OutputValues["review-note"]);
        Assert.Equal("승인 완료", item.ExportDisplayValues["review-note"]);
        Assert.True(item.Matches("승인"));
        Assert.False(item.Matches("확인 필요"));
    }

    [Fact]
    public async Task User_input_commit_flushes_the_latest_debounced_value()
    {
        var writes = new List<(string FieldId, string Value)>();
        var input = new CatalogUserInputValueViewModel(
            "note",
            "메모 입력",
            string.Empty,
            (fieldId, value) =>
            {
                writes.Add((fieldId, value));
                return Task.CompletedTask;
            },
            static () => { });

        input.Value = "first";
        input.Value = "latest";
        Assert.True(input.HasPendingSave);

        await input.CommitAsync();

        Assert.False(input.HasPendingSave);
        Assert.Equal(("note", "latest"), Assert.Single(writes));
    }

    [Fact]
    public async Task Older_user_input_save_cannot_overwrite_a_newer_commit()
    {
        var firstWriteStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<string>();
        var input = new CatalogUserInputValueViewModel(
            "note",
            null,
            string.Empty,
            async (_, value) =>
            {
                if (value == "first")
                {
                    firstWriteStarted.TrySetResult();
                    await releaseFirstWrite.Task;
                }

                lock (writes)
                {
                    writes.Add(value);
                }
            },
            static () => { });

        input.Value = "first";
        await firstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        input.Value = "latest";
        var latestCommit = input.CommitAsync();
        releaseFirstWrite.TrySetResult();

        await latestCommit.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["first", "latest"], writes);
        Assert.False(input.HasPendingSave);
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

    // WPF-UI resources belong to the composition test's sole Application/STA.
    // Running these assertions there avoids competing resource initialization
    // and shutdown races; assertion failures still fail the smoke test directly.
    internal static void VerifyHighlightingRendering()
    {
        Assert.NotNull(System.Windows.Application.Current);
        System.Windows.Application.Current.Dispatcher.VerifyAccess();
        const string source = "Apollo + apollo [x]";
        var textBlock = new TextBlock();
        TextHighlighting.SetDisplayText(textBlock, source);
        TextHighlighting.SetHighlightText(textBlock, "  APOLLO  ");

        var runs = textBlock.Inlines.OfType<Run>().ToArray();
        Assert.Equal(source, string.Concat(runs.Select(static run => run.Text)));
        Assert.Equal(2, runs.Count(static run => run.Background is not null));

        TextHighlighting.SetHighlightText(textBlock, "APOLLO");
        Assert.Equal(runs, textBlock.Inlines.OfType<Run>().ToArray());

        TextHighlighting.SetHighlightText(textBlock, "Apollo ollo");
        runs = textBlock.Inlines.OfType<Run>().ToArray();
        Assert.Equal(source, string.Concat(runs.Select(static run => run.Text)));
        Assert.Equal(2, runs.Count(static run => run.Background is not null));

        TextHighlighting.SetHighlightText(textBlock, "Apollo [x]");
        runs = textBlock.Inlines.OfType<Run>().ToArray();
        Assert.Equal(source, string.Concat(runs.Select(static run => run.Text)));
        Assert.Equal(3, runs.Count(static run => run.Background is not null));

        TextHighlighting.SetHighlightText(textBlock, "[");
        runs = textBlock.Inlines.OfType<Run>().ToArray();
        Assert.Equal(source, string.Concat(runs.Select(static run => run.Text)));
        Assert.Single(runs, static run => run.Background is not null);

        TextHighlighting.SetHighlightText(textBlock, string.Empty);
        runs = textBlock.Inlines.OfType<Run>().ToArray();
        Assert.Single(runs);
        Assert.Equal(source, runs[0].Text);
        Assert.Null(runs[0].Background);

        TextHighlighting.SetHighlightText(textBlock, "no-match");
        runs = textBlock.Inlines.OfType<Run>().ToArray();
        Assert.Single(runs);
        Assert.Equal(source, textBlock.Text);
        Assert.Null(runs[0].Background);
    }
}
