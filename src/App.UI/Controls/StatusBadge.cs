using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using ContextMole.App.UI.ViewModels;

namespace ContextMole.App.UI.Controls;

/// <summary>Applies one CSS-like class per tone so App.axaml owns every status color.</summary>
internal static class StatusToneClasses
{
    private static readonly (AppStatusTone Tone, string Name)[] Names =
    [
        (AppStatusTone.Neutral, "neutral"), (AppStatusTone.Busy, "busy"), (AppStatusTone.Ready, "ready"),
        (AppStatusTone.Warning, "warning"), (AppStatusTone.Error, "error")
    ];

    public static void Apply(StyledElement element, AppStatusTone tone)
    {
        foreach (var (candidate, name) in Names) element.Classes.Set(name, candidate == tone);
    }
}

/// <summary>A rounded status pill. Text and tone come from view-model presentation state.</summary>
public sealed class StatusBadge : Border
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StatusBadge, string?>(nameof(Text));

    public static readonly StyledProperty<AppStatusTone> ToneProperty =
        AvaloniaProperty.Register<StatusBadge, AppStatusTone>(nameof(Tone));

    private readonly TextBlock _text = new()
    {
        FontSize = 12,
        FontWeight = FontWeight.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };

    public StatusBadge()
    {
        Child = _text;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        StatusToneClasses.Apply(this, Tone);
    }

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public AppStatusTone Tone { get => GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty) _text.Text = Text;
        else if (change.Property == ToneProperty) StatusToneClasses.Apply(this, Tone);
    }
}

/// <summary>A small colored dot that matches <see cref="StatusBadge"/> tones.</summary>
public sealed class StatusDot : Border
{
    public static readonly StyledProperty<AppStatusTone> ToneProperty =
        AvaloniaProperty.Register<StatusDot, AppStatusTone>(nameof(Tone));

    public StatusDot()
    {
        Width = 8;
        Height = 8;
        CornerRadius = new CornerRadius(4);
        VerticalAlignment = VerticalAlignment.Center;
        StatusToneClasses.Apply(this, Tone);
    }

    public AppStatusTone Tone { get => GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ToneProperty) StatusToneClasses.Apply(this, Tone);
    }
}
