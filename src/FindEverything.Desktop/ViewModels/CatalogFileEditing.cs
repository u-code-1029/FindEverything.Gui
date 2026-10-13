using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public sealed record CatalogTextFileEditContext(CatalogItemViewModel Row, ILoadedProfile Profile,
    ProfileTextFileFieldDescriptor Field, ProfileFieldResourceInfo Resource, string Generation, long Revision);

public partial class CatalogViewModel
{
    public bool CanEditTextFile(CatalogItemViewModel row, string fieldId)
    {
        if (row.IsPlaceholder || !CanEditWorkspace || SelectedProfile is null || _projectionSnapshot is not { IsBuilding: false }) return false;
        var field = SelectedProfile.Profile.Descriptor.TextFileFields.FirstOrDefault(x => x.FieldId.Equals(fieldId, StringComparison.OrdinalIgnoreCase));
        if (field?.EditOptions?.Enabled != true || !row.Resources.TryGetValue(fieldId, out var resource)) return false;
        return resource.State == ProfileFieldResourceState.Present
            || resource.State == ProfileFieldResourceState.Missing && !string.IsNullOrWhiteSpace(field.EditOptions.CreationFileNameTemplate);
    }

    public CatalogTextFileEditContext CaptureTextFileEditContext(CatalogItemViewModel row, string fieldId)
    {
        if (!CanEditTextFile(row, fieldId)) throw new InvalidOperationException(L("Loc.FileEditor.Unavailable", "이 항목은 편집할 수 없습니다. 로컬 결과에서 컬럼을 다시 읽어 확인하세요."));
        return new(row, SelectedProfile!.Profile, SelectedProfile.Profile.Descriptor.TextFileFields.First(x => x.FieldId.Equals(fieldId, StringComparison.OrdinalIgnoreCase)),
            row.Resources[fieldId], _projectionSnapshot!.Generation, _projectionSnapshot.Revision);
    }

    public void ValidateTextFileEditContext(CatalogTextFileEditContext context)
    {
        var snapshot = _projectionSnapshot;
        if (!CanEditWorkspace || !ReferenceEquals(SelectedProfile?.Profile, context.Profile)
            || snapshot is null || snapshot.Generation != context.Generation || snapshot.Revision != context.Revision)
            throw new InvalidOperationException(L("Loc.FileEditor.ContextChanged", "프로필이나 데이터 원본이 변경되어 저장을 중단했습니다. 다시 열어주세요."));
    }

    public void ShowTextFileEditResult(string path, bool cacheUpdated)
    {
        SetStatus(L("Loc.FileEditor.Saved", "파일 저장 완료"), cacheUpdated ? path
            : L("Loc.FileEditor.CacheFailed", "파일은 저장했지만 목록의 캐시 갱신에 실패했습니다. 이 컬럼을 다시 읽어주세요."),
            cacheUpdated ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }
    public void ShowTextFileEditError(Exception exception) => SetStatus(L("Loc.Catalog.Status.Error", "오류"), exception.Message, InfoBarSeverity.Error);
}
