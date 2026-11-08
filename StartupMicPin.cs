namespace VoiceAec;

// Shares the existing audio recovery worker; owns no timer or notification subscription.
internal sealed class StartupMicPin(bool enabled, Func<bool> tryPin)
{
    private readonly object gate = new();
    private bool enabled = enabled;
    private bool completed;
    private int polls;
    private const int MaxPolls = 30; // One minute at the recovery worker's 2s interval.

    public bool Enabled { get { lock (gate) return enabled; } }

    public void SetEnabled(bool value)
    {
        lock (gate)
        {
            if (enabled == value) return;
            enabled = value;
            completed = false;
            polls = 0;
        }
    }

    public void TryPin(bool eventDriven, bool paused)
    {
        lock (gate)
        {
            // Check before doing any COM calls or endpoint enumeration, including
            // when the cable is missing. Notifications can retry after the poll budget.
            if (!enabled || completed || paused || (!eventDriven && polls >= MaxPolls)) return;
            if (!eventDriven) polls++;
            completed = tryPin();
        }
    }
}
