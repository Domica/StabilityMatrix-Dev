using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StabilityMatrix.Avalonia.Models.Inference;

/// <summary>
/// One reference image plus the user's optional per-image note.
///
/// The <see cref="Note"/> is the user's explicit instruction for this specific
/// image — e.g. "the groom", "on the left", "in a red dress". It is appended to
/// the Florence-2 appearance description so Z-Image knows what to align with what
/// (character vs environment, left vs right, etc.). Keeping it on the item means
/// the note travels with the image through add / remove / reorder / save / load.
/// </summary>
public class ReferenceItem : INotifyPropertyChanged
{
    public string Path { get; }

    private string? note;

    public string? Note
    {
        get => note;
        set
        {
            if (!string.Equals(note, value, System.StringComparison.Ordinal))
            {
                note = value;
                OnPropertyChanged();
            }
        }
    }

    public ReferenceItem(string path, string? note = null)
    {
        Path = path;
        this.note = note;
    }

    public override string ToString() => Path;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
