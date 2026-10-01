namespace BetterPetitPlanet.GameTask;

public interface ITaskTrigger
{
    string Name { get; }
    bool IsEnabled { get; set; }
    int Priority { get; }
    void OnCapture(CaptureContent content);
}
