namespace MusicSpin.App.Services;

/// <summary>The turntable scene's steps (mockup: playOnTurntable).</summary>
public enum SceneStep
{
    /// <summary>No scene.</summary>
    None,

    /// <summary>The sleeve lifts from where it sits and flies to the deck; Now Playing fades in behind it.</summary>
    Flying,

    /// <summary>The record slides out of the sleeve.</summary>
    SlidingOut,

    /// <summary>The sleeve drifts away and fades; the record lifts, crosses back and settles on the platter.</summary>
    Crossing,

    /// <summary>The platter spins up while the tonearm swings over the lead-in.</summary>
    SwingingOver,

    /// <summary>The arm lowers; playback starts at the end.</summary>
    Lowering,
}

/// <summary>
/// The scene's times in milliseconds at Motion Smooth, as the mockup's <c>playOnTurntable</c> (each multiplied by the
/// Motion setting when it plays).
/// </summary>
public static class SceneTimeline
{
    /// <summary>The sleeve's flight (mockup: 1000ms, cubic-bezier(.35,.6,.25,1)).</summary>
    public const double FlightMs = 1000;

    /// <summary>When Now Playing starts fading in, into the flight.</summary>
    public const double OpenAt = 220;

    /// <summary>The pause after the sleeve lands.</summary>
    public const double SettleMs = 120;

    /// <summary>The record sliding out (850ms, cubic-bezier(.5,0,.2,1)).</summary>
    public const double SlideOutMs = 850;

    /// <summary>The sleeve drifting away (750ms, cubic-bezier(.4,0,.6,1)).</summary>
    public const double DriftMs = 750;

    /// <summary>The record crossing back onto the platter (1400ms after 120ms, cubic-bezier(.45,0,.3,1)).</summary>
    public const double CrossDelayMs = 120;

    public const double CrossMs = 1400;

    /// <summary>The platter starts, then the arm swings over (after 40ms; 950ms) and lowers (500ms).</summary>
    public const double ArmOnDelayMs = 40;

    public const double SwingMs = 950;

    public const double LowerMs = 500;

    public const double SlideOutAt = FlightMs + SettleMs;

    public const double CrossAt = SlideOutAt + SlideOutMs;

    /// <summary>The record is on the platter: the travelling sleeve and record go, the platter spins up.</summary>
    public const double LandAt = CrossAt + CrossDelayMs + CrossMs;

    public const double ArmOnAt = LandAt + ArmOnDelayMs;

    public const double LowerAt = ArmOnAt + SwingMs;

    /// <summary>Playback starts.</summary>
    public const double End = LowerAt + LowerMs;

    /// <summary>The step in progress at <paramref name="ms"/> into the scene (Smooth time).</summary>
    public static SceneStep StepAt(double ms) => ms switch
    {
        < 0 => SceneStep.None,
        < SlideOutAt => SceneStep.Flying,
        < CrossAt => SceneStep.SlidingOut,
        < LandAt => SceneStep.Crossing,
        < LowerAt => SceneStep.SwingingOver,
        < End => SceneStep.Lowering,
        _ => SceneStep.None,
    };
}
