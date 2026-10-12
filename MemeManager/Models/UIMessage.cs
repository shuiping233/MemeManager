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

public class BrowseFolderRequestedMessage
{
}

public class OpenFolderRequestedMessage(string path)
{
    public string Path { get; } = path;
}

public class CloseSettingsRequestedMessage
{
}

public class AboutRequestedMessage
{
}

public class ProgramExitRequestedMessage
{
}
