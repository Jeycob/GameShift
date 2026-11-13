namespace GameShift.Core;

public enum SwitchMode { Ai, Background, Gaming, Automatic }
public enum WorkState { Ai, Background, Gaming }

public sealed class SwitchPolicy
{
    private DateTimeOffset? lastGameSeen;
    public void RestartCooldown(DateTimeOffset now) => lastGameSeen = now;
    public WorkState Evaluate(SwitchMode mode, bool gameRunning, DateTimeOffset now, int resumeDelaySeconds)
    {
        if (gameRunning) lastGameSeen = now;
        return mode switch
        {
            SwitchMode.Ai => WorkState.Ai,
            SwitchMode.Background => WorkState.Background,
            SwitchMode.Gaming => WorkState.Gaming,
            _ => gameRunning || lastGameSeen is { } seen &&
                now - seen < TimeSpan.FromSeconds(Math.Clamp(resumeDelaySeconds, 5, 600))
                ? WorkState.Gaming : WorkState.Ai
        };
    }
}
