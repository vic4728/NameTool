using NameTool.Infrastructure;
using NameTool.Models;

namespace NameTool.ViewModels;

public sealed class ReplaceRuleViewModel : ObservableObject
{
    private string _findText = string.Empty;
    private string _replaceText = string.Empty;

    public string FindText
    {
        get => _findText;
        set => SetProperty(ref _findText, value);
    }

    public string ReplaceText
    {
        get => _replaceText;
        set => SetProperty(ref _replaceText, value);
    }

    public ReplaceRule ToModel() => new()
    {
        FindText = FindText,
        ReplaceText = ReplaceText
    };
}
