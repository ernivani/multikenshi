using System.Collections.Generic;

namespace KenshiLauncher.ViewModels;

public class ChangelogEntry
{
    public string Version { get; set; } = "";
    public string Date { get; set; } = "";
    public List<ChangelogLine> Lines { get; set; } = new();
}

public class ChangelogLine
{
    public string Tag { get; set; } = "";
    public string Text { get; set; } = "";

    public ChangelogLine() { }

    public ChangelogLine(string tag, string text)
    {
        Tag = tag;
        Text = text;
    }
}
