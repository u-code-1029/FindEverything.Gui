using FindEverything.Desktop.Localization;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.Services;

public enum CatalogMissingDataDecision { Later, BuildFromIndex, Scan }

public interface ICatalogMissingDataPrompt
{
    Task<CatalogMissingDataDecision> ChooseAsync(string profileName, bool hasIndex,
        CancellationToken cancellationToken);
}

public sealed class ContentDialogCatalogMissingDataPrompt(
    IContentDialogService dialogs, IAppLocalizer localizer) : ICatalogMissingDataPrompt
{
    public async Task<CatalogMissingDataDecision> ChooseAsync(string profileName, bool hasIndex,
        CancellationToken cancellationToken)
    {
        var result = await dialogs.ShowAsync(new ContentDialog
        {
            Title = localizer.Get("Loc.Catalog.Missing.Title", "구조화된 결과를 만들까요?"),
            Content = localizer.Format(hasIndex ? "Loc.Catalog.Missing.WithIndex" : "Loc.Catalog.Missing.WithoutIndex",
                hasIndex
                    ? "'{0}'의 저장된 구조화 결과가 없습니다. 기존 인덱스를 해석하거나 새로 스캔할 수 있습니다. 기존 인덱스 해석도 텍스트 컬럼의 파일을 읽을 수 있습니다."
                    : "'{0}'의 저장된 결과와 사용할 인덱스가 없습니다. 선택한 위치를 스캔해야 결과를 만들 수 있습니다.", profileName),
            PrimaryButtonText = localizer.Get(hasIndex ? "Loc.Catalog.Missing.Build" : "Loc.Workspace.Scan",
                hasIndex ? "기존 인덱스로 만들기" : "스캔 시작"),
            SecondaryButtonText = hasIndex ? localizer.Get("Loc.Catalog.Missing.Scan", "새로 스캔") : string.Empty,
            CloseButtonText = localizer.Get("Loc.Catalog.Missing.Later", "나중에"),
            DefaultButton = ContentDialogButton.Close,
        }, cancellationToken).ConfigureAwait(true);
        return result switch
        {
            ContentDialogResult.Primary => hasIndex ? CatalogMissingDataDecision.BuildFromIndex : CatalogMissingDataDecision.Scan,
            ContentDialogResult.Secondary => CatalogMissingDataDecision.Scan,
            _ => CatalogMissingDataDecision.Later,
        };
    }
}
