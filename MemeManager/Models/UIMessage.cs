namespace MemeManager.Models;

public class CloseAppMessage
{

}

public class ResetCategorySplitterMesssage
{

}

public class CategorySplitterEnabledMessage(bool enabled)
{
    public bool Enabled { get; } = enabled;
}
