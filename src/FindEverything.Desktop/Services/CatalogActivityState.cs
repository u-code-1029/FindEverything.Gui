using CommunityToolkit.Mvvm.ComponentModel;

namespace FindEverything.Desktop.Services;

/// <summary>Shell-visible activity without constructing the catalog's page/session graph.</summary>
public sealed partial class CatalogActivityState : ObservableObject
{
    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isFilesScanning;
}
