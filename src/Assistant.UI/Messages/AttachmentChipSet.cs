using System.Collections.ObjectModel;

namespace Assistant.UI.Messages;

/// <summary>
/// The chips above a composer, kept in step with what is attached: one for each picture, document and text, in the order they were
/// attached, whatever their kinds. A view model holds its attachments as it likes and tells this set what they are now; a chip that
/// is still attached stays where it is (and stays the same object, so its thumbnail is not loaded again), one that is gone is
/// removed, and a new one joins the end.
/// </summary>
public sealed class AttachmentChipSet
{
    private readonly ObservableCollection<AttachmentChip> _chips = [];

    /// <summary>Creates an empty set.</summary>
    public AttachmentChipSet() => Chips = new ReadOnlyObservableCollection<AttachmentChip>(_chips);

    /// <summary>The chips, oldest first. It reports each one that comes and goes.</summary>
    public ReadOnlyObservableCollection<AttachmentChip> Chips { get; }

    /// <summary>
    /// Brings the chips up to date with <paramref name="attached"/>: the images, documents and texts attached now, matched to the
    /// chips by reference. Anything else in it is ignored.
    /// </summary>
    public void Sync(IEnumerable<object> attached)
    {
        ArgumentNullException.ThrowIfNull(attached);
        var now = attached.Where(item => item is ImageItem or DocumentAttachment or TextAttachment).ToList();
        for (var index = _chips.Count - 1; index >= 0; index--)
        {
            if (!now.Any(item => ReferenceEquals(item, _chips[index].Source)))
            {
                _chips.RemoveAt(index);
            }
        }

        foreach (var item in now.Where(item => !_chips.Any(chip => ReferenceEquals(chip.Source, item))))
        {
            _chips.Add(item switch
            {
                ImageItem image => AttachmentChip.For(image),
                DocumentAttachment document => AttachmentChip.For(document),
                _ => AttachmentChip.For((TextAttachment)item),
            });
        }
    }
}
