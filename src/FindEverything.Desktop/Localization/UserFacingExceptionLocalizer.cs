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
            MappedDrivePathResolutionException mappedDrive => localizer.Format(
                "Loc.Path.Error.MappedDriveResolutionFailed",
                "매핑된 네트워크 경로 '{0}'를 UNC 경로로 변환하지 못했습니다. 연결을 다시 확인하거나 UNC 경로를 직접 입력하세요.",
                mappedDrive.MappedPath),
            _ => root.Message,
        };
    }
}
