using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Assistant.Core.Audit;
using Assistant.Core.Home;
using Assistant.Core.Tools;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Messages;

/// <summary>
/// A device of the user's home that the Assistant has just switched or set, as the conversation shows it (the home reference): a round button with
/// the device's glyph, its name, and what it is now. Pressing the glyph switches the device itself, at once, since that is the user's own press;
/// pressing anywhere else on it opens what the Assistant did to get there, step by step. While the answer is only this (the request was "turn off
/// the fan"), the window shows it alone, as a small pill, and opens into the conversation when it is pressed.
/// </summary>
/// <remarks>
/// The steps are the run's own (<see cref="AgentTaskSnapshot"/>), which hold no private content. The device's name is the user's, so nothing of this
/// reaches <see cref="object.ToString"/>.
/// </remarks>
public sealed class HomeDeviceContent : MessageContent, INotifyPropertyChanged, IDisposable
{
    private static readonly HashSet<string> OnStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "on", "open", "opening", "unlocked", "heat", "cool", "heat_cool", "auto", "dry", "fan_only", "playing", "cleaning", "home",
    };

    private static readonly HashSet<string> Switched = new(StringComparer.Ordinal)
    {
        "light", "switch", "fan", "climate", "media_player", "input_boolean", "humidifier", "siren", "remote", "automation", "group", "cover", "lock", "vacuum",
    };

    private static readonly Dictionary<string, Geometry> Glyphs = new(StringComparer.Ordinal)
    {
        ["fan"] = Glyph(
            "M 8,1 A 6,6 0 1 0 8,13 A 6,6 0 1 0 8,1 Z M 7,7 A 1,1 0 1 0 9,7 A 1,1 0 1 0 7,7 Z M 8,6 C 6.5,4.5 7,3 8,2.8 C 9,3 9.5,4.5 8,6 Z "
            + "M 9,7 C 10.5,5.5 12,6 12.2,7 C 12,8 10.5,8.5 9,7 Z M 8,8 C 9.5,9.5 9,11 8,11.2 C 7,11 6.5,9.5 8,8 Z M 7,7 C 5.5,8.5 4,8 3.8,7 C 4,6 5.5,5.5 7,7 Z "
            + "M 8,13 L 8,15 M 5.5,15 L 10.5,15"),
        ["light"] = Glyph("M 8,1.5 A 4.5,4.5 0 0 0 5.6,9.8 L 5.6,11.5 L 10.4,11.5 L 10.4,9.8 A 4.5,4.5 0 0 0 8,1.5 Z M 6.3,13.2 L 9.7,13.2 M 7,14.8 L 9,14.8"),
        ["climate"] = Glyph("M 6.5,3 A 1.5,1.5 0 0 1 9.5,3 L 9.5,9.2 A 3,3 0 1 1 6.5,9.2 Z M 8,6 L 8,10.2 M 7,11.2 A 1,1 0 1 0 9,11.2 A 1,1 0 1 0 7,11.2 Z"),
        ["lock"] = Glyph("M 4,7.5 L 12,7.5 L 12,14 L 4,14 Z M 5.8,7.5 L 5.8,5 A 2.2,2.2 0 0 1 10.2,5 L 10.2,7.5"),
        ["cover"] = Glyph("M 2.5,2.5 L 13.5,2.5 M 3.5,5.5 L 12.5,5.5 M 3.5,8.5 L 12.5,8.5 M 3.5,11.5 L 12.5,11.5 M 8,11.5 L 8,14.5"),
        ["media_player"] = Glyph("M 5,3 L 12.5,8 L 5,13 Z"),
        ["scene"] = Glyph("M 8,1.5 L 9.4,6.6 L 14.5,8 L 9.4,9.4 L 8,14.5 L 6.6,9.4 L 1.5,8 L 6.6,6.6 Z"),
        ["power"] = Glyph("M 8,1.5 L 8,7.5 M 4.6,3.8 A 5.5,5.5 0 1 0 11.4,3.8"),
    };

    private readonly IHomeAssistant? _home;
    private readonly IAgentTaskView? _run;
    private readonly Dispatcher _dispatcher;
    private readonly RelayCommand _toggle;
    private string _state;
    private bool _busy;
    private bool _expanded;
    private string _problem = string.Empty;
    private bool _disposed;

    /// <summary>Creates the card for <paramref name="device"/>. It must be created on the user interface's thread.</summary>
    /// <param name="device">The device as the tool left it.</param>
    /// <param name="home">The user's Home Assistant, for the glyph's press; without it the glyph only shows what the device is.</param>
    /// <param name="run">The run that controlled it, whose steps the card can open; without it there are none to open.</param>
    public HomeDeviceContent(HomeControlled device, IHomeAssistant? home = null, IAgentTaskView? run = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        Id = device.Id;
        Name = device.Name;
        Kind = device.Kind;
        _state = device.State;
        _home = home;
        _run = run;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _toggle = new RelayCommand(_ => _ = ToggleAsync(), _ => CanToggle && !IsBusy);
        OpenCommand = new RelayCommand(_ => Open());
        if (_run is not null)
        {
            ShowSteps(_run.Snapshot);
            _run.Changed += OnRunChanged;
        }
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the card is pressed anywhere but on its glyph: whoever shows it alone, as a pill, opens the conversation then.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>The card is as wide as a card.</summary>
    public override bool IsWide => true;

    /// <summary>Home Assistant's id of the device.</summary>
    public string Id { get; }

    /// <summary>What the device is called.</summary>
    public string Name { get; }

    /// <summary>What kind of thing it is: <c>fan</c>, <c>light</c>, <c>climate</c>.</summary>
    public string Kind { get; }

    /// <summary>What it is now, as it is read: "Off", "On", "Heat cool". Empty when Home Assistant did not say.</summary>
    public string StateText
    {
        get
        {
            var words = _state.Replace('_', ' ').Trim();
            return words.Length == 0 ? string.Empty : char.ToUpper(words[0], CultureInfo.CurrentCulture) + words[1..];
        }
    }

    /// <summary>Whether it is on (or open, or unlocked): the glyph is lit then.</summary>
    public bool IsOn => OnStates.Contains(_state);

    /// <summary>The device's glyph, drawn in a 16 by 16 box.</summary>
    public Geometry Icon => Glyphs.TryGetValue(GlyphOf(Kind), out var glyph) ? glyph : Glyphs["power"];

    /// <summary>Whether pressing the glyph switches the device: a Home Assistant is there, and it is a kind of thing that is switched.</summary>
    public bool CanToggle => _home is not null && Switched.Contains(Kind);

    /// <summary>Whether Home Assistant is being told to switch it.</summary>
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (_busy != value)
            {
                _busy = value;
                OnPropertyChanged();
                _toggle.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>What went wrong with the last press of the glyph, in a few words; empty when nothing did.</summary>
    public string Problem
    {
        get => _problem;
        private set
        {
            if (_problem != value)
            {
                _problem = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasProblem));
                OnPropertyChanged(nameof(Text));
            }
        }
    }

    /// <summary>Whether there is a <see cref="Problem"/>.</summary>
    public bool HasProblem => _problem.Length > 0;

    /// <summary>What the Assistant did to get here, step by step.</summary>
    public ObservableCollection<ActivityStepItem> Steps { get; } = [];

    /// <summary>Whether there are steps to open.</summary>
    public bool HasSteps => Steps.Count > 0;

    /// <summary>Whether the steps are open under the card.</summary>
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded != value)
            {
                _expanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowsSteps));
            }
        }
    }

    /// <summary>Whether the steps are drawn: they are open, and there are some.</summary>
    public bool ShowsSteps => _expanded && Steps.Count > 0;

    /// <summary>Switches the device: the glyph's press.</summary>
    public ICommand ToggleCommand => _toggle;

    /// <summary>Opens what was done, or closes it: a press anywhere else on the card.</summary>
    public ICommand OpenCommand { get; }

    /// <summary>What the glyph's button is called, for assistive technology: "Turn on Fan".</summary>
    public string ToggleName => (IsOn ? "Turn off " : "Turn on ") + Name;

    /// <summary>The card as one text, for assistive technology.</summary>
    public string Text => Name + (StateText.Length > 0 ? ", " + StateText : string.Empty) + (HasProblem ? ". " + Problem : string.Empty);

    /// <summary>Tells Home Assistant to switch the device the other way, and shows what it then says the device is. It never throws.</summary>
    public async Task ToggleAsync()
    {
        if (_home is null || !CanToggle || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var (domain, service) = Kind switch
            {
                "lock" => ("lock", IsOn ? "lock" : "unlock"),
                "cover" => ("cover", IsOn ? "close_cover" : "open_cover"),
                "vacuum" => ("vacuum", IsOn ? "return_to_base" : "start"),
                _ => ("homeassistant", "toggle"),
            };
            var wasOn = IsOn;
            var result = await _home.CallAsync(domain, service, Id).ConfigureAwait(true);
            if (!result.Done)
            {
                Problem = result.Failure == HomeFailure.Refused ? "Home Assistant could not do that." : "Home Assistant did not answer.";
                return;
            }

            Problem = string.Empty;
            SetState(result.State is { Length: > 0 } state ? state : wasOn ? "off" : "on");
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or System.IO.IOException or System.Net.Http.HttpRequestException)
        {
            Problem = "Home Assistant did not answer.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_run is not null)
            {
                _run.Changed -= OnRunChanged;
            }
        }
    }

    private void Open()
    {
        // Shown alone, the conversation opens around it with the steps open; in the conversation, the steps open and close.
        if (OpenRequested is { } opened)
        {
            IsExpanded = true;
            opened(this, EventArgs.Empty);
        }
        else
        {
            IsExpanded = !IsExpanded;
        }
    }

    private void SetState(string state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(ToggleName));
        OnPropertyChanged(nameof(Text));
    }

    private void OnRunChanged(object? sender, EventArgs e)
    {
        if (_disposed || _run is null)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            ShowSteps(_run.Snapshot);
        }
        else
        {
            _ = _dispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    ShowSteps(_run.Snapshot);
                }
            });
        }
    }

    private void ShowSteps(AgentTaskSnapshot snapshot)
    {
        for (var index = 0; index < snapshot.Steps.Count; index++)
        {
            if (index < Steps.Count)
            {
                Steps[index].Show(snapshot.Steps[index]);
            }
            else
            {
                Steps.Add(new ActivityStepItem(snapshot.Steps[index]));
            }
        }

        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(ShowsSteps));
    }

    private static string GlyphOf(string kind) => kind switch
    {
        "fan" => "fan",
        "light" => "light",
        "climate" or "water_heater" or "humidifier" => "climate",
        "lock" => "lock",
        "cover" or "valve" => "cover",
        "media_player" => "media_player",
        "scene" or "script" or "automation" => "scene",
        _ => "power",
    };

    private static Geometry Glyph(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
