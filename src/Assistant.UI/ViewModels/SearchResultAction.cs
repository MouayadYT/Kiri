using System.Windows.Input;

namespace Assistant.UI.ViewModels;

/// <summary>
/// Something a search result can do besides what pressing Enter on it does, by a key of its own while the row is highlighted:
/// a file can be shown in File Explorer, an image attached to a conversation. The bar's editor keeps the keyboard, so the keys
/// are taken by the results (<see cref="SearchResultsViewModel.HandleKey"/>) before the editor sees them.
/// </summary>
/// <param name="Key">The key that does it.</param>
/// <param name="Modifiers">The modifier keys held with it, exactly.</param>
/// <param name="Title">What it does, in words: "Show in Explorer".</param>
/// <param name="Command">What it runs.</param>
public sealed record SearchResultAction(Key Key, ModifierKeys Modifiers, string Title, ICommand Command);

/// <summary>
/// One of the other things a result can do, as it is listed when the user asks for them (<c>Tab</c> on the highlighted row, PROJECT_SPEC
/// §4.1): its words, what it runs and, when it also has a key of its own on the row, that key as it is written on a key cap.
/// </summary>
/// <param name="Title">What it does, in words: "Show in File Explorer".</param>
/// <param name="Command">What it runs.</param>
/// <param name="KeyText">The key that does it straight from the row, as written on its cap, or <see langword="null"/> for none.</param>
public sealed record SearchResultAlternate(string Title, ICommand Command, string? KeyText = null);
