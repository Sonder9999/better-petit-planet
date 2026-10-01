using Vanara.PInvoke;

namespace BetterPetitPlanet.WindowsInput;

internal interface IInputMessageDispatcher
{
    public void DispatchInput(User32.INPUT[] inputs);
}
