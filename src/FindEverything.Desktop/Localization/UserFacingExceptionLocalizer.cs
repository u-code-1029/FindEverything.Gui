using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Desktop.Services;

namespace FindEverything.Desktop.Localization;

internal static class UserFacingExceptionLocalizer
{
    public static string TranslateLanguageChangeFailure(IAppLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Get(
            "Loc.Language.ChangeFailed.Message",
            "언어 설정을 저장하지 못했습니다. 사용자 설정 파일을 확인한 뒤 다시 시도하세요.");
    }

    public static string TranslateOperationFailure(
        IAppLocalizer localizer,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(exception);

        var root = exception.GetBaseException();
        return root switch
        {
            ApplicationOperationBusyException => localizer.Get(
                "Loc.Common.TryAfterOperation",
                "현재 작업이 끝난 뒤 다시 시도하세요."),
            CatalogIndexUnavailableException
            {
                Availability: IndexRootAvailability.DatabaseMissing,
            } unavailable => localizer.Format(
                "Loc.Catalog.Error.IndexDatabaseMissing",
                "인덱스 DB '{0}'가 없습니다. '인덱싱 후 불러오기'를 실행하거나 올바른 DB를 선택하세요.",
                unavailable.DatabasePath),
            CatalogIndexUnavailableException
            {
                Availability: IndexRootAvailability.RootNotIndexed,
            } unavailable => localizer.Format(
                "Loc.Catalog.Error.RootNotIndexed",
                "DB '{1}'에는 검색 위치 '{0}'를 정확한 루트로 만든 인덱스가 없습니다. 같은 검색 위치로 '인덱싱 후 불러오기'를 실행하세요.",
                unavailable.RootPath,
                unavailable.DatabasePath),
            MappedDrivePathResolutionException mappedDrive => localizer.Format(
                "Loc.Path.Error.MappedDriveResolutionFailed",
                "매핑된 네트워크 경로 '{0}'를 UNC 경로로 변환하지 못했습니다. 연결을 다시 확인하거나 UNC 경로를 직접 입력하세요.",
                mappedDrive.MappedPath),
            _ => root.Message,
        };
    }
}
