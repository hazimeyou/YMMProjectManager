namespace YMMProjectManager.Presentation.ViewModels;

public sealed class SelectionOption<T>
{
    public T Value { get; }
    public string Label { get; }

    public SelectionOption(T value, string label)
    {
        Value = value;
        Label = label;
    }
}
